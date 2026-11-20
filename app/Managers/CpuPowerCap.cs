using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// Sustained (PL1) and burst (PL2) power caps written to MSR 0x610.
///
/// Lower-only by construction: each field is clamped to the current mode's own request, so nothing here
/// can ask the package for more power than the firmware already asked for. PL1 is also held at or below
/// PL2, because a sustained limit above the burst limit is not a configuration the firmware ever sets.
///
/// "Off" writes the mode's own request back rather than replaying a captured value: a snapshot restored
/// whatever another tool had left in the register, and there was no snapshot at all when the cap had been
/// applied by a different process.
///
/// The firmware's request is read from MCHBAR 0x59A0, which a write to 0x610 has been reported moving, so
/// once a cap is written the live copy is not trusted alone: the last reading taken while the firmware
/// owned it is kept and the smaller of the two is used.
///
/// Bit 63 (the lock) is never written: that would make the register refuse changes until the next boot.
/// </summary>
internal static class CpuPowerCap
{
    /// <summary>
    /// Below this a laptop is not usable for anything. It is a usability floor, not a vendor limit.
    /// </summary>
    public const int FloorWatts = 10;

    private const string Pl1Key = "CpuPowerCapWatts";
    private const string Pl2Key = "CpuPowerCapBurstWatts";
    private const string Pl1CeilingKey = "CpuPowerCapCeiling";
    private const string Pl2CeilingKey = "CpuPowerCapBurstCeiling";

    private const ulong Pl1Mask = 0x7FFF;
    private const ulong Pl2Mask = 0x7FFFUL << 32;
    private const ulong Fields = Pl1Mask | Pl2Mask;
    private const ulong LockBit = 1UL << 63;

    private static readonly object Gate = new();

    /// <summary>Whether a write of ours may have moved the MCHBAR copy since the firmware last wrote it.</summary>
    private static bool _moved;

    /// <summary>The last MCHBAR limits read while the copy was still the firmware's.</summary>
    private static Limits? _pristine;

    public readonly record struct Limits(int Pl1, int Pl2);

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>The caps the user asked for, zero where a field is left at the mode's value.</summary>
    public static Limits Saved => new(
        Math.Max(0, SettingsManager.Get(Pl1Key, 0)),
        Math.Max(0, SettingsManager.Get(Pl2Key, 0)));

    /// <summary>
    /// The firmware's own limits for this mode: the highest either cap may be set to. While a cap may
    /// have moved the copy, the smaller of the remembered and live readings is used, so a stale value can
    /// only ever make this more conservative.
    /// </summary>
    public static Limits? Ceilings()
    {
        var firmware = CpuPowerLimits.ReadFirmware();
        var live = firmware is { Pl1Watts: > 0, Pl2Watts: > 0 } mode
            ? (Limits?)new Limits((int)Math.Round(mode.Pl1Watts), (int)Math.Round(mode.Pl2Watts))
            : null;

        if (!_moved)
        {
            _pristine = live;
            return live;
        }

        return (_pristine, live) switch
        {
            ({ } remembered, { } seen) => new Limits(
                Math.Min(remembered.Pl1, seen.Pl1),
                Math.Min(remembered.Pl2, seen.Pl2)),
            ({ } remembered, null) => remembered,
            _ => live
        };
    }

    /// <summary>What the register holds right now, or null when it cannot be read.</summary>
    public static Limits? Applied() =>
        CpuPowerLimits.ReadRegister() is { Pl1Watts: > 0 } held
            ? new Limits((int)Math.Round(held.Pl1Watts), (int)Math.Round(held.Pl2Watts))
            : null;

    /// <summary>Whether the register may be written right now.</summary>
    public static bool IsWritable() =>
        CpuPowerLimits.ReadRegisterRaw() is { } raw && (raw & LockBit) == 0;

