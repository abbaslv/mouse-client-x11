using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using X11Mouse.App.Views;
using X11Mouse.Core.Hid;
using X11Mouse.Core.Models;
using X11Mouse.Core.Protocol;
using X11Mouse.Core.Services;
using X11Mouse.Core.Storage;

namespace X11Mouse.App;

public partial class MainWindow : Window
{
    private sealed record ButtonUiSlot(int DisplayNum, int SlotIndex, string DefaultTitle);

    private static readonly ButtonUiSlot[] PhysicalButtons =
    [
        new(1, 0, "Left Click"),
        new(2, 1, "Right Click"),
        new(3, 2, "Middle Wheel"),
        new(4, 6, "Forward Side"),
        new(5, 7, "Backward Side"),
    ];

    private static readonly string[] LightModeLabels =
    [
        "LED Off",
        "Static",
        "Breathing",
        "Neon",
        "Color Breathing",
        "Static DPI",
        "Breathing DPI",
    ];

    private readonly ProfileRepository _repository;
    private readonly AppConfigStore _store;
    private readonly Win32HidTransport _transport;
    private readonly MouseDeviceService _deviceService;
    private readonly DpiOsdWindow _dpiOsd = new();
    private readonly DispatcherTimer _autoApplyTimer;
    private readonly DispatcherTimer _chargingAnimTimer;
    private readonly bool _smokeTestMode;

    private readonly Dictionary<int, Border> _buttonRowBorders = new();
    private readonly Dictionary<int, TextBlock> _buttonRowActionLabels = new();
    private readonly Dictionary<int, Border> _dpiCardBorders = new();
    private readonly Dictionary<int, CheckBox> _dpiCardChecks = new();
    private readonly Dictionary<int, Border> _dpiCardColorBoxes = new();
    private readonly Dictionary<int, TextBlock> _dpiCardValLabels = new();

    private int _selectedButtonSlot = 0;
    private int? _hoveredButtonSlot;
    private int _editingDpiStage = 1;
    private int _chargeAnimPct;
    private bool _isLoadingUi;
    private bool _hasPendingChanges;

    public bool ForceExit { get; set; }
    public event Action<IReadOnlyList<string>, int>? ProfilesListUpdated;
    public event Action<string>? TooltipUpdated;

    public MainWindow(bool smokeTestMode = false)
    {
        _isLoadingUi = true;
        InitializeComponent();
        _smokeTestMode = smokeTestMode;

        _repository = new ProfileRepository();
        _store = _repository.LoadOrInitialize();
        _transport = new Win32HidTransport();
        _deviceService = new MouseDeviceService(_transport);

        _autoApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _autoApplyTimer.Tick += async (_, _) =>
        {
            _autoApplyTimer.Stop();
            await ApplyCurrentProfileAsync();
        };

        _chargingAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _chargingAnimTimer.Tick += (_, _) =>
        {
            _chargeAnimPct = (_chargeAnimPct + 2) % 102;
            BatteryFillBar.Width = Math.Max(2, (_chargeAnimPct / 100.0) * 24.0);
        };

        BuildButtonRows();
        BuildDpiStageCards();
        PopulateStaticCombos();

        _deviceService.StatusChanged += status => Dispatcher.BeginInvoke(() => UpdateDeviceStatusUi(status));
        _deviceService.ApplyStateChanged += (applying, err) => Dispatcher.BeginInvoke(() => OnApplyStateChanged(applying, err));
        _deviceService.DpiStageChangedByMouse += stage => Dispatcher.BeginInvoke(() => OnHardwareDpiChanged(stage));
        _deviceService.PollingRateChangedByMouse += pr => Dispatcher.BeginInvoke(() => OnHardwarePollingRateChanged(pr));
        _deviceService.LightModeChangedByMouse += lm => Dispatcher.BeginInvoke(() => OnHardwareLightModeChanged(lm));
        _deviceService.ProfileChangedByMouse += idx => Dispatcher.BeginInvoke(() => SelectProfileIndex(idx));

        LoadStoreToUi();

        if (!_smokeTestMode)
        {
            _deviceService.Start();
            UpdateDeviceStatusUi(_deviceService.Status);
        }
        else
        {
            StatusBanner.Text = "Smoke Test Mode";
        }
    }

    private MouseProfile ActiveProfile => _store.Profiles[Math.Clamp(_store.ActiveProfileIndex, 0, _store.Profiles.Count - 1)];

