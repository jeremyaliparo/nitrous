using System;

namespace Nitrous.Helpers;

/// <summary>
/// Asymmetric fan slew: a rise is applied at once, a fall is spread over several ticks.
///
/// The curve engine used to apply every change immediately, so a burst of load that lifted the
/// temperature for a second raised the fans and dropped them again — audible revving, and the
/// heatsink kept the heat either way. Slowing only the descent removes that without weakening the
/// response to a real spike.
///
/// Two things stop this from being a plain rate limiter:
/// a <b>dwell</b>, so a descent only starts after the target has stayed low for a moment, and a
/// <b>hard override</b>, so the fans are never held back while the chip is hot.
///
/// Not thread-safe; the engine owns one instance per fan.
/// </summary>
public sealed class FanRampState
{
    private int _applied = -1;
    private DateTime? _belowSince;

    /// <summary>The value last handed to the firmware; -1 before the first call.</summary>
    public int Applied => _applied;

    /// <summary>Forgets everything, for when curve mode is left or the curve is replaced.</summary>
    public void Reset()
    {
        _applied = -1;
        _belowSince = null;
    }

    /// <summary>
    /// The speed to apply this tick.
    /// </summary>
    /// <param name="target">What the curve asks for at the current temperature.</param>
    /// <param name="temperature">The reading the target came from.</param>
    /// <param name="ratePerSecond">Descent rate; 0 disables ramping entirely.</param>
    /// <param name="tick">How long the engine sleeps between calls.</param>
    /// <param name="hold">How long a descent must be wanted before it starts.</param>
    /// <param name="now">Current time, passed in so this stays deterministic.</param>
    public int Next(
        int target,
        int temperature,
        int ratePerSecond,
        TimeSpan tick,
        TimeSpan hold,
        DateTime now)
    {
        // First application, a rise, a hot chip, or ramping turned off: do what the curve says.
        if (_applied < 0 ||
            target >= _applied ||
            temperature >= FanRamp.NeverRampDownAtOrAbove ||
            ratePerSecond <= 0)
        {
            _applied = target;
            _belowSince = null;
            return _applied;
        }

        // A descent is wanted. Hold the current speed until it has been wanted for long enough.
        _belowSince ??= now;
        if (now - _belowSince.Value < hold)
            return _applied;

        var step = Math.Max(1, (int)Math.Round(ratePerSecond * tick.TotalSeconds));
        _applied = Math.Max(target, _applied - step);
        return _applied;
    }
}

/// <summary>The user-facing ramp-down choices and the rates they stand for.</summary>
public static class FanRamp
{
    /// <summary>At or above this the fans go where the curve says, ramp or no ramp.</summary>
    public const int NeverRampDownAtOrAbove = 90;

    /// <summary>How long a lower target must persist before the fans start following it.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    public const string Off = "Off";
    public const string Gentle = "Gentle";
    public const string Balanced = "Balanced";
    public const string Fast = "Fast";

    public static readonly string[] Options = { Off, Gentle, Balanced, Fast };

    /// <summary>Percentage points per second, so the engine's tick length does not change the feel.</summary>
    public static int PerSecondFor(string? setting) => setting switch
    {
        Off => 0,
        Gentle => 5,
        Fast => 20,
        _ => 10 // Balanced, and anything unrecognised
    };
}
