using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using X11Mouse.Core.Hooks;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;
using X11Mouse.Core.Storage;

namespace X11Mouse.App.Views;

public sealed record MacroActionRow(int Index, string KeyName, string StateName, ushort DelayMs);

public partial class MacroManagerWindow : Window
{
    private readonly List<MacroDefinition> _macros;
    private readonly ProfileRepository _repository;
    private readonly GlobalInputRecorder _recorder = new();
    private readonly ObservableCollection<MacroActionRow> _rows = new();

    public MacroManagerWindow(List<MacroDefinition> macros, ProfileRepository repository)
    {
        InitializeComponent();
        _macros = macros;
        _repository = repository;

        ActionListView.ItemsSource = _rows;
        _recorder.ActionRecorded += OnActionRecorded;
        _recorder.ShouldIgnoreMouseClick = IsPointInsideRecordButton;

        RefreshMacroList(0);
        Closing += (_, _) => _recorder.Dispose();
    }

    private MacroDefinition? CurrentMacro => MacroListBox.SelectedItem as MacroDefinition;

    private bool IsPointInsideRecordButton(int screenX, int screenY)
    {
        if (!RecordButton.IsVisible)
            return false;

        try
        {
            var topLeft = RecordButton.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelperDpiScale();
            double width = RecordButton.ActualWidth * dpi;
            double height = RecordButton.ActualHeight * dpi;
            return screenX >= topLeft.X && screenX <= topLeft.X + width &&
                   screenY >= topLeft.Y && screenY <= topLeft.Y + height;
        }
        catch
        {
            return false;
        }
    }

    private double VisualTreeHelperDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private void RefreshMacroList(int selectIdx)
    {
        MacroListBox.ItemsSource = null;
        MacroListBox.ItemsSource = _macros;
        if (_macros.Count > 0)
            MacroListBox.SelectedIndex = Math.Clamp(selectIdx, 0, _macros.Count - 1);
        RefreshActionsView();
    }

    private void RefreshActionsView(int selectIdx = -1)
    {
        _rows.Clear();
        var m = CurrentMacro;
        if (m is null)
        {
            ActionCountText.Text = "Actions (0 / 46)";
            return;
        }

        for (int i = 0; i < m.Actions.Count; i++)
        {
            var a = m.Actions[i];
            _rows.Add(new MacroActionRow(
                i + 1,
                KeyCodeTable.GetDisplayName(a.FwCode),
                a.State == MacroActionState.Down ? "Down" : "Up",
                a.DelayMs));
        }

        ActionCountText.Text = $"Actions ({m.Actions.Count} / {MacroDefinition.MaxActions})";
        if (selectIdx >= 0 && selectIdx < _rows.Count)
            ActionListView.SelectedIndex = selectIdx;
    }

