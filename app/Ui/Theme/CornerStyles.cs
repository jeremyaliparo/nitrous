using Nitrous.Managers;

namespace Nitrous.Ui.Theme;

internal enum CornerStyle
{
    Rounded,
    Soft,
    Square
}

/// <summary>
/// The Theme "Corners" choice, shared by the dashboard and the fan-curve window so the two
/// cannot drift apart. Each window turns these numbers into its own resources.
/// </summary>
internal static class CornerStyles
{
    public static CornerStyle Current
    {
        get
        {
            var saved = SettingsManager.Get("CornerStyle", "");
            if (Enum.TryParse<CornerStyle>(saved, ignoreCase: true, out var style))
                return style;

            // 0.17.0-test.3 and earlier stored the binary toggle.
            return SettingsManager.Get("SquareCorners", 0) == 1
                ? CornerStyle.Square
                : CornerStyle.Rounded;
        }
    }

    public static void Save(CornerStyle style) => SettingsManager.Save("CornerStyle", style.ToString());

    public static CornerRadii Radii(CornerStyle style) => style switch
    {
        CornerStyle.Square => new CornerRadii(0, 0, 0, 0, 0, 0, 0, 0),
        CornerStyle.Soft => new CornerRadii(8, 6, 5, 4, 3, 1, 4, 7),
        _ => new CornerRadii(16, 12, 10, 8, 6, 2, 9, 15)
    };
}

internal readonly record struct CornerRadii(
    double Frame,
    double Hero,
    double Card,
    double Surface,
    double Chip,
    double Micro,
    double Ring,
    double Nav);
