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
    private const string SurfaceKey = "BackgroundChoice";
    public const string Auto = "auto";

    /// <summary>
    /// What a machine with no saved choice gets. Nitrous is violet: following the Windows accent is
    /// offered, not assumed, so a fresh install looks like the screenshots and the branding.
    /// </summary>
    public static readonly string DefaultChoice = ThemePalette.Hex(ThemePalette.DefaultAccent);

    private static ResourceDictionary? _installed;
    private static string _choice = DefaultChoice;
    private static Color _accent = ThemePalette.DefaultAccent;
    private static ThemePalette.ThemeSurface _surface = ThemePalette.ThemeSurface.Midnight;

    /// <summary>The window background presets, in the order the SETUP list shows them.</summary>
    public static readonly (string Name, ThemePalette.ThemeSurface Surface)[] Surfaces =
    [
        ("Midnight", ThemePalette.ThemeSurface.Midnight),
        ("Black", ThemePalette.ThemeSurface.Black)
    ];

    /// <summary>Raised after the palette has been replaced, so code that caches a brush can refresh.</summary>
    public static event Action? Changed;

    public static string Choice => _choice;

    public static Color Accent => _accent;

    public static ThemePalette.ThemeSurface Surface => _surface;

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
        var saved = SettingsManager.Get(ChoiceKey, DefaultChoice);
        _surface = ReadSurface(SettingsManager.Get(SurfaceKey, nameof(ThemePalette.ThemeSurface.Midnight)));
        Install(app, string.IsNullOrWhiteSpace(saved) ? DefaultChoice : saved);

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

    /// <summary>Switches the window background preset and keeps the accent.</summary>
    public static void SetBackground(Application? app, ThemePalette.ThemeSurface surface)
    {
        if (app is null)
            return;

        _surface = surface;
        Install(app, _choice);
        SettingsManager.Save(SurfaceKey, surface.ToString());
        Changed?.Invoke();
    }

    private static ThemePalette.ThemeSurface ReadSurface(string? saved) =>
        Enum.TryParse<ThemePalette.ThemeSurface>(saved, ignoreCase: true, out var surface)
            ? surface
            : ThemePalette.ThemeSurface.Midnight;

    /// <summary>The accent a choice resolves to right now.</summary>
    public static Color Resolve(string choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return ThemePalette.DefaultAccent;

        if (choice == Auto)
            return WindowsAccent();

        foreach (var (name, hex) in ThemePalette.Choices)
        {
            if (string.Equals(hex, choice, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, choice, StringComparison.OrdinalIgnoreCase))
                return ThemePalette.Parse(hex);
        }

        // A colour the user picked themselves. Anything else falls back to the shipped accent.
        return TryParseHex(choice, out var custom) ? custom : ThemePalette.DefaultAccent;
    }

    /// <summary>
    /// <c>#RRGGBB</c>, the same form <see cref="ThemePalette.Hex"/> writes. Kept strict so a bad
    /// saved value can never reach the palette.
    /// </summary>
    private static bool TryParseHex(string? value, out Color colour)
    {
        colour = default;
        var s = value?.Trim();

        if (s is not { Length: 7 } || s[0] != '#')
            return false;

        for (var i = 1; i < s.Length; i++)
        {
            if (!Uri.IsHexDigit(s[i]))
                return false;
        }

        colour = ThemePalette.Parse(s);
        return true;
    }

    private static void Install(Application app, string choice)
    {
        var accent = Resolve(choice);

        // Normalises anything unrecognised back to the shipped accent rather than persisting a bad
        // value. Kept in step with Resolve above so the stored choice and the applied accent agree.
        _choice = MatchChoice(choice);
        _accent = accent;

        var next = ThemePalette.Build(accent, _surface);
        var merged = app.Resources.MergedDictionaries;

        if (_installed is not null)
            merged.Remove(_installed);

        merged.Add(next);
        _installed = next;
    }

    private static string MatchChoice(string choice)
    {
        if (string.IsNullOrWhiteSpace(choice))
            return DefaultChoice;

        if (choice == Auto)
            return Auto;

        foreach (var (name, hex) in ThemePalette.Choices)
        {
            if (string.Equals(hex, choice, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, choice, StringComparison.OrdinalIgnoreCase))
                return hex;
        }

        // A colour picked by the user is stored as-is, normalised through the same formatter.
        if (TryParseHex(choice, out var custom))
            return ThemePalette.Hex(custom);

        return DefaultChoice;
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
