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
/// colours (danger, warn, success) are deliberately fixed: a temperature crossing a threshold
/// must look the same whatever accent the user picked.
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
    /// Curated accents. Every entry clears 4.5:1 against <see cref="WindowBg"/> at its normal
    /// size, so a pick can never produce unreadable labels. Red is deliberately absent: it would
    /// be indistinguishable from the danger colour on a temperature warning.
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

    // Fixed chrome. These are the dark surfaces the accent is read against.
    public static readonly Color WindowBg = Parse("#121214");
    public static readonly Color PanelDeep = Parse("#141416");
    public static readonly Color TextMuted = Parse("#888890");
    public static readonly Color TextPrimary = Parse("#FFFFFF");

    /// <summary>Minimum relative luminance an accent needs to stay readable on the dark chrome.</summary>
    private const double MinAccentLuminance = 0.22;

    /// <summary>Cap so an almost-white accent does not wash out the surfaces it sits on.</summary>
    private const double MaxAccentLuminance = 0.70;

    /// <summary>
    /// The dark surfaces the accent is read against. Two presets rather than a colour picker: the
    /// text, border and hover shades all have to be built around whatever the window colour is, and
    /// an arbitrary pick could not keep them readable.
    /// </summary>
    public enum ThemeSurface
    {
        /// <summary>The shipped dark grey.</summary>
        Midnight,

        /// <summary>Pure black, for OLED panels and anyone who wants the window to vanish.</summary>
        Black
    }

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

    private static ChromeColors Chrome(ThemeSurface surface) => surface switch
    {
        ThemeSurface.Black => new ChromeColors(
            Parse("#000000"), Parse("#050506"), Parse("#08080A"), Parse("#0B0B0D"),
            Parse("#101012"), Parse("#141417"), Parse("#19191C"), Parse("#1F1F23"),
            Parse("#17171A"), Parse("#2A2A2F"), Parse("#3A3A40")),
        _ => new ChromeColors(
            WindowBg, PanelDeep, Parse("#17171A"), Parse("#1C1C1F"),
            Parse("#222226"), Parse("#26262A"), Parse("#2D2D32"), Parse("#2A2A30"),
            Parse("#232328"), Parse("#35353A"), Parse("#45454D"))
    };

    public static ResourceDictionary Build(Color requestedAccent, ThemeSurface surface = ThemeSurface.Midnight)
    {
        var accent = ClampLuminance(requestedAccent, MinAccentLuminance, MaxAccentLuminance);
        var chrome = Chrome(surface);

        // The deeper accent is used as text (hotkey box) and as a fill, so it has to stay legible
        // on the card surface as well.
        var accentDeep = ClampLuminance(Blend(accent, Colors.Black, 0.12), 0.20, MaxAccentLuminance);
        var accentHover = Blend(accent, Colors.White, 0.22);
        var accentWash = Tint(accent, saturation: 0.45, lightness: 0.09);

        // The fan-curve ramp reads cool -> hot. Three stops follow the accent; the hot end stays
        // red because it means heat, not brand.
        var ramp0 = accent;
        var ramp1 = Blend(accent, Colors.White, 0.20);
        var ramp2 = Blend(accent, Colors.White, 0.45);
        var hot = Parse("#FF5C7A");

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
        Add(d, "TextPrimaryBrush", TextPrimary);
        Add(d, "TextStrongBrush", Parse("#E0E0E0"));
        Add(d, "TextBodyBrush", Parse("#C8C8D0"));
        Add(d, "TextSecondaryBrush", Parse("#A0A0A8"));
        Add(d, "TextMutedBrush", TextMuted);
        Add(d, "TextDimBrush", Parse("#6E6E76"));

        // --- accent -------------------------------------------------------------------------
        Add(d, "AccentBrush", accent);
        Add(d, "AccentHoverBrush", accentHover);
        Add(d, "AccentDeepBrush", accentDeep);
        Add(d, "AccentWashBrush", accentWash);

        // --- fixed status -------------------------------------------------------------------
        Add(d, "DangerBrush", Parse("#FF453A"));
        Add(d, "WarnBrush", Parse("#FF9F0A"));
        Add(d, "SuccessBrush", Parse("#34C759"));
        Add(d, "HotBrush", hot);
        Add(d, "ScrimBrush", Parse("#99000000"));

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
