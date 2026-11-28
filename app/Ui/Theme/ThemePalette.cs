using System.Windows;
using System.Windows.Media;

using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Nitrous.Ui.Theme;

/// <summary>
/// Every themeable colour in the UI, in one place.
///
/// This is appearance only. Nothing here decides what a control does, which controls are
/// available, or what gets written to hardware - it only decides what the user sees. Status
/// colours (danger, warn, success) are deliberately fixed per mode: a temperature crossing a
/// threshold must look the same whatever accent the user picked.
///
/// Brushes are rebuilt rather than mutated so that <c>DynamicResource</c> consumers always get a
/// fresh, unfrozen object. A frozen <see cref="Freezable"/> in a resource dictionary cannot be
/// recoloured in place, and WPF is free to freeze anything it likes.
/// </summary>
internal static class ThemePalette
{
    /// <summary>The accent Nitrous ships with. Also the fallback when Windows cannot be asked.</summary>
    public static readonly Color DefaultAccent = Parse("#B388FF");

    /// <summary>
    /// Curated accents. Every entry clears 4.5:1 against the dark chrome at its normal size, and
    /// the light palette darkens them until they clear the same bar on white. Red is deliberately
    /// absent: it would be indistinguishable from the danger colour on a temperature warning.
    /// </summary>
    public static readonly (string Name, string Hex)[] Choices =
    [
        ("Violet", "#B388FF"),
        ("Blue", "#5B9DFF"),
        ("Cyan", "#2FD4E6"),
        ("Green", "#34C759"),
        ("Amber", "#FFB020"),
        ("Orange", "#FF8A3D"),
        ("Pink", "#FF6FB5"),
    ];

    // Fixed chrome for the shipped dark theme. The light palette builds its own.
    public static readonly Color WindowBg = Parse("#121214");
    public static readonly Color PanelDeep = Parse("#141416");
    public static readonly Color TextMuted = Parse("#888890");
    public static readonly Color TextPrimary = Parse("#FFFFFF");

    /// <summary>
    /// The window background. Presets rather than a colour picker: the text, border and hover
    /// shades have to be built around whatever the window colour is, and an arbitrary pick could
    /// not keep them readable.
    /// </summary>
    public enum ThemeSurface
    {
        /// <summary>The shipped dark grey.</summary>
        Midnight,

        /// <summary>Pure black, for OLED panels and anyone who wants the window to vanish.</summary>
        Black,

        /// <summary>A lighter charcoal, between midnight and the cards of the shipped palette.</summary>
        Graphite,

        /// <summary>Cool blue-grey.</summary>
        Slate,

        /// <summary>Deep blue.</summary>
        Navy,

        /// <summary>The default light paper surface.</summary>
        Paper,

        /// <summary>A clean white window.</summary>
        White,

        /// <summary>Warm off-white.</summary>
        Warm,

        /// <summary>Cool pale blue-grey.</summary>
        Cool
    }

    /// <summary>
    /// The card surface - the "sections" - chosen separately from the window. <see cref="Default"/>
    /// keeps each window preset's tuned card; anything else lifts (dark) or shades (light) the
    /// hover, border and toggle shades off the chosen card so the controls stay visible.
    /// </summary>
    public enum ThemeCard
    {
        /// <summary>The card colour that belongs to the window preset.</summary>
        Default,

        Black,
        Graphite,
        Slate,

        /// <summary>White light-mode card.</summary>
        White,

        /// <summary>Warm light-mode card.</summary>
        Warm,

        /// <summary>Cool light-mode card.</summary>
        Cool
    }

    public readonly record struct DisplayColors(
        Color TextPrimary,
        Color TextStrong,
        Color TextBody,
        Color TextSecondary,
        Color TextMuted,
        Color TextDim,
        Color Danger,
        Color Warn,
        Color Success,
        Color Hot,
        Color Scrim,
        double AccentMin,
        double AccentMax);

    private readonly record struct ChromeColors(
        Color Window,
        Color Panel,
        Color RailHover,
        Color Card,
        Color SurfaceHover,
        Color SurfaceRaised,
        Color SurfaceActive,
        Color Border,
        Color Grid,
        Color Toggle,
        Color Divider);

