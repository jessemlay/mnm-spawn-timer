using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SpawnTimer.Core;
using SpawnTimer.Native;
using SpawnTimer.UI;
using Forms = System.Windows.Forms;

namespace SpawnTimer;

public partial class App : Application
{
    private readonly List<(Hotkey Key, Action Action)> _bindings = new();
    private readonly Alerts _alerts = new();
    private Mutex? _singleInstance;
    private KeyboardHook? _hook;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _lockMenuItem;
    private TimerOverlay? _timerOverlay;
    private MeterOverlay? _meterOverlay;
    private AlertOverlay? _alertOverlay;
    private SettingsWindow? _settingsWindow;
    private readonly HashSet<string> _knownPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastNamedAlert = new(StringComparer.OrdinalIgnoreCase);
    private LedgerWatcher? _ledger;
    private DispatcherTimer? _tick;
    private DateTime _lastMeterRefresh = DateTime.MinValue;
    private DateTimeOffset _appStart;
    private bool _editMode;
    private bool _holdMove;
    private int _activeIndex;

    public static App Instance => (App)Current;

    public AppSettings Settings { get; private set; } = new();
    public ObservableCollection<SpawnTimerState> Timers { get; } = new();
    public SessionStats Session { get; } = new();
    public string LedgerStatus => _ledger?.Status ?? "Off";

    private SpawnTimerState? ActiveTimer =>
        Timers.Count == 0 ? null : Timers[Math.Clamp(_activeIndex, 0, Timers.Count - 1)];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\SpawnTimer.MnM.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Spawn Timer is already running. Look for its icon in the system tray.", "Spawn Timer");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Spawn Timer error", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _appStart = DateTimeOffset.Now;
        Settings = SettingsStore.Load();

        _timerOverlay = new TimerOverlay(Timers);
        _meterOverlay = new MeterOverlay();
        _alertOverlay = new AlertOverlay();
        var area = SystemParameters.WorkArea;
        _timerOverlay.BindPosition(Settings.TimerOverlay, area.Left + area.Width / 2 - 130, area.Top + 40);
        _meterOverlay.BindPosition(Settings.MeterOverlay, area.Right - 320, area.Top + 220);
        _alertOverlay.BindPosition(Settings.AlertOverlay, area.Left + area.Width / 2 - 170, area.Top + area.Height * 0.22);

        try
        {
            _hook = new KeyboardHook { KeyPressed = OnGlobalKey };
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show("Global hotkeys could not be enabled: " + ex.Message, "Spawn Timer");
        }
        HotkeyBox.CapturingChanged += capturing =>
        {
            if (_hook != null) _hook.Suspended = capturing;
        };

        CreateTray();
        ApplySettings(save: false);

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        OnTick();

        if (Settings.FirstRun)
        {
            Settings.FirstRun = false;
            SettingsStore.Save(Settings);
            OpenSettings();
            _tray?.ShowBalloonTip(5000, "Spawn Timer is running",
                "Right-click the tray icon for settings. Default hotkeys: Ctrl+Alt+1 start, 2 stop, 3 restart.",
                Forms.ToolTipIcon.Info);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tick?.Stop();
        _ledger?.Dispose();
        _hook?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_singleInstance != null) SettingsStore.Save(Settings);
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Push the current settings to timers, hotkeys, overlays and the Ledger reader.</summary>
    public void ApplySettings(bool save = true)
    {
        SyncTimers();
        RebuildHotkeys();

        if (_hook != null) _hook.PassThrough = Settings.Hotkeys.PassThrough;

        _timerOverlay!.ApplyAppearance(Settings.TimerOverlay);
        _meterOverlay!.ApplyAppearance(Settings.MeterOverlay);
        _alertOverlay!.ApplyAppearance(Settings.AlertOverlay);
        SetOverlayVisible(_timerOverlay, Settings.TimerOverlay.Visible);
        SetOverlayVisible(_meterOverlay, Settings.MeterOverlay.Visible);
        SetOverlayVisible(_alertOverlay, Settings.AlertOverlay.Visible);

        var wantLedger = Settings.Ledger.Enabled;
        var folder = Settings.Ledger.FolderOverride?.Trim() ?? "";
        if (!wantLedger || (_ledger != null && _ledger.OverridePath != folder))
        {
            _ledger?.Dispose();
            _ledger = null;
        }
        if (wantLedger && _ledger == null)
        {
            // Replay recent history so party size is known; SessionStats ignores loot before the session start.
            _ledger = new LedgerWatcher(folder, _appStart.AddHours(-12), Dispatcher);
            _ledger.EventsArrived += OnLedgerEvents;
        }

        _lastMeterRefresh = DateTime.MinValue;
        OnTick();
        if (save) SettingsStore.Save(Settings);
    }

