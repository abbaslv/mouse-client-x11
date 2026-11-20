using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using X11Mouse.Core.Models;

namespace X11Mouse.BatteryTray;

/// <summary>
/// Renders razor-sharp native-resolution taskbar notification icons (`SM_CXSMICON`)
/// for Attack Shark X11 battery state without GDI handle leaks.
/// </summary>
public static class TrayIconRenderer
{
    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static int GetNativeSmallIconSize() =>
        Math.Clamp(GetSystemMetrics(SM_CXSMICON), 16, 64);

    public static Icon RenderIcon(DeviceStatus status, TrayIconStyle style, bool isLightTaskbar)
    {
        int size = GetNativeSmallIconSize();
        using var bmp = RenderIconBitmap(status, style, isLightTaskbar, size);

        IntPtr rawHicon = bmp.GetHicon();
        try
        {
            using var transient = Icon.FromHandle(rawHicon);
            return (Icon)transient.Clone();
        }
        finally
        {
            DestroyIcon(rawHicon);
        }
    }

    public static Bitmap RenderIconBitmap(DeviceStatus status, TrayIconStyle style, bool isLightTaskbar, int size)
    {
        size = Math.Clamp(size, 16, 64);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        switch (style)
        {
            case TrayIconStyle.BatteryGauge:
                DrawBatteryGauge(g, size, status, isLightTaskbar);
                break;
            case TrayIconStyle.LargeDigits:
                DrawLargeDigits(g, size, status, isLightTaskbar);
                break;
            default:
                DrawDigitsWithBar(g, size, status, isLightTaskbar);
                break;
        }

        return bmp;
    }

    private static void DrawDigitsWithBar(Graphics g, int size, DeviceStatus status, bool isLightTaskbar)
    {
        Color primaryText = isLightTaskbar ? Color.FromArgb(20, 20, 20) : Color.White;
        Color mutedText = isLightTaskbar ? Color.FromArgb(110, 110, 110) : Color.FromArgb(185, 185, 185);
        Color barTrack = isLightTaskbar ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(75, 255, 255, 255);

        int barH = Math.Clamp((int)Math.Round(size * 0.16), 3, 5);
        float bottomPad = 1f;
        float gap = size >= 24 ? 2f : 1.5f;
        float textAreaH = size - barH - bottomPad - gap;

        var textBox = new RectangleF(0.25f, 0.5f, size - 0.5f, textAreaH);

        Color barColor;
        double fillRatio;

        if (status.Mode == ConnectionMode.Disconnected)
        {
            DrawTightGlyphs(g, "×", textBox, mutedText);
            barColor = Color.FromArgb(125, 125, 125);
            fillRatio = 0;
        }
        else if (!status.HasBatteryReading)
        {
            DrawTightGlyphs(g, "?", textBox, primaryText);
            barColor = Color.FromArgb(150, 165, 185);
            fillRatio = 1.0;
        }
        else if (status.IsCharging && !status.IsFullyCharged)
        {
            DrawBolt(g, new RectangleF(size * 0.24f, 0.5f, size * 0.52f, textAreaH - 0.5f),
                Color.FromArgb(56, 189, 248));
            barColor = Color.FromArgb(56, 189, 248);
            fillRatio = Math.Clamp(status.BatteryPercent / 100.0, 0.12, 1.0);
        }
        else
        {
            int pct = Math.Clamp(status.BatteryPercent, 0, 100);
            // Keep bright pure contrast even during idle sleep so taskbar digits match Windows icons.
            DrawTightGlyphs(g, pct.ToString(), textBox, primaryText);
            barColor = GetVividBatteryColor(pct);
            fillRatio = Math.Clamp(pct / 100.0, 0.10, 1.0);
        }

        // Crisp bottom bar anchored cleanly to bottom edge
        float sidePad = 0.5f;
        float barY = size - barH - bottomPad;
        var trackRect = new RectangleF(sidePad, barY, size - sidePad * 2f, barH);

        using (var trackPath = CreateRoundedRect(trackRect, barH / 2f))
        using (var trackBrush = new SolidBrush(barTrack))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (fillRatio > 0)
        {
            float fillW = Math.Max(barH, (float)Math.Round(trackRect.Width * fillRatio));
            var fillRect = new RectangleF(trackRect.X, trackRect.Y, Math.Min(fillW, trackRect.Width), barH);
            using var fillPath = CreateRoundedRect(fillRect, barH / 2f);
            using var fillBrush = new SolidBrush(barColor);
            g.FillPath(fillBrush, fillPath);
        }
    }

    private static void DrawLargeDigits(Graphics g, int size, DeviceStatus status, bool isLightTaskbar)
    {
        Color neutralText = isLightTaskbar ? Color.FromArgb(20, 20, 20) : Color.White;
        Color mutedText = isLightTaskbar ? Color.FromArgb(110, 110, 110) : Color.FromArgb(175, 175, 175);
        var box = new RectangleF(0.5f, size * 0.12f, size - 1f, size * 0.76f);

        if (status.Mode == ConnectionMode.Disconnected)
        {
            DrawTightGlyphs(g, "×", box, mutedText);
            return;
        }

        if (!status.HasBatteryReading)
        {
            DrawTightGlyphs(g, "?", box, neutralText);
            return;
        }

        if (status.IsCharging && !status.IsFullyCharged)
        {
            DrawBolt(g, new RectangleF(size * 0.22f, size * 0.08f, size * 0.56f, size * 0.84f),
                Color.FromArgb(56, 189, 248));
            return;
        }

        int pct = Math.Clamp(status.BatteryPercent, 0, 100);
        Color color = pct <= 20
            ? Color.FromArgb(255, 82, 74)
            : pct <= 40
                ? Color.FromArgb(255, 200, 40)
                : neutralText;
        DrawTightGlyphs(g, pct.ToString(), box, color);
    }

