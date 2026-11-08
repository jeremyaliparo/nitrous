using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Nitrous.Diagnostics;

namespace Nitrous.Managers;

/// <summary>
/// A software screen-sync effect: the primary screen is reduced to four column averages and written
/// as static zone colours, while the cover logo follows the whole-screen average at a slower rate.
///
/// This is not a firmware effect. It writes continuously, so it runs in the tray process (the
/// dashboard is a separate process and may be closed) and only while <c>KbScreenSync</c> is on.
/// Stopping restores whichever preset was last applied (Global or Area) from its saved settings.
/// </summary>
internal static class ScreenSync
{
    private const int Columns = 64;
    private const int Rows = 36;
    private const int ZoneCount = AcerKeyboard.ZoneCount;
    private const int DefaultRateMs = 125;
    private const int LogoEveryFrames = 4;

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static volatile bool _running;

    public static bool IsRunning => _running;

    public static void Start()
    {
        lock (Gate)
        {
            if (_running) return;
            if (!AcerKeyboard.IsAvailable()) return;

            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Nitrous screen sync"
            };
            _thread.Start();
            Logger.Info("Screen sync: started.");
        }
    }

    public static void Stop()
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

        RestoreSavedPreset();
        Logger.Info("Screen sync: stopped.");
    }

    private static void Loop()
    {
        using var capture = new ScreenCapture(Columns, Rows);
        var zones = new KeyboardColor[ZoneCount];
        var frame = 0;
        var logoAvailable = true;
        var writeWarned = false;

        try
        {
            while (_running)
            {
                var rate = Math.Clamp(SettingsManager.Get("KbScreenSyncRate", DefaultRateMs), 80, 500);
                var brightness = Math.Clamp(SettingsManager.Get("KbGlobal_Brightness", 100), 0, 100);

                if (capture.TryCapture(out var pixels))
                {
                    if (!_running) break;

                    ReadZones(pixels, zones);
                    if (!AcerKeyboard.SetZoneColors(zones, brightness) && !writeWarned)
                    {
                        Logger.Warn("Screen sync: the keyboard refused a frame.");
                        writeWarned = true;
                    }

                    if (logoAvailable && frame % LogoEveryFrames == 0)
                    {
                        logoAvailable = AcerKeyboard.SetEffect(
                            KeyboardEffect.Static, brightness, 0, leftwards: false, Average(pixels),
                            AcerKeyboard.LidDevice);
                    }
                }

                frame++;
                Thread.Sleep(rate);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Screen sync: the worker stopped.", ex);
            _running = false;
            RestoreSavedPreset();
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
            Logger.Warn("Screen sync: could not restore the saved lighting.", ex);
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