    public void ResetSession()
    {
        Session.Reset();
        _lastMeterRefresh = DateTime.MinValue;
    }

    public void ExportSession(Window? owner = null)
    {
        if (Session.Events.Count == 0)
        {
            var reason = Settings.Ledger.Enabled
                ? "Nothing recorded yet this session. Loot something first."
                : "Session data comes from the game's Ledger. Turn on Settings ▸ Game data first.";
            MessageBox.Show(reason, "Export session");
            return;
        }

        var folder = SessionExport.DefaultFolder;
        try
        {
            System.IO.Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export session to CSV",
            Filter = "CSV file (*.csv)|*.csv",
            InitialDirectory = folder,
            FileName = SessionExport.DefaultFileName(Session),
            AddExtension = true,
            DefaultExt = ".csv",
        };
        var picked = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (picked != true) return;

        string mobsPath;
        try
        {
            mobsPath = SessionExport.Write(Session, dialog.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't write the file: {ex.Message}\n\nIf it's open in Excel, close it and try again.", "Export session");
            return;
        }

        var open = MessageBox.Show(
            $"Exported {Session.Events.Count} events and {Session.Mobs.Count} mobs:\n\n{dialog.FileName}\n{mobsPath}\n\nOpen the folder?",
            "Export session", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
    }

    public void TestAlert(AlertKind? kind)
    {
        switch (kind)
        {
            case null:
                _alerts.Warn(new AlertSettings { SoundOnWarning = true });
                break;
            case AlertKind.Drop:
                ShowDropAlert("Head of General Overlord Bloodynose", "General Overlord Bloodynose", "You", forceSound: true);
                break;
            case AlertKind.NamedDown:
                ShowNamedDown("General Overlord Bloodynose", "telekir", "You", Settings.Named.DefaultRespawnSec, forceSound: true);
                break;
            default:
                ShowTimerUp(ActiveTimer?.Def ?? new TimerDef(), forceSound: true);
                break;
        }
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        _alertOverlay?.Tick(now);

        // Holding Ctrl+Shift unlocks the overlays for as long as the keys are down.
        var hold = Settings.Hotkeys.HoldToMove && NativeMethods.IsDown(0x11) && NativeMethods.IsDown(0x10);
        if (hold != _holdMove)
        {
            _holdMove = hold;
            UpdateOverlayUnlock();
            if (!hold && !_editMode) SettingsStore.Save(Settings); // keep the new positions
        }
        var unlocked = _editMode || _holdMove;

        for (var i = 0; i < Timers.Count; i++)
        {
            var t = Timers[i];
            t.Tick(now);
            t.IsActive = i == _activeIndex;
            t.ShowInOverlay = !Settings.TimerOverlay.HideIdle || t.IsActive || t.Phase != TimerPhase.Idle || unlocked;
        }

        if (now - _lastMeterRefresh >= TimeSpan.FromSeconds(1) && _meterOverlay is { IsVisible: true })
        {
            _lastMeterRefresh = now;
            _meterOverlay.Refresh(Session, Settings.Meter, Settings.Ledger.Enabled, LedgerStatus);
        }
    }

    private void SyncTimers()
    {
        var existing = new Dictionary<Guid, SpawnTimerState>();
        foreach (var state in Timers) existing.TryAdd(state.Def.Id, state);

        var desired = new List<SpawnTimerState>();
        foreach (var def in Settings.Timers)
        {
            if (existing.Remove(def.Id, out var state) && ReferenceEquals(state.Def, def))
            {
                desired.Add(state);
                continue;
            }
            state = new SpawnTimerState(def);
            state.Warned += _ => _alerts.Warn(Settings.Alerts);
            state.Expired += s => ShowTimerUp(s.Def, forceSound: false);
            desired.Add(state);
        }
        foreach (var removed in existing.Values) removed.Detach();

        if (!desired.SequenceEqual(Timers))
        {
            var active = ActiveTimer;
            Timers.Clear();
            foreach (var state in desired) Timers.Add(state);
            var index = active == null ? -1 : Timers.IndexOf(active);
            _activeIndex = index >= 0 ? index : Math.Clamp(_activeIndex, 0, Math.Max(0, Timers.Count - 1));
        }
    }

    private void RebuildHotkeys()
    {
        _bindings.Clear();
        var h = Settings.Hotkeys;
        Bind(h.Start, () => ActiveTimer?.Start(DateTime.UtcNow));
        Bind(h.Stop, () => ActiveTimer?.Stop(DateTime.UtcNow));
        Bind(h.Restart, () => ActiveTimer?.Restart(DateTime.UtcNow, DateTime.UtcNow));
        Bind(h.NextTimer, () =>
        {
            if (Timers.Count > 0) _activeIndex = (_activeIndex + 1) % Timers.Count;
        });
        Bind(h.ToggleEdit, () => SetEditMode(!_editMode));

        foreach (var state in Timers)
        {
            var target = state;
            Bind(target.Def.RestartHotkey, () =>
            {
                _activeIndex = Timers.IndexOf(target);
                target.Restart(DateTime.UtcNow, DateTime.UtcNow);
            });
        }
    }

    private void Bind(string text, Action action)
    {
        var hotkey = Hotkey.Parse(text);
        if (!hotkey.IsEmpty) _bindings.Add((hotkey, action));
    }

    /// <summary>Runs inside the keyboard hook: match only, and defer the action so the hook returns fast.</summary>
    private bool OnGlobalKey(Hotkey pressed)
    {
        var matched = false;
        foreach (var (key, action) in _bindings)
        {
            if (key != pressed) continue;
            matched = true;
            Dispatcher.InvokeAsync(() =>
            {
                action();
                OnTick();
            }, DispatcherPriority.Send);
        }
        return matched;
    }

    private void OnLedgerEvents(IReadOnlyList<LedgerEvent> events)
    {
        var now = DateTime.UtcNow;
        foreach (var e in events)
        {
            // Anyone acting in or named by the ledger is a player, never a named mob.
            if (e.Actor.Length > 0) _knownPlayers.Add(e.Actor);
            if (e.Owner.Length > 0) _knownPlayers.Add(e.Owner);
            if (!string.IsNullOrEmpty(e.Subject)) _knownPlayers.Add(e.Subject);

            Session.Apply(e);

            // Alerts and auto-restarts only for loot from this session, not history replayed on startup.
            if (e.Time < _appStart || e.Kind is not (LedgerKind.ItemLoot or LedgerKind.CoinLoot)) continue;

            if (e.Kind == LedgerKind.ItemLoot && Settings.DropAlerts.Enabled && !string.IsNullOrEmpty(e.Item)
                && (!Settings.DropAlerts.MineOnly || e.IsMine)
                && NameMatcher.MatchesAny(e.Item, Settings.DropAlerts.ItemNames))
            {
                ShowDropAlert(e.Item, e.Corpse, e.IsMine ? "You" : e.Actor, forceSound: false);
            }

            if (e.Corpse == null) continue;
            if (Settings.Named.Enabled && NamedDetector.IsNamed(e.Corpse, Settings.Named, _knownPlayers))
                OnNamedLooted(e);
            foreach (var timer in Timers) timer.TryAutoRestart(e.Corpse, e.Time.UtcDateTime, now);
        }
        _lastMeterRefresh = DateTime.MinValue;
    }

    private void OnNamedLooted(LedgerEvent e)
    {
        var corpse = e.Corpse!;
        // One alert per kill: a corpse produces several loot lines within seconds.
        if (_lastNamedAlert.TryGetValue(corpse, out var last) && e.Time - last < TimeSpan.FromSeconds(60)) return;
        _lastNamedAlert[corpse] = e.Time;

        var timer = Settings.Timers.FirstOrDefault(t => t.AutoStart && t.MatchesCorpse(corpse));
        if (timer == null && Settings.Named.AutoTimers)
        {
            timer = new TimerDef
            {
                Name = corpse,
                DurationSec = Settings.Named.DefaultRespawnSec,
                WarnSec = 60,
                AutoStart = true,
                MobNames = corpse,
                AutoCreated = true,
            };
            Settings.Timers.Add(timer);
            ApplySettings(); // creates the runtime timer so the restart loop below picks it up
        }

        if (Settings.Named.AlertOnLoot)
            ShowNamedDown(corpse, e.Zone, e.IsMine ? "You" : e.Actor, timer?.DurationSec, forceSound: false);
    }

    private void ShowDropAlert(string item, string? corpse, string looter, bool forceSound)
    {
        var detail = corpse == null ? $"Looted by {looter}" : $"Looted by {looter} from {corpse}";
        Banner(AlertKind.Drop, item, detail);
        var s = Settings.DropAlerts;
        _alerts.Drop(forceSound ? new DropAlertSettings { Sound = true, Speak = s.Speak } : s, item);
    }

    private void ShowNamedDown(string corpse, string zone, string looter, int? respawnSec, bool forceSound)
    {
        var where = zone.Length > 0 ? $"{char.ToUpperInvariant(zone[0])}{zone[1..]} · " : "";
        var due = respawnSec.HasValue
            ? $" · spawn due in {TimeFormat.Clock(TimeSpan.FromSeconds(respawnSec.Value))} ({DateTime.Now.AddSeconds(respawnSec.Value):t})"
            : "";
        Banner(AlertKind.NamedDown, corpse, $"{where}looted by {looter}{due}");
        var s = Settings.Named;
        _alerts.Named(forceSound ? new NamedSettings { Sound = true, Speak = s.Speak } : s, $"{corpse} down");
    }

    private void ShowTimerUp(TimerDef def, bool forceSound)
    {
        var alerts = forceSound ? new AlertSettings { SoundOnSpawn = true, SpeakOnSpawn = Settings.Alerts.SpeakOnSpawn } : Settings.Alerts;
        if (def.AutoCreated)
        {
            // Named respawn timers use the named-mob sound and a "spawn due" banner.
            Banner(AlertKind.SpawnDue, def.Name, "Respawn window open (predicted from when it was looted)");
            var s = Settings.Named;
            _alerts.Named(forceSound ? new NamedSettings { Sound = true, Speak = s.Speak } : s, $"{def.Name} spawn due");
            return;
        }
        if (Settings.Alerts.BannerOnTimerUp || forceSound) Banner(AlertKind.TimerUp, $"{def.Name} is up", "Spawn timer reached zero");
        _alerts.Spawn(alerts, def.Name);
    }

    private void Banner(AlertKind kind, string title, string detail)
    {
        if (_alertOverlay == null || !Settings.AlertOverlay.Visible) return;
        _alertOverlay.Push(kind, title, detail, Settings.Alerts.BannerSeconds);
    }

    private void SetEditMode(bool on)
    {
        _editMode = on;
        UpdateOverlayUnlock();
        if (_lockMenuItem != null) _lockMenuItem.Text = on ? "Lock overlays" : "Unlock overlays (move)";
        if (!on) SettingsStore.Save(Settings);
        OnTick();
    }

    private void UpdateOverlayUnlock()
    {
        var unlocked = _editMode || _holdMove;
        if (_timerOverlay == null || _timerOverlay.EditMode == unlocked) return;
        _timerOverlay.EditMode = unlocked;
        _meterOverlay!.EditMode = unlocked;
        _alertOverlay!.EditMode = unlocked;
    }

    private static void SetOverlayVisible(Window window, bool visible)
    {
        if (visible && !window.IsVisible) window.Show();
        else if (!visible && window.IsVisible) window.Hide();
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ApplySettings();
            SetEditMode(false);
        };
        _settingsWindow.Show();
        SetEditMode(true);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        _lockMenuItem = new Forms.ToolStripMenuItem("Unlock overlays (move)", null, (_, _) => SetEditMode(!_editMode));
        menu.Items.Add(_lockMenuItem);
        menu.Items.Add("Export session to CSV…", null, (_, _) => ExportSession());
        menu.Items.Add("Reset session", null, (_, _) => ResetSession());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "Spawn Timer · click: move overlays · double-click: settings",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SetEditMode(!_editMode);
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var ring = new Pen(System.Drawing.Color.FromArgb(0x7F, 0xD4, 0xFF), 4);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            using var hand = new Pen(System.Drawing.Color.White, 3);
            g.DrawLine(hand, 16, 16, 16, 7);
            g.DrawLine(hand, 16, 16, 23, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
