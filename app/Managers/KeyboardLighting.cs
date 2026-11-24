using System.Collections.Generic;

namespace Nitrous.Managers;

/// <summary>
/// The keyboard's lighting, whichever controller this model has.
///
/// Two exist and they share nothing: the ENEK HID interface on the newer Nitros (11-byte feature
/// reports, and the only one that drives the lid logo and the mode-key blink), and the Acer WMI
/// gaming class on the older ones, where the embedded controller owns four zone colours
/// (<see cref="WmiKeyboard"/>, §23.196). The UI and the sync worker talk to this, so neither has to
/// know which one answered. ENEK wins where both exist: it is the richer controller.
/// </summary>
internal static class KeyboardLighting
{
    /// <summary>Whether a lighting controller Nitrous can drive is present at all.</summary>
    public static bool IsAvailable() => AcerKeyboard.IsAvailable() || WmiKeyboard.IsAvailable();

    /// <summary>Whether the lid logo and the mode-key blink can be driven, which is ENEK only.</summary>
    public static bool HasLidDevice => AcerKeyboard.IsAvailable();

    /// <summary>
    /// Whether effects and the brightness slider reach the LEDs. False on a WMI BIOS that declares
    /// the record's input as a number: the zone colours still work, the rest has nowhere to go.
    /// </summary>
    public static bool EffectsWritable => AcerKeyboard.IsAvailable() || WmiKeyboard.EffectWritable;

    public static bool SetZoneColors(IReadOnlyList<KeyboardColor> colors, int brightness) =>
        AcerKeyboard.IsAvailable()
            ? AcerKeyboard.SetZoneColors(colors, brightness)
            : WmiKeyboard.SetZoneColors(colors, brightness);

    public static bool SetZoneColors(IReadOnlyList<KeyboardColor> colors, IReadOnlyList<int> brightness) =>
        AcerKeyboard.IsAvailable()
            ? AcerKeyboard.SetZoneColors(colors, brightness)
            : WmiKeyboard.SetZoneColors(colors, brightness);

    public static bool SetEffect(
        KeyboardEffect effect, int brightness, int speed, bool leftwards, KeyboardColor color) =>
        AcerKeyboard.IsAvailable()
            ? AcerKeyboard.SetEffect(effect, brightness, speed, leftwards, color)
            : WmiKeyboard.SetEffect(effect, brightness, speed, leftwards, color);
}
