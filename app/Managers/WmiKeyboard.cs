using System;
using System.Collections.Generic;
using System.Linq;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// The four-zone keyboard on the Nitros whose embedded controller owns the LEDs and exposes them
/// only through Acer's WMI gaming class - the AN515/AN517 family, which has no ENEK HID interface
/// (probe §23.196, after OpenSense's controller of the same registers).
///
/// Two writes carry everything: <c>SetGamingKBBacklight</c> takes the 16-byte NitroSense record
/// (effect, speed, brightness, flags, direction, colour), and <c>SetGamingRgbKb</c> one zone's
/// colour as a u64. Zone on/off (<c>SetGamingLED</c>) is never written: the UI has no per-zone
/// switch, so zones stay as the firmware has them.
///
/// The record's input is declared as a byte array on the firmware this was written against, and
/// as a number on some others; there the effect and brightness cannot be set, and only the zone
/// colours are written. <see cref="EffectWritable"/> says which, so the UI can be honest.
///
/// Nothing here runs on a machine with the ENEK controller: <see cref="KeyboardLighting"/> prefers
/// it, because that controller also drives the lid logo and the mode-key blink.
/// </summary>
internal static class WmiKeyboard
{
    /// <summary>The effect codes this interface takes, which are not the ENEK controller's.</summary>
    private static byte EffectCode(KeyboardEffect effect) => effect switch
    {
        KeyboardEffect.Breathing => 1,
        KeyboardEffect.Neon => 2,
        KeyboardEffect.Wave => 3,
        KeyboardEffect.Shifting => 4,   // shown as "Ripple"
        KeyboardEffect.Zoom => 5,
        KeyboardEffect.Meteor => 6,     // shown as "Snake"
        KeyboardEffect.Twinkling => 7,  // shown as "Disco"
        _ => 0                          // Static
    };

    /// <summary>Neon and Wave draw their own colours, so the record carries none for them.</summary>
    private static bool TakesColor(KeyboardEffect effect) =>
        effect is not (KeyboardEffect.Static or KeyboardEffect.Neon or KeyboardEffect.Wave);

    /// <summary>Only Wave and Shifting have a direction in the NitroSense layout.</summary>
    private static bool TakesDirection(KeyboardEffect effect) =>
        effect is KeyboardEffect.Wave or KeyboardEffect.Shifting;

    private static bool? _available;
    private static bool _effectWritable;
    private static bool _switchesWritable;
    private static bool _switchesAttempted;
    private static int _zones = AcerWmiProtocol.KeyboardZones;

    /// <summary>
    /// The record last written, so a sync loop that rewrites the same effect and brightness every
    /// frame costs four zone calls instead of five and no settle wait. Only an identical record is
    /// skipped, so a real change always goes out.
    /// </summary>
    private static byte[]? _writtenRecord;

    /// <summary>
    /// Whether this machine is a WMI four-zone keyboard: SMBIOS says RGB, the keyboard type is one
    /// the zoned controls reach, and the zone register answers. Asked once per process.
    /// </summary>
    public static bool IsAvailable()
    {
        if (_available is { } known) return known;

        _available = Probe();
        return _available.Value;
    }

    /// <summary>False where this BIOS declares the record's input as a number, leaving only zone colours.</summary>
    public static bool EffectWritable => IsAvailable() && _effectWritable;

    private static bool Probe()
    {
        try
        {
            var smbios = AcerCapabilitiesProvider.Current.Smbios;

            // 0x0A: 1 single colour, 2 RGB. 0x08: the type is 0 for a per-key keyboard, which the
            // four zoned controls cannot address; 1, 3 and 4 are the zoned ones.
            if (smbios.KeyboardColor != 2) return false;
            if (smbios.KeyboardType is null or 0) return false;
            _zones = smbios.KeyboardType is 1 or 3 or 4 ? smbios.KeyboardType.Value : AcerWmiProtocol.KeyboardZones;

            if (!AcerWmiManager.IsHardwareSupported()) return false;

            var zone = AcerWmiManager.ReadGamingScalar(
                AcerWmiProtocol.GetGamingRgbKb, AcerWmiProtocol.KeyboardZoneQuery(1));
            if (zone is not { } answer) return false;

            _effectWritable = AcerWmiManager.GamingInputIsArray(AcerWmiProtocol.SetGamingKBBacklight);
            // The zone switches are a u64 up to gaming interface 2.90 and a byte array after it
            // (AcerSmbios.LedArrayLength); only the integer form is written here.
            _switchesWritable = smbios.LedArrayLength <= 8;

            var (r, g, b) = AcerWmiProtocol.KeyboardZoneAnswer(answer);
            var record = ReadRecord();

            Logger.Info($"Keyboard: WMI four-zone lighting, zone 1 #{r:X2}{g:X2}{b:X2}, " +
                        $"record {(record is null ? "unreadable" : Convert.ToHexString(record))}, " +
                        $"effect record {(_effectWritable ? "writable" : "not writable here, colours only")}, " +
                        $"zone switches {(_switchesWritable ? "integer form" : "array form, not written")}.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Keyboard: the WMI lighting interface could not be probed.", ex);
            return false;
        }
    }