    private void PopulateStaticCombos()
    {
        LightModeCombo.ItemsSource = LightModeLabels;

        var categories = new List<string> { "All Categories" };
        foreach (var item in X11Protocol.ActionTable)
        {
            if (!categories.Contains(item.Category))
                categories.Add(item.Category);
        }
        ActionCategoryCombo.ItemsSource = categories;
        ActionCategoryCombo.SelectedIndex = 0;
    }

    private void BuildButtonRows()
    {
        ButtonRowsPanel.Children.Clear();
        _buttonRowBorders.Clear();
        _buttonRowActionLabels.Clear();

        foreach (var btn in PhysicalButtons)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var numBadge = new Border
            {
                Width = 19,
                Height = 19,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource("AccentBrush"),
                Child = new TextBlock
                {
                    Text = btn.DisplayNum.ToString(),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var nameBlock = new TextBlock
            {
                Text = btn.DefaultTitle,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
            };

            var actionBlock = new TextBlock
            {
                Text = "Left Click",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Grid.SetColumn(numBadge, 0);
            Grid.SetColumn(nameBlock, 1);
            Grid.SetColumn(actionBlock, 2);
            grid.Children.Add(numBadge);
            grid.Children.Add(nameBlock);
            grid.Children.Add(actionBlock);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand,
                Tag = btn.SlotIndex,
                Child = grid,
            };

            int capturedSlot = btn.SlotIndex;
            border.MouseLeftButtonDown += (_, _) => SelectButtonSlot(capturedSlot);
            border.MouseEnter += (_, _) =>
            {
                _hoveredButtonSlot = capturedSlot;
                UpdateButtonVisualStates();
            };
            border.MouseLeave += (_, _) =>
            {
                if (_hoveredButtonSlot == capturedSlot)
                {
                    _hoveredButtonSlot = null;
                    UpdateButtonVisualStates();
                }
            };

            _buttonRowBorders[btn.SlotIndex] = border;
            _buttonRowActionLabels[btn.SlotIndex] = actionBlock;
            ButtonRowsPanel.Children.Add(border);
        }
    }

    private void BuildDpiStageCards()
    {
        DpiStagesGrid.Children.Clear();
        _dpiCardBorders.Clear();
        _dpiCardChecks.Clear();
        _dpiCardColorBoxes.Clear();
        _dpiCardValLabels.Clear();

        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            int stageIdx = i;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox
            {
                Style = (Style)FindResource("DarkCheckBoxStyle"),
                Content = $"S{i + 1}",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Checked += (_, _) => OnStageCheckChanged(stageIdx, true);
            cb.Unchecked += (_, _) => OnStageCheckChanged(stageIdx, false);

            var valLabel = new TextBlock
            {
                Text = "800",
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0),
            };

            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                Background = Brushes.Red,
                Cursor = Cursors.Hand,
                ToolTip = $"Pick Stage {i + 1} DPI LED Color",
            };
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                EditStageColor(stageIdx);
            };