    private void MacroListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshActionsView();

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        int n = _macros.Count + 1;
        string suggestion = $"Macro{n}";
        var dlg = new ProfileNameWindow("New Macro", suggestion) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _macros.Add(new MacroDefinition { Name = dlg.ResultName, RepeatCount = 1 });
            RefreshMacroList(_macros.Count - 1);
        }
    }

    private void RenameMacro_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro is null) return;
        var dlg = new ProfileNameWindow("Rename Macro", CurrentMacro.Name) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            CurrentMacro.Name = dlg.ResultName;
            RefreshMacroList(MacroListBox.SelectedIndex);
        }
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro is null) return;
        if (_macros.Count <= 1)
        {
            MessageDialog.ShowInfo(this, "Macro Editor", "At least one macro entry must remain.");
            return;
        }
        int idx = MacroListBox.SelectedIndex;
        _macros.RemoveAt(idx);
        RefreshMacroList(Math.Max(0, idx - 1));
    }

    private void ImportMacro_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "Macro Files (*.macro;*.json)|*.macro;*.json|All Files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true)
            return;

        try
        {
            var imported = _repository.ImportMacroFile(ofd.FileName);
            foreach (var m in imported)
                _macros.Add(m);
            RefreshMacroList(Math.Max(0, _macros.Count - 1));
            StatusText.Text = $"Imported {imported.Count} macro(s).";
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Import Failed", ex.Message);
        }
    }

    private void ExportMacro_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro is null) return;
        var sfd = new SaveFileDialog
        {
            Filter = "Official Macro (*.macro)|*.macro|JSON Macro (*.json)|*.json",
            FileName = $"{CurrentMacro.Name}.macro",
        };
        if (sfd.ShowDialog(this) != true)
            return;

        try
        {
            _repository.ExportMacroFile(CurrentMacro, sfd.FileName);
            StatusText.Text = $"Exported '{CurrentMacro.Name}'.";
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Export Failed", ex.Message);
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro is null) return;

        if (!_recorder.IsRecording)
        {
            if (CurrentMacro.Actions.Count >= MacroDefinition.MaxActions)
            {
                MessageDialog.ShowInfo(this, "Macro Full", $"Maximum of {MacroDefinition.MaxActions} actions reached.");
                return;
            }

            _recorder.DelayMode = DefaultDelayRadio.IsChecked == true
                ? MacroDelayMode.DefaultDelay
                : NoDelayRadio.IsChecked == true
                    ? MacroDelayMode.NoDelay
                    : MacroDelayMode.RecordDelay;

            if (ushort.TryParse(DefaultDelayBox.Text.Trim(), out ushort defMs))
                _recorder.DefaultDelayMs = Math.Clamp(defMs, (ushort)1, (ushort)65000);

            _recorder.Start();
            RecordButton.Content = "■ Stop Record";
            StatusText.Text = "Recording keyboard & mouse actions... Click Stop Record when finished.";
        }
        else
        {
            StopRecording();
        }
    }

    private void StopRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            RecordButton.Content = "● Start Record";
            StatusText.Text = $"Recording stopped ({CurrentMacro?.Actions.Count ?? 0} actions).";
        }
    }

    private void OnActionRecorded(MacroAction action)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (CurrentMacro is null || !_recorder.IsRecording)
                return;

            if (CurrentMacro.Actions.Count >= MacroDefinition.MaxActions)
            {
                StopRecording();
                return;
            }

            CurrentMacro.Actions.Add(action);
            RefreshActionsView(CurrentMacro.Actions.Count - 1);
            if (CurrentMacro.Actions.Count >= MacroDefinition.MaxActions)
                StopRecording();
        });
    }

    private void DelayMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_recorder is null || DefaultDelayBox is null) return;
        DefaultDelayBox.IsEnabled = DefaultDelayRadio.IsChecked == true;
    }

    private void InsertMouseLeft_Click(object sender, RoutedEventArgs e) => AppendClickPair(KeyCodeTable.MouseLeftFw);
    private void InsertMouseRight_Click(object sender, RoutedEventArgs e) => AppendClickPair(KeyCodeTable.MouseRightFw);

    private void AppendClickPair(byte fwCode)
    {
        if (CurrentMacro is null || CurrentMacro.Actions.Count + 2 > MacroDefinition.MaxActions) return;
        CurrentMacro.Actions.Add(new MacroAction { FwCode = fwCode, State = MacroActionState.Down, DelayMs = 20 });
        CurrentMacro.Actions.Add(new MacroAction { FwCode = fwCode, State = MacroActionState.Up, DelayMs = 20 });
        RefreshActionsView(CurrentMacro.Actions.Count - 1);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = ActionListView.SelectedIndex;
        if (CurrentMacro is null || idx <= 0) return;
        (CurrentMacro.Actions[idx - 1], CurrentMacro.Actions[idx]) = (CurrentMacro.Actions[idx], CurrentMacro.Actions[idx - 1]);
        RefreshActionsView(idx - 1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = ActionListView.SelectedIndex;
        if (CurrentMacro is null || idx < 0 || idx >= CurrentMacro.Actions.Count - 1) return;
        (CurrentMacro.Actions[idx + 1], CurrentMacro.Actions[idx]) = (CurrentMacro.Actions[idx], CurrentMacro.Actions[idx + 1]);
        RefreshActionsView(idx + 1);
    }

    private void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        int idx = ActionListView.SelectedIndex;
        if (CurrentMacro is null || idx < 0) return;
        CurrentMacro.Actions.RemoveAt(idx);
        RefreshActionsView(Math.Min(idx, CurrentMacro.Actions.Count - 1));
    }

    private void ClearActions_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentMacro is null) return;
        CurrentMacro.Actions.Clear();
        RefreshActionsView();
    }

    private void UpdateSelectedDelay_Click(object sender, RoutedEventArgs e)
    {
        int idx = ActionListView.SelectedIndex;
        if (CurrentMacro is null || idx < 0) return;
        if (ushort.TryParse(EditDelayBox.Text.Trim(), out ushort ms))
        {
            CurrentMacro.Actions[idx].DelayMs = Math.Max((ushort)1, ms);
            RefreshActionsView(idx);
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        DialogResult = true;
        Close();
    }
}
