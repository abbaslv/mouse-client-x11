using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace X11Mouse.App.Services;

/// <summary>
/// Manages the clean-room Windows system tray icon and context menu for background monitoring.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly WinForms.ToolStripMenuItem _profilesMenu;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<int>? ProfileSwitchRequested;

    public TrayIconService()
    {
        _trayIcon = CreateCleanRoomIcon();
        _profilesMenu = new WinForms.ToolStripMenuItem("Profiles");

        var menu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem("Open X11 Control Center");
        showItem.Click += (_, _) => ShowRequested?.Invoke();

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(showItem);
        menu.Items.Add(_profilesMenu);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Attack Shark X11 Mouse",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void UpdateProfiles(IReadOnlyList<string> profileNames, int activeIndex)
    {
        _profilesMenu.DropDownItems.Clear();
        for (int i = 0; i < profileNames.Count; i++)
        {
            int captured = i;
            var item = new WinForms.ToolStripMenuItem(profileNames[i])
            {
                Checked = i == activeIndex,
            };
            item.Click += (_, _) => ProfileSwitchRequested?.Invoke(captured);
            _profilesMenu.DropDownItems.Add(item);
        }
    }

    public void UpdateTooltip(string statusText)
    {
        string truncated = statusText.Length > 63 ? statusText[..63] : statusText;
        _notifyIcon.Text = truncated;
    }

    private static Icon CreateCleanRoomIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgBrush = new SolidBrush(Color.FromArgb(20, 20, 20));
        using var rimPen = new Pen(Color.FromArgb(226, 33, 28), 2.2f);
        g.FillEllipse(bgBrush, 2, 2, 28, 28);
        g.DrawEllipse(rimPen, 2, 2, 28, 28);

        using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("X11", font, textBrush, new RectangleF(1, 1, 30, 30), sf);

        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }
}
