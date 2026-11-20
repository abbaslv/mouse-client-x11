using System;
using System.Collections.Generic;
using System.Windows;
using X11Mouse.Core.Models;

namespace X11Mouse.App.Views;

public partial class SelectMacroWindow : Window
{
    public int SelectedMacroIndex { get; private set; }
    public MacroDefinition? SelectedMacro { get; private set; }
    public MacroCycleMode SelectedCycleMode { get; private set; } = MacroCycleMode.LoopCount;
    public uint SelectedRepeatCount { get; private set; } = 1;

    public SelectMacroWindow(
        IReadOnlyList<MacroDefinition> macros,
        int initialIndex,
        MacroCycleMode initialCycleMode,
        uint initialRepeat)
    {
        InitializeComponent();
        MacroList.ItemsSource = macros;
        if (macros.Count > 0)
            MacroList.SelectedIndex = Math.Clamp(initialIndex, 0, macros.Count - 1);

        RepeatBox.Text = Math.Clamp(initialRepeat, 1u, 255u).ToString();
        switch (initialCycleMode)
        {
            case MacroCycleMode.StopOnAnyKey: CycleAnyKeyRadio.IsChecked = true; break;
            case MacroCycleMode.HoldWhilePressed: CycleHoldRadio.IsChecked = true; break;
            default: CycleLoopRadio.IsChecked = true; break;
        }
        UpdateRepeatEnabled();
    }

    private void CycleChanged(object sender, RoutedEventArgs e) => UpdateRepeatEnabled();

    private void UpdateRepeatEnabled()
    {
        if (RepeatBox is not null && CycleLoopRadio is not null)
            RepeatBox.IsEnabled = CycleLoopRadio.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (MacroList.SelectedItem is not MacroDefinition macro)
            return;

        SelectedMacroIndex = MacroList.SelectedIndex;
        SelectedMacro = macro;
        SelectedCycleMode = CycleAnyKeyRadio.IsChecked == true
            ? MacroCycleMode.StopOnAnyKey
            : CycleHoldRadio.IsChecked == true
                ? MacroCycleMode.HoldWhilePressed
                : MacroCycleMode.LoopCount;

        if (!uint.TryParse(RepeatBox.Text.Trim(), out uint reps))
            reps = 1;
        SelectedRepeatCount = Math.Clamp(reps, 1u, 255u);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