    /// <summary>The text and status colours for a mode. Accent limits are in WCAG luminance.</summary>
    public static DisplayColors Display(bool light) => light
        ? new DisplayColors(
            Parse("#16161A"), Parse("#2C2C33"), Parse("#3C3C45"), Parse("#5A5A64"),
            Parse("#6E6E78"), Parse("#8A8A94"), Parse("#B42318"), Parse("#9A5B00"),
            Parse("#1B7F3A"), Parse("#C2182E"), Parse("#66000000"), 0.08, 0.18)
        : new DisplayColors(
            TextPrimary, Parse("#E0E0E0"), Parse("#C8C8D0"), Parse("#A0A0A8"),
            TextMuted, Parse("#6E6E76"), Parse("#FF453A"), Parse("#FF9F0A"),
            Parse("#34C759"), Parse("#FF5C7A"), Parse("#99000000"), 0.22, 0.70);

    private static ChromeColors Chrome(ThemeSurface surface) => surface switch
    {
        ThemeSurface.Black => new ChromeColors(
            Parse("#000000"), Parse("#050506"), Parse("#08080A"), Parse("#0B0B0D"),
            Parse("#101012"), Parse("#141417"), Parse("#19191C"), Parse("#1F1F23"),
            Parse("#17171A"), Parse("#2A2A2F"), Parse("#3A3A40")),
        ThemeSurface.Graphite => new ChromeColors(
            Parse("#1A1A1D"), Parse("#1C1C1F"), Parse("#202024"), Parse("#242428"),
            Parse("#2A2A2F"), Parse("#2E2E33"), Parse("#35353B"), Parse("#333339"),
            Parse("#2C2C32"), Parse("#3E3E45"), Parse("#4E4E57")),
        ThemeSurface.Slate => new ChromeColors(
            Parse("#191C21"), Parse("#1B1E24"), Parse("#1F2329"), Parse("#23272E"),
            Parse("#292E36"), Parse("#2D323A"), Parse("#343A43"), Parse("#32373F"),
            Parse("#2B2F36"), Parse("#3C424C"), Parse("#4C535F")),
        ThemeSurface.Navy => new ChromeColors(
            Parse("#101828"), Parse("#121A2C"), Parse("#151E32"), Parse("#172033"),
            Parse("#1D273B"), Parse("#212C41"), Parse("#283349"), Parse("#263044"),
            Parse("#1E273A"), Parse("#303C54"), Parse("#414D67")),
        _ => new ChromeColors(
            WindowBg, PanelDeep, Parse("#17171A"), Parse("#1C1C1F"),
            Parse("#222226"), Parse("#26262A"), Parse("#2D2D32"), Parse("#2A2A30"),
            Parse("#232328"), Parse("#35353A"), Parse("#45454D"))
    };

    private static ChromeColors ChromeLight(ThemeSurface surface) => surface switch
    {
        ThemeSurface.White => new ChromeColors(
            Parse("#FCFCFD"), Parse("#FFFFFF"), Parse("#F4F4F7"), Parse("#FFFFFF"),
            Parse("#F3F3F7"), Parse("#EEEEF3"), Parse("#E6E6EC"), Parse("#D9D9E1"),
            Parse("#E7E7EE"), Parse("#C9C9D2"), Parse("#B8B8C4")),
        ThemeSurface.Warm => new ChromeColors(
            Parse("#F7F2EA"), Parse("#FFFDF8"), Parse("#F1EADF"), Parse("#FFFDF8"),
            Parse("#F5EFE5"), Parse("#EFE7DA"), Parse("#E6DDD0"), Parse("#DCCFC0"),
            Parse("#E8E0D5"), Parse("#CDBFAC"), Parse("#BCAB96")),
        ThemeSurface.Cool => new ChromeColors(
            Parse("#EEF3F8"), Parse("#F8FBFF"), Parse("#E5EDF5"), Parse("#F8FBFF"),
            Parse("#EFF5FA"), Parse("#E8F0F7"), Parse("#DFE8F1"), Parse("#D3DEE9"),
            Parse("#E2EAF3"), Parse("#C3D1E0"), Parse("#B3C3D4")),
        _ => new ChromeColors(
            Parse("#F2F2F5"), Parse("#FAFAFC"), Parse("#ECECF0"), Parse("#FFFFFF"),
            Parse("#F2F2F6"), Parse("#F7F7FA"), Parse("#EAEAF0"), Parse("#D8D8E0"),
            Parse("#E6E6EC"), Parse("#C9C9D2"), Parse("#B8B8C4"))
    };

