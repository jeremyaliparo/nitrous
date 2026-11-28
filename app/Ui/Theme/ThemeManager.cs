using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Nitrous.Diagnostics;
using Nitrous.Managers;

using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using SystemFonts = System.Windows.SystemFonts;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;

namespace Nitrous.Ui.Theme;

/// <summary>Light, dark, or follow the Windows app theme.</summary>
public enum AppTheme
{
    Dark,
    Light,
    System
}

/// <summary>
/// Installs and swaps the appearance palette. Called once before the dashboard window exists,
/// then again whenever the user changes a theme choice or Windows flips its app theme.
///
/// Dark and light keep separate accent, background and card choices. The stored choice is either
/// "auto" (follow the Windows accent) or the hex of one of <see cref="ThemePalette.Choices"/>.
/// Appearance only - this never touches a hardware setting.
/// </summary>
internal static class ThemeManager
{
    private const string ChoiceKey = "AccentChoice";
    private const string SurfaceKey = "BackgroundChoice";
    private const string CardKey = "SectionsChoice";
    private const string FontKey = "FontChoice";
    private const string ModeKey = "AppTheme";
    private const string LightPrefix = "Light";
    private const string LightDefaultChoice = "#B388FF";
    public const string Auto = "auto";

    /// <summary>Stored when the font should be whatever Windows uses for its own UI text.</summary>
    public const string AutoFont = "auto";

    /// <summary>
    /// What a machine with no saved choice gets. Nitrous is violet: following the Windows accent is
    /// offered, not assumed, so a fresh install looks like the screenshots and the branding.
    /// </summary>
    public static readonly string DefaultChoice = ThemePalette.Hex(ThemePalette.DefaultAccent);

    private static ResourceDictionary? _installed;
    private static AppTheme _mode = AppTheme.Dark;
    private static bool _light;
    private static ModeState _dark = new();
    private static ModeState _lightState = new();
    private static Color _accent = ThemePalette.DefaultAccent;
    private static ThemePalette.DisplayColors _display = ThemePalette.Display(false);
    private static string _font = AutoFont;

    /// <summary>One mode's saved choices. Surface and card belong to the same light/dark family.</summary>
    private sealed class ModeState
    {
        public string Choice = DefaultChoice;
        public ThemePalette.ThemeSurface Surface = ThemePalette.ThemeSurface.Midnight;
        public ThemePalette.ThemeCard Card = ThemePalette.ThemeCard.Default;
    }

    private static ModeState Active => _light ? _lightState : _dark;

    private static readonly (string Name, ThemePalette.ThemeSurface Surface)[] DarkSurfaces =
    [
        ("Midnight", ThemePalette.ThemeSurface.Midnight),
        ("Black", ThemePalette.ThemeSurface.Black),
        ("Graphite", ThemePalette.ThemeSurface.Graphite),
        ("Slate", ThemePalette.ThemeSurface.Slate),
        ("Navy", ThemePalette.ThemeSurface.Navy)
    ];

    private static readonly (string Name, ThemePalette.ThemeSurface Surface)[] LightSurfaces =
    [
        ("Paper", ThemePalette.ThemeSurface.Paper),
        ("White", ThemePalette.ThemeSurface.White),
        ("Warm", ThemePalette.ThemeSurface.Warm),
        ("Cool", ThemePalette.ThemeSurface.Cool)
    ];

    private static readonly (string Name, ThemePalette.ThemeCard Card)[] DarkCards =
    [
        ("Default", ThemePalette.ThemeCard.Default),
        ("Black", ThemePalette.ThemeCard.Black),
        ("Graphite", ThemePalette.ThemeCard.Graphite),
        ("Slate", ThemePalette.ThemeCard.Slate)
    ];

    private static readonly (string Name, ThemePalette.ThemeCard Card)[] LightCards =
    [
        ("Default", ThemePalette.ThemeCard.Default),
        ("White", ThemePalette.ThemeCard.White),
        ("Warm", ThemePalette.ThemeCard.Warm),
        ("Cool", ThemePalette.ThemeCard.Cool)
    ];

