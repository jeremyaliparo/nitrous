using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace Nitrous.Ui;

/// <summary>
/// Draws a small tray icon at runtime. The bitmap is wrapped in an in-memory ICO, so the Icon
/// owns no HICON that has to be destroyed every time the tray refreshes it.
/// </summary>
internal static class TrayIconRenderer
{
    public static Icon Create(string glyph, Color fill, Color glyphColor)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

            using var background = new SolidBrush(fill);
            graphics.FillEllipse(background, 1, 1, size - 2, size - 2);

            using var font = new Font("Segoe UI", glyph.Length > 1 ? 14f : 17f,
                FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using var text = new SolidBrush(glyphColor);
            graphics.DrawString(glyph, font, text, new RectangleF(0, 0, size, size), format);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        var payload = png.ToArray();

        // ICO header, one directory entry, then a PNG payload (accepted since Vista).
        using var ico = new MemoryStream(22 + payload.Length);
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);            // reserved
            writer.Write((ushort)1);            // icon
            writer.Write((ushort)1);            // one image
            writer.Write((byte)size);           // width
            writer.Write((byte)size);           // height
            writer.Write((byte)0);              // palette size
            writer.Write((byte)0);              // reserved
            writer.Write((ushort)1);            // colour planes
            writer.Write((ushort)32);           // bits per pixel
            writer.Write(payload.Length);
            writer.Write(22);                   // payload offset
            writer.Write(payload);
        }

        ico.Position = 0;
        return new Icon(ico);
    }
}
