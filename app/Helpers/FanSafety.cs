using System;

namespace Nitrous.Helpers;

/// <summary>
/// Lower bound for user-controlled fan speeds. Nitrous allows the firmware's full
/// usable range down to 1%; every custom/manual path is clamped here so a stale or
/// malformed registry value cannot request 0% or an out-of-range value.
/// </summary>
public static class FanSafety
{
    public const int MinPercent = 1;

    public static int Clamp(int percent) => Math.Clamp(percent, MinPercent, 100);
}