    private static void DrawBatteryGauge(Graphics g, int size, DeviceStatus status, bool isLightTaskbar)
    {
        Color border = isLightTaskbar ? Color.FromArgb(25, 25, 25) : Color.White;
        Color muted = isLightTaskbar ? Color.FromArgb(125, 125, 125) : Color.FromArgb(165, 165, 165);

        float pad = Math.Max(1f, size * 0.06f);
        float penW = Math.Max(1.5f, size * 0.085f);
        float nubW = Math.Max(2f, size * 0.10f);
        float bodyW = size - pad * 2f - nubW - 1f;
        float bodyH = size * 0.56f;
        float bodyX = pad;
        float bodyY = (size - bodyH) / 2f;

        var bodyRect = new RectangleF(bodyX, bodyY, bodyW, bodyH);
        using (var framePath = CreateRoundedRect(bodyRect, size * 0.10f))
        using (var framePen = new Pen(status.Mode == ConnectionMode.Disconnected ? muted : border, penW))
        {
            g.DrawPath(framePen, framePath);
        }

        float nubH = bodyH * 0.48f;
        var nubRect = new RectangleF(bodyRect.Right + penW * 0.4f, bodyY + (bodyH - nubH) / 2f, nubW, nubH);
        using (var nubBrush = new SolidBrush(status.Mode == ConnectionMode.Disconnected ? muted : border))
        {
            g.FillRectangle(nubBrush, nubRect);
        }

        if (status.Mode == ConnectionMode.Disconnected)
        {
            DrawTightGlyphs(g, "×", new RectangleF(bodyRect.X + 2f, bodyRect.Y + 2f, bodyRect.Width - 4f, bodyRect.Height - 4f), muted);
            return;
        }

        if (!status.HasBatteryReading)
        {
            DrawTightGlyphs(g, "?", new RectangleF(bodyRect.X + 2f, bodyRect.Y + 2f, bodyRect.Width - 4f, bodyRect.Height - 4f), border);
            return;
        }

        int pct = Math.Clamp(status.BatteryPercent, 0, 100);
        float inset = penW * 0.95f;
        float innerW = Math.Max(2f, bodyRect.Width - inset * 2f);
        float fillW = (float)(innerW * Math.Clamp(pct / 100.0, 0.12, 1.0));
        var innerRect = new RectangleF(bodyRect.X + inset, bodyRect.Y + inset, fillW, bodyRect.Height - inset * 2f);

        Color fillColor = status.IsCharging && !status.IsFullyCharged
            ? Color.FromArgb(56, 189, 248)
            : GetVividBatteryColor(pct);
        using (var innerBrush = new SolidBrush(fillColor))
        {
            g.FillRectangle(innerBrush, innerRect);
        }

        if (status.IsCharging && !status.IsFullyCharged)
        {
            DrawBolt(g, new RectangleF(bodyRect.X + bodyRect.Width * 0.25f, bodyRect.Y - size * 0.06f,
                bodyRect.Width * 0.50f, bodyRect.Height + size * 0.12f), Color.White);
        }
    }

    /// <summary>
    /// Measures exact glyph ink bounds via GraphicsPath and scales snugly into `targetBox`
    /// so small tray numbers (including "100") stay crisp, bright, and unclipped at 16px-32px.
    /// </summary>
    private static void DrawTightGlyphs(Graphics g, string text, RectangleF targetBox, Color color)
    {
        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
        };

        // Render baseline path at large em size for exact tight ink bounds measurement.
        path.AddString(text, family, (int)FontStyle.Bold, 48f, PointF.Empty, sf);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0.1f || bounds.Height <= 0.1f)
            return;

        float scaleY = targetBox.Height / bounds.Height;
        float scaleX = targetBox.Width / bounds.Width;
        float uniform = Math.Min(scaleX, scaleY);

        // Allow very slight horizontal tightening for 3-digit "100" so height remains tall & readable.
        float chosenScaleX = text.Length >= 3 ? Math.Min(scaleX, uniform * 1.12f) : uniform;
        float chosenScaleY = text.Length >= 3 ? Math.Min(scaleY, chosenScaleX * 1.18f) : uniform;

        float drawW = bounds.Width * chosenScaleX;
        float drawH = bounds.Height * chosenScaleY;
        float offsetX = targetBox.X + (targetBox.Width - drawW) / 2f;
        float offsetY = targetBox.Y + (targetBox.Height - drawH) / 2f;

        using var matrix = new Matrix();
        matrix.Translate(-bounds.X, -bounds.Y);
        matrix.Scale(chosenScaleX, chosenScaleY, MatrixOrder.Append);
        matrix.Translate(offsetX, offsetY, MatrixOrder.Append);
        path.Transform(matrix);

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static Color GetVividBatteryColor(int pct) => pct <= 20
        ? Color.FromArgb(255, 69, 58)
        : pct <= 40
            ? Color.FromArgb(255, 196, 20)
            : Color.FromArgb(52, 215, 95);

    private static void DrawBolt(Graphics g, RectangleF r, Color color)
    {
        PointF[] pts =
        [
            new(r.Left + r.Width * 0.60f, r.Top),
            new(r.Left + r.Width * 0.14f, r.Top + r.Height * 0.54f),
            new(r.Left + r.Width * 0.48f, r.Top + r.Height * 0.54f),
            new(r.Left + r.Width * 0.34f, r.Bottom),
            new(r.Left + r.Width * 0.86f, r.Top + r.Height * 0.44f),
            new(r.Left + r.Width * 0.52f, r.Top + r.Height * 0.44f),
        ];
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
    }

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (d <= 0.1f)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
