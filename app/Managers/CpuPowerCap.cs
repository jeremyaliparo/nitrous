using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// A sustained (PL1) power cap written to MSR 0x610.
///
/// Lower-only by construction: the cap is clamped to the current mode's own request, so nothing here
/// can ask the package for more power than the firmware already asked for. Measured on pr4u's
/// ANV15-52: a lower PL1 takes effect immediately, is accepted well below the mode's value, and the
/// CPU honours it over the mode's request.
///
/// "Off" writes the mode's own request back rather than replaying a captured value. Replaying a
/// snapshot looked right but was wrong twice over: it restored whatever the register held before this
/// process wrote - another tool's cap, if one was running - and there was no snapshot to replay at all
/// when the cap had been applied by the tray or by an earlier dashboard process.
///
/// The firmware's request is read from MCHBAR 0x59A0, which a write to 0x610 has been reported moving.
/// So once a cap is written the live copy is not trusted on its own: the last reading taken while the
/// firmware owned it is kept, and the smaller of the two is used.
///
/// Bit 63 (the lock) is never written: that would make the register refuse changes until the next boot.
/// </summary>
internal static class CpuPowerCap
{
    /// <summary>
    /// Below this a laptop is not usable for anything. It is a usability floor, not a vendor limit.
    /// </summary>
    public const int FloorWatts = 10;

    private const string WattsKey = "CpuPowerCapWatts";
    private const string CeilingKey = "CpuPowerCapCeiling";

    private const ulong Pl1Mask = 0x7FFF;
    private const ulong LockBit = 1UL << 63;

    private static readonly object Gate = new();

    /// <summary>Whether a write of ours may have moved the MCHBAR copy since the firmware last wrote it.</summary>
    private static bool _moved;

    /// <summary>The last MCHBAR PL1 read while the copy was still the firmware's.</summary>
    private static int? _pristine;

    public readonly record struct Result(bool Ok, string Message);

    private static Result Fail(string message)
    {
        Logger.Warn($"CPU power cap: {message}");
        return new Result(false, message);
    }

    /// <summary>The cap the user asked for, or zero for none.</summary>
    public static int SavedWatts => Math.Max(0, SettingsManager.Get(WattsKey, 0));

    /// <summary>
    /// The firmware's own PL1 for this mode: the highest a cap may be set to. While a cap may have
    /// moved the copy, the smaller of the remembered and the live reading is used, so a stale value can
    /// only ever make this more conservative.
    /// </summary>
    public static int? Ceiling()
    {
        var firmware = CpuPowerLimits.ReadFirmware();
        var live = firmware is { Pl1Watts: > 0 } mode ? (int?)Math.Round(mode.Pl1Watts) : null;

        if (!_moved)
        {
            _pristine = live;
            return live;
        }

        return (_pristine, live) switch
        {
            ({ } remembered, { } seen) => Math.Min(remembered, seen),
            ({ } remembered, null) => remembered,
            _ => live
        };
    }

    /// <summary>Whether the register may be written right now.</summary>
    public static bool IsWritable() =>
        CpuPowerLimits.ReadRegisterRaw() is { } raw && (raw & LockBit) == 0;

    /// <summary>
    /// Writes the cap, clamped to <see cref="FloorWatts"/> and to the mode's own request, then reads it
    /// back. A write that does not stick is undone and reported.
    /// </summary>
    public static Result Apply(int watts)
    {
        lock (Gate)
        {
            if (Ceiling() is not { } ceiling)
                return Fail("this mode does not report a sustained limit.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return Fail("MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return Fail("the firmware has locked this until the next reboot.");

            var target = Math.Clamp(watts, FloorWatts, ceiling);
            var value = (raw & ~Pl1Mask) | ((ulong)target * 8 & Pl1Mask);

            // Only the PL1 field may differ, so the lock bit, PL2 and the time windows are provably
            // untouched by this write.
            if ((value & ~Pl1Mask) != (raw & ~Pl1Mask))
                return Fail("the new value would have changed another field; nothing written.");

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return Fail("the register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Pl1Mask) != (value & Pl1Mask))
            {
                PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, raw);
                return Fail("the register did not keep the value; the old one is back.");
            }

            _moved = true;
            _pristine = ceiling;

            SettingsManager.Save(WattsKey, target);
            SettingsManager.Save(CeilingKey, ceiling);

            Logger.Info($"CPU power cap: {target} W sustained (this mode asks {ceiling} W).");
            return new Result(true, $"Capped to {target} W.");
        }
    }

    /// <summary>
    /// Puts the mode's own request back. That is the definition of "no cap", and because the ceiling is
    /// the firmware's own number it can never raise the limit above what the mode asked for.
    /// </summary>
    public static Result Reset()
    {
        lock (Gate)
        {
            if (Ceiling() is not { } ceiling)
                return Fail("this mode does not report a sustained limit.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return Fail("MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return Fail("the firmware has locked this until the next reboot.");

            Logger.Info($"CPU power cap: clearing, this mode asks {ceiling} W, register holds " +
                        $"{Math.Round((raw & Pl1Mask) * 0.125)} W.");

            var value = (raw & ~Pl1Mask) | ((ulong)ceiling * 8 & Pl1Mask);

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return Fail("the register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Pl1Mask) != (value & Pl1Mask))
                return Fail("the value did not stick; the register may be locked.");

            SettingsManager.Save(WattsKey, 0);

            // The register now holds the firmware's number, but the write may have moved the MCHBAR
            // copy, so keep that number as the trusted ceiling until the firmware rewrites it.
            _moved = true;
            _pristine = ceiling;

            Logger.Info($"CPU power cap: cleared, back to the mode's {ceiling} W.");
            return new Result(true, $"Back to {ceiling} W, the mode's own limit.");
        }
    }

    /// <summary>
    /// Applies the saved cap at start-up, after a resume, or when a mode change lowered the ceiling
    /// under it. Does nothing when no cap is saved.
    /// </summary>
    public static void Reassert()
    {
        var saved = SavedWatts;
        if (saved <= 0) return;

        if (Ceiling() is not { } ceiling) return;

        Apply(Math.Min(saved, ceiling));
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
    /// Picks up the remembered ceiling in a process that did not perform the write, so a cap applied by
    /// the tray or by an earlier dashboard is still bounded correctly.
    /// </summary>
    internal static void Load()
    {
        lock (Gate)
        {
            _moved = false;
            _pristine = null;

            if (SavedWatts <= 0) return;

            var remembered = SettingsManager.Get(CeilingKey, 0);
            if (remembered <= 0) return;

            _moved = true;
            _pristine = remembered;
        }
    }
}
