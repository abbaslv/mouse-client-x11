using System.Diagnostics;
using System.Drawing;
using X11Mouse.Core.Hid;
using X11Mouse.Core.Models;
using X11Mouse.Core.Services;

namespace X11Mouse.BatteryTray;

/// <summary>
/// Application context hosting the lightweight HID listener and Windows taskbar tray icon.
/// Zero background writes or config mutations are ever performed to the mouse.
/// </summary>
public sealed class BatteryTrayAppContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly TraySettings _settings;
    private readonly Win32HidTransport _transport;
    private readonly MouseDeviceService _deviceService;
    private readonly NotifyIcon _notifyIcon;
    private readonly BatteryFlyoutForm _flyout;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly ToolStripMenuItem _statusHeaderItem;
    private readonly ToolStripMenuItem _styleDigitsItem;
    private readonly ToolStripMenuItem _styleLargeDigitsItem;
    private readonly ToolStripMenuItem _styleGaugeItem;
    private readonly ToolStripMenuItem _lowBatteryAlertItem;
    private readonly ToolStripMenuItem _autoStartItem;

    private Icon? _currentIcon;
    private string? _lastIconKey;
    private bool _lowAlertSent;
    private bool _disposed;

    public BatteryTrayAppContext(bool smokeTestMode = false)
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = TraySettingsStore.Load();
        _transport = new Win32HidTransport();
        _deviceService = new MouseDeviceService(_transport);

        _flyout = new BatteryFlyoutForm(
            onRescan: () => _deviceService.RefreshConnection(),
            onOpenControlCenter: OpenControlCenter,
            onExit: ExitApplication,
            onPinTaskbar: OpenTaskbarSettings);

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };

        _statusHeaderItem = new ToolStripMenuItem("Attack Shark X11: Отключена") { Enabled = false };
        _styleDigitsItem = new ToolStripMenuItem("Цифры и полоса") { Checked = _settings.IconStyle == TrayIconStyle.DigitsWithBar };
        _styleDigitsItem.Click += (_, _) => SetIconStyle(TrayIconStyle.DigitsWithBar);

        _styleLargeDigitsItem = new ToolStripMenuItem("Крупные цифры") { Checked = _settings.IconStyle == TrayIconStyle.LargeDigits };
        _styleLargeDigitsItem.Click += (_, _) => SetIconStyle(TrayIconStyle.LargeDigits);

        _styleGaugeItem = new ToolStripMenuItem("Иконка батареи") { Checked = _settings.IconStyle == TrayIconStyle.BatteryGauge };
        _styleGaugeItem.Click += (_, _) => SetIconStyle(TrayIconStyle.BatteryGauge);

        var styleMenu = new ToolStripMenuItem("Стиль иконки");
        styleMenu.DropDownItems.AddRange([_styleDigitsItem, _styleLargeDigitsItem, _styleGaugeItem]);

        _lowBatteryAlertItem = new ToolStripMenuItem($"Уведомлять при <= {_settings.LowBatteryThreshold}%")
        {
            Checked = _settings.LowBatteryAlertEnabled,
            CheckOnClick = true,
        };
        _lowBatteryAlertItem.CheckedChanged += (_, _) =>
        {
            _settings.LowBatteryAlertEnabled = _lowBatteryAlertItem.Checked;
            TraySettingsStore.Save(_settings);
        };

        _autoStartItem = new ToolStripMenuItem("Автозапуск с Windows")
        {
            Checked = StartupRegistryHelper.IsEnabled(),
            CheckOnClick = true,
        };
        _autoStartItem.CheckedChanged += (_, _) => StartupRegistryHelper.SetEnabled(_autoStartItem.Checked);

        var pinTaskbarItem = new ToolStripMenuItem("Закрепить на панели задач...");
        pinTaskbarItem.Click += (_, _) => OpenTaskbarSettings();

        var rescanItem = new ToolStripMenuItem("Обновить / Найти мышь");
        rescanItem.Click += (_, _) => _deviceService.RefreshConnection();

        var openCenterItem = new ToolStripMenuItem("Открыть панель X11");
        openCenterItem.Click += (_, _) => OpenControlCenter();

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.AddRange(
        [
            _statusHeaderItem,
            new ToolStripSeparator(),
            styleMenu,
            _lowBatteryAlertItem,
            _autoStartItem,
            pinTaskbarItem,
            new ToolStripSeparator(),
            rescanItem,
            openCenterItem,
            new ToolStripSeparator(),
            exitItem,
        ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = !smokeTestMode,
            Text = "Attack Shark X11",
        };
        _notifyIcon.MouseClick += NotifyIcon_MouseClick;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _refreshTimer.Tick += (_, _) => UpdateUiFromStatus(_deviceService.Status);

        _deviceService.StatusChanged += status =>
        {
            _uiContext.Post(_ =>
            {
                if (!_disposed)
                    UpdateUiFromStatus(status);
            }, null);
        };

        UpdateUiFromStatus(_deviceService.Status);

        if (!smokeTestMode)
        {
            _deviceService.Start();
            _refreshTimer.Start();
        }
    }

    public void RunFlyoutSmokeCheck()
    {
        var sample = new DeviceStatus
        {
            Mode = ConnectionMode.Wireless24G,
            IsAwake = true,
            HasBatteryReading = true,
            BatteryPercent = 85,
            LastBatteryUpdateUtc = DateTime.UtcNow,
            ProductName = "2.4G Wireless Device",
        };
        UpdateUiFromStatus(sample);
        _flyout.PositionNearTaskbar();
        _flyout.Show();
        Application.DoEvents();
        _flyout.Hide();
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        if (_flyout.Visible)
        {
            _flyout.Hide();
            return;
        }

        // Prevent immediate reopen when user clicked tray icon to dismiss flyout.
        if ((DateTime.UtcNow - _flyout.LastDeactivatedUtc).TotalMilliseconds < 180)
            return;

        _flyout.UpdateStatus(_deviceService.Status);
        _flyout.PositionNearTaskbar();
        _flyout.Show();
        _flyout.Activate();
    }

    private void SetIconStyle(TrayIconStyle style)
    {
        _settings.IconStyle = style;
        _styleDigitsItem.Checked = style == TrayIconStyle.DigitsWithBar;
        _styleLargeDigitsItem.Checked = style == TrayIconStyle.LargeDigits;
        _styleGaugeItem.Checked = style == TrayIconStyle.BatteryGauge;
        TraySettingsStore.Save(_settings);
        UpdateUiFromStatus(_deviceService.Status);
    }

    private void UpdateUiFromStatus(DeviceStatus status)
    {
        bool isLightTaskbar = TaskbarThemeHelper.IsLightTaskbar();
        string key = $"{(int)status.Mode}|{status.HasBatteryReading}|{status.BatteryPercent}|{status.IsCharging}|{status.IsFullyCharged}|{(int)_settings.IconStyle}|{isLightTaskbar}";

        if (key != _lastIconKey)
        {
            _lastIconKey = key;
            var nextIcon = TrayIconRenderer.RenderIcon(status, _settings.IconStyle, isLightTaskbar);
            var previousIcon = _currentIcon;
            _currentIcon = nextIcon;
            _notifyIcon.Icon = nextIcon;
            previousIcon?.Dispose();
        }

        string statusText = FormatStatusSummary(status);
        _statusHeaderItem.Text = statusText;
        _notifyIcon.Text = statusText.Length > 63 ? statusText[..63] : statusText;

        if (_flyout.Visible)
            _flyout.UpdateStatus(status);

        CheckLowBatteryAlert(status);
    }

    private void CheckLowBatteryAlert(DeviceStatus status)
    {
        if (status.Mode == ConnectionMode.Disconnected)
        {
            _lowAlertSent = false;
            return;
        }

        if (!status.HasBatteryReading || status.IsCharging || status.IsFullyCharged)
            return;

        if (status.BatteryPercent > _settings.LowBatteryThreshold + 5)
        {
            _lowAlertSent = false;
            return;
        }

        if (_settings.LowBatteryAlertEnabled && !_lowAlertSent && status.BatteryPercent <= _settings.LowBatteryThreshold)
        {
            _lowAlertSent = true;
            if (_notifyIcon.Visible)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Attack Shark X11",
                    $"Низкий заряд мыши: {status.BatteryPercent}%",
                    ToolTipIcon.Warning);
            }
        }
    }

    private static string FormatStatusSummary(DeviceStatus s)
    {
        if (s.Mode == ConnectionMode.Disconnected)
            return "X11: Отключена";

        string modeLabel = s.Mode == ConnectionMode.Wired ? "USB" : "2.4G";
        if (!s.HasBatteryReading)
            return $"X11 ({modeLabel}): Ожидание данных";

        if (s.IsCharging && !s.IsFullyCharged)
            return $"X11 ({modeLabel}): Зарядка ({s.BatteryPercent}%)";

        if (s.IsFullyCharged)
            return $"X11 ({modeLabel}): 100% (Заряжена)";

        return s.IsAwake
            ? $"X11 ({modeLabel}): {s.BatteryPercent}%"
            : $"X11 ({modeLabel}): {s.BatteryPercent}% (Сон)";
    }

    private void OpenControlCenter()
    {
        string? exePath = FindControlCenterExe();
        if (exePath is null)
        {
            MessageBox.Show(
                "Не найден исполняемый файл X11Mouse.exe рядом с утилитой или в каталоге сборки.",
                "Attack Shark X11",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch
        {
            // Ignore launch failures silently in tray helper.
        }
    }

    private static void OpenTaskbarSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal if ms-settings handler is unavailable.
        }
    }

    private static string? FindControlCenterExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string sibling = Path.Combine(baseDir, "X11Mouse.exe");
        if (File.Exists(sibling))
            return sibling;

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            string releaseCandidate = Path.Combine(dir.FullName, "src", "X11Mouse.App", "bin", "Release", "net8.0-windows", "X11Mouse.exe");
            if (File.Exists(releaseCandidate))
                return releaseCandidate;

            string debugCandidate = Path.Combine(dir.FullName, "src", "X11Mouse.App", "bin", "Debug", "net8.0-windows", "X11Mouse.exe");
            if (File.Exists(debugCandidate))
                return debugCandidate;

            dir = dir.Parent;
        }

        return null;
    }

    private void ExitApplication()
    {
        DisposeResources();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeResources();
        base.Dispose(disposing);
    }

    private void DisposeResources()
    {
        if (_disposed)
            return;
        _disposed = true;

        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        _flyout.Dispose();
        _deviceService.Dispose();
        _transport.Dispose();
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(145, 145, 145);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => Color.FromArgb(56, 56, 56);
        public override Color MenuItemBorder => Color.FromArgb(226, 33, 28);
        public override Color MenuItemSelected => Color.FromArgb(62, 28, 28);
        public override Color ToolStripDropDownBackground => Color.FromArgb(26, 26, 26);
        public override Color ImageMarginGradientBegin => Color.FromArgb(26, 26, 26);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(26, 26, 26);
        public override Color ImageMarginGradientEnd => Color.FromArgb(26, 26, 26);
        public override Color SeparatorDark => Color.FromArgb(56, 56, 56);
        public override Color CheckBackground => Color.FromArgb(226, 33, 28);
        public override Color CheckSelectedBackground => Color.FromArgb(226, 33, 28);
    }
}
