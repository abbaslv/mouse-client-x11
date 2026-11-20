using System.Windows;
using X11Mouse.Core.Models;

namespace X11Mouse.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(DeviceStatus status)
    {
        InitializeComponent();
        DeviceDetailsText.Text =
            $"Connection : {status.Mode}\n" +
            $"Product    : {status.ProductName}\n" +
            $"VID / PID  : 0x{status.VendorId:X4} / 0x{status.ProductId:X4} (Rev 0x{status.VersionNumber:X4})\n" +
            $"Battery    : {status.BatteryPercent}% (Charging: {status.IsCharging}, Full: {status.IsFullyCharged})\n" +
            $"Awake      : {status.IsAwake}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