    /// <summary>Replaces the card surface and re-derives the shades that sit on it.</summary>
    private static ChromeColors WithCard(ChromeColors chrome, ThemeCard card, bool light)
    {
        var colour = card switch
        {
            ThemeCard.Black => Parse("#0B0B0D"),
            ThemeCard.Graphite => Parse("#242428"),
            ThemeCard.Slate => Parse("#23272E"),
            ThemeCard.White => Parse("#FFFFFF"),
            ThemeCard.Warm => Parse("#FFF9F1"),
            ThemeCard.Cool => Parse("#F7FAFD"),
            _ => chrome.Card
        };

        if (card == ThemeCard.Default)
            return chrome;

        if (light)
        {
            return chrome with
            {
                Card = colour,
                SurfaceHover = Shade(colour, 6),
                SurfaceRaised = Shade(colour, 10),
                SurfaceActive = Shade(colour, 16),
                Border = Shade(colour, 24),
                Grid = Shade(colour, 10),
                Toggle = Shade(colour, 32),
                Divider = Shade(colour, 46)
            };
        }

        return chrome with
        {
            Card = colour,
            SurfaceHover = Lift(colour, 7),
            SurfaceRaised = Lift(colour, 11),
            SurfaceActive = Lift(colour, 18),
            Border = Lift(colour, 16),
            Grid = Lift(colour, 8),
            Toggle = Lift(colour, 27),
            Divider = Lift(colour, 44)
        };
    }

    private static Color Lift(Color colour, int amount) => Color.FromRgb(
        (byte)Math.Min(255, colour.R + amount),
        (byte)Math.Min(255, colour.G + amount),
        (byte)Math.Min(255, colour.B + amount));

    private static Color Shade(Color colour, int amount) => Color.FromRgb(
        (byte)Math.Max(0, colour.R - amount),
        (byte)Math.Max(0, colour.G - amount),
        (byte)Math.Max(0, colour.B - amount));