    /// <summary>
    /// Writes both caps, clamped to <see cref="FloorWatts"/> and to the mode's own request, then reads
    /// them back. A null field means "leave this one at the mode's value". A write that does not stick is
    /// undone and reported.
    /// </summary>
    public static Result Apply(int? pl1Watts, int? pl2Watts)
    {
        lock (Gate)
        {
            if (Ceilings() is not { } mode)
                return Fail("this mode does not report its limits.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return Fail("MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return Fail("the firmware has locked this until the next reboot.");

            var pl1 = Math.Clamp(pl1Watts ?? mode.Pl1, FloorWatts, mode.Pl1);
            var pl2 = Math.Clamp(pl2Watts ?? mode.Pl2, Math.Max(FloorWatts, pl1), mode.Pl2);

            if (pl1 > pl2)
                return Fail("the sustained limit cannot sit above the burst limit.");

            var value = (raw & ~Fields)
                        | ((ulong)pl1 * 8 & Pl1Mask)
                        | (((ulong)pl2 * 8) << 32 & Pl2Mask);

            // Only the two limit fields may differ, so the lock, the enables, the clamp bits and the
            // time windows are provably untouched by this write.
            if ((value & ~Fields) != (raw & ~Fields))
                return Fail("the new value would have changed another field; nothing written.");

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return Fail("the register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Fields) != (value & Fields))
            {
                PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, raw);
                return Fail("the register did not keep the value; the old one is back.");
            }

            _moved = true;
            _pristine = mode;

            SettingsManager.Save(Pl1Key, pl1 == mode.Pl1 ? 0 : pl1);
            SettingsManager.Save(Pl2Key, pl2 == mode.Pl2 ? 0 : pl2);
            SettingsManager.Save(Pl1CeilingKey, mode.Pl1);
            SettingsManager.Save(Pl2CeilingKey, mode.Pl2);

            Logger.Info($"CPU power cap: {pl1} W sustained / {pl2} W burst " +
                        $"(this mode asks {mode.Pl1}/{mode.Pl2}).");
            return new Result(true, $"Capped to {pl1} W sustained / {pl2} W burst.");
        }
    }

    /// <summary>
    /// Puts both fields back to the mode's own request. That is the definition of "no cap", and because
    /// the ceilings are the firmware's own numbers it can never raise a limit above what the mode asked for.
    /// </summary>
    public static Result Reset()
    {
        lock (Gate)
        {
            if (Ceilings() is not { } mode)
                return Fail("this mode does not report its limits.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return Fail("MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return Fail("the firmware has locked this until the next reboot.");

            Logger.Info($"CPU power cap: clearing, this mode asks {mode.Pl1}/{mode.Pl2} W, register holds " +
                        $"{Applied()?.Pl1 ?? 0}/{Applied()?.Pl2 ?? 0} W.");

            var value = (raw & ~Fields)
                        | ((ulong)mode.Pl1 * 8 & Pl1Mask)
                        | (((ulong)mode.Pl2 * 8) << 32 & Pl2Mask);

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return Fail("the register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Fields) != (value & Fields))
                return Fail("the value did not stick; the register may be locked.");

            SettingsManager.Save(Pl1Key, 0);
            SettingsManager.Save(Pl2Key, 0);

            // The register now holds the firmware's numbers, but the write may have moved the MCHBAR
            // copy, so keep them as the trusted ceiling until the firmware rewrites it.
            _moved = true;
            _pristine = mode;

            Logger.Info($"CPU power cap: cleared, back to the mode's {mode.Pl1}/{mode.Pl2} W.");
            return new Result(true, $"Back to {mode.Pl1} W sustained / {mode.Pl2} W burst.");
        }
    }

    /// <summary>
    /// Keeps the register inside the current mode's request: re-applies a saved cap after a resume, and
    /// pulls the register down when a mode change left it above what the new mode asks for. A mode change
    /// does not rewrite MSR 0x610, so a cap set in a higher mode otherwise outlives the mode that allowed
    /// it. Never raises anything.
    /// </summary>
    public static void Reassert()
    {
        if (Ceilings() is not { } mode) return;
        if (Applied() is not { } now) return;

        var saved = Saved;

        var pl1 = Math.Min(saved.Pl1 > 0 ? saved.Pl1 : now.Pl1, mode.Pl1);
        var pl2 = Math.Min(saved.Pl2 > 0 ? saved.Pl2 : now.Pl2, mode.Pl2);

        if (pl1 == now.Pl1 && pl2 == now.Pl2) return;

        Apply(pl1, pl2);
    }

    /// <summary>
    /// The firmware has rewritten MCHBAR - a mode was applied, or the machine resumed - so the copy can
    /// be trusted again.
    /// </summary>
    public static void NoteFirmwareRewrite()
    {
        lock (Gate)
        {
            _moved = false;
            _pristine = null;
        }
    }

    /// <summary>
    /// Picks up the remembered ceilings in a process that did not perform the write, so a cap applied by
    /// the tray or by an earlier dashboard is still bounded correctly.
    /// </summary>
    internal static void Load()
    {
        lock (Gate)
        {
            _moved = false;
            _pristine = null;

            var saved = Saved;
            if (saved.Pl1 == 0 && saved.Pl2 == 0) return;

            var pl1 = SettingsManager.Get(Pl1CeilingKey, 0);
            var pl2 = SettingsManager.Get(Pl2CeilingKey, 0);
            if (pl1 <= 0 || pl2 <= 0) return;

            _moved = true;
            _pristine = new Limits(pl1, pl2);
        }
    }

    private static Result Fail(string message)
    {
        Logger.Warn($"CPU power cap: {message}");
        return new Result(false, message);
    }
}