    /// <summary>
    /// One colour per zone, in Static. The record has to be written first: it is what puts the
    /// keyboard in Static at the chosen brightness, with no colour of its own.
    /// </summary>
    public static bool SetZoneColors(IReadOnlyList<KeyboardColor> colors, int brightness)
    {
        if (!IsAvailable()) return false;

        var ok = !_effectWritable ||
                 WriteRecord(KeyboardEffect.Static, brightness, 0, leftwards: false, default);

        // A zone switched off in Acer's software would take the colour and stay dark, leaving a
        // swatch that looks broken. All four are lit once per process, in the integer form only.
        if (_switchesWritable && !_switchesAttempted)
        {
            _switchesAttempted = true;
            ok &= AcerWmiManager.WriteGamingScalar(
                AcerWmiProtocol.SetGamingLED, AcerWmiProtocol.KeyboardAllZonesOn);
        }

        ok &= WriteZones(colors);
        return ok;
    }

    /// <summary>
    /// The same with a brightness per zone, which this interface has no byte for: the peak becomes
    /// the record's brightness and each zone's colour is scaled to its own level, so the gradient
    /// the spectrum effect wants still shows.
    /// </summary>
    public static bool SetZoneColors(IReadOnlyList<KeyboardColor> colors, IReadOnlyList<int> levels)
    {
        if (!IsAvailable()) return false;

        var peak = levels.Count == 0 ? 100 : Math.Clamp(levels.Max(), 0, 100);
        var scaled = new KeyboardColor[colors.Count];

        for (var i = 0; i < colors.Count; i++)
        {
            var level = i < levels.Count ? Math.Clamp(levels[i], 0, 100) : peak;
            var factor = peak == 0 ? 0 : level / (double)peak;
            var color = colors[i];
            scaled[i] = new KeyboardColor(
                (byte)Math.Round(color.R * factor),
                (byte)Math.Round(color.G * factor),
                (byte)Math.Round(color.B * factor));
        }

        return SetZoneColors(scaled, peak);
    }

    /// <summary>One effect across all four zones.</summary>
    public static bool SetEffect(
        KeyboardEffect effect, int brightness, int speed, bool leftwards, KeyboardColor color)
    {
        if (!EffectWritable) return false;
        return WriteRecord(effect, brightness, speed, leftwards, color);
    }

    /// <summary>The current effect, speed and brightness, and the effect's colour, when the firmware answers.</summary>
    public static byte[]? ReadRecord() => AcerWmiManager.ReadGamingArray(AcerWmiProtocol.GetGamingKBBacklight);

    private static bool WriteZones(IReadOnlyList<KeyboardColor> colors)
    {
        var ok = true;
        for (var zone = 1; zone <= _zones; zone++)
        {
            var color = zone <= colors.Count ? colors[zone - 1] : KeyboardColor.White;
            ok &= AcerWmiManager.WriteGamingScalar(
                AcerWmiProtocol.SetGamingRgbKb,
                AcerWmiProtocol.KeyboardZoneColor(zone, color.R, color.G, color.B));
        }

        return ok;
    }

    /// <summary>
    /// The 16-byte NitroSense record: the 8-byte record, then the same bytes repeated with byte 9
    /// set to 1, which is how Acer's own software sends it (§23.196).
    /// </summary>
    internal static byte[] Record(
        KeyboardEffect effect, int brightness, int speed, bool leftwards, KeyboardColor color)
    {
        var record = new byte[16];
        record[0] = EffectCode(effect);
        record[1] = (byte)(effect == KeyboardEffect.Static
            ? 0
            : Math.Clamp(speed, AcerKeyboard.MinSpeed, AcerKeyboard.MaxSpeed));
        record[2] = (byte)Math.Clamp(brightness, 0, 100);
        record[3] = (byte)(effect == KeyboardEffect.Wave ? 0x08 : 0);
        record[4] = (byte)(TakesDirection(effect) ? (leftwards ? 2 : 1) : 0);

        if (TakesColor(effect))
        {
            record[5] = color.R;
            record[6] = color.G;
            record[7] = color.B;
        }

        Array.Copy(record, 0, record, 8, 8);
        record[9] = 1;
        return record;
    }

    private static bool WriteRecord(
        KeyboardEffect effect, int brightness, int speed, bool leftwards, KeyboardColor color)
    {
        var record = Record(effect, brightness, speed, leftwards, color);

        // A sync frame rewrites the same record 25 times a second; only a different one goes out.
        if (_writtenRecord is { } previous && record.AsSpan().SequenceEqual(previous))
            return true;

        var ok = AcerWmiManager.WriteGamingArray(AcerWmiProtocol.SetGamingKBBacklight, record);
        if (!ok) return false;

        _writtenRecord = record;

        // Acer's service waits before the next lighting write on the same controller.
        System.Threading.Thread.Sleep(30);
        return true;
    }
}
