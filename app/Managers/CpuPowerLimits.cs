namespace Nitrous.Managers;

/// <summary>
/// The CPU package power limits as the firmware currently has them.
///
/// Two copies exist on an Intel platform, and they are not the same thing:
///
/// - MSR 0x610 (<see cref="PackagePowerLimit"/>) is the register the CPU enforces. Firmware seeds it
///   once at boot with the then-active mode's numbers and, on the machines measured so far, never
///   updates it again - a power-mode switch does not rewrite it.
/// - MCHBAR 0x59A0 (<see cref="FirmwarePowerLimit"/>) is the firmware's working copy. This is the one
///   a mode switch does rewrite, so it is the only one that reflects Quiet/Balanced/Performance.
///
/// Intel documents the layout for both (SDM Vol. 4, MSR_PKG_POWER_LIMIT): PL1 in bits 14:0, PL2 in
/// bits 46:32, both in 1/8 W, bit 15/47 the enables, bit 63 the lock.
///
/// Read-only by design. Writing either copy is how a laptop is made to exceed its own sustained power
/// budget, and no per-model ceiling has been established that would bound a write safely.
/// </summary>
internal static class CpuPowerLimits
{
    /// <summary>MSR_PKG_POWER_LIMIT: what the CPU enforces, seeded at boot.</summary>
    public const uint PackagePowerLimit = 0x610;

    /// <summary>The firmware's copy inside MCHBAR: what the current power mode asks for.</summary>
    public const ulong FirmwarePowerLimit = 0x59A0;

    private const double PowerUnitWatts = 1.0 / 8.0;

    public readonly record struct Reading(double Pl1Watts, double Pl2Watts, bool Locked);

    /// <summary>
    /// What the current power mode asks for, read from the firmware's MCHBAR copy. Null when PawnIO is
    /// absent, the CPU is not Intel this module supports, or the value reads as zero.
    /// </summary>
    public static Reading? ReadFirmware()
    {
        if (PawnIo.ReadMchbar(FirmwarePowerLimit) is not { } raw)
            return null;

        var reading = Decode(raw);

        // An unpopulated or locked-out copy reads as zero; nothing worth showing then.
        return reading.Pl1Watts <= 0 && reading.Pl2Watts <= 0 ? null : reading;
    }

    /// <summary>
    /// What the CPU is actually enforcing, read from MSR 0x610. Differs from
    /// <see cref="ReadFirmware"/> when a driver has set the limits itself, or simply because the
    /// register was seeded by whichever mode was active at boot.
    /// </summary>
    public static Reading? ReadRegister()
    {
        if (PawnIo.ReadMsr(PackagePowerLimit) is not { } raw)
            return null;

        var reading = Decode(raw);
        return reading.Pl1Watts <= 0 && reading.Pl2Watts <= 0 ? null : reading;
    }

    private static Reading Decode(ulong raw) => new(
        (raw & 0x7FFF) * PowerUnitWatts,
        ((raw >> 32) & 0x7FFF) * PowerUnitWatts,
        Locked: ((raw >> 63) & 1) == 1);
}
