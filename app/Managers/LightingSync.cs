using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// A software lighting-sync worker with two sources: the primary screen (four column averages into
/// four static zones) and the default audio output (peak level into brightness, one colour).
/// Both write continuously, so the worker runs in the tray process and only while
/// <c>KbSyncEnabled</c> is on. Stopping restores whichever preset was last applied.
/// </summary>
internal static class LightingSync
{
    public const string SourceScreen = "Screen";
    public const string SourceSound = "Sound";
    public const string SoundModePeak = "Peak";
    public const string SoundModeSpectrum = "Spectrum";
    public const string SoundModeBeat = "Beat";
    public const string TargetBoth = "Both";
    public const string TargetKeyboard = "Keyboard";

    private const int Columns = 64;
    private const int Rows = 36;
    private const int ZoneCount = AcerKeyboard.ZoneCount;
    private const int ScreenRateMs = 125;
    private const int SoundRateMs = 40;
    private const int PulseRateMs = 40;
    private const int BrightnessSoundRateMs = 20;
    private const int LogoEveryFrames = 4;
    private const int SoundFloorPercent = 0;

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static volatile bool _running;
    private static string _source = SourceScreen;
    private static bool _writeWarned;
    private static bool _logoAvailable = true;
    private static bool _brightnessOnly;
    private static bool _brightnessWarned;
    private static int _lastBrightness = -1;
    private static DateTime _lastBrightnessWrite = DateTime.MinValue;

    public static bool IsRunning => _running;

    public static string Normalize(string? source) =>
        string.Equals(source, SourceSound, StringComparison.OrdinalIgnoreCase) ? SourceSound : SourceScreen;

    public static string NormalizeTarget(string? target) =>
        string.Equals(target, TargetKeyboard, StringComparison.OrdinalIgnoreCase) ? TargetKeyboard : TargetBoth;

