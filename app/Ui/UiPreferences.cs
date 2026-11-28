using Nitrous.Managers;

namespace Nitrous.Ui;

/// <summary>
/// Choice lists and reads shared by the dashboard, the OSD and the tray. The dashboard writes
/// these preferences; the tray and OSD are separate windows and read them when they draw.
/// </summary>
internal static class UiPreferences
{
    public static readonly string[] OsdPositions =
        ["Bottom centre", "Top centre", "Bottom right", "Bottom left"];

    public static readonly int[] OsdDurations = [1, 2, 3, 4];
    public static readonly int[] OsdSizes = [80, 90, 100, 110, 120];

    public static readonly string[] TrayIconStyles = ["logo", "mode", "temperature"];
    public static readonly string[] TrayIconLabels = ["Logo", "Mode colour", "CPU temperature"];

    public static string OsdPosition
    {
        get
        {
            var saved = SettingsManager.Get("OsdPosition", OsdPositions[0]);
            return Array.IndexOf(OsdPositions, saved) >= 0 ? saved : OsdPositions[0];
        }
    }

    public static int OsdDurationSeconds => Nearest(SettingsManager.Get("OsdDuration", 2), OsdDurations);

    public static int OsdSizePercent => Nearest(SettingsManager.Get("OsdSize", 100), OsdSizes);

    public static string TrayIconStyle
    {
        get
        {
            var saved = SettingsManager.Get("TrayIconStyle", TrayIconStyles[0]);
            return Array.IndexOf(TrayIconStyles, saved) >= 0 ? saved : TrayIconStyles[0];
        }
    }

    /// <summary>Closest allowed choice, so a hand-edited registry value cannot scale the OSD absurdly.</summary>
    private static int Nearest(int value, int[] allowed)
    {
        var best = allowed[0];
        foreach (var candidate in allowed)
        {
            if (Math.Abs(candidate - value) < Math.Abs(best - value))
                best = candidate;
        }

        return best;
    }
}
