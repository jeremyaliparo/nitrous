namespace Nitrous.Managers;

/// <summary>
/// The CPU package power limits as the firmware currently has them, read from MSR 0x610.
///
/// Intel documents the register (SDM Vol. 4, MSR_PKG_POWER_LIMIT): PL1 in bits 14:0, PL2 in bits
/// 46:32, both in 1/8 W on every part seen so far; bit 15 and 31 are the clamp enables, bit 63 the
/// lock. MCHBAR holds a firmware copy of the same values and is intentionally not touched.
///
/// Read-only by design. Writing this register is how a laptop is made to exceed its own sustained
/// power budget, so Nitrous reports the numbers and leaves the writing to tools whose users asked
/// for it explicitly.
/// </summary>
internal static class CpuPowerLimits
{
    /// <summary>MSR_PKG_POWER_LIMIT.</summary>
    public const uint PackagePowerLimit = 0x610;

    private const double PowerUnitWatts = 1.0 / 8.0;

    public readonly record struct Reading(
        double Pl1Watts,
        double Pl2Watts,
        bool Pl1Clamped,
        bool Pl2Clamped,
        bool Locked);

    /// <summary>The current limits, or null when PawnIO is absent, the CPU is not Intel, or the
    /// register reads as zero.</summary>
    public static Reading? Read()
    {
        if (PawnIo.ReadMsr(PackagePowerLimit) is not { } raw)
            return null;

        var pl1 = (raw & 0x7FFF) * PowerUnitWatts;
        var pl2 = ((raw >> 32) & 0x7FFF) * PowerUnitWatts;

        // A locked or unpopulated register reads as zero on some firmware; nothing to show then.
        if (pl1 <= 0 && pl2 <= 0)
            return null;

        return new Reading(
            pl1,
            pl2,
            Pl1Clamped: ((raw >> 15) & 1) == 1,
            Pl2Clamped: ((raw >> 31) & 1) == 1,
            Locked: ((raw >> 63) & 1) == 1);
    }
}
