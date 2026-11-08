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

    private const int Columns = 64;
    private const int Rows = 36;
    private const int ZoneCount = AcerKeyboard.ZoneCount;
    private const int ScreenRateMs = 125;
    private const int SoundRateMs = 40;
    private const int LogoEveryFrames = 4;
    private const int SoundFloorPercent = 15;

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static volatile bool _running;
    private static string _source = SourceScreen;
    private static bool _writeWarned;
    private static bool _logoAvailable = true;

    public static bool IsRunning => _running;

    public static string Normalize(string? source) =>
        string.Equals(source, SourceSound, StringComparison.OrdinalIgnoreCase) ? SourceSound : SourceScreen;

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

            if (!AcerKeyboard.IsAvailable()) return;

            _source = normalized;
            _writeWarned = false;
            _logoAvailable = true;
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
        var frame = 0;

        while (_running)
        {
            if (Paused())
            {
                Thread.Sleep(50);
                continue;
            }

            var rate = Math.Clamp(
                SettingsManager.Get("KbSyncRate",
                    SettingsManager.Get("KbScreenSyncRate", ScreenRateMs)),
                80, 500);
            var brightness = Math.Clamp(SettingsManager.Get("KbGlobal_Brightness", 100), 0, 100);

            if (capture.TryCapture(out var pixels))
            {
                if (!_running) break;

                ReadZones(pixels, zones);
                WriteFrame(zones, brightness, Average(pixels), frame % LogoEveryFrames == 0);
            }

            frame++;
            Thread.Sleep(rate);
        }
    }

    private static void SoundLoop()
    {
        using var meter = AudioMeter.TryCreate();
        if (meter == null)
        {
            Logger.Warn("Sound sync: no default audio output to meter.");
            _running = false;
            RestoreSavedPreset();
            return;
        }

        var color = KeyboardColor.FromHex(SettingsManager.Get("KbGlobal_Color", "#B388FF"));
        var zones = Enumerable.Repeat(color, ZoneCount).ToArray();
        var level = 0f;
        var frame = 0;

        while (_running)
        {
            if (Paused())
            {
                Thread.Sleep(50);
                continue;
            }

            // Follow a colour change about once a second without a registry read every frame.
            if (frame % 25 == 0)
            {
                color = KeyboardColor.FromHex(SettingsManager.Get("KbGlobal_Color", "#B388FF"));
                zones = Enumerable.Repeat(color, ZoneCount).ToArray();
            }

            var peak = meter.Peak();
            level = Math.Max(peak, level * 0.80f); // fast attack, slower release

            var ceiling = Math.Clamp(SettingsManager.Get("KbGlobal_Brightness", 100), 0, 100);
            var shaped = SoundFloorPercent + level * (100 - SoundFloorPercent);
            var brightness = (int)Math.Round(shaped * ceiling / 100.0);

            WriteFrame(zones, Math.Clamp(brightness, 0, 100), color, frame % LogoEveryFrames == 0);

            frame++;
            Thread.Sleep(SoundRateMs);
        }
    }

    private static void WriteFrame(KeyboardColor[] zones, int brightness, KeyboardColor logo, bool writeLogo)
    {
        if (!AcerKeyboard.SetZoneColors(zones, brightness) && !_writeWarned)
        {
            Logger.Warn("Lighting sync: the keyboard refused a frame.");
            _writeWarned = true;
        }

        if (writeLogo && _logoAvailable)
        {
            _logoAvailable = AcerKeyboard.SetEffect(
                KeyboardEffect.Static, brightness, 0, leftwards: false, logo, AcerKeyboard.LidDevice);
        }
    }

    /// <summary>Four equal screen columns, left to right, as zone colours.</summary>
    private static void ReadZones(byte[] bgra, KeyboardColor[] zones)
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

            zones[zone] = new KeyboardColor(
                (byte)(red / count), (byte)(green / count), (byte)(blue / count));
        }
    }

    private static KeyboardColor Average(byte[] bgra)
    {
        long red = 0, green = 0, blue = 0;
        var count = bgra.Length / 4;

        for (var i = 0; i < bgra.Length; i += 4)
        {
            blue += bgra[i];
            green += bgra[i + 1];
            red += bgra[i + 2];
        }

        return new KeyboardColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    /// <summary>Puts the keyboard back on the preset that was in use before sync started.</summary>
    private static void RestoreSavedPreset()
    {
        try
        {
            if (!AcerKeyboard.IsAvailable()) return;

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

            var shaped = MathF.Sqrt(Math.Clamp(peak, 0f, 1f)) * 1.5f;
            return Math.Clamp(shaped, 0f, 1f);
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
