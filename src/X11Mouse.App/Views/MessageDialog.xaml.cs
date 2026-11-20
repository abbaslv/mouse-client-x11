using System.Windows;

namespace X11Mouse.App.Views;

public partial class MessageDialog : Window
{
    public MessageDialog(string title, string message, bool showCancel = false, string okText = "OK")
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = message;
        OkBtn.Content = okText;
        CancelBtn.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    public static bool ShowConfirm(Window owner, string title, string message)
    {
        var dlg = new MessageDialog(title, message, showCancel: true, okText: "Confirm") { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    public static void ShowInfo(Window owner, string title, string message)
    {
        var dlg = new MessageDialog(title, message, showCancel: false, okText: "OK") { Owner = owner };
        dlg.ShowDialog();
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
