using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HyperXBatteryHID.Ui;

internal static class BatteryTrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<(int PercentKey, bool IsCharging), Icon> _cache = new();

    /// <summary>
    /// Returns a cached icon instance for the given state. Cache is kept for the lifetime of the app.
    /// </summary>
    public static Icon GetIcon(int? percent, bool isCharging)
    {
        var pKey = percent is >= 0 and <= 100 ? percent.Value : -1;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue((pKey, isCharging), out var cached))
                return cached;

            var created = CreateIcon(percent, isCharging);
            _cache[(pKey, isCharging)] = created;
            return created;
        }
    }

    /// <summary>
    /// Dispose cached icon handles. Call on app shutdown.
    /// </summary>
    public static void DisposeCache()
    {
        lock (_cacheLock)
        {
            foreach (var kv in _cache)
            {
                try { kv.Value.Dispose(); } catch { /* ignore */ }
            }
            _cache.Clear();
        }
    }

    private static Icon CreateIcon(int? percent, bool isCharging)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

        // High-contrast icon: solid background + outlined number + optional charging bolt.
        var hasPercent = percent is >= 0 and <= 100;
        var p = hasPercent ? percent!.Value : (int?)null;

        // Background color conveys status; text stays readable in both light/dark taskbars.
        var bg = !hasPercent ? Color.FromArgb(235, 60, 60, 60)
            : p <= 15 ? Color.FromArgb(235, 170, 35, 35)
            : p <= 35 ? Color.FromArgb(235, 190, 140, 20)
            : Color.FromArgb(235, 20, 140, 60);

        var bgRect = new Rectangle(0, 0, 16, 16);
        using (var bgBrush = new SolidBrush(bg))
            g.FillRectangle(bgBrush, bgRect);
        using (var borderPen = new Pen(Color.FromArgb(200, 0, 0, 0), 1))
            g.DrawRectangle(borderPen, 0, 0, 15, 15);

        var text = hasPercent ? p!.Value.ToString() : "--";

        // Auto-fit text into the available box (16x16 minus a small margin).
        // This is more reliable than guessing sizes by digit count across Windows font metrics.
        // When charging, reserve top-right space for the bolt so it doesn't collide with the digits.
        var boltRect = isCharging ? new RectangleF(11.0f, 0.5f, 4.5f, 7.5f) : RectangleF.Empty;
        var textRect = isCharging ? new RectangleF(-1.0f, 0, 16, 16) : new RectangleF(0, 0, 16, 16);
        var fitRect = isCharging
            ? new RectangleF(0.5f, 0.5f, 11.0f, 15.0f)
            : new RectangleF(0.5f, 0.5f, 15.0f, 15.0f);
        using var font = CreateFittedFont(g, text, "Segoe UI", FontStyle.Bold, fitRect, maxPt: 10.0f, minPt: 5.5f);

        // Draw outlined text (stroke simulated by offsets) for maximum contrast.
        DrawCenteredOutlined(g, text, font, fill: Color.White, outline: Color.Black, textRect);

        if (isCharging)
        {
            // Small lightning bolt overlay in the top-right (inside reserved area).
            DrawChargingBolt(g, boltRect);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            // Clone to detach from the HICON handle we will destroy
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            return icon;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect, float dx, float dy)
    {
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var r = rect;
        r.Offset(dx, dy);
        g.DrawString(text, font, brush, r, sf);
    }

    private static void DrawCenteredOutlined(Graphics g, string text, Font font, Color fill, Color outline, RectangleF rect)
    {
        using var fillBrush = new SolidBrush(fill);
        using var outlineBrush = new SolidBrush(outline);

        // Outline (8 directions) then fill.
        const float o = 0.75f;
        DrawCentered(g, text, font, outlineBrush, rect, -o, 0f);
        DrawCentered(g, text, font, outlineBrush, rect, o, 0f);
        DrawCentered(g, text, font, outlineBrush, rect, 0f, -o);
        DrawCentered(g, text, font, outlineBrush, rect, 0f, o);
        DrawCentered(g, text, font, outlineBrush, rect, -o, -o);
        DrawCentered(g, text, font, outlineBrush, rect, o, -o);
        DrawCentered(g, text, font, outlineBrush, rect, -o, o);
        DrawCentered(g, text, font, outlineBrush, rect, o, o);
        DrawCentered(g, text, font, fillBrush, rect, 0f, 0f);
    }

    private static Font CreateFittedFont(Graphics g, string text, string family, FontStyle style, RectangleF fitRect, float maxPt, float minPt)
    {
        // Measure using typographic settings so we don't over-estimate widths.
        using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= StringFormatFlags.NoWrap;
        sf.Trimming = StringTrimming.None;

        // Leave a small safety margin for outline and pixel rounding.
        var maxW = Math.Max(1f, fitRect.Width - 1.5f);
        var maxH = Math.Max(1f, fitRect.Height - 1.0f);

        for (var size = maxPt; size >= minPt; size -= 0.25f)
        {
            var f = new Font(family, size, style, GraphicsUnit.Point);
            var measured = g.MeasureString(text, f, new SizeF(100, 100), sf);
            if (measured.Width <= maxW && measured.Height <= maxH)
                return f;

            f.Dispose();
        }

        return new Font(family, minPt, style, GraphicsUnit.Point);
    }

    private static void DrawChargingBolt(Graphics g, RectangleF rect)
    {
        // Simple bolt polygon sized to fit within 'rect', drawn with a dark outline for contrast.
        // Coordinates are expressed in 0..1 and then scaled into the rectangle.
        static PointF P(RectangleF r, float x, float y) => new(r.Left + (r.Width * x), r.Top + (r.Height * y));

        var pts = new[]
        {
            P(rect, 0.65f, 0.00f),
            P(rect, 0.15f, 0.55f),
            P(rect, 0.50f, 0.55f),
            P(rect, 0.30f, 1.00f),
            P(rect, 1.00f, 0.50f),
            P(rect, 0.65f, 0.50f),
        };

        using var fill = new SolidBrush(Color.FromArgb(245, 255, 220, 50));
        using var outline = new Pen(Color.FromArgb(220, 0, 0, 0), 1);
        g.FillPolygon(fill, pts);
        g.DrawPolygon(outline, pts);
    }
}