            Grid.SetColumn(cb, 0);
            Grid.SetColumn(valLabel, 1);
            Grid.SetColumn(swatch, 2);
            grid.Children.Add(cb);
            grid.Children.Add(valLabel);
            grid.Children.Add(swatch);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                Child = grid,
            };
            card.MouseLeftButtonDown += (_, _) => SelectDpiStage(stageIdx);

            _dpiCardBorders[stageIdx] = card;
            _dpiCardChecks[stageIdx] = cb;
            _dpiCardValLabels[stageIdx] = valLabel;
            _dpiCardColorBoxes[stageIdx] = swatch;
            DpiStagesGrid.Children.Add(card);
        }
    }

    private void LoadStoreToUi()
    {
        _isLoadingUi = true;
        try
        {
            ProfileCombo.ItemsSource = null;
            ProfileCombo.ItemsSource = _store.Profiles;
            ProfileCombo.SelectedIndex = Math.Clamp(_store.ActiveProfileIndex, 0, _store.Profiles.Count - 1);

            AutoApplyCheck.IsChecked = _store.AutoApplyOnChange;
            _deviceService.SeedCommittedProfile(ActiveProfile);
            LoadActiveProfileToControls();
            NotifyProfilesToTray();
            SetDirtyState(false);
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void LoadActiveProfileToControls()
    {
        _isLoadingUi = true;
        try
        {
            var p = ActiveProfile;

            foreach (var btn in PhysicalButtons)
            {
                _buttonRowActionLabels[btn.SlotIndex].Text = X11Protocol.GetActionDisplayName(p.Buttons[btn.SlotIndex]);
            }

            _editingDpiStage = Math.Clamp(p.ActiveDpiStage, 0, MouseProfile.DpiStageCount - 1);
            RefreshDpiCardsAndEditor();

            switch (p.PollingRate)
            {
                case PollingRate.Hz125: Rate125Radio.IsChecked = true; PollingDialHz.Text = "125"; break;
                case PollingRate.Hz250: Rate250Radio.IsChecked = true; PollingDialHz.Text = "250"; break;
                case PollingRate.Hz500: Rate500Radio.IsChecked = true; PollingDialHz.Text = "500"; break;
                default: Rate1000Radio.IsChecked = true; PollingDialHz.Text = "1000"; break;
            }

            LightModeCombo.SelectedIndex = Math.Clamp((int)p.LightMode, 0, LightModeLabels.Length - 1);
            BrightnessSlider.Value = Math.Clamp(p.LightBrightness, 1, 8);
            BreathSpeedSlider.Value = Math.Clamp(p.LightBreathSpeed, 4, 8);
            BrightnessValText.Text = p.LightBrightness.ToString();
            BreathSpeedValText.Text = p.LightBreathSpeed.ToString();
            LightColorBtn.Background = new SolidColorBrush(Color.FromRgb(p.LightColor.R, p.LightColor.G, p.LightColor.B));

            DebounceSlider.Value = Math.Clamp(p.KeyDebounceMs, 2, 25);
            DebounceValText.Text = $"{p.KeyDebounceMs} ms";
            LightOffTimerSlider.Value = Math.Clamp(p.LightOffTimerMin, 1, 60);
            LightOffTimerValText.Text = $"{p.LightOffTimerMin} m";
            SleepTimerSlider.Value = Math.Clamp(p.SleepTimerMin, 1, 60);
            SleepTimerValText.Text = $"{p.SleepTimerMin} m";

            if (p.LiftOfDistanceMm == 1) Lod2Radio.IsChecked = true;
            else Lod1Radio.IsChecked = true;

            RippleCheck.IsChecked = p.RippleControl;
            AngleSnapCheck.IsChecked = p.AngleSnap;

            SelectButtonSlot(_selectedButtonSlot);
            UpdateMouseLedPreview();
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void RefreshDpiCardsAndEditor()
    {
        var p = ActiveProfile;
        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            var stage = p.DpiStages[i];
            bool wasLoading = _isLoadingUi;
            _isLoadingUi = true;
            _dpiCardChecks[i].IsChecked = stage.Enabled;
            _isLoadingUi = wasLoading;

            _dpiCardValLabels[i].Text = stage.DpiXRaw > 0 ? stage.DpiValue.ToString() : "Off";
            _dpiCardColorBoxes[i].Background = new SolidColorBrush(Color.FromRgb(stage.Color.R, stage.Color.G, stage.Color.B));

            bool isSelected = i == _editingDpiStage;
            _dpiCardBorders[i].BorderBrush = isSelected
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("BorderSubtleBrush");
            _dpiCardBorders[i].Background = isSelected
                ? new SolidColorBrush(Color.FromArgb(42, 226, 33, 28))
                : new SolidColorBrush(Color.FromRgb(20, 20, 20));
        }

        var active = p.DpiStages[_editingDpiStage];
        int raw = Math.Clamp(active.DpiXRaw, 1, 220);
        bool prev = _isLoadingUi;
        _isLoadingUi = true;
        SelectedDpiLabel.Text = $"Stage {_editingDpiStage + 1} DPI";
        DpiSlider.Value = raw;
        DpiValueBox.Text = (raw * 100).ToString();
        _isLoadingUi = prev;
    }

    private void SelectDpiStage(int stageIdx)
    {
        _editingDpiStage = Math.Clamp(stageIdx, 0, MouseProfile.DpiStageCount - 1);
        var p = ActiveProfile;
        if (p.ActiveDpiStage != _editingDpiStage)
        {
            p.ActiveDpiStage = _editingDpiStage;
            MarkModified();
        }
        RefreshDpiCardsAndEditor();
        UpdateMouseLedPreview();
    }

    private void OnStageCheckChanged(int stageIdx, bool isChecked)
    {
        if (_isLoadingUi) return;
        var p = ActiveProfile;

        if (!isChecked && p.DpiStages.Count(s => s.Enabled) <= 1)
        {
            _isLoadingUi = true;
            _dpiCardChecks[stageIdx].IsChecked = true;
            _isLoadingUi = false;
            return;
        }

        p.DpiStages[stageIdx].Enabled = isChecked;
        if (isChecked && p.DpiStages[stageIdx].DpiXRaw <= 0)
        {
            p.DpiStages[stageIdx].DpiXRaw = 8;
            p.DpiStages[stageIdx].DpiYRaw = 8;
        }

        RefreshDpiCardsAndEditor();
        MarkModified();
    }

    private void EditStageColor(int stageIdx)
    {
        var p = ActiveProfile;
        var dlg = new ColorPickerWindow(p.DpiStages[stageIdx].Color) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            p.DpiStages[stageIdx].Color = dlg.SelectedColor;
            RefreshDpiCardsAndEditor();
            UpdateMouseLedPreview();
            MarkModified();
        }
    }

    private void DpiSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi) return;
        int raw = Math.Clamp((int)Math.Round(DpiSlider.Value), 1, 220);
        var stage = ActiveProfile.DpiStages[_editingDpiStage];
        stage.DpiXRaw = raw;
        stage.DpiYRaw = raw;
        RefreshDpiCardsAndEditor();
        MarkModified();
    }

    private void DpiValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitDpiTextBox();
    }

    private void DpiValueBox_LostFocus(object sender, RoutedEventArgs e) => CommitDpiTextBox();

    private void CommitDpiTextBox()
    {
        if (_isLoadingUi) return;
        if (int.TryParse(DpiValueBox.Text.Trim(), out int dpi))
        {
            int raw = Math.Clamp((int)Math.Round(dpi / 100.0), 1, 220);
            var stage = ActiveProfile.DpiStages[_editingDpiStage];
            stage.DpiXRaw = raw;
            stage.DpiYRaw = raw;
            RefreshDpiCardsAndEditor();
            MarkModified();
        }
        else
        {
            RefreshDpiCardsAndEditor();
        }
    }

    private void ResetDpi_Click(object sender, RoutedEventArgs e)
    {
        var p = ActiveProfile;
        for (int i = 0; i < MouseProfile.DpiStageCount; i++)
        {
            p.DpiStages[i].DpiXRaw = X11Protocol.DefaultDpiRawValues[i];
            p.DpiStages[i].DpiYRaw = X11Protocol.DefaultDpiRawValues[i];
            p.DpiStages[i].Enabled = X11Protocol.DefaultDpiEnabled[i];
            p.DpiStages[i].Color = X11Protocol.DefaultDpiColors[i];
        }
        p.ActiveDpiStage = 1;
        _editingDpiStage = 1;
        RefreshDpiCardsAndEditor();
        UpdateMouseLedPreview();
        MarkModified();
    }

    private void SelectButtonSlot(int slotIndex)
    {
        _selectedButtonSlot = slotIndex;
        var slotInfo = PhysicalButtons.FirstOrDefault(b => b.SlotIndex == slotIndex) ?? PhysicalButtons[0];
        var btnCfg = ActiveProfile.Buttons[slotInfo.SlotIndex];
        SelectedButtonHeader.Text = $"Button {slotInfo.DisplayNum}: {slotInfo.DefaultTitle} ({X11Protocol.GetActionDisplayName(btnCfg)})";

        UpdateButtonVisualStates();

        bool prev = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            if (ActionListBox.ItemsSource is IEnumerable<ActionWireSpec> items)
            {
                var match = items.FirstOrDefault(a => a.ActionId == btnCfg.ActionId);
                ActionListBox.SelectedItem = match.ActionId == btnCfg.ActionId && !string.IsNullOrEmpty(match.Name) ? match : null;
            }
        }
        finally
        {
            _isLoadingUi = prev;
        }
    }

    private void UpdateButtonVisualStates()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var hoverBorder = (Brush)FindResource("AccentHoverBrush");
        var subtle = (Brush)FindResource("BorderSubtleBrush");

        foreach (var btn in PhysicalButtons)
        {
            bool selected = _selectedButtonSlot == btn.SlotIndex;
            bool hovered = _hoveredButtonSlot == btn.SlotIndex;

            if (_buttonRowBorders.TryGetValue(btn.SlotIndex, out var row))
            {
                row.BorderBrush = selected ? accent : hovered ? hoverBorder : subtle;
                row.Background = selected
                    ? new SolidColorBrush(Color.FromArgb(42, 226, 33, 28))
                    : hovered
                        ? new SolidColorBrush(Color.FromRgb(34, 34, 34))
                        : new SolidColorBrush(Color.FromRgb(20, 20, 20));
            }

            ApplyHotspotHighlight(btn.SlotIndex, selected, hovered);
        }
    }

    private void ApplyHotspotHighlight(int slot, bool selected, bool hovered)
    {
        Brush fill = selected
            ? new SolidColorBrush(Color.FromArgb(140, 226, 33, 28))
            : hovered
                ? new SolidColorBrush(Color.FromArgb(80, 255, 80, 72))
                : new SolidColorBrush(Color.FromRgb(36, 36, 36));
        Brush stroke = selected
            ? (Brush)FindResource("AccentHoverBrush")
            : hovered
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(85, 85, 85));

        switch (slot)
        {
            case 0:
                MouseLeftBtnPath.Fill = fill;
                MouseLeftBtnPath.Stroke = stroke;
                UpdateBadge(Badge0, selected, hovered);
                break;
            case 1:
                MouseRightBtnPath.Fill = fill;
                MouseRightBtnPath.Stroke = stroke;
                UpdateBadge(Badge1, selected, hovered);
                break;
            case 2:
                MouseWheelHotspot.Background = selected ? new SolidColorBrush(Color.FromRgb(180, 30, 25)) : new SolidColorBrush(Color.FromRgb(51, 51, 51));
                MouseWheelHotspot.BorderBrush = stroke;
                UpdateBadge(Badge2, selected, hovered);
                break;
            case 6:
                MouseForwardBtnPath.Fill = fill;
                MouseForwardBtnPath.Stroke = stroke;
                UpdateBadge(Badge6, selected, hovered);
                break;
            case 7:
                MouseBackwardBtnPath.Fill = fill;
                MouseBackwardBtnPath.Stroke = stroke;
                UpdateBadge(Badge7, selected, hovered);
                break;
        }
    }

    private void UpdateBadge(Border badge, bool selected, bool hovered)
    {
        badge.Background = selected
            ? (Brush)FindResource("AccentBrush")
            : hovered
                ? new SolidColorBrush(Color.FromRgb(64, 64, 64))
                : new SolidColorBrush(Color.FromRgb(42, 42, 42));
        badge.BorderBrush = selected || hovered ? Brushes.White : new SolidColorBrush(Color.FromRgb(110, 110, 110));
    }

    private void MouseHotspot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int slot))
            SelectButtonSlot(slot);
    }

    private void MouseHotspot_Enter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int slot))
        {
            _hoveredButtonSlot = slot;
            UpdateButtonVisualStates();
        }
    }

    private void MouseHotspot_Leave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int slot) && _hoveredButtonSlot == slot)
        {
            _hoveredButtonSlot = null;
            UpdateButtonVisualStates();
        }
    }

    private void ActionCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionCategoryCombo.SelectedItem is not string cat)
            return;

        var filtered = cat == "All Categories"
            ? X11Protocol.ActionTable.ToList()
            : X11Protocol.ActionTable.Where(a => a.Category == cat).ToList();

        _isLoadingUi = true;
        ActionListBox.ItemsSource = filtered;
        _isLoadingUi = false;
        SelectButtonSlot(_selectedButtonSlot);
    }

    private void ActionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingUi)
            ApplySelectedActionItem();
    }

    private void ActionListBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_isLoadingUi)
            ApplySelectedActionItem();
    }

    private void ApplySelectedActionItem()
    {
        if (ActionListBox.SelectedItem is not ActionWireSpec spec)
            return;

        if (spec.ActionId == 0x10)
        {
            AssignShortcut_Click(this, new RoutedEventArgs());
            return;
        }
        if (spec.ActionId == 0x11)
        {
            AssignMacro_Click(this, new RoutedEventArgs());
            return;
        }

        var btn = ActiveProfile.Buttons[_selectedButtonSlot];
        btn.ActionId = spec.ActionId;
        RefreshButtonAssignmentRow();
        MarkModified();
    }

    private void AssignShortcut_Click(object sender, RoutedEventArgs e)
    {
        var btn = ActiveProfile.Buttons[_selectedButtonSlot];
        var dlg = new ShortcutWindow(btn.ShortcutModifiers, btn.ShortcutKeyFwCode) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            btn.ActionId = 0x10;
            btn.ShortcutModifiers = dlg.Modifiers;
            btn.ShortcutKeyFwCode = dlg.KeyFwCode;
            RefreshButtonAssignmentRow();
            MarkModified();
        }
    }

    private void AssignMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Macros.Count == 0)
        {
            MessageDialog.ShowInfo(this, "No Macros", "Open the Macro Editor to create or record a macro first.");
            return;
        }

        var btn = ActiveProfile.Buttons[_selectedButtonSlot];
        var dlg = new SelectMacroWindow(_store.Macros, btn.MacroLibraryIndex, btn.MacroCycleMode, btn.MacroRepeatCount) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedMacro is not null)
        {
            btn.AssignMacro(dlg.SelectedMacro, dlg.SelectedMacroIndex, dlg.SelectedCycleMode, dlg.SelectedRepeatCount);
            RefreshButtonAssignmentRow();
            MarkModified();
        }
    }

    private void RefreshButtonAssignmentRow()
    {
        var btn = ActiveProfile.Buttons[_selectedButtonSlot];
        string label = X11Protocol.GetActionDisplayName(btn);
        if (_buttonRowActionLabels.TryGetValue(_selectedButtonSlot, out var tb))
            tb.Text = label;

        var slotInfo = PhysicalButtons.First(b => b.SlotIndex == _selectedButtonSlot);
        SelectedButtonHeader.Text = $"Button {slotInfo.DisplayNum}: {slotInfo.DefaultTitle} ({label})";
    }

    private void PollingRate_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || !int.TryParse(rb.Tag?.ToString(), out int rateIdx))
            return;

        ActiveProfile.PollingRate = (PollingRate)rateIdx;
        PollingDialHz.Text = ActiveProfile.PollingRate switch
        {
            PollingRate.Hz125 => "125",
            PollingRate.Hz250 => "250",
            PollingRate.Hz500 => "500",
            _ => "1000",
        };
        MarkModified();
    }

    private void LightModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi || LightModeCombo.SelectedIndex < 0)
            return;

        ActiveProfile.LightMode = (LightMode)LightModeCombo.SelectedIndex;
        UpdateMouseLedPreview();
        MarkModified();
    }

    private void LightColorBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = ActiveProfile;
        var dlg = new ColorPickerWindow(p.LightColor) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            p.LightColor = dlg.SelectedColor;
            LightColorBtn.Background = new SolidColorBrush(Color.FromRgb(p.LightColor.R, p.LightColor.G, p.LightColor.B));
            UpdateMouseLedPreview();
            MarkModified();
        }
    }

    private void LightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi) return;
        ActiveProfile.LightBrightness = Math.Clamp((int)Math.Round(BrightnessSlider.Value), 1, 8);
        ActiveProfile.LightBreathSpeed = Math.Clamp((int)Math.Round(BreathSpeedSlider.Value), 4, 8);
        BrightnessValText.Text = ActiveProfile.LightBrightness.ToString();
        BreathSpeedValText.Text = ActiveProfile.LightBreathSpeed.ToString();
        MarkModified();
    }

    private void ParamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi) return;
        ActiveProfile.KeyDebounceMs = Math.Clamp((int)Math.Round(DebounceSlider.Value), 2, 25);
        ActiveProfile.LightOffTimerMin = Math.Clamp((int)Math.Round(LightOffTimerSlider.Value), 1, 60);
        ActiveProfile.SleepTimerMin = Math.Clamp((int)Math.Round(SleepTimerSlider.Value), 1, 60);

        DebounceValText.Text = $"{ActiveProfile.KeyDebounceMs} ms";
        LightOffTimerValText.Text = $"{ActiveProfile.LightOffTimerMin} m";
        SleepTimerValText.Text = $"{ActiveProfile.SleepTimerMin} m";
        MarkModified();
    }

    private void LodRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || sender is not RadioButton rb || !int.TryParse(rb.Tag?.ToString(), out int lod))
            return;
        ActiveProfile.LiftOfDistanceMm = lod;
        MarkModified();
    }

    private void SensorCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;
        ActiveProfile.RippleControl = RippleCheck.IsChecked == true;
        ActiveProfile.AngleSnap = AngleSnapCheck.IsChecked == true;
        MarkModified();
    }

    private void UpdateMouseLedPreview()
    {
        var p = ActiveProfile;
        RgbColor preview = p.LightMode is LightMode.StaticDpi or LightMode.BreathingDpi or LightMode.LedOff
            ? p.DpiStages[Math.Clamp(p.ActiveDpiStage, 0, 7)].Color
            : p.LightColor;

        var c = Color.FromRgb(preview.R, preview.G, preview.B);
        var brush = new SolidColorBrush(c);
        AmbientGlowColor.Color = c;
        LogoLedPath.Fill = p.LightMode == LightMode.LedOff ? new SolidColorBrush(Color.FromRgb(65, 65, 65)) : brush;
        TailLedPath.Stroke = p.LightMode == LightMode.LedOff ? new SolidColorBrush(Color.FromRgb(65, 65, 65)) : brush;
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi || ProfileCombo.SelectedIndex < 0)
            return;
        SelectProfileIndex(ProfileCombo.SelectedIndex);
    }

    public async void SelectProfileIndex(int index)
    {
        if (index < 0 || index >= _store.Profiles.Count)
            return;

        _store.ActiveProfileIndex = index;
        _repository.Save(_store);
        LoadActiveProfileToControls();
        NotifyProfilesToTray();

        if (!_smokeTestMode && _transport.CurrentMode != ConnectionMode.Disconnected && _store.AutoApplyOnChange)
        {
            await ApplyCurrentProfileAsync(forceAll: true);
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileNameWindow("New Profile", $"Profile{_store.Profiles.Count + 1}") { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var created = X11Protocol.CreateDefaultProfile(dlg.ResultName);
            _store.Profiles.Add(created);
            _store.ActiveProfileIndex = _store.Profiles.Count - 1;
            _repository.Save(_store);
            LoadStoreToUi();
        }
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileNameWindow("Rename Profile", ActiveProfile.Name) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ActiveProfile.Name = dlg.ResultName;
            _repository.Save(_store);
            LoadStoreToUi();
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_store.Profiles.Count <= 1)
        {
            MessageDialog.ShowInfo(this, "Profile", "At least one profile must remain.");
            return;
        }

        if (!MessageDialog.ShowConfirm(this, "Delete Profile", $"Delete profile '{ActiveProfile.Name}'?"))
            return;

        _store.Profiles.RemoveAt(_store.ActiveProfileIndex);
        _store.ActiveProfileIndex = Math.Max(0, _store.ActiveProfileIndex - 1);
        _repository.Save(_store);
        LoadStoreToUi();
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "Profiles (*.profile;*.json;pro.data)|*.profile;*.json;pro.data|All Files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true)
            return;

        try
        {
            if (System.IO.Path.GetFileName(ofd.FileName).Equals("pro.data", StringComparison.OrdinalIgnoreCase))
            {
                var bundle = LegacyBinaryFormat.ReadProData(ofd.FileName);
                foreach (var prof in bundle.Profiles)
                    _store.Profiles.Add(prof);
                _store.ActiveProfileIndex = _store.Profiles.Count - 1;
                StatusBanner.Text = $"Imported {bundle.Profiles.Count} profile(s) from pro.data.";
            }
            else
            {
                var prof = _repository.ImportProfileFile(ofd.FileName);
                _store.Profiles.Add(prof);
                _store.ActiveProfileIndex = _store.Profiles.Count - 1;
                StatusBanner.Text = $"Imported '{prof.Name}'.";
            }

            _repository.Save(_store);
            LoadStoreToUi();
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Import Failed", ex.Message);
        }
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "Official Profile (*.profile)|*.profile|JSON Profile (*.json)|*.json",
            FileName = $"{ActiveProfile.Name}.profile",
        };
        if (sfd.ShowDialog(this) != true)
            return;

        try
        {
            _repository.ExportProfileFile(ActiveProfile, sfd.FileName);
            StatusBanner.Text = $"Exported '{ActiveProfile.Name}'.";
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Export Failed", ex.Message);
        }
    }

    private void MacroEditor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new MacroManagerWindow(_store.Macros, _repository) { Owner = this };
        dlg.ShowDialog();
        _repository.Save(_store);
    }

    private void AutoApplyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;
        _store.AutoApplyOnChange = AutoApplyCheck.IsChecked == true;
        _repository.Save(_store);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        _autoApplyTimer.Stop();
        await ApplyCurrentProfileAsync();
    }

    private async Task ApplyCurrentProfileAsync(bool forceAll = false, bool sendResetHeader = false)
    {
        _repository.Save(_store);
        if (_transport.CurrentMode == ConnectionMode.Disconnected)
        {
            StatusBanner.Text = "Saved locally (mouse disconnected).";
            SetDirtyState(false);
            return;
        }

        bool ok = await _deviceService.ApplyProfileAsync(ActiveProfile, forceAll, sendResetHeader);
        if (ok)
        {
            SetDirtyState(false);
            StatusBanner.Text = $"Applied '{ActiveProfile.Name}' to X11 hardware.";
        }
    }

    private async void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.ShowConfirm(this, "Restore Factory Defaults", "Reset current profile parameters and sync to the mouse?"))
            return;

        string currentName = ActiveProfile.Name;
        _store.Profiles[_store.ActiveProfileIndex] = X11Protocol.CreateDefaultProfile(currentName);
        _repository.Save(_store);
        LoadActiveProfileToControls();
        await ApplyCurrentProfileAsync(forceAll: true, sendResetHeader: true);
    }

    private void MarkModified()
    {
        _repository.Save(_store);
        SetDirtyState(true);
        if (_store.AutoApplyOnChange && !_smokeTestMode && _transport.CurrentMode != ConnectionMode.Disconnected)
        {
            _autoApplyTimer.Stop();
            _autoApplyTimer.Start();
        }
    }

    private void SetDirtyState(bool dirty)
    {
        _hasPendingChanges = dirty;
        DirtyBadge.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        if (dirty)
            StatusBanner.Text = "Unsaved hardware changes — click Apply.";
    }

    private void UpdateDeviceStatusUi(DeviceStatus status)
    {
        switch (status.Mode)
        {
            case ConnectionMode.Wired:
                ConnectionText.Text = "USB Wired (FA55)";
                ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(52, 199, 89));
                SleepPill.Visibility = Visibility.Collapsed;
                break;
            case ConnectionMode.Wireless24G:
                ConnectionText.Text = "2.4G Wireless (FA60)";
                ConnectionDot.Fill = status.IsAwake
                    ? new SolidColorBrush(Color.FromRgb(52, 199, 89))
                    : new SolidColorBrush(Color.FromRgb(255, 184, 0));
                SleepPill.Visibility = status.IsAwake ? Visibility.Collapsed : Visibility.Visible;
                break;
            default:
                ConnectionText.Text = "Disconnected";
                ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(125, 125, 125));
                SleepPill.Visibility = Visibility.Collapsed;
                break;
        }

        if (status.Mode == ConnectionMode.Disconnected)
        {
            _chargingAnimTimer.Stop();
            BatteryText.Text = "--%";
            BatteryFillBar.Width = 0;
        }
        else if (status.IsCharging && !status.IsFullyCharged)
        {
            BatteryText.Text = "Charging";
            BatteryFillBar.Background = new SolidColorBrush(Color.FromRgb(255, 184, 0));
            if (!_chargingAnimTimer.IsEnabled)
                _chargingAnimTimer.Start();
        }
        else
        {
            _chargingAnimTimer.Stop();
            int pct = Math.Clamp(status.BatteryPercent, 0, 100);
            BatteryText.Text = $"{pct}%";
            BatteryFillBar.Width = Math.Max(2, (pct / 100.0) * 24.0);
            BatteryFillBar.Background = pct <= 20
                ? (Brush)FindResource("AccentBrush")
                : new SolidColorBrush(Color.FromRgb(52, 199, 89));
        }

        TooltipUpdated?.Invoke($"Attack Shark X11 | {ConnectionText.Text} | {BatteryText.Text}");
    }

    private void OnApplyStateChanged(bool applying, string? error)
    {
        ApplyingOverlay.Visibility = applying ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = !applying;
        if (!applying && !string.IsNullOrEmpty(error))
            StatusBanner.Text = error;
    }

    private void OnHardwareDpiChanged(int stage0)
    {
        if (stage0 is < 0 or >= MouseProfile.DpiStageCount) return;
        ActiveProfile.ActiveDpiStage = stage0;
        _editingDpiStage = stage0;
        _repository.Save(_store);
        RefreshDpiCardsAndEditor();
        UpdateMouseLedPreview();

        var st = ActiveProfile.DpiStages[stage0];
        _dpiOsd.ShowDpi(stage0, st.DpiValue, st.Color);
    }

    private void OnHardwarePollingRateChanged(PollingRate rate)
    {
        ActiveProfile.PollingRate = rate;
        _repository.Save(_store);
        LoadActiveProfileToControls();
    }

    private void OnHardwareLightModeChanged(LightMode mode)
    {
        ActiveProfile.LightMode = mode;
        _repository.Save(_store);
        LoadActiveProfileToControls();
    }

    private void NotifyProfilesToTray()
    {
        ProfilesListUpdated?.Invoke(_store.Profiles.Select(p => p.Name).ToList(), _store.ActiveProfileIndex);
    }

    private void RescanHid_Click(object sender, RoutedEventArgs e)
    {
        _deviceService.RefreshConnection();
        StatusBanner.Text = _transport.CurrentMode == ConnectionMode.Disconnected
            ? "No Attack Shark X11 device detected."
            : $"Connected: {_deviceService.Status.ProductName}";
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow(_deviceService.Status) { Owner = this }.ShowDialog();
    }

    private void WinMouseProps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "main.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.ShowInfo(this, "Error", ex.Message);
        }
    }

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_smokeTestMode || ForceExit)
        {
            Close();
        }
        else
        {
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_smokeTestMode && !ForceExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _autoApplyTimer.Stop();
        _chargingAnimTimer.Stop();
        _dpiOsd.Close();
        _deviceService.Dispose();
        _transport.Dispose();
        base.OnClosing(e);
    }
}
