using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nitrous.Diagnostics;
using Nitrous.Enums;

namespace Nitrous.Ui;

/// <summary>
/// Bottom-center toast shown when the power profile changes.
///
/// The card is rendered once into a 32 bpp premultiplied bitmap and shown through
/// UpdateLayeredWindow, so the animation only changes alpha and window position. There is no
/// per-frame GDI+ text rendering, no Form.Opacity animation, and no WPF dependency in the tray.
/// </summary>
public sealed class OsdForm : Form
{
    private const int BaseWidthDip = 360;
    private const int BaseHeightDip = 96;
    private const int SlideDip = 12;

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(200);

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x00000002;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int WS_EX_TOPMOST = 0x00000008;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly System.Windows.Forms.Timer _frameTimer;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<CardKey, CardSurface> _cards = new();
    private readonly Color _quiet = Color.FromArgb(52, 199, 89);
    private readonly Color _balanced = Color.FromArgb(10, 132, 255);
    private readonly Color _performance = Color.FromArgb(255, 159, 10);
    private readonly Color _turbo = Color.FromArgb(255, 69, 58);

    private Bitmap? _logo;
    private CardSurface? _surface;
    private Size _surfaceSize;
    private Point _baseLocation;
    private int _slide;
    private float _scale = 1f;
    private Color _accent = Color.White;
    private Phase _phase = Phase.Idle;
    private TimeSpan _phaseStart;
    private bool _animating;
    private bool _reduceMotion;
    private bool _updateFailed;

    private enum Phase
    {
        Idle,
        In,
        Hold,
        Out
    }

    private readonly record struct CardKey(PowerProfile Profile, int Width, int Height);