    /// <summary>The window background presets for the active mode, in SETUP list order.</summary>
    public static (string Name, ThemePalette.ThemeSurface Surface)[] Surfaces =>
        _light ? LightSurfaces : DarkSurfaces;

    /// <summary>The card ("sections") presets for the active mode, in SETUP list order.</summary>
    public static (string Name, ThemePalette.ThemeCard Card)[] Cards =>
        _light ? LightCards : DarkCards;

    /// <summary>
    /// Font candidates, in display order, split into UI faces and code faces. Only the ones this
    /// machine actually has are offered - see <see cref="FontFamilies"/> - so the list can never
    /// show a choice that would quietly fall back to the Windows font.
    /// </summary>
    private static readonly (string Name, string Family)[] FontCandidates =
    [
        ("Segoe UI", "Segoe UI"),
        ("Bahnschrift", "Bahnschrift"),
        ("Calibri", "Calibri"),
        ("Tahoma", "Tahoma"),
        ("Trebuchet MS", "Trebuchet MS"),
        ("Inter", "Inter"),

        // Code faces. Wider than the UI faces at the same size, so they look best in the SETUP
        // list rather than on the dense fan cards.
        ("Cascadia Code", "Cascadia Code"),
        ("Cascadia Mono", "Cascadia Mono"),
        ("Consolas", "Consolas"),
        ("JetBrains Mono", "JetBrains Mono"),
        ("Iosevka NF", "Iosevka NF"),
        ("Fira Code", "Fira Code")
    ];

    private static (string Name, string Family)[]? _fonts;

    /// <summary>Windows default, then every candidate family this machine has installed.</summary>
    public static (string Name, string Family)[] FontFamilies => _fonts ??= BuildFonts();

    /// <summary>Raised after the palette has been replaced, so code that caches a brush can refresh.</summary>
    public static event Action? Changed;

    public static AppTheme Mode => _mode;

    public static bool IsLight => _light;

    public static bool IsSystem => _mode == AppTheme.System;

    public static string Choice => Active.Choice;

    public static Color Accent => _accent;

    public static ThemePalette.ThemeSurface Surface => Active.Surface;

    public static ThemePalette.ThemeCard Card => Active.Card;

    public static string FontChoice => _font;

    /// <summary>The family the windows inherit. Resolved on every install, never cached.</summary>
    public static FontFamily FontFamily => ResolveFont(_font);

    /// <summary>Hex strings for the view-model properties that bind directly to a brush.</summary>
    public static string AccentHex => ThemePalette.Hex(_accent);

    public static string AccentDeepHex =>
        ThemePalette.Hex(ThemePalette.Blend(_accent, Colors.Black, 0.12));

    public static string TextPrimaryHex => ThemePalette.Hex(_display.TextPrimary);

    public static string TextMutedHex => ThemePalette.Hex(_display.TextMuted);

    public static string DangerHex => ThemePalette.Hex(_display.Danger);

    public static string WarnHex => ThemePalette.Hex(_display.Warn);

    public static string SuccessHex => ThemePalette.Hex(_display.Success);

    /// <summary>The accent as it is actually drawn in this mode, after the readability clamp.</summary>
    public static Color DisplayAccent(string choice) =>
        ThemePalette.ClampLuminance(Resolve(choice), _display.AccentMin, _display.AccentMax);