    public static ResourceDictionary Build(
        Color requestedAccent,
        ThemeSurface surface,
        ThemeCard card,
        bool light)
    {
        var display = Display(light);
        var accent = ClampLuminance(requestedAccent, display.AccentMin, display.AccentMax);
        var chrome = WithCard(light ? ChromeLight(surface) : Chrome(surface), card, light);

        // The deeper accent is used as text (hotkey box) and as a fill, so it has to stay legible
        // on the card surface as well.
        var accentDeep = ClampLuminance(
            Blend(accent, Colors.Black, 0.12), display.AccentMin, display.AccentMax);
        var accentHover = light
            ? Blend(accent, Colors.Black, 0.18)
            : Blend(accent, Colors.White, 0.22);
        var accentWash = light
            ? Tint(accent, saturation: 0.30, lightness: 0.93)
            : Tint(accent, saturation: 0.45, lightness: 0.09);

        // The fan-curve ramp reads cool -> hot. Three stops follow the accent; the hot end stays
        // red because it means heat, not brand.
        var ramp0 = accent;
        var ramp1 = light ? Blend(accent, Colors.Black, 0.18) : Blend(accent, Colors.White, 0.20);
        var ramp2 = light ? Blend(accent, Colors.Black, 0.32) : Blend(accent, Colors.White, 0.45);
        var hot = display.Hot;

        var d = new ResourceDictionary();

        // --- chrome -------------------------------------------------------------------------
        Add(d, "WindowBgBrush", chrome.Window);
        Add(d, "PanelDeepBrush", chrome.Panel);
        Add(d, "RailHoverBrush", chrome.RailHover);
        Add(d, "CardBgBrush", chrome.Card);
        Add(d, "SurfaceHoverBrush", chrome.SurfaceHover);
        Add(d, "SurfaceRaisedBrush", chrome.SurfaceRaised);
        Add(d, "SurfaceActiveBrush", chrome.SurfaceActive);
        Add(d, "BorderSubtleBrush", chrome.Border);
        Add(d, "GridLineBrush", chrome.Grid);
        Add(d, "ToggleTrackBrush", chrome.Toggle);
        Add(d, "DividerBrush", chrome.Divider);

        // --- text ---------------------------------------------------------------------------
        Add(d, "TextPrimaryBrush", display.TextPrimary);
        Add(d, "TextStrongBrush", display.TextStrong);
        Add(d, "TextBodyBrush", display.TextBody);
        Add(d, "TextSecondaryBrush", display.TextSecondary);
        Add(d, "TextMutedBrush", display.TextMuted);
        Add(d, "TextDimBrush", display.TextDim);

        // --- accent -------------------------------------------------------------------------
        Add(d, "AccentBrush", accent);
        Add(d, "AccentHoverBrush", accentHover);
        Add(d, "AccentDeepBrush", accentDeep);
        Add(d, "AccentWashBrush", accentWash);
        Add(d, "OnAccentBrush", Colors.White);

        // --- fixed status -------------------------------------------------------------------
        Add(d, "DangerBrush", display.Danger);
        Add(d, "WarnBrush", display.Warn);
        Add(d, "SuccessBrush", display.Success);
        Add(d, "HotBrush", hot);
        Add(d, "ScrimBrush", display.Scrim);

        // --- fan curve ramp -----------------------------------------------------------------
        var ramp = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        ramp.GradientStops.Add(new GradientStop(ramp0, 0.0));
        ramp.GradientStops.Add(new GradientStop(ramp1, 0.35));
        ramp.GradientStops.Add(new GradientStop(ramp2, 0.70));
        ramp.GradientStops.Add(new GradientStop(hot, 1.0));
        d["FanRampBrush"] = ramp;

        return d;
    }

    // -- helpers -----------------------------------------------------------------------------

    private static void Add(ResourceDictionary d, string key, Color c) =>
        d[key] = new SolidColorBrush(c);

    public static Color Parse(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = 255;
        return c;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>WCAG relative luminance, used both for the clamp and for documenting contrast.</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    public static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * t));

        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    /// <summary>
    /// Pulls a colour into a luminance window by blending towards white or black. Binary search
    /// because relative luminance is not linear in the sRGB channels.
    /// </summary>
    public static Color ClampLuminance(Color c, double min, double max)
    {
        var l = Luminance(c);
        if (l >= min && l <= max)
            return c;

        var target = l < min ? Colors.White : Colors.Black;

        double lo = 0, hi = 1;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            var candidate = Blend(c, target, mid);
            var cl = Luminance(candidate);

            if (l < min)
            {
                if (cl < min) lo = mid;
                else hi = mid;
            }
            else
            {
                if (cl > max) lo = mid;
                else hi = mid;
            }
        }

        return Blend(c, target, hi);
    }

    /// <summary>Keeps the accent's hue but resets saturation and lightness, which is how the
    /// original hand-picked wash (#1A1225) was made. A plain blend does not reproduce it.</summary>
    private static Color Tint(Color accent, double saturation, double lightness)
    {
        var (h, _, _) = ToHsl(accent);
        return FromHsl(h, saturation, lightness);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        double h = 0, s = 0;

        if (max != min)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

            if (max == r) h = ((g - b) / d) + (g < b ? 6 : 0);
            else if (max == g) h = ((b - r) / d) + 2;
            else h = ((r - g) / d) + 4;

            h /= 6;
        }

        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s == 0)
        {
            var v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + ((q - p) * 6 * t);
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + ((q - p) * 6 * (2.0 / 3 - t));
            return p;
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;

        return Color.FromRgb(
            (byte)Math.Round(Hue(p, q, h + (1.0 / 3)) * 255),
            (byte)Math.Round(Hue(p, q, h) * 255),
            (byte)Math.Round(Hue(p, q, h - (1.0 / 3)) * 255));
    }
}
