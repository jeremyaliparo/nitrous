namespace Nitrous.Enums;

/// <summary>How the refresh profile behaves: a fixed rate, or Auto (chosen rate on AC, 60 on battery).</summary>
public enum RefreshMode
{
    /// <summary>AC: the rate last chosen, DC: 60 Hz.</summary>
    Auto = 0,

    /// <summary>The chosen rate, on AC and on battery.</summary>
    Fixed = 1
}
