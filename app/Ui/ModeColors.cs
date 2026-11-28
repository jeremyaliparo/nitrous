using System;
using System.Drawing;
using Nitrous.Enums;
using Nitrous.Managers;

namespace Nitrous.Ui;

/// <summary>
/// The power-profile colours shared by the toast and the tray icon. The defaults are the colours
/// the firmware flashes on the keyboard for each mode; a user pick overrides one mode for Nitrous'
/// own surfaces only. The keyboard flash itself is fixed by the controller.
/// </summary>
internal static class ModeColors
{
    public static readonly (PowerProfile Profile, string Name, Color Default)[] Choices =
    [
        (PowerProfile.Quiet, "Quiet", Color.FromArgb(255, 255, 255)),
        (PowerProfile.Balanced, "Balanced", Color.FromArgb(255, 159, 10)),
        (PowerProfile.Performance, "Performance", Color.FromArgb(224, 30, 55)),
        (PowerProfile.Turbo, "Turbo", Color.FromArgb(179, 136, 255))
    ];

    public static Color For(PowerProfile profile)
    {
        var saved = SettingsManager.Get(Key(profile), "");
        return TryParse(saved, out var colour) ? colour : DefaultFor(profile);
    }

    public static string HexFor(PowerProfile profile) => ToHex(For(profile));

    public static void Save(PowerProfile profile, Color colour) =>
        SettingsManager.Save(Key(profile), ToHex(colour));

    public static void Reset()
    {
        foreach (var (profile, _, _) in Choices)
            SettingsManager.Delete(Key(profile));
    }

    public static Color DefaultFor(PowerProfile profile)
    {
        foreach (var (candidate, _, colour) in Choices)
        {
            if (candidate == profile)
                return colour;
        }

        return Color.White;
    }

    /// <summary>Black or white, whichever stays readable on the given fill.</summary>
    public static Color InkFor(Color fill)
    {
        var luminance = (0.2126 * Channel(fill.R)) +
                        (0.7152 * Channel(fill.G)) +
                        (0.0722 * Channel(fill.B));

        return luminance > 0.45 ? Color.FromArgb(18, 18, 20) : Color.White;
    }

    private static string Key(PowerProfile profile) => $"OsdColour_{profile}";

    private static string ToHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    private static bool TryParse(string saved, out Color colour)
    {
        colour = Color.White;
        if (saved.Length != 7 || saved[0] != '#')
            return false;

        try
        {
            colour = ColorTranslator.FromHtml(saved);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
