using System.Windows;
using System.Windows.Input;

namespace X11Mouse.App.Views;

public partial class ProfileNameWindow : Window
{
    public string ResultName { get; private set; } = string.Empty;

    public ProfileNameWindow(string title, string initialValue)
    {
        InitializeComponent();
        TitleText.Text = title;
        NameBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string trimmed = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;
        ResultName = trimmed;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