    public static void Start(string? source)
    {
        var normalized = Normalize(source);

        lock (Gate)
        {
            if (_running)
            {
                if (string.Equals(_source, normalized, StringComparison.Ordinal)) return;

                // Switching source: stop the old loop without restoring the preset, then start the
                // new one straight away. A brief frame gap is fine; an intermediate preset is not.
                _running = false;
                try { _thread?.Join(1200); } catch { }
            }

            var hasLighting = AcerKeyboard.IsAvailable();
            var hasBrightness = !hasLighting
                && AcerWmiManager.GetKeyboardBacklightPath()
                != AcerWmiManager.KeyboardBacklightPath.None;

            // A model with no lighting controller can still pulse its backlight with sound. There
            // are no colours to follow, so screen sync is not offered on that path.
            if (!hasLighting && (!hasBrightness || normalized != SourceSound)) return;

            _source = normalized;
            _brightnessOnly = !hasLighting;
            _writeWarned = false;
            _brightnessWarned = false;
            _logoAvailable = true;
            _lastBrightness = -1;
            _lastBrightnessWrite = DateTime.MinValue;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = $"Nitrous {normalized.ToLowerInvariant()} sync"
            };
            _thread.Start();
            Logger.Info($"Lighting sync: started ({normalized}).");
        }
    }

    public static void Stop(bool restorePreset = true)
    {
        Thread? thread;
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            thread = _thread;
            _thread = null;
        }

        try { thread?.Join(1500); } catch { }

        if (restorePreset) RestoreSavedPreset();
        Logger.Info(restorePreset
            ? "Lighting sync: stopped."
            : "Lighting sync: suspended for Area.");
    }

    /// <summary>Re-applies the saved preset immediately, for a hotkey outside the dashboard.</summary>
    public static void ReapplySavedPreset() => RestoreSavedPreset();

    /// <summary>
    /// Skips frames while the firmware plays an acknowledgement, so sync doesn't erase it. The
    /// deadline lives in settings because the dashboard and tray are separate processes.
    /// </summary>
    public static void Pause(int milliseconds) =>
        SettingsManager.Save(
            "KbSyncPauseUntil",
            DateTime.UtcNow.AddMilliseconds(milliseconds).Ticks);

    private static bool Paused() =>
        DateTime.UtcNow.Ticks < SettingsManager.Get("KbSyncPauseUntil", 0L);

    /// <summary>Whether the lid follows the sync, or the keyboard alone.</summary>
    private static bool WritesLogo() =>
        !string.Equals(SettingsManager.Get("KbSyncTarget", TargetBoth), TargetKeyboard,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The static colours of the preset in use. Area Static gives each zone its own colour; Global,
    /// an Area animation or anything else gives one colour for all four. Read live so a colour edit
    /// while sync is on takes effect without stopping it.
    /// </summary>
    private static KeyboardColor[] BaseColors()
    {
        var colors = new KeyboardColor[ZoneCount];
        var area = string.Equals(SettingsManager.Get("KbLightingSource", "Global"), "Area",
                       StringComparison.OrdinalIgnoreCase)
                   && string.Equals(SettingsManager.Get("KbStyle", "Static"), "Static",
                       StringComparison.OrdinalIgnoreCase);

        if (area)
        {
            for (var i = 0; i < ZoneCount; i++)
                colors[i] = KeyboardColor.FromHex(SettingsManager.Get($"KbZone{i + 1}", "#B388FF"));
            return colors;
        }

        var color = KeyboardColor.FromHex(SettingsManager.Get("KbGlobal_Color", "#B388FF"));
        for (var i = 0; i < ZoneCount; i++) colors[i] = color;
        return colors;
    }

    private static KeyboardColor AverageColor(IReadOnlyList<KeyboardColor> colors)
    {
        if (colors.Count == 0) return KeyboardColor.White;

        long red = 0, green = 0, blue = 0;
        foreach (var color in colors)
        {
            red += color.R;
            green += color.G;
            blue += color.B;
        }

        return new KeyboardColor(
            (byte)(red / colors.Count), (byte)(green / colors.Count), (byte)(blue / colors.Count));
    }

    /// <summary>The brightness cap of the preset in use: Area's slider or Global's.</summary>
    private static int BaseBrightness()
    {
        var area = string.Equals(SettingsManager.Get("KbLightingSource", "Global"), "Area",
            StringComparison.OrdinalIgnoreCase);
        return Math.Clamp(
            SettingsManager.Get(area ? "KbBrightness" : "KbGlobal_Brightness", 100), 0, 100);
    }

    private static bool AreaDynamic() =>
        string.Equals(SettingsManager.Get("KbLightingSource", "Global"), "Area",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SettingsManager.Get("KbStyle", "Static"), "Dynamic",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The effect sound sync should run: Global's effect, or Area Dynamic's.</summary>
    private static KeyboardEffect BaseEffect()
    {
        if (AreaDynamic())
            return AcerKeyboard.ParseEffect(SettingsManager.Get("KbEffect", "Breathing"));

        var area = string.Equals(SettingsManager.Get("KbLightingSource", "Global"), "Area",
            StringComparison.OrdinalIgnoreCase);
        return area
            ? KeyboardEffect.Static
            : AcerKeyboard.ParseEffect(SettingsManager.Get("KbGlobal_Effect", "Static"));
    }

    /// <summary>The effect's speed byte; Global stores five steps, Area stores 1-9.</summary>
    private static int BaseSpeed()
    {
        if (AreaDynamic())
            return Math.Clamp(SettingsManager.Get("KbSpeed", 4),
                AcerKeyboard.MinSpeed, AcerKeyboard.MaxSpeed);

        var step = Math.Clamp(SettingsManager.Get("KbGlobal_Speed", 3), 1, 5);
        return AcerKeyboard.MinSpeed + (step - 1) * 2;
    }

    private static void Loop()
    {
        try
        {
            if (_source == SourceSound) SoundLoop();
            else ScreenLoop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Lighting sync: the {_source} worker stopped.", ex);
            _running = false;
            RestoreSavedPreset();
        }
    }

    private static void ScreenLoop()
    {
        using var capture = new ScreenCapture(Columns, Rows);
        var zones = new KeyboardColor[ZoneCount];
        var logo = KeyboardColor.White;
        var frame = 0;
        var envelope = new SoundEnvelope();
        var samples = new float[SpectrumAnalyzer.Size];
        SpectrumSource? spectrum = null;
        var sourceWarned = false;

        try
        {
            while (_running)
            {
                if (Paused())
                {
                    Thread.Sleep(50);
                    continue;
                }

                var wanted = SettingsManager.Get("KbScreenPulse", 0) == 1;
                if (wanted && spectrum == null && !sourceWarned)
                {
                    spectrum = SpectrumSource.TryCreate();
                    if (spectrum == null)
                    {
                        Logger.Warn("Screen sync: no audio output for the pulse.");
                        sourceWarned = true;
                    }
                    else
                    {
                        envelope.Configure(spectrum.SampleRate);
                        envelope.Reset();
                    }
                }
                else if (!wanted && spectrum != null)
                {
                    spectrum.Dispose();
                    spectrum = null;
                    sourceWarned = false;
                    envelope.Reset();
                }

                var pulseOn = wanted && spectrum != null;
                var rate = pulseOn
                    ? PulseRateMs
                    : Math.Clamp(SettingsManager.Get("KbSyncRate",
                        SettingsManager.Get("KbScreenSyncRate", ScreenRateMs)), 80, 500);
                var saturation = Math.Clamp(
                    SettingsManager.Get("KbSyncSaturation", 100), 0, 200) / 100f;
                var cap = BaseBrightness();

                // At 50 Hz the pulse is smooth; the screen only needs a new frame every third one.
                if (frame % (pulseOn ? 3 : 1) == 0 && capture.TryCapture(out var pixels))
                {
                    if (!_running) break;

                    ReadZones(pixels, zones, saturation);
                    logo = Average(pixels, saturation);
                }

                var brightness = cap;
                if (pulseOn && spectrum != null)
                {
                    float bass = 0f, mid = 0f, full = 0f;
                    if (spectrum.Read(samples))
                        envelope.Analyze(samples, out bass, out mid, out full);

                    var sensitivity = Math.Clamp(SettingsManager.Get("KbSoundSensitivity", 4), 1, 10);
                    var floor = Math.Clamp(SettingsManager.Get("KbSyncFloor", 0), 0, 10);
                    var gain = 0.5f + sensitivity * 0.1f;
                    var level = envelope.Beat(bass, mid, full, gain, PulseRateMs / 1000f);
                    brightness = (int)Math.Round((floor + level * (100 - floor)) * cap / 100.0);

                    if (SettingsManager.Get("KbSyncDebug", 0) == 1 && frame % 25 == 0)
                        Logger.Info($"Screen sync (pulse): bass={bass:0.000} full={full:0.000} " +
                                    $"level={level:0.00} brightness={brightness}");
                }

                WriteFrame(zones, Math.Clamp(brightness, 0, 100), logo,
                    frame % LogoEveryFrames == 0 && WritesLogo());

                frame++;
                Thread.Sleep(rate);
            }
        }
        finally
        {
            spectrum?.Dispose();
        }
    }

    private static void SoundLoop()
    {
        var zones = BaseColors();
        var levels = new int[ZoneCount];
        var baseline = 0f;
        var pulse = 0f;
        var frame = 0;
        var mode = "";
        var logoWritten = false;
        AudioMeter? meter = null;
        SpectrumSource? spectrum = null;
        var analyzer = new SpectrumAnalyzer();
        var envelope = new SoundEnvelope();
        var samples = new float[SpectrumAnalyzer.Size];

        try
        {
            while (_running)
            {
                if (Paused())
                {
                    Thread.Sleep(50);
                    continue;
                }

                var soundMode = _brightnessOnly
                    ? SettingsManager.Get("KbBrightSyncMode", SoundModeBeat)
                    : SettingsManager.Get("KbSoundMode", SoundModeSpectrum);

                var desired = soundMode switch
                {
                    var m when string.Equals(m, SoundModePeak, StringComparison.OrdinalIgnoreCase) => SoundModePeak,
                    var m when string.Equals(m, SoundModeBeat, StringComparison.OrdinalIgnoreCase) => SoundModeBeat,
                    _ => SoundModeSpectrum
                };

                // A single-colour backlight has no zones to split, so Spectrum becomes Peak.
                if (_brightnessOnly && desired == SoundModeSpectrum) desired = SoundModePeak;

                if (!string.Equals(desired, mode, StringComparison.Ordinal))
                {
                    meter?.Dispose();
                    meter = null;
                    spectrum?.Dispose();
                    spectrum = null;
                    mode = desired;
                    baseline = 0f;
                    pulse = 0f;
                    Array.Clear(levels);
                    envelope.Reset();
                    logoWritten = false;

                    spectrum = SpectrumSource.TryCreate();
                    if (spectrum == null && mode == SoundModePeak)
                        meter = AudioMeter.TryCreate();

                    if (meter == null && spectrum == null)
                    {
                        Logger.Warn($"Sound sync: no audio source for {mode}.");
                        _running = false;
                        RestoreSavedPreset();
                        return;
                    }

                    if (spectrum != null) envelope.Configure(spectrum.SampleRate);
                }

                if (frame % 25 == 0)
                    zones = BaseColors();

                // The saved level is what the backlight returns to when sync stops; the pulse itself
                // uses the full five steps, because a 25% ceiling would be barely visible.
                var ceiling = _brightnessOnly ? 100 : BaseBrightness();
                var sensitivity = Math.Clamp(SettingsManager.Get("KbSoundSensitivity", 4), 1, 10);
                var gain = 0.5f + sensitivity * 0.1f;
                var rate = _brightnessOnly ? BrightnessSoundRateMs : SoundRateMs;
                var dt = rate / 1000f;

                if (SettingsManager.Get("KbSyncDebug", 0) == 1 && frame % 25 == 0)
                {
                    Logger.Info($"Sound sync ({mode}) base=" +
                                $"{string.Join(",", zones.Select(z => z.ToHex()))} cap={ceiling}");
                }

                var haveSamples = spectrum?.Read(samples) == true;

                if (mode == SoundModePeak)
                {
                    int brightness;

                    if (spectrum != null)
                    {
                        float bass = 0f, mid = 0f, full = 0f;
                        if (haveSamples) envelope.Analyze(samples, out bass, out mid, out full);
                        var level = envelope.Overall(bass, full, gain, dt);
                        brightness = (int)Math.Round(level * ceiling);

                        if (SettingsManager.Get("KbSyncDebug", 0) == 1 && frame % 25 == 0)
                            Logger.Info($"Sound sync (Overall): bass={bass:0.000} full={full:0.000} " +
                                        $"level={level:0.00} brightness={brightness}");
                    }
                    else if (meter != null)
                    {
                        // Fallback for endpoints whose mix format the spectrum reader cannot open.
                        var peak = meter.Peak();
                        var transient = Math.Max(0f, peak - baseline);
                        baseline += (peak - baseline) * (1f - MathF.Exp(-dt / 0.5f));
                        pulse = Math.Max(transient, pulse * MathF.Exp(-dt / 0.15f));
                        var presence = Math.Max(0f, peak - 0.08f) * 0.35f;
                        var level = Math.Clamp(pulse * gain + presence, 0f, 1f);
                        brightness = (int)Math.Round(level * ceiling);

                        if (SettingsManager.Get("KbSyncDebug", 0) == 1 && frame % 25 == 0)
                            Logger.Info($"Sound sync (Peak fallback): peak={peak:0.00} " +
                                        $"pulse={pulse:0.00} brightness={brightness}");
                    }
                    else
                    {
                        brightness = 0;
                    }

                    for (var i = 0; i < levels.Length; i++) levels[i] = brightness;
                }
                else if (mode == SoundModeBeat && spectrum != null)
                {
                    float bass = 0f, mid = 0f, full = 0f;
                    if (haveSamples) envelope.Analyze(samples, out bass, out mid, out full);
                    var level = envelope.Beat(bass, mid, full, gain, dt);
                    var brightness = (int)Math.Round(level * ceiling);
                    for (var i = 0; i < levels.Length; i++) levels[i] = brightness;

                    if (SettingsManager.Get("KbSyncDebug", 0) == 1 && frame % 25 == 0)
                        Logger.Info($"Sound sync (Beat): bass={bass:0.000} mid={mid:0.000} " +
                                    $"level={level:0.00} brightness={brightness}");
                }
                else if (spectrum != null)
                {
                    if (haveSamples)
                        analyzer.Analyze(spectrum.SampleRate, sensitivity, ceiling, levels);

                    if (SettingsManager.Get("KbSyncDebug", 0) == 1 && frame % 25 == 0)
                        Logger.Info($"Sound sync (Spectrum): {string.Join(",", levels)}");
                }

                if (_brightnessOnly)
                {
                    WriteBrightness(levels.Max());
                }
                else
                {
                    // The lid is a separate, slower device: setting it once avoids the stutter
                    // heard when its brightness or speed is rewritten per frame.
                    if (!logoWritten && WritesLogo() && _logoAvailable)
                    {
                        _logoAvailable = AcerKeyboard.SetEffect(
                            KeyboardEffect.Static, BaseBrightness(), 0, leftwards: false,
                            AverageColor(zones), AcerKeyboard.LidDevice);
                        logoWritten = true;
                    }

                    var effect = BaseEffect();
                    if (effect == KeyboardEffect.Static)
                    {
                        WriteZones(zones, levels, AverageColor(zones), levels.Max(), false);
                    }
                    else
                    {
                        WriteEffect(effect, levels.Max(), BaseSpeed(), AverageColor(zones));
                    }
                }

                frame++;
                Thread.Sleep(rate);
            }
        }
        finally
        {
            meter?.Dispose();
            spectrum?.Dispose();
        }
    }

    private static void WriteEffect(
        KeyboardEffect effect, int brightness, int speed, KeyboardColor color)
    {
        if (!AcerKeyboard.SetEffect(effect, brightness, speed, leftwards: false, color) &&
            !_writeWarned)
        {
            Logger.Warn("Lighting sync: the keyboard refused an effect frame.");
            _writeWarned = true;
        }
    }

    private static void WriteZones(
        IReadOnlyList<KeyboardColor> zones, IReadOnlyList<int> levels,
        KeyboardColor logo, int logoBrightness, bool writeLogo)
    {
        if (!AcerKeyboard.SetZoneColors(zones, levels) && !_writeWarned)
        {
            Logger.Warn("Lighting sync: the keyboard refused a frame.");
            _writeWarned = true;
        }

        if (writeLogo && _logoAvailable)
        {
            _logoAvailable = AcerKeyboard.SetEffect(
                KeyboardEffect.Static, logoBrightness, 0, leftwards: false, logo, AcerKeyboard.LidDevice);
        }
    }

    private static void WriteFrame(KeyboardColor[] zones, int brightness, KeyboardColor logo, bool writeLogo)
    {
        var levels = new int[ZoneCount];
        for (var i = 0; i < levels.Length; i++) levels[i] = brightness;
        WriteZones(zones, levels, logo, brightness, writeLogo);
    }


    /// <summary>Four equal screen columns, left to right, as zone colours.</summary>
    private static void ReadZones(byte[] bgra, KeyboardColor[] zones, float saturation)
    {
        var zoneWidth = Columns / ZoneCount;

        for (var zone = 0; zone < ZoneCount; zone++)
        {
            long red = 0, green = 0, blue = 0;
            var count = 0;

            for (var y = 0; y < Rows; y++)
            {
                var row = y * Columns * 4;
                for (var x = zone * zoneWidth; x < (zone + 1) * zoneWidth; x++)
                {
                    var i = row + x * 4;
                    blue += bgra[i];
                    green += bgra[i + 1];
                    red += bgra[i + 2];
                    count++;
                }
            }

            zones[zone] = Saturate(new KeyboardColor(
                (byte)(red / count), (byte)(green / count), (byte)(blue / count)), saturation);
        }
    }

    private static KeyboardColor Average(byte[] bgra, float saturation)
    {
        long red = 0, green = 0, blue = 0;
        var count = bgra.Length / 4;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            blue += bgra[i];
            green += bgra[i + 1];
            red += bgra[i + 2];
        }

        return Saturate(new KeyboardColor(
            (byte)(red / count), (byte)(green / count), (byte)(blue / count)), saturation);
    }

    /// <summary>
    /// Software saturation around luminance: 0 is grey, 1 unchanged, 2 twice as saturated. The LEDs
    /// have no saturation control; this changes the RGB values sent to them.
    /// </summary>
    private static KeyboardColor Saturate(KeyboardColor color, float factor)
    {
        if (Math.Abs(factor - 1f) < 0.001f) return color;

        var luma = 0.299f * color.R + 0.587f * color.G + 0.114f * color.B;
        byte Mix(byte channel) => (byte)Math.Clamp(luma + (channel - luma) * factor, 0, 255);

        return new KeyboardColor(Mix(color.R), Mix(color.G), Mix(color.B));
    }

    /// <summary>Puts the keyboard back on the preset that was in use before sync started.</summary>
    private static void RestoreSavedPreset()
    {
        try
        {
            if (!AcerKeyboard.IsAvailable())
            {
                if (_brightnessOnly) RestoreBrightness();
                return;
            }

            var source = SettingsManager.Get("KbLightingSource", "Global");
            if (string.Equals(source, "Area", StringComparison.OrdinalIgnoreCase))
                RestoreArea();
            else
                RestoreGlobal();
        }
        catch (Exception ex)
        {
            Logger.Warn("Lighting sync: could not restore the saved lighting.", ex);
        }
    }

    /// <summary>
    /// Snap a 0-100 level to the firmware's five steps. The path is allowed to reach 0 between
    /// hits: pr4u reported that stopping at 25% made the beat read as a level change rather than
    /// a pulse. The saved level is restored when sync stops.
    /// </summary>
    private static int SnapBrightness(int raw)
    {
        var step = (int)Math.Round(Math.Clamp(raw, 0, 100) / 25.0) * 25;
        return Math.Clamp(step, 0, 100);
    }

    private static void WriteBrightness(int raw)
    {
        var level = SnapBrightness(raw);
        if (level == _lastBrightness) return;

        var now = DateTime.UtcNow;
        if ((now - _lastBrightnessWrite).TotalMilliseconds < 70) return;

        if (!AcerWmiManager.SetKeyboardBrightness(level))
        {
            if (!_brightnessWarned)
            {
                Logger.Warn("Sound sync: the backlight refused a level; keeping the last one.");
                _brightnessWarned = true;
            }
            return;
        }

        _lastBrightness = level;
        _lastBrightnessWrite = now;

        if (SettingsManager.Get("KbSyncDebug", 0) == 1)
            Logger.Info($"Sound sync (brightness): level={level}");
    }

    private static void RestoreBrightness()
    {
        var level = Math.Clamp(SettingsManager.Get("KbBrightLevel", 100), 0, 100);
        if (AcerWmiManager.SetKeyboardBrightness(level))
            Logger.Info($"Lighting sync: restored the backlight to {level}%.");
        else
            Logger.Warn("Lighting sync: could not restore the backlight level.");
    }

    private static void RestoreGlobal()
    {
        var effect = AcerKeyboard.ParseEffect(SettingsManager.Get("KbGlobal_Effect", "Static"));
        var brightness = Math.Clamp(SettingsManager.Get("KbGlobal_Brightness", 100), 0, 100);
        var color = KeyboardColor.FromHex(SettingsManager.Get("KbGlobal_Color", "#B388FF"));
        var step = Math.Clamp(SettingsManager.Get("KbGlobal_Speed", 3), 1, 5);
        var speed = AcerKeyboard.MinSpeed + (step - 1) * 2;

        if (effect == KeyboardEffect.Static)
            AcerKeyboard.SetZoneColors(Enumerable.Repeat(color, ZoneCount).ToArray(), brightness);
        else
            AcerKeyboard.SetEffect(effect, brightness, speed, leftwards: false, color);

        if (effect is KeyboardEffect.Static or KeyboardEffect.Breathing or KeyboardEffect.Neon)
            AcerKeyboard.SetEffect(effect, brightness, step, leftwards: false, color, AcerKeyboard.LidDevice);
    }

    private static void RestoreArea()
    {
        var style = SettingsManager.Get("KbStyle", "Static");
        var brightness = Math.Clamp(SettingsManager.Get("KbBrightness", 100), 0, 100);

        if (string.Equals(style, "Dynamic", StringComparison.OrdinalIgnoreCase))
        {
            var effect = AcerKeyboard.ParseEffect(SettingsManager.Get("KbEffect", "Breathing"));
            var speed = Math.Clamp(SettingsManager.Get("KbSpeed", 4), AcerKeyboard.MinSpeed, AcerKeyboard.MaxSpeed);
            var color = KeyboardColor.FromHex(
                SettingsManager.Get("KbZone1", SettingsManager.Get("KbGlobal_Color", "#B388FF")));
            AcerKeyboard.SetEffect(effect, brightness, speed, leftwards: false, color);
        }
        else
        {
            var colors = Enumerable.Range(1, ZoneCount)
                .Select(i => KeyboardColor.FromHex(SettingsManager.Get($"KbZone{i}", "#B388FF")))
                .ToArray();
            AcerKeyboard.SetZoneColors(colors, brightness);
        }

        var logoEffect = AcerKeyboard.ParseEffect(SettingsManager.Get("KbLogo_Effect", "Static"));
        if (logoEffect is KeyboardEffect.Static or KeyboardEffect.Breathing or KeyboardEffect.Neon)
        {
            AcerKeyboard.SetEffect(
                logoEffect,
                Math.Clamp(SettingsManager.Get("KbLogo_Brightness", 100), 0, 100),
                Math.Clamp(SettingsManager.Get("KbLogo_Speed", 3), 1, 5),
                leftwards: false,
                KeyboardColor.FromHex(SettingsManager.Get("KbLogo_Color", "#B388FF")),
                AcerKeyboard.LidDevice);
        }
    }

    /// <summary>
    /// The default render endpoint's peak meter (system audio, not the microphone). The peak is
    /// square-rooted so quiet passages still move, and clamped after a modest gain.
    /// </summary>
    private sealed class AudioMeter : IDisposable
    {
        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioMeterInformation? _meter;

        public static AudioMeter? TryCreate()
        {
            var meter = new AudioMeter();
            try
            {
                meter._enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (meter._enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device == null)
                    throw new InvalidOperationException("No default audio endpoint.");

                meter._device = device;
                var iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out var instance) != 0 ||
                    instance is not IAudioMeterInformation audio)
                    throw new InvalidOperationException("The endpoint has no meter.");

                meter._meter = audio;
                return meter;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Sound sync: could not open the audio meter ({ex.GetType().Name}).");
                meter.Dispose();
                return null;
            }
        }

        public float Peak()
        {
            if (_meter == null || _meter.GetPeakValue(out var peak) != 0) return 0;
            return Math.Clamp(peak, 0f, 1f);
        }

        public void Dispose()
        {
            Release(_meter);
            Release(_device);
            Release(_enumerator);
            _meter = null;
            _device = null;
            _enumerator = null;
        }

        private static void Release(object? instance)
        {
            if (instance != null && Marshal.IsComObject(instance))
            {
                try { Marshal.ReleaseComObject(instance); } catch { }
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(
                int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);
            [PreserveSig] int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string id,
                [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(
                ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out int channelCount);
            [PreserveSig] int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);
            [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
        }
    }

    /// <summary>
    /// StretchBlt straight from the screen into a tiny 32-bit DIB. Scaling during the copy keeps a
    /// 1920x1080 desktop from being copied and then shrunk every frame.
    /// </summary>
    /// <summary>
    /// 2048-point FFT on the loopback samples, grouped into bass, low-mid, high-mid and treble.
    /// Each band is dB-mapped, smoothed with a fast release, and written to its own zone.
    /// </summary>
    private sealed class SpectrumAnalyzer
    {
        public const int Size = 2048;

        public float[] Samples { get; } = new float[Size];

        /// <summary>The raw bass band level (0-1), before smoothing or sensitivity, for beat onsets.</summary>
        public float Bass { get; private set; }

        private readonly double[] _real = new double[Size];
        private readonly double[] _imaginary = new double[Size];
        private readonly double[] _window = new double[Size];
        private readonly float[] _bands = new float[ZoneCount];

        private static readonly (double Low, double High, double Gain)[] Bands =
        {
            (20, 200, 0.9),
            (200, 800, 1.0),
            (800, 3000, 1.1),
            (3000, 10000, 1.3)
        };

        public SpectrumAnalyzer()
        {
            for (var i = 0; i < Size; i++)
                _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1));
        }

        public void Analyze(int sampleRate, int sensitivity, int ceiling, int[] levels)
        {
            if (sampleRate <= 0 || levels.Length < Bands.Length) return;

            for (var i = 0; i < Size; i++)
            {
                _real[i] = Samples[i] * _window[i];
                _imaginary[i] = 0;
            }

            Fft(_real, _imaginary);

            var scale = Size / 4.0; // a full-scale sine through the Hann window
            for (var band = 0; band < Bands.Length; band++)
            {
                var low = Math.Max(1, (int)(Bands[band].Low * Size / sampleRate));
                var high = Math.Min(Size / 2 - 1, (int)(Bands[band].High * Size / sampleRate));
                if (high < low) high = low;

                double sum = 0;
                var count = 0;
                for (var bin = low; bin <= high; bin++)
                {
                    var magnitude = Math.Sqrt(_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]);
                    sum += magnitude * magnitude;
                    count++;
                }

                var rms = Math.Sqrt(sum / Math.Max(1, count)) / scale;
                var db = 20 * Math.Log10(Math.Max(rms, 1e-7));
                var raw = Math.Clamp((db + 55) / 55, 0, 1) * Bands[band].Gain;
                if (band == 0) Bass = (float)Math.Clamp(raw, 0, 1);

                var level = raw * (sensitivity / 4.0);
                _bands[band] = (float)Math.Max(Math.Min(level, 1), _bands[band] * 0.72);
                levels[band] = (int)Math.Round(_bands[band] * ceiling);
            }
        }

        private static void Fft(double[] real, double[] imaginary)
        {
            var n = real.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;

                if (i >= j) continue;
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }

            for (var length = 2; length <= n; length <<= 1)
            {
                var angle = -2 * Math.PI / length;
                var stepReal = Math.Cos(angle);
                var stepImaginary = Math.Sin(angle);

                for (var start = 0; start < n; start += length)
                {
                    double weightReal = 1, weightImaginary = 0;
                    for (var offset = 0; offset < length / 2; offset++)
                    {
                        var even = start + offset;
                        var odd = even + length / 2;
                        var oddReal = real[odd] * weightReal - imaginary[odd] * weightImaginary;
                        var oddImaginary = real[odd] * weightImaginary + imaginary[odd] * weightReal;

                        real[odd] = real[even] - oddReal;
                        imaginary[odd] = imaginary[even] - oddImaginary;
                        real[even] += oddReal;
                        imaginary[even] += oddImaginary;

                        var nextReal = weightReal * stepReal - weightImaginary * stepImaginary;
                        weightImaginary = weightReal * stepImaginary + weightImaginary * stepReal;
                        weightReal = nextReal;
                    }
                }
            }
        }
    }

    /// <summary>
    /// WASAPI loopback on the default render endpoint, downmixed to mono and kept in a short ring
    /// so the FFT always has the latest 2048 samples.
    /// </summary>
    /// <summary>
    /// Time-domain envelope ported from pr4u's KB-Breathe-Acer-Nitro. The kick band is a two-pole
    /// 150 Hz low-pass, the snare body a 2 kHz low-pass, and the levels are RMS over the latest
    /// audio window. Beat is an adaptive attack detector with an 80 ms hold and a straight fall;
    /// Overall follows the tune's loudness with an auto-ranged envelope.
    /// </summary>
    private sealed class SoundEnvelope
    {
        private float _bassCoefficient;
        private float _midCoefficient;

        private float _bassSlow;
        private float _midSlow;
        private float _onsetAvg;
        private float _onsetPeak;
        private float _flash;
        private float _holdUntil;
        private float _time;
        private float _lastBeat;

        private float _hi = 0.004f;
        private float _lo;
        private float _env;

        public void Configure(int sampleRate)
        {
            _bassCoefficient = 1f - MathF.Exp(-2f * MathF.PI * 150f / sampleRate);
            _midCoefficient = 1f - MathF.Exp(-2f * MathF.PI * 2000f / sampleRate);
        }

        public void Reset()
        {
            _bassSlow = _midSlow = _onsetAvg = _onsetPeak = 0f;
            _flash = _holdUntil = _time = 0f;
            _lastBeat = -1f;
            _hi = 0.004f;
            _lo = 0f;
            _env = 0f;
        }

        public void Analyze(float[] samples, out float bass, out float mid, out float full)
        {
            var low1 = 0f;
            var low2 = 0f;
            var body = 0f;
            double bassSum = 0;
            double midSum = 0;
            double fullSum = 0;
            var count = 0;

            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                low1 += _bassCoefficient * (sample - low1);
                low2 += _bassCoefficient * (low1 - low2);
                body += _midCoefficient * ((sample - low2) - body);

                // Let the filters settle before accumulating; the window starts cold.
                if (i < 128) continue;

                bassSum += low2 * low2;
                midSum += body * body;
                fullSum += sample * sample;
                count++;
            }

            if (count == 0)
            {
                bass = mid = full = 0f;
                return;
            }

            bass = (float)Math.Sqrt(bassSum / count);
            mid = (float)Math.Sqrt(midSum / count);
            full = (float)Math.Sqrt(fullSum / count);
        }

        public float Beat(float bass, float mid, float full, float gain, float dt)
        {
            var live = full >= 0.0006f;
            _bassSlow += (bass - _bassSlow) * Math.Min(1f, dt / 0.25f);
            _midSlow += (mid - _midSlow) * Math.Min(1f, dt / 0.25f);

            var onset = live
                ? Math.Max(0f, bass - _bassSlow * 1.25f) +
                  Math.Max(0f, mid - _midSlow * 1.30f) * 0.55f
                : 0f;

            _onsetAvg += (onset - _onsetAvg) * Math.Min(1f, dt / 2.0f);
            _onsetPeak = Math.Max(onset, _onsetPeak * MathF.Exp(-dt / 3.0f));

            var threshold = Math.Max(0.0012f, _onsetAvg * 1.8f) / gain;
            _time += dt;

            if (onset > threshold && _time - _lastBeat >= 0.10f)
            {
                var strength = Math.Clamp(
                    onset / Math.Max(_onsetPeak * 0.9f, threshold), 0.4f, 1f);
                _flash = Math.Max(_flash, strength);
                _holdUntil = _time + 0.08f;
                _lastBeat = _time;
            }
            else if (_time >= _holdUntil)
            {
                _flash = Math.Max(0f, _flash - dt * 5f);
            }

            return _flash;
        }

        public float Overall(float bass, float full, float gain, float dt)
        {
            var loudness = full < 0.0006f ? 0f : bass * 2.1f + full * 0.55f;

            _hi = Math.Max(loudness, _hi * MathF.Exp(-dt / 3.0f));
            if (loudness < _lo) _lo = loudness;
            else _lo += (loudness - _lo) * Math.Min(1f, dt / 8.0f);

            var range = Math.Max(_hi - _lo, Math.Max(0.0006f, _hi * 0.25f));
            var normalized = Math.Clamp((loudness - _lo) / range, 0f, 1f) * gain;
            if (normalized > 1f) normalized = 1f;

            if (normalized > _env)
                _env += (normalized - _env) * Math.Min(1f, dt / 0.035f);
            else
                _env = Math.Max(0f, _env - dt * 2.0f);

            return _env;
        }
    }

    private sealed class SpectrumSource : IDisposable
    {
        private const int Shared = 0;
        private const uint Loopback = 0x00020000;
        private const uint Silent = 0x0002;
        private const int RingLength = SpectrumAnalyzer.Size * 2;

        private IMMDeviceEnumerator2? _enumerator;
        private IMMDevice2? _device;
        private IAudioClient? _client;
        private IAudioCaptureClient? _capture;
        private IntPtr _format;
        private int _channels;
        private int _bits;
        private readonly float[] _ring = new float[RingLength];
        private int _write;
        private int _count;
        private float[] _packet = new float[4096];
        private short[] _packet16 = new short[4096];
        private byte[] _packet24 = new byte[12288];

        public int SampleRate { get; private set; }

        public static SpectrumSource? TryCreate()
        {
            var source = new SpectrumSource();
            try
            {
                source._enumerator = (IMMDeviceEnumerator2)new MMDeviceEnumerator2ComObject();
                if (source._enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device == null)
                    throw new InvalidOperationException("No default audio endpoint.");

                source._device = device;
                var iid = typeof(IAudioClient).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out var instance) != 0 ||
                    instance is not IAudioClient client)
                    throw new InvalidOperationException("No audio client.");

                source._client = client;
                if (client.GetMixFormat(out var format) != 0 || format == IntPtr.Zero)
                    throw new InvalidOperationException("No mix format.");

                source._format = format;
                var wave = Marshal.PtrToStructure<WaveFormatEx>(format);
                source._channels = Math.Max(1, (int)wave.Channels);
                source.SampleRate = (int)wave.SamplesPerSec;
                source._bits = wave.BitsPerSample;
                if (source._bits is not (16 or 24 or 32) || source.SampleRate <= 0)
                    throw new InvalidOperationException($"Unsupported sample format ({source._bits}-bit).");

                if (client.Initialize(Shared, Loopback, 1_000_000, 0, format, IntPtr.Zero) != 0)
                    throw new InvalidOperationException("Loopback initialise failed.");

                var captureIid = typeof(IAudioCaptureClient).GUID;
                if (client.GetService(ref captureIid, out var capturePointer) != 0 ||
                    capturePointer == IntPtr.Zero)
                    throw new InvalidOperationException("No capture client.");

                source._capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePointer);
                Marshal.Release(capturePointer);

                if (client.Start() != 0)
                    throw new InvalidOperationException("Loopback start failed.");

                return source;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Spectrum sync: could not open WASAPI loopback ({ex.GetType().Name}).");
                source.Dispose();
                return null;
            }
        }

        /// <summary>Drains the loopback packets and fills <paramref name="destination"/> with the latest mono samples.</summary>
        public bool Read(float[] destination)
        {
            if (_capture == null) return false;

            var appended = false;
            while (_capture.GetNextPacketSize(out var frameCount) == 0 && frameCount > 0)
            {
                if (_capture.GetBuffer(out var data, out var count, out var flags, out _, out _) != 0)
                    break;

                try
                {
                    if ((flags & Silent) != 0 || data == IntPtr.Zero)
                        AppendSilence(count);
                    else
                        Append(data, count);
                    appended = true;
                }
                finally
                {
                    _capture.ReleaseBuffer(count);
                }
            }

            var start = (_write - destination.Length + RingLength) % RingLength;
            for (var i = 0; i < destination.Length; i++)
                destination[i] = _count >= destination.Length ? _ring[(start + i) % RingLength] : 0f;

            return appended;
        }

        private void Append(IntPtr data, uint frameCount)
        {
            var samples = (int)frameCount * _channels;
            if (samples <= 0) return;

            if (_packet.Length < samples) _packet = new float[samples];
            if (_packet16.Length < samples) _packet16 = new short[samples];

            if (_bits == 32) Marshal.Copy(data, _packet, 0, samples);
            else if (_bits == 16)
            {
                Marshal.Copy(data, _packet16, 0, samples);
                for (var i = 0; i < samples; i++) _packet[i] = _packet16[i] / 32768f;
            }
            else
            {
                // 24-bit packed: the same sign-extended conversion pr4u's capture uses.
                var bytes = samples * 3;
                if (_packet24.Length < bytes) _packet24 = new byte[bytes];
                Marshal.Copy(data, _packet24, 0, bytes);
                for (var i = 0; i < samples; i++)
                {
                    var o = i * 3;
                    var value = (_packet24[o] << 8) | (_packet24[o + 1] << 16) | (_packet24[o + 2] << 24);
                    _packet[i] = value / 2147483648f;
                }
            }

            var frames = (int)frameCount;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var offset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                    sum += _packet[offset + channel];
                AppendSample(sum / _channels);
            }
        }

        private void AppendSilence(uint frameCount)
        {
            for (var i = 0; i < frameCount; i++) AppendSample(0f);
        }

        private void AppendSample(float sample)
        {
            _ring[_write] = sample;
            _write = (_write + 1) % RingLength;
            if (_count < RingLength) _count++;
        }

        public void Dispose()
        {
            try { _client?.Stop(); } catch { }
            Release(_capture);
            Release(_client);
            Release(_device);
            Release(_enumerator);
            _capture = null;
            _client = null;
            _device = null;
            _enumerator = null;

            if (_format != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_format);
                _format = IntPtr.Zero;
            }
        }

        private static void Release(object? instance)
        {
            if (instance != null && Marshal.IsComObject(instance))
            {
                try { Marshal.ReleaseComObject(instance); } catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator2ComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator2
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(
                int dataFlow, int role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice2 endpoint);
            [PreserveSig] int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string id,
                [MarshalAs(UnmanagedType.Interface)] out IMMDevice2 device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice2
        {
            [PreserveSig] int Activate(
                ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(
                int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(
                out IntPtr data, out uint frameCount, out uint flags,
                out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frameCount);
            [PreserveSig] int GetNextPacketSize(out uint frameCount);
        }
    }

    private sealed class ScreenCapture : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _buffer;
        private IntPtr _dc;
        private IntPtr _bitmap;
        private IntPtr _previous;
        private IntPtr _bits;
        private bool _failed;

        public ScreenCapture(int width, int height)
        {
            _width = width;
            _height = height;
            _buffer = new byte[width * height * 4];

            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = width * height * 4
                }
            };

            _dc = CreateCompatibleDC(IntPtr.Zero);
            if (_dc == IntPtr.Zero)
            {
                _failed = true;
                return;
            }

            _bitmap = CreateDIBSection(_dc, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                _failed = true;
                return;
            }

            _previous = SelectObject(_dc, _bitmap);
            SetStretchBltMode(_dc, HalfTone);
            SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
        }

        public bool TryCapture(out byte[] pixels)
        {
            pixels = _buffer;
            if (_failed) return false;

            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;

            try
            {
                var width = GetSystemMetrics(ScreenWidth);
                var height = GetSystemMetrics(ScreenHeight);
                if (width <= 0 || height <= 0) return false;

                if (!StretchBlt(_dc, 0, 0, _width, _height,
                        screen, 0, 0, width, height, SourceCopy))
                    return false;

                Marshal.Copy(_bits, _buffer, 0, _buffer.Length);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Dispose()
        {
            if (_dc == IntPtr.Zero) return;

            if (_previous != IntPtr.Zero) SelectObject(_dc, _previous);
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            DeleteDC(_dc);
            _dc = IntPtr.Zero;
            _bitmap = IntPtr.Zero;
            _bits = IntPtr.Zero;
        }

        private const int ScreenWidth = 0;
        private const int ScreenHeight = 1;
        private const int HalfTone = 4;
        private const uint SourceCopy = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ColorsUsed;
            public int ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Color;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(
            IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    }
}
