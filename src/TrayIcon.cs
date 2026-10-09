using System.Drawing.Text;

namespace AllTemp;

/// <summary>Draws the current CPU temperature as the tray icon itself.</summary>
static class TrayIcon
{
    public static Icon Render(string number, Color color)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var fmt = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            // Largest bold font whose text still fits the icon.
            float px = size;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                var m = g.MeasureString(number, font, PointF.Empty, fmt);
                if ((m.Width <= size + 1 && m.Height <= size * 1.35f) || px <= 6) break;
                font.Dispose();
                px -= 0.5f;
            }
            using (font)
            using (var brush = new SolidBrush(color))
                g.DrawString(number, font, brush, new RectangleF(0, 1, size, size), fmt);
        }
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { Native.DestroyIcon(h); }
    }
}
