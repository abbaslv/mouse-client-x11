using System.Drawing;
using System.Drawing.Drawing2D;
using X11Mouse.Core.Models;

namespace X11Mouse.BatteryTray;

/// <summary>
/// Lightweight borderless dark popup displayed above the Windows taskbar notification area
/// when clicking the X11 battery tray icon.
/// </summary>
public sealed class BatteryFlyoutForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _modeBadge;
    private readonly Label _percentLabel;
    private readonly Label _statusLabel;
    private readonly Label _telemetryLabel;
    private readonly Panel _barPanel;

    private DeviceStatus _status = new();

    public DateTime LastDeactivatedUtc { get; private set; }

    public BatteryFlyoutForm(Action onRescan, Action onOpenControlCenter, Action onExit, Action? onPinTaskbar = null)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(286, 204);
        BackColor = Color.FromArgb(22, 22, 22);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        _titleLabel = new Label
        {
            Text = "ATTACK SHARK X11",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(14, 14),
            AutoSize = true,
        };

        _modeBadge = new Label
        {
            Text = "Отключена",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 180, 180),
            BackColor = Color.FromArgb(36, 36, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(182, 13),
            Size = new Size(90, 22),
        };

        _percentLabel = new Label
        {
            Text = "--%",
            Font = new Font("Segoe UI", 26f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(12, 44),
            Size = new Size(122, 46),
        };

        _statusLabel = new Label
        {
            Text = "Поиск мыши...",
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            ForeColor = Color.FromArgb(195, 195, 195),
            Location = new Point(136, 54),
            Size = new Size(138, 36),
        };

        _barPanel = new Panel
        {
            Location = new Point(14, 96),
            Size = new Size(258, 12),
            BackColor = Color.Transparent,
        };
        _barPanel.Paint += BarPanel_Paint;

        _telemetryLabel = new Label
        {
            Text = "Ожидание телеметрии HID (Report 0x03)",
            Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
            ForeColor = Color.FromArgb(145, 145, 145),
            Location = new Point(14, 114),
            Size = new Size(258, 20),
        };

        var pinLink = new LinkLabel
        {
            Text = "Закрепить на панели задач Windows →",
            Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
            LinkColor = Color.FromArgb(235, 95, 90),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(235, 95, 90),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Location = new Point(14, 136),
            AutoSize = true,
        };
        pinLink.LinkClicked += (_, _) => onPinTaskbar?.Invoke();

        var rescanBtn = CreateActionButton("Обновить", 14, 162, 78, onRescan);
        var openAppBtn = CreateActionButton("Панель X11", 98, 162, 102, onOpenControlCenter);
        var exitBtn = CreateActionButton("Выход", 206, 162, 66, onExit, isDanger: true);

        Controls.AddRange([_titleLabel, _modeBadge, _percentLabel, _statusLabel, _barPanel, _telemetryLabel, pinLink, rescanBtn, openAppBtn, exitBtn]);

        Deactivate += (_, _) =>
        {
            LastDeactivatedUtc = DateTime.UtcNow;
            Hide();
        };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Hide();
        };
    }

    private static Button CreateActionButton(string text, int x, int y, int width, Action onClick, bool isDanger = false)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = isDanger ? Color.FromArgb(62, 24, 24) : Color.FromArgb(36, 36, 36),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = isDanger ? Color.FromArgb(165, 38, 34) : Color.FromArgb(62, 62, 62);
        btn.FlatAppearance.MouseOverBackColor = isDanger ? Color.FromArgb(180, 33, 28) : Color.FromArgb(54, 54, 54);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public void PositionNearTaskbar()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        int x = Math.Clamp(wa.Right - Width - 12, wa.Left + 8, wa.Right - Width - 8);
        int y = Math.Clamp(wa.Bottom - Height - 12, wa.Top + 8, wa.Bottom - Height - 8);
        Location = new Point(x, y);
    }

    public void UpdateStatus(DeviceStatus status)
    {
        _status = status.Clone();

        switch (_status.Mode)
        {
            case ConnectionMode.Wired:
                _modeBadge.Text = "USB Wired";
                _modeBadge.ForeColor = Color.FromArgb(82, 224, 120);
                break;
            case ConnectionMode.Wireless24G:
                _modeBadge.Text = "2.4 GHz";
                _modeBadge.ForeColor = _status.IsAwake ? Color.FromArgb(82, 224, 120) : Color.FromArgb(255, 198, 70);
                break;
            default:
                _modeBadge.Text = "Отключена";
                _modeBadge.ForeColor = Color.FromArgb(165, 165, 165);
                break;
        }

        if (_status.Mode == ConnectionMode.Disconnected)
        {
            _percentLabel.Text = "--%";
            _statusLabel.Text = "Мышь не обнаружена";
            _telemetryLabel.Text = "Подключите кабель или ресивер 2.4G";
        }
        else if (!_status.HasBatteryReading)
        {
            _percentLabel.Text = "?%";
            _statusLabel.Text = "Ожидание пакета заряда...";
            _telemetryLabel.Text = "Пошевелите или нажмите кнопку мыши";
        }
        else if (_status.IsCharging && !_status.IsFullyCharged)
        {
            _percentLabel.Text = $"{_status.BatteryPercent}%";
            _statusLabel.Text = "Идёт зарядка";
            _telemetryLabel.Text = FormatTelemetryLine(_status);
        }
        else if (_status.IsFullyCharged)
        {
            _percentLabel.Text = "100%";
            _statusLabel.Text = "Полностью заряжена";
            _telemetryLabel.Text = FormatTelemetryLine(_status);
        }
        else if (_status.Mode == ConnectionMode.Wireless24G && !_status.IsAwake)
        {
            _percentLabel.Text = $"{_status.BatteryPercent}%";
            _statusLabel.Text = $"Режим сна\n{FormatLastSeen(_status.LastBatteryUpdateUtc)}";
            _telemetryLabel.Text = FormatTelemetryLine(_status);
        }
        else
        {
            _percentLabel.Text = $"{_status.BatteryPercent}%";
            _statusLabel.Text = $"Активна\n{FormatLastSeen(_status.LastBatteryUpdateUtc)}";
            _telemetryLabel.Text = FormatTelemetryLine(_status);
        }

        _barPanel.Invalidate();
    }

    private static string FormatTelemetryLine(DeviceStatus s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.ProductName))
            parts.Add(s.ProductName);
        if (s.ActiveDpiStage is >= 0 and < 8)
            parts.Add($"DPI S{s.ActiveDpiStage + 1}");
        if (s.PollingRate is not null)
        {
            int hz = s.PollingRate switch
            {
                PollingRate.Hz125 => 125,
                PollingRate.Hz250 => 250,
                PollingRate.Hz500 => 500,
                _ => 1000,
            };
            parts.Add($"{hz} Hz");
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "HID 0x1D57";
    }

    private static string FormatLastSeen(DateTime? utc)
    {
        if (utc is null)
            return string.Empty;
        var age = DateTime.UtcNow - utc.Value;
        if (age.TotalSeconds < 60)
            return "обновлено только что";
        int mins = (int)Math.Floor(age.TotalMinutes);
        return $"обновлено {mins} мин назад";
    }

    private void BarPanel_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var trackRect = new RectangleF(0, 1, _barPanel.Width - 1, _barPanel.Height - 2);
        using (var trackBrush = new SolidBrush(Color.FromArgb(42, 42, 42)))
        using (var trackPath = RoundedRect(trackRect, 5))
        {
            g.FillPath(trackBrush, trackPath);
        }

        if (_status.Mode == ConnectionMode.Disconnected || !_status.HasBatteryReading)
            return;

        int pct = Math.Clamp(_status.BatteryPercent, 0, 100);
        float fillW = Math.Max(8f, trackRect.Width * (pct / 100f));
        var fillRect = new RectangleF(trackRect.X, trackRect.Y, fillW, trackRect.Height);

        Color fillColor = _status.IsCharging && !_status.IsFullyCharged
            ? Color.FromArgb(56, 189, 248)
            : pct <= 20
                ? Color.FromArgb(226, 33, 28)
                : pct <= 40
                    ? Color.FromArgb(255, 184, 0)
                    : Color.FromArgb(52, 199, 89);

        using var fillBrush = new SolidBrush(fillColor);
        using var fillPath = RoundedRect(fillRect, 5);
        g.FillPath(fillBrush, fillPath);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var borderPen = new Pen(Color.FromArgb(58, 58, 58), 1f);
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        using var accentBrush = new SolidBrush(Color.FromArgb(226, 33, 28));
        e.Graphics.FillRectangle(accentBrush, 1, 1, Width - 2, 3);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