    private readonly record struct OsdLayout(Screen Screen, int Dpi, Size Size, Point BaseLocation, int Slide);

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(1, 1);
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.Selectable, false);

        _frameTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _frameTimer.Tick += OnFrame;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT (click-through)
            return cp;
        }
    }

    /// <summary>
    /// Builds the window handle, logo and the common cards while the app is idle so the first
    /// hotkey does not pay the first-render cost. UI thread only.
    /// </summary>
    public void Prewarm()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(Prewarm)); }
            catch (InvalidOperationException) { }
            return;
        }

        try
        {
            _ = Handle;
            EnsureLogo();

            var layout = ComputeLayout();
            foreach (var profile in Enum.GetValues<PowerProfile>())
                GetSurface(profile, layout);
        }
        catch (Exception ex)
        {
            Logger.Warn("OSD: prewarm failed.", ex);
        }
    }

    public void ShowProfile(Color color, PowerProfile profile)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => ShowProfile(color, profile))); }
            catch (InvalidOperationException) { }
            return;
        }

        try
        {
            _accent = color;

            var layout = ComputeLayout();
            var surface = GetSurface(profile, layout);
            var sizeChanged = _surface != surface || _surfaceSize != layout.Size;

            _surface = surface;
            _surfaceSize = layout.Size;
            _baseLocation = layout.BaseLocation;
            _slide = layout.Slide;
            _scale = layout.Size.Width / (float)BaseWidthDip;

            _ = Handle;

            if (Visible && _animating && _phase is Phase.In or Phase.Hold)
            {
                // A second profile change while the toast is visible: swap the card without
                // flashing it out and back in.
                SetFrame(255, 0);
                _phase = Phase.Hold;
                _phaseStart = _clock.Elapsed;
                return;
            }

            if (sizeChanged)
                Size = layout.Size;

            // Put the surface in place while the window is still hidden, then show at alpha 0.
            SetFrame(0, _slide);
            if (!Visible) Show();
            RaiseToTop();

            _phase = Phase.In;
            _phaseStart = TimeSpan.Zero;
            _clock.Restart();
            _animating = true;
            _frameTimer.Start();

            _reduceMotion = !SystemInformation.UIEffectsEnabled;

            if (_reduceMotion)
            {
                // Reduced motion: no fade, just hold the card for the normal duration.
                SetFrame(255, 0);
                _phase = Phase.Hold;
            }
            else
            {
                OnFrame(null, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("OSD: could not show the profile card.", ex);
            try { Hide(); } catch { }
            _animating = false;
            _frameTimer.Stop();
            _phase = Phase.Idle;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_animating || _surface == null) return;

        var now = _clock.Elapsed;

        switch (_phase)
        {
            case Phase.In:
            {
                var t = Math.Clamp((now - _phaseStart).TotalMilliseconds / FadeIn.TotalMilliseconds, 0.0, 1.0);
                var eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                SetFrame((byte)Math.Round(255 * eased), (int)Math.Round((1.0 - eased) * _slide));
                if (t >= 1.0)
                {
                    _phase = Phase.Hold;
                    _phaseStart = now;
                }
                break;
            }
            case Phase.Hold:
            {
                SetFrame(255, 0);
                if (now - _phaseStart >= Hold)
                {
                    if (_reduceMotion)
                    {
                        Hide();
                        _animating = false;
                        _phase = Phase.Idle;
                        _frameTimer.Stop();
                    }
                    else
                    {
                        _phase = Phase.Out;
                        _phaseStart = now;
                    }
                }
                break;
            }
            case Phase.Out:
            {
                var t = Math.Clamp((now - _phaseStart).TotalMilliseconds / FadeOut.TotalMilliseconds, 0.0, 1.0);
                var eased = t * t;
                SetFrame((byte)Math.Round(255 * (1.0 - eased)), (int)Math.Round(eased * 6 * _scale));
                if (t >= 1.0)
                {
                    Hide();
                    _animating = false;
                    _phase = Phase.Idle;
                    _frameTimer.Stop();
                }
                break;
            }
        }
    }

    private void RaiseToTop()
    {
        if (Handle == IntPtr.Zero) return;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void SetFrame(byte alpha, int yOffset)
    {
        if (_surface == null || Handle == IntPtr.Zero) return;

        var destination = new POINT { X = _baseLocation.X, Y = _baseLocation.Y + yOffset };
        var size = new SIZE { CX = _surfaceSize.Width, CY = _surfaceSize.Height };
        var source = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = AC_SRC_ALPHA
        };

        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _surface.MemDc,
                ref source, 0, ref blend, ULW_ALPHA))
        {
            if (!_updateFailed)
            {
                _updateFailed = true;
                Logger.Warn($"OSD: UpdateLayeredWindow failed (error {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    private OsdLayout ComputeLayout()
    {
        var screen = GetTargetScreen();
        var dpi = GetMonitorDpi(screen);
        var dpiScale = dpi / 96f;

        // DIP-based sizing keeps the card physically consistent across DPI settings. The fit
        // clamp keeps it under ~30% of the screen width on small laptop panels.
        var fit = Math.Min(1f, screen.WorkingArea.Width * 0.30f / (BaseWidthDip * dpiScale));
        var scale = dpiScale * fit;

        var width = Math.Max(1, (int)Math.Round(BaseWidthDip * scale));
        var height = Math.Max(1, (int)Math.Round(BaseHeightDip * scale));

        var work = screen.WorkingArea;
        var x = work.Left + (work.Width - width) / 2;
        var bottomMargin = Math.Max((int)Math.Round(48 * scale), (int)Math.Round(work.Height * 0.08f));
        var y = work.Bottom - height - bottomMargin;

        return new OsdLayout(screen, dpi, new Size(width, height), new Point(x, y),
            Math.Max(1, (int)Math.Round(SlideDip * scale)));
    }

    private static Screen GetTargetScreen()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foreground, out var processId);
                if (processId != (uint)Environment.ProcessId)
                {
                    var screen = Screen.FromHandle(foreground);
                    if (screen != null) return screen;
                }
            }
        }
        catch { }

        try { return Screen.FromPoint(Cursor.Position); }
        catch { }

        return Screen.PrimaryScreen ?? Screen.AllScreens.First();
    }

    private static int GetMonitorDpi(Screen screen)
    {
        try
        {
            var point = new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 };
            var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return (int)Math.Round((double)dpiX);
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch { }

        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round((double)graphics.DpiX);
        }
        catch
        {
            return 96;
        }
    }

    private Bitmap? EnsureLogo()
    {
        if (_logo != null) return _logo;

        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Nitrous.assets.logo.png");
            if (stream == null) return null;

            using var source = Image.FromStream(stream);
            _logo = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(_logo);
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
            return _logo;
        }
        catch (Exception ex)
        {
            Logger.Warn("OSD: could not load the logo.", ex);
            return null;
        }
    }

    private CardSurface GetSurface(PowerProfile profile, OsdLayout layout)
    {
        var key = new CardKey(profile, layout.Size.Width, layout.Size.Height);
        if (_cards.TryGetValue(key, out var existing)) return existing;

        var surface = CardSurface.Create(layout.Size.Width, layout.Size.Height,
            graphics => RenderCard(graphics, layout.Size, profile));
        _cards[key] = surface;
        return surface;
    }

    private void RenderCard(Graphics graphics, Size size, PowerProfile profile)
    {
        var scale = size.Width / (float)BaseWidthDip;
        var width = (float)size.Width;
        var height = (float)size.Height;

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        var accent = GetAccent(profile);

        using (var card = RoundedRect(new RectangleF(0.5f, 0.5f, width - 1f, height - 1f), 22f * scale))
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, width, height),
                   Color.FromArgb(242, 24, 24, 28), Color.FromArgb(242, 14, 14, 18), 90f))
        {
            graphics.FillPath(fill, card);
            using var border = new Pen(Color.FromArgb(44, 255, 255, 255), Math.Max(1f, scale));
            graphics.DrawPath(border, card);
        }

        using (var bar = RoundedRect(new RectangleF(14f * scale, 18f * scale, 4f * scale,
                   height - 36f * scale), 2f * scale))
        using (var brush = new LinearGradientBrush(
                   new RectangleF(14f * scale, 18f * scale, 4f * scale, height - 36f * scale),
                   Color.FromArgb(255, accent), Color.FromArgb(96, accent), 90f))
        {
            graphics.FillPath(brush, bar);
        }

        var tile = new RectangleF(32f * scale, (height - 52f * scale) / 2f, 52f * scale, 52f * scale);
        using (var tilePath = RoundedRect(tile, 14f * scale))
        using (var tileBrush = new SolidBrush(Color.FromArgb(255, 30, 30, 35)))
        {
            graphics.FillPath(tileBrush, tilePath);
            using var tileBorder = new Pen(Color.FromArgb(26, 255, 255, 255), Math.Max(1f, scale));
            graphics.DrawPath(tileBorder, tilePath);
        }

        var logoSize = 34f * scale;
        var logoBounds = new RectangleF(
            tile.X + (tile.Width - logoSize) / 2f,
            tile.Y + (tile.Height - logoSize) / 2f,
            logoSize,
            logoSize);

        var logo = EnsureLogo();
        if (logo != null)
        {
            graphics.DrawImage(logo, logoBounds, new RectangleF(0, 0, logo.Width, logo.Height),
                GraphicsUnit.Pixel);
        }
        else
        {
            using var fallbackFont = new Font("Segoe UI", 24f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fallbackBrush = new LinearGradientBrush(logoBounds,
                Color.FromArgb(255, 255, 159, 10), Color.FromArgb(255, 10, 132, 255), 45f);
            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString("N", fallbackFont, fallbackBrush, logoBounds, centered);
        }

        var textX = 100f * scale;
        using var captionFont = new Font("Segoe UI", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleFont = new Font("Segoe UI Semibold", 22f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var captionBrush = new SolidBrush(Color.FromArgb(255, 154, 154, 166));
        using var titleBrush = new SolidBrush(Color.White);
        using var textFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };

        graphics.DrawString("POWER PROFILE", captionFont, captionBrush,
            new RectangleF(textX, 24f * scale, width - textX - 24f * scale, 18f * scale), textFormat);
        graphics.DrawString(profile.ToString().ToUpperInvariant(), titleFont, titleBrush,
            new RectangleF(textX, 41f * scale, width - textX - 24f * scale, 36f * scale), textFormat);

        var dotX = width - 28f * scale;
        var dotY = height / 2f;
        using (var glow = new SolidBrush(Color.FromArgb(40, accent)))
            graphics.FillEllipse(glow, dotX - 8f * scale, dotY - 8f * scale, 16f * scale, 16f * scale);
        using (var dot = new SolidBrush(accent))
            graphics.FillEllipse(dot, dotX - 4f * scale, dotY - 4f * scale, 8f * scale, 8f * scale);
    }

    private Color GetAccent(PowerProfile profile) => profile switch
    {
        PowerProfile.Quiet => _quiet,
        PowerProfile.Balanced => _balanced,
        PowerProfile.Performance => _performance,
        PowerProfile.Turbo => _turbo,
        _ => _accent
    };

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));

        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The layered surface is supplied by UpdateLayeredWindow.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // The layered surface is supplied by UpdateLayeredWindow.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Stop();
            _frameTimer.Dispose();

            foreach (var surface in _cards.Values)
                surface.Dispose();
            _cards.Clear();
            _surface = null;

            _logo?.Dispose();
            _logo = null;
        }

        base.Dispose(disposing);
    }

    private sealed class CardSurface : IDisposable
    {
        public IntPtr MemDc { get; private set; }
        public IntPtr HBitmap { get; private set; }

        private IntPtr _oldObject;

        private CardSurface()
        {
        }

        public static CardSurface Create(int width, int height, Action<Graphics> draw)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                draw(graphics);

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            var surface = new CardSurface();
            surface.HBitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (surface.HBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                surface.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed.");
            }

            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                CopyRows(data.Scan0, data.Stride, bits, width * 4, height);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            surface.MemDc = CreateCompatibleDC(IntPtr.Zero);
            if (surface.MemDc == IntPtr.Zero)
            {
                surface.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
            }

            surface._oldObject = SelectObject(surface.MemDc, surface.HBitmap);
            return surface;
        }

        private static unsafe void CopyRows(IntPtr source, int sourceStride, IntPtr destination,
            int rowBytes, int height)
        {
            var sourceBytes = (byte*)source;
            var destinationBytes = (byte*)destination;

            if (sourceStride == rowBytes)
            {
                Buffer.MemoryCopy(sourceBytes, destinationBytes, rowBytes * (long)height, rowBytes * (long)height);
                return;
            }

            for (var y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(sourceBytes + y * sourceStride, destinationBytes + y * rowBytes,
                    rowBytes, rowBytes);
            }
        }

        public void Dispose()
        {
            if (MemDc != IntPtr.Zero)
            {
                if (_oldObject != IntPtr.Zero) SelectObject(MemDc, _oldObject);
                DeleteDC(MemDc);
                MemDc = IntPtr.Zero;
            }

            if (HBitmap != IntPtr.Zero)
            {
                DeleteObject(HBitmap);
                HBitmap = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RGBQUAD
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public RGBQUAD bmiColors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y,
        int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);
}
