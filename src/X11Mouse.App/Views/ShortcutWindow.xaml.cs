using System.Windows;
using System.Windows.Input;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;

namespace X11Mouse.App.Views;

public partial class ShortcutWindow : Window
{
    public ShortcutModifiers Modifiers { get; private set; }
    public byte KeyFwCode { get; private set; }

    public ShortcutWindow(ShortcutModifiers initialMods, byte initialKeyFwCode)
    {
        InitializeComponent();
        Modifiers = initialMods;
        KeyFwCode = initialKeyFwCode;

        CtrlCheck.IsChecked = initialMods.HasFlag(ShortcutModifiers.Ctrl);
        ShiftCheck.IsChecked = initialMods.HasFlag(ShortcutModifiers.Shift);
        AltCheck.IsChecked = initialMods.HasFlag(ShortcutModifiers.Alt);
        WinCheck.IsChecked = initialMods.HasFlag(ShortcutModifiers.Win);

        UpdatePreview();
        Loaded += (_, _) => CaptureSurface.Focus();
    }

    private void ModChanged(object sender, RoutedEventArgs e)
    {
        var mods = ShortcutModifiers.None;
        if (CtrlCheck.IsChecked == true) mods |= ShortcutModifiers.Ctrl;
        if (ShiftCheck.IsChecked == true) mods |= ShortcutModifiers.Shift;
        if (AltCheck.IsChecked == true) mods |= ShortcutModifiers.Alt;
        if (WinCheck.IsChecked == true) mods |= ShortcutModifiers.Win;
        Modifiers = mods;
        UpdatePreview();
    }

    private void CaptureSurface_MouseDown(object sender, MouseButtonEventArgs e) => CaptureSurface.Focus();

    private void CaptureSurface_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        byte fw = KeyCodeTable.VkToFwCode(vk);
        if (fw != 0)
        {
            KeyFwCode = fw;
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        CapturedKeyText.Text = KeyFwCode == 0 ? "Press any key..." : KeyCodeTable.GetDisplayName(KeyFwCode);
        CombinedPreviewText.Text = $"Shortcut: {KeyCodeTable.FormatShortcut(Modifiers, KeyFwCode)}";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CtrlCheck.IsChecked = false;
        ShiftCheck.IsChecked = false;
        AltCheck.IsChecked = false;
        WinCheck.IsChecked = false;
        Modifiers = ShortcutModifiers.None;
        KeyFwCode = 0;
        UpdatePreview();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Modifiers == ShortcutModifiers.None && KeyFwCode == 0)
            return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
