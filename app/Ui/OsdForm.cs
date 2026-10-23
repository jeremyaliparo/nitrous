using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Nitrous.Enums;

namespace Nitrous.Ui;

public class OsdForm : Form
{
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly System.Windows.Forms.Timer _holdTimer;
    private string _profileText = "PROFILE";
    private Color _accentColor = Color.White;
    private PowerProfile _activeProfile = PowerProfile.Balanced;

    // Set peak opacity to 85% for a subtle translucent effect
    private double _opacity = 0.85;
    private readonly int _cornerRadius = 10;

    protected override bool ShowWithoutActivation => true;

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(24, 24, 27);
        TopMost = true;
        ShowInTaskbar = false;

        Size = new Size(280, 70);

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        StartPosition = FormStartPosition.Manual;
        Location = new Point((screen.Width - Width) / 2, screen.Height - 160);

        using (var path = GetRoundedPath(new Rectangle(0, 0, Width, Height), _cornerRadius))
            Region = new Region(path);

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _fadeTimer.Tick += FadeTimer_Tick;

        _holdTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            _fadeTimer.Start();
        };
    }

    public void ShowProfile(string profileName, Color color, PowerProfile profile)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => ShowProfile(profileName, color, profile))); }
            catch (InvalidOperationException) { /* handle not created or already disposed; ignore */ }
            return;
        }

        _profileText = profileName.ToUpperInvariant();
        _accentColor = color;
        _activeProfile = profile;

        // Reset to the subtle peak opacity
        _opacity = 0.85;
        Opacity = _opacity;

        Invalidate();
        Show();

        _fadeTimer.Stop();
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void FadeTimer_Tick(object? sender, EventArgs e)
    {
        _opacity -= 0.05;
        Opacity = _opacity;
        if (_opacity <= 0)
        {
            _fadeTimer.Stop();
            Hide();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 1. Accent bar (Form region automatically trims the top/bottom left corners)
        using (var brush = new SolidBrush(_accentColor))
        {
            e.Graphics.FillRectangle(brush, 0, 0, 8, Height);
        }

        // 2. Draw Rounded Border perfectly tracing the form's curved edge
        using (var path = GetRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), _cornerRadius))
        using (var pen = new Pen(Color.FromArgb(48, 48, 54), 1))
        {
            e.Graphics.DrawPath(pen, path);
        }

        // 3. Draw Speedometer Icon
        DrawSpeedometerIcon(e.Graphics, new Rectangle(25, 17, 36, 36), _accentColor, _activeProfile);

        // 4. Draw Text
        using var font = new Font("Segoe UI", 16, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        var textRect = new Rectangle(65, 0, Width - 65, Height);
        e.Graphics.DrawString(_profileText, font, textBrush, textRect, format);
    }

    // Helper method to generate standard geometric curves for rounded edges
    private GraphicsPath GetRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        path.AddArc(rect.X, rect.Y, d, d, 180, 90); // Top-Left
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90); // Top-Right
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); // Bottom-Right
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); // Bottom-Left
        path.CloseFigure();

        return path;
    }

    private void DrawSpeedometerIcon(Graphics g, Rectangle bounds, Color color, PowerProfile profile)
    {
        using var thickPen = new Pen(color, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var thinPen = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dotBrush = new SolidBrush(color);

        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        var r = bounds.Width / 2;

        g.DrawArc(thickPen, cx - r, cy - r, r * 2, r * 2, 135, 270);

        double angle = 270;
        switch (profile)
        {
            case PowerProfile.Quiet: angle = 180; break;
            case PowerProfile.Balanced: angle = 270; break;
            case PowerProfile.Performance: angle = 360; break;
            case PowerProfile.Turbo: angle = 45; break;
        }

        g.FillEllipse(dotBrush, cx - 3, cy - 3, 6, 6);

        var rad = angle * Math.PI / 180.0;
        var nx = cx + (int)((r - 2) * Math.Cos(rad));
        var ny = cy + (int)((r - 2) * Math.Sin(rad));

        g.DrawLine(thinPen, cx, cy, nx, ny);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000020;
            cp.ExStyle |= 0x08000000;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _holdTimer.Stop();
            _fadeTimer.Stop();
            _holdTimer.Dispose();
            _fadeTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
