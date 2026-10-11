using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SpawnTimer.Core;

namespace SpawnTimer.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _applyDebounce;
    private readonly DispatcherTimer _statusTimer;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = settings;
        TimerListBox.SelectedIndex = 0;

        // Bindings push values on change/lost-focus; apply shortly after any edit so overlays update live.
        _applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _applyDebounce.Tick += (_, _) =>
        {
            _applyDebounce.Stop();
            App.Instance.ApplySettings();
        };
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => QueueApply()));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => QueueApply()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => QueueApply()));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueApply()));
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => QueueApply()));

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _applyDebounce.Stop();
        base.OnClosed(e);
    }

    private void QueueApply()
    {
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private void UpdateStatus() =>
        LedgerStatus.Text = _settings.Ledger.Enabled ? App.Instance.LedgerStatus : "Off";

    private void AddTimer_Click(object sender, RoutedEventArgs e)
    {
        var def = new TimerDef { Name = $"Timer {_settings.Timers.Count + 1}" };
        _settings.Timers.Add(def);
        TimerListBox.SelectedItem = def;
        QueueApply();
    }

    private void RemoveTimer_Click(object sender, RoutedEventArgs e)
    {
        if (TimerListBox.SelectedItem is not TimerDef def) return;
        var index = TimerListBox.SelectedIndex;
        // Same path as the overlay/tray so removed named timers aren't recreated on the next kill.
        App.Instance.RemoveTimer(def);
        Rebind();
        TimerListBox.SelectedIndex = Math.Min(index, _settings.Timers.Count - 1);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        var index = TimerListBox.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _settings.Timers.Count) return;
        _settings.Timers.Move(index, target);
        TimerListBox.SelectedIndex = target;
        QueueApply();
    }

    private void ResetSession_Click(object sender, RoutedEventArgs e) => App.Instance.ResetSession();

    private void ResetHistory_Click(object sender, RoutedEventArgs e)
    {
        if (TimerListBox.SelectedItem is not TimerDef def) return;
        def.ResetHistory();
        QueueApply();
    }

    private void ExportSession_Click(object sender, RoutedEventArgs e) => App.Instance.ExportSession(this);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pick a character folder (the one containing \"Ledger\")",
            InitialDirectory = System.IO.Directory.Exists(LedgerWatcher.GameDataRoot) ? LedgerWatcher.GameDataRoot : "",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.Ledger.FolderOverride = dialog.FolderName;
            Rebind();
            QueueApply();
        }
    }

    private void ZoneImport_Click(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus(); // commit any typing in the item list first
        var dialog = new ZoneImportWindow(_settings, App.Instance.Session.Zone) { Owner = this };
        dialog.ShowDialog();
        if (dialog.AddedItems > 0) Rebind();
    }

    /// <summary>Settings objects don't raise change notifications; rebind so edited values show.</summary>
    private void Rebind()
    {
        var selected = TimerListBox.SelectedIndex;
        DataContext = null;
        DataContext = _settings;
        TimerListBox.SelectedIndex = selected;
    }

    private void TestTimerUp_Click(object sender, RoutedEventArgs e) => App.Instance.TestAlert(AlertKind.TimerUp);

    private void TestWarn_Click(object sender, RoutedEventArgs e) => App.Instance.TestAlert(null);

    private void TestDrop_Click(object sender, RoutedEventArgs e) => App.Instance.TestAlert(AlertKind.Drop);

    private void TestNamed_Click(object sender, RoutedEventArgs e) => App.Instance.TestAlert(AlertKind.NamedDown);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
