using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.App.Views;

public partial class ColorPickerWindow : Window
{
    private bool _updating;
    public RgbColor SelectedColor { get; private set; }

    public ColorPickerWindow(RgbColor initial)
    {
        InitializeComponent();
        SelectedColor = initial;
        PopulatePresets();
        SetRgb(initial);
    }

    private void PopulatePresets()
    {
        foreach (var preset in X11Protocol.DefaultDpiColors)
        {
            var btn = new Button
            {
                Width = 34,
                Height = 24,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(Color.FromRgb(preset.R, preset.G, preset.B)),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = preset,
            };
            btn.Click += (s, _) =>
            {
                if (s is Button b && b.Tag is RgbColor c)
                    SetRgb(c);
            };
            PresetGrid.Children.Add(btn);
        }
    }

    private void SetRgb(RgbColor c)
    {
        _updating = true;
        SelectedColor = c;
        RSlider.Value = c.R;
        GSlider.Value = c.G;
        BSlider.Value = c.B;
        RValText.Text = c.R.ToString();
        GValText.Text = c.G.ToString();
        BValText.Text = c.B.ToString();
        HexBox.Text = c.ToHex();
        PreviewSwatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        _updating = false;
    }

    private void RgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        SetRgb(new RgbColor((byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value));
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        SetRgb(FromHsv(HueSlider.Value, 1.0, 1.0));
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        string raw = HexBox.Text.Trim().TrimStart('#');
        if (raw.Length == 6 && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
        {
            SetRgb(new RgbColor((byte)((val >> 16) & 0xFF), (byte)((val >> 8) & 0xFF), (byte)(val & 0xFF)));
        }
    }

    private static RgbColor FromHsv(double hue, double sat, double val)
    {
        int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);
        double v = val * 255;
        byte vB = (byte)v;
        byte p = (byte)(v * (1 - sat));
        byte q = (byte)(v * (1 - f * sat));
        byte t = (byte)(v * (1 - (1 - f) * sat));

        return hi switch
        {
            0 => new RgbColor(vB, t, p),
            1 => new RgbColor(q, vB, p),
            2 => new RgbColor(p, vB, t),
            3 => new RgbColor(p, q, vB),
            4 => new RgbColor(t, p, vB),
            _ => new RgbColor(vB, p, q),
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
