using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace AllTemp;

/// <summary>Draws the current CPU temperature as the tray icon itself.</summary>
static class TrayIcon
{
    public static Icon Render(string number, Color color, int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // no ClearType: it needs an opaque background

            // Dark rounded badge so the number reads on both light and dark taskbars.
            using (var badge = RoundRect(new RectangleF(0, 0, size, size), size * 0.22f))
            using (var bg = new SolidBrush(Color.FromArgb(235, 24, 24, 24)))
                g.FillPath(bg, badge);

            // Fit the digits as a path, scaled to fill the badge width.
            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            path.AddString(number, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float pad = size * 0.08f;
            float scale = Math.Min((size - 2 * pad) / b.Width, (size - 2 * pad) / b.Height);
            using var m = new Matrix();
            m.Translate(size / 2f, size / 2f);
            m.Scale(scale, scale);
            m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
            path.Transform(m);
            using var fg = new SolidBrush(color);
            g.FillPath(fg, path);
        }
        return FromBitmap(bmp);
    }

    /// <summary>Wraps the bitmap as a PNG-in-ICO stream so the Icon owns its pixels (keeps alpha, no HICON juggling).</summary>
    static Icon FromBitmap(Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);                        // reserved
            w.Write((short)1);                        // type: icon
            w.Write((short)1);                        // image count
            w.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
            w.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
            w.Write((byte)0);                         // palette
            w.Write((byte)0);                         // reserved
            w.Write((short)1);                        // planes
            w.Write((short)32);                       // bpp
            w.Write((int)png.Length);
            w.Write(6 + 16);                          // data offset
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico, bmp.Width, bmp.Height);
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
