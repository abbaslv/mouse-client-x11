using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using X11Mouse.Core.Models;

namespace X11Mouse.App.Views;

public partial class DpiOsdWindow : Window
{
    private readonly DispatcherTimer _hideTimer;

    public DpiOsdWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void ShowDpi(int stageIndex, int dpiValue, RgbColor stageColor)
    {
        StageDot.Fill = new SolidColorBrush(Color.FromRgb(stageColor.R, stageColor.G, stageColor.B));
        DpiLabel.Text = $"{dpiValue} DPI (S{stageIndex + 1})";

        var area = SystemParameters.WorkArea;
        if (!IsVisible)
            Show();
        UpdateLayout();
        Left = area.Right - ActualWidth - 20;
        Top = area.Bottom - ActualHeight - 20;

        _hideTimer.Stop();
        _hideTimer.Start();
    }
}
