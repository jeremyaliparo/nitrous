using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// A sustained (PL1) power cap written to MSR 0x610.
///
/// Lower-only by construction: the cap is clamped to the current mode's own request, read from
/// MCHBAR 0x59A0, so nothing here can ask the package for more power than the firmware already asked
/// for. Measured on pr4u's ANV15-52: a lower PL1 takes effect immediately, is accepted well below the
/// mode's value, and the CPU honours it over the mode's request.
///
/// Two behaviours belong to the firmware and are worked around rather than assumed away:
/// - sleep/resume restores its own value and clears the clamp bits, so the tray re-applies the cap;
/// - writing 0x610 also moves the MCHBAR copy, so <see cref="Reset"/> restores the register value
///   captured before the first write instead of re-reading MCHBAR, which would be the moved value.
///
/// Bit 63 (the lock) is never touched: writing it would make the register refuse changes until the
/// next boot, so a locked register is reported instead of written.
/// </summary>
internal static class CpuPowerCap
{
    /// <summary>
    /// Below this a laptop is not usable for anything. It is a usability floor, not a vendor limit.
    /// </summary>
    public const int FloorWatts = 10;

    private const string WattsKey = "CpuPowerCapWatts";
    private const string OriginalKey = "CpuPowerCapOriginal";

    private const ulong Pl1Mask = 0x7FFF;
    private const ulong LockBit = 1UL << 63;

    private static readonly object Gate = new();

    /// <summary>The register as it was before the first write, while that state still exists.</summary>
    private static ulong? _original;

    public readonly record struct Result(bool Ok, string Message);

    /// <summary>The cap the user asked for, or zero for none.</summary>
    public static int SavedWatts => Math.Max(0, SettingsManager.Get(WattsKey, 0));

    /// <summary>The current mode's own PL1: the highest a cap may be set to.</summary>
    public static int? Ceiling()
    {
        var firmware = CpuPowerLimits.ReadFirmware();
        return firmware is { Pl1Watts: > 0 } mode ? (int)Math.Round(mode.Pl1Watts) : null;
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
            if (CpuPowerLimits.ReadFirmware() is not { Pl1Watts: > 0 } mode)
                return new Result(false, "This mode does not report a sustained limit.");

            var ceiling = (int)Math.Round(mode.Pl1Watts);

            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw)
                return new Result(false, "MSR 0x610 could not be read.");

            if ((raw & LockBit) != 0)
                return new Result(false, "The firmware has locked this until the next reboot.");

            var target = Math.Clamp(watts, FloorWatts, ceiling);
            var value = (raw & ~Pl1Mask) | ((ulong)target * 8 & Pl1Mask);

            // Only the PL1 field may differ, so the lock bit, PL2 and the time windows are provably
            // untouched by this write.
            if ((value & ~Pl1Mask) != (raw & ~Pl1Mask))
                return new Result(false, "The new value would have changed another field.");

            if (_original is null)
            {
                _original = raw;
                PersistOriginal();
            }

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, value))
                return new Result(false, "The register refused the value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Pl1Mask) != (value & Pl1Mask))
            {
                PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, raw);
                return new Result(false, "The register did not keep the value; the old one is back.");
            }

            SettingsManager.Save(WattsKey, target);

            Logger.Info($"CPU power cap: {target} W sustained (this mode asks {ceiling} W).");
            return new Result(true, $"Capped to {target} W.");
        }
    }

    /// <summary>Puts back the register value captured before the first write.</summary>
    public static Result Reset()
    {
        lock (Gate)
        {
            SettingsManager.Save(WattsKey, 0);

            if (_original is not { } before)
                return new Result(true, "No cap is set.");

            if (!PawnIo.WriteMsr(CpuPowerLimits.PackagePowerLimit, before))
                return new Result(false, "The register refused the original value.");

            if (CpuPowerLimits.ReadRegisterRaw() is not { } check || (check & Pl1Mask) != (before & Pl1Mask))
                return new Result(false, "The original value did not stick; reboot to clear the cap.");

            _original = null;
            PersistOriginal();

            Logger.Info("CPU power cap: cleared, the firmware's own value is back.");
            return new Result(true, "The firmware's value is back.");
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

        // A mode change can put the ceiling under an existing cap; never leave the register above
        // what the current mode asks for.
        Apply(Math.Min(saved, ceiling));
    }

    /// <summary>Drops the captured value after the firmware re-seeded the register (resume).</summary>
    public static void ForgetOriginal()
    {
        lock (Gate)
        {
            _original = null;
            PersistOriginal();
        }
    }

    /// <summary>
    /// Adopts a captured value from an earlier run only when the register still holds our cap. After a
    /// reboot the firmware has put its own value back, so an older capture describes a state that no
    /// longer exists and must not be written.
    /// </summary>
    internal static void Load()
    {
        lock (Gate)
        {
            _original = null;

            var saved = SavedWatts;
            if (saved <= 0) return;
            if (CpuPowerLimits.ReadRegisterRaw() is not { } raw) return;

            var held = Math.Round((raw & Pl1Mask) * 0.125);
            if (Math.Abs(held - saved) > 1) return;

            if (!ulong.TryParse(SettingsManager.Get(OriginalKey, ""), out var stored)) return;
            if ((stored & Pl1Mask) < (raw & Pl1Mask)) return;

            _original = stored;
        }
    }

    private static void PersistOriginal() =>
        SettingsManager.Save(OriginalKey, _original is { } value ? value.ToString() : "");
}