    /// <summary>Loads the saved choices and installs the palette. Safe to call twice.</summary>
    public static void Initialise(Application app)
    {
        _mode = ReadMode(SettingsManager.Get(ModeKey, nameof(AppTheme.Dark)));
        _dark = LoadState(light: false);
        _lightState = LoadState(light: true);
        _font = ReadFont(SettingsManager.Get(FontKey, AutoFont));
        _light = ResolveLight(_mode);
        Install(app);

        // Diagnostics answer "why don't I see Inter?" without a round trip: it lists what this
        // machine offered and what the saved choice resolved to.
        Logger.Info($"Font: saved '{_font}' resolved to '{FontFamily.Source}'; " +
                    $"{FontFamilies.Length - 1} of {FontCandidates.Length} candidate families installed.");
        Logger.Info($"Theme: mode {_mode} resolved to {(_light ? "light" : "dark")}; " +
                    $"accent {ThemePalette.Hex(_accent)}.");

        // The user can change the Windows accent while Nitrous is open.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.WindowGlassColor) or "")
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (Active.Choice == Auto)
                        Install(app);
                });
        };

        // Windows' light/dark app theme can flip while Nitrous is open. The event arrives on
        // SystemEvents' own thread, so marshal it to the application.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General
                or Microsoft.Win32.UserPreferenceCategory.VisualStyle
                or Microsoft.Win32.UserPreferenceCategory.Color))
            {
                return;
            }

            app.Dispatcher.BeginInvoke(() =>
            {
                if (_mode != AppTheme.System) return;
                var wasLight = _light;
                _light = ResolveLight(_mode);
                if (_light == wasLight) return;

                Install(app);
                Changed?.Invoke();
            });
        };
    }

    /// <summary>Applies a new accent choice to the active mode, persists it, and reports the change.</summary>
    public static void Set(Application? app, string choice)
    {
        if (app is null)
            return;

        Active.Choice = choice;
        Install(app);
        SaveActive(ChoiceKey, Active.Choice);
        Changed?.Invoke();
    }

    /// <summary>Switches the window background preset and keeps everything else.</summary>
    public static void SetBackground(Application? app, ThemePalette.ThemeSurface surface)
    {
        if (app is null)
            return;

        Active.Surface = surface;
        Install(app);
        SaveActive(SurfaceKey, surface.ToString());
        Changed?.Invoke();
    }

    /// <summary>Switches the card ("sections") preset and keeps everything else.</summary>
    public static void SetSections(Application? app, ThemePalette.ThemeCard card)
    {
        if (app is null)
            return;

        Active.Card = card;
        Install(app);
        SaveActive(CardKey, card.ToString());
        Changed?.Invoke();
    }

    /// <summary>Switches the UI font family and keeps everything else.</summary>
    public static void SetFont(Application? app, string family)
    {
        if (app is null)
            return;

        _font = ReadFont(family);
        Install(app);
        SettingsManager.Save(FontKey, _font);
        Changed?.Invoke();
    }

    /// <summary>Switches light, dark or system and installs the matching saved choices.</summary>
    public static void SetMode(Application? app, AppTheme mode)
    {
        if (app is null)
            return;

        _mode = mode;
        _light = ResolveLight(mode);
        Install(app);
        SettingsManager.Save(ModeKey, mode.ToString());
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a named preset: accent, window background and card surface at once, so the
    /// dashboard is rebuilt once rather than three times.
    /// </summary>
    public static void SetPreset(
        Application? app,
        string choice,
        ThemePalette.ThemeSurface surface,
        ThemePalette.ThemeCard card)
    {
        if (app is null)
            return;

        Active.Surface = surface;
        Active.Card = card;
        Active.Choice = choice;
        Install(app);
        SaveActive(ChoiceKey, Active.Choice);
        SaveActive(SurfaceKey, surface.ToString());
        SaveActive(CardKey, card.ToString());
        Changed?.Invoke();
    }

    private static ModeState LoadState(bool light)
    {
        var prefix = light ? LightPrefix : "";
        var defaultSurface = light
            ? ThemePalette.ThemeSurface.Paper
            : ThemePalette.ThemeSurface.Midnight;

        return new ModeState
        {
            Choice = SettingsManager.Get(prefix + ChoiceKey, light ? LightDefaultChoice : DefaultChoice),
            Surface = ReadSurface(
                SettingsManager.Get(prefix + SurfaceKey, defaultSurface.ToString()), light),
            Card = ReadCard(SettingsManager.Get(prefix + CardKey, nameof(ThemePalette.ThemeCard.Default)), light)
        };
    }

    /// <summary>Writes to the keys of the resolved mode, so System edits the theme you can see.</summary>
    private static void SaveActive(string key, string value) =>
        SettingsManager.Save((_light ? LightPrefix : "") + key, value);

    private static AppTheme ReadMode(string? saved) =>
        Enum.TryParse<AppTheme>(saved, ignoreCase: true, out var mode) ? mode : AppTheme.Dark;

    private static bool ResolveLight(AppTheme mode) => mode switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => WindowsUsesLightTheme()
    };

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadFont(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved) ||
            string.Equals(saved, AutoFont, StringComparison.OrdinalIgnoreCase))
            return AutoFont;

        // Only a curated family is accepted, so a stale or hand-edited settings value cannot put
        // the UI on a font that was never tested.
        foreach (var (_, family) in FontFamilies)
        {
            if (string.Equals(family, saved, StringComparison.OrdinalIgnoreCase))
                return family;
        }

        return AutoFont;
    }

    /// <summary>
    /// The family for a choice. Anything that is not installed falls back to the Windows UI font
    /// rather than letting WPF substitute a random one.
    /// </summary>
    private static FontFamily ResolveFont(string choice)
    {
        if (choice != AutoFont && IsInstalled(choice))
            return new FontFamily(choice);

        return SystemFonts.MessageFontFamily;
    }

    private static HashSet<string>? _installedFamilies;

    /// <summary>Every family WPF reports, read once per process.</summary>
    private static HashSet<string> InstalledFamilies =>
        _installedFamilies ??= new HashSet<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);

    private static (string Name, string Family)[] BuildFonts()
    {
        var list = new List<(string Name, string Family)> { ("Windows default", AutoFont) };

        foreach (var candidate in FontCandidates)
        {
            if (InstalledFamilies.Contains(candidate.Family))
                list.Add(candidate);
        }

        return [.. list];
    }

    private static bool IsInstalled(string family) => InstalledFamilies.Contains(family);

    private static bool IsLightSurface(ThemePalette.ThemeSurface surface) =>
        surface is ThemePalette.ThemeSurface.Paper or ThemePalette.ThemeSurface.White
            or ThemePalette.ThemeSurface.Warm or ThemePalette.ThemeSurface.Cool;

    private static bool IsLightCard(ThemePalette.ThemeCard card) =>
        card is ThemePalette.ThemeCard.White or ThemePalette.ThemeCard.Warm
            or ThemePalette.ThemeCard.Cool;

    private static ThemePalette.ThemeSurface ReadSurface(string? saved, bool light)
    {
        if (!Enum.TryParse<ThemePalette.ThemeSurface>(saved, ignoreCase: true, out var surface))
            return light ? ThemePalette.ThemeSurface.Paper : ThemePalette.ThemeSurface.Midnight;

        // A stale value from the other family is not shown in this mode's list; fall back.
        if (IsLightSurface(surface) != light)
            return light ? ThemePalette.ThemeSurface.Paper : ThemePalette.ThemeSurface.Midnight;

        return surface;
    }

    private static ThemePalette.ThemeCard ReadCard(string? saved, bool light)
    {
        if (!Enum.TryParse<ThemePalette.ThemeCard>(saved, ignoreCase: true, out var card))
            return ThemePalette.ThemeCard.Default;

        if (card == ThemePalette.ThemeCard.Default)
            return card;

        if (IsLightCard(card) != light)
            return ThemePalette.ThemeCard.Default;

        return card;
    }

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

    private static void Install(Application app)
    {
        var state = Active;
        var requested = Resolve(state.Choice);

        // Normalises anything unrecognised back to the shipped accent rather than persisting a bad
        // value. Kept in step with Resolve above so the stored choice and the applied accent agree.
        state.Choice = MatchChoice(state.Choice);
        _display = ThemePalette.Display(_light);
        _accent = ThemePalette.ClampLuminance(requested, _display.AccentMin, _display.AccentMax);

        var next = ThemePalette.Build(_accent, state.Surface, state.Card, _light);
        var merged = app.Resources.MergedDictionaries;

        if (_installed is not null)
            merged.Remove(_installed);

        // Inherited by every control in the window, and by the fan-curve window too.
        next["AppFontFamily"] = ResolveFont(_font);

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
