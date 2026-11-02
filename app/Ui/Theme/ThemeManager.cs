using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Nitrous.Managers;

using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;

namespace Nitrous.Ui.Theme;

/// <summary>
/// Installs and swaps the appearance palette. Called once before the dashboard window exists,
/// then again whenever the user changes the accent.
///
/// The choice is stored as either "auto" (follow Windows) or the hex of one of
/// <see cref="ThemePalette.Choices"/>. Appearance only - this never touches a hardware setting.
/// </summary>
internal static class ThemeManager
{
    private const string ChoiceKey = "AccentChoice";
    public const string Auto = "auto";

    private static ResourceDictionary? _installed;
    private static string _choice = Auto;
    private static Color _accent = ThemePalette.DefaultAccent;

    /// <summary>Raised after the palette has been replaced, so code that caches a brush can refresh.</summary>
    public static event Action? Changed;

    public static string Choice => _choice;

    public static Color Accent => _accent;

    /// <summary>Hex strings for the view-model properties that bind directly to a brush.</summary>
    public static string AccentHex => ThemePalette.Hex(_accent);

    public static string AccentDeepHex => ThemePalette.Hex(ThemePalette.Blend(_accent, Colors.Black, 0.12));

    public static string TextPrimaryHex => ThemePalette.Hex(ThemePalette.TextPrimary);

    public static string TextMutedHex => ThemePalette.Hex(ThemePalette.TextMuted);

    public static string DangerHex => ThemePalette.Hex(ThemePalette.Parse("#FF453A"));

    public static string WarnHex => ThemePalette.Hex(ThemePalette.Parse("#FF9F0A"));

    public static string SuccessHex => ThemePalette.Hex(ThemePalette.Parse("#34C759"));

    /// <summary>Loads the saved choice and installs the palette. Safe to call twice.</summary>
    public static void Initialise(Application app)
    {
        var saved = SettingsManager.Get(ChoiceKey, Auto);
        Install(app, string.IsNullOrWhiteSpace(saved) ? Auto : saved);

        // The user can change the Windows accent while Nitrous is open.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.WindowGlassColor) or "")
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (_choice == Auto)
                        Install(app, Auto);
                });
        };
    }

    /// <summary>Applies a new choice, persists it, and reports the change.</summary>
    public static void Set(Application? app, string choice)
    {
        if (app is null)
            return;

        Install(app, choice);
        SettingsManager.Save(ChoiceKey, _choice);
        Changed?.Invoke();
    }

    /// <summary>The accent a choice resolves to right now.</summary>
    public static Color Resolve(string choice)
    {
        if (!string.IsNullOrWhiteSpace(choice) && choice != Auto)
        {
            foreach (var (name, hex) in ThemePalette.Choices)
            {
                if (string.Equals(hex, choice, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, choice, StringComparison.OrdinalIgnoreCase))
                    return ThemePalette.Parse(hex);
            }
        }

        return WindowsAccent();
    }

    private static void Install(Application app, string choice)
    {
        var accent = Resolve(choice);

        // Normalises anything unrecognised back to "auto" rather than persisting a bad value.
        _choice = MatchChoice(choice);
        _accent = accent;

        var next = ThemePalette.Build(accent);
        var merged = app.Resources.MergedDictionaries;

        if (_installed is not null)
            merged.Remove(_installed);

        merged.Add(next);
        _installed = next;
    }

    private static string MatchChoice(string choice)
    {
        if (string.IsNullOrWhiteSpace(choice) || choice == Auto)
            return Auto;

        foreach (var (name, hex) in ThemePalette.Choices)
        {
            if (string.Equals(hex, choice, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, choice, StringComparison.OrdinalIgnoreCase))
                return hex;
        }

        return Auto;
    }

    /// <summary>
    /// The Windows accent. <see cref="SystemParameters.WindowGlassColor"/> is the DWM
    /// colourisation colour, which is what the rest of the shell uses; it also honours
    /// "automatically pick an accent colour from my background".
    /// </summary>
    private static Color WindowsAccent()
    {
        try
        {
            var glass = SystemParameters.WindowGlassColor;
            if (glass.A > 0 && (glass.R + glass.G + glass.B) > 0)
                return Color.FromRgb(glass.R, glass.G, glass.B);
        }
        catch
        {
            // Fall through to the shipped accent rather than showing an unthemed window.
        }

        return ThemePalette.DefaultAccent;
    }
}
