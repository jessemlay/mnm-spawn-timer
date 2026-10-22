using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SpawnTimer.Core;
using SpawnTimer.Native;
using SpawnTimer.UI;
using Forms = System.Windows.Forms;

namespace SpawnTimer;

public partial class App : Application, ITimerLinkHost
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
    private DispatcherTimer? _updateTimer;
    private ItemLookupController? _itemLookup;
    // Recent Ledger entries, so a new session can be rebuilt from any start time (e.g. when the game launched).
    private readonly List<LedgerEvent> _ledgerHistory = new();
    private DispatcherTimer? _gameWatch;
    private DateTime? _seenGameStart;
    private Forms.ToolStripMenuItem? _updateMenuItem;
    private UpdateInfo? _availableUpdate;
    private bool _updateBusy;
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

    /// <summary>Your merchant sales (prices for the meter and lookup cards); null if the database couldn't open.</summary>
    public SalesDb? Sales { get; private set; }

    /// <summary>Format check for the Ledger (new entry types, unreadable entries after a patch).</summary>
    public LedgerHealth LedgerHealth { get; } = new();

    private bool _salesBackfillStarted;
    private string _salesStatus = "";

    /// <summary>One line for Settings ▸ Game data.</summary>
    public string SalesSummary => Sales == null
        ? "Sales database unavailable" + (_salesStatus.Length > 0 ? $": {_salesStatus}" : ".")
        : $"{Sales.SaleCount} sales, {Sales.PricedItemCount} items priced{(_salesStatus.Length > 0 ? $" · {_salesStatus}" : "")}. ({Sales.FilePath})";

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

        // Never pop a modal box mid-game: log the error and show a banner instead.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            var log = System.IO.Path.Combine(SettingsStore.Folder, "errors.log");
            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Folder);
                System.IO.File.AppendAllText(log, $"{DateTime.Now:u}  {args.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
            Banner(AlertKind.Info, "Spawn Timer hit an error", $"{args.Exception.Message} (details in Documents\\Spawn Timer\\errors.log)");
        };

        _appStart = DateTimeOffset.Now;
        Settings = SettingsStore.Load();
        OpenSalesDb();

        _timerOverlay = new TimerOverlay(Timers);
        _meterOverlay = new MeterOverlay();
        _meterOverlay.RestartRequested += ResetSession;
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

        _itemLookup = new ItemLookupController(Settings) { Timers = this };
        _itemLookup.OpenChanged += open =>
        {
            if (_hook != null) _hook.Capture = open ? CaptureKey : null;
        };
        _itemLookup.PriceOf = item => Sales?.TryGetPrice(item);
        _ = _itemLookup.PreloadIndexAsync();

        CreateTray();
        ApplySettings(save: false);

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        // Update check: shortly after startup, then every 2 hours. Updates install themselves whenever the game
        // isn't running (or as soon as it closes).
        var firstCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        firstCheck.Tick += async (_, _) =>
        {
            firstCheck.Stop();
            await CheckForUpdatesAsync(manual: false);
        };
        firstCheck.Start();
        // The session follows the game: starts when mnm.exe launches, ends when it closes.
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gameWatch.Tick += (_, _) => CheckGameState();
        _gameWatch.Start();
        CheckGameState();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(2) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
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

    /// <summary>Shown in Settings ▸ Updates.</summary>
    public string UpdateStatus { get; private set; } = $"Spawn Timer {Updates.CurrentVersion}. Not checked yet.";

    public Task CheckForUpdatesNow() => CheckForUpdatesAsync(manual: true);

    /// <summary>A version whose admin prompt was declined this run; not offered automatically again until restart.</summary>
    private string? _declinedVersion;

    /// <summary>
    /// Looks for a newer GitHub release. With "install automatically" on it installs right away when the game isn't
    /// running, or as soon as the game closes; otherwise it announces it once with a banner and adds a tray item.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateBusy || (!manual && !Settings.Updates.CheckForUpdates)) return;
        try
        {
            var update = await Updates.CheckAsync(CancellationToken.None);
            _availableUpdate = update;
            RefreshUpdateMenu();
            if (update == null)
            {
                UpdateStatus = $"Spawn Timer {Updates.CurrentVersion} is up to date (checked {DateTime.Now:t}).";
                if (manual) Banner(AlertKind.Info, "You're up to date", $"Spawn Timer {Updates.CurrentVersion}");
                return;
            }

            UpdateStatus = $"Spawn Timer {Updates.CurrentVersion}. Update available: v{update.Version} (checked {DateTime.Now:t}).";
            if (await TryAutoInstallAsync()) return;
            if (manual || Settings.Updates.LastAnnouncedVersion != update.Version.ToString())
            {
                Banner(AlertKind.Info, $"Update available: v{update.Version}", AutoInstallOn
                    ? "It installs itself when you close the game (one admin prompt), or Tray ▸ Install update now."
                    : "Tray ▸ Install update. One admin prompt, then the app restarts with your settings.");
                Settings.Updates.LastAnnouncedVersion = update.Version.ToString();
                SettingsStore.Save(Settings);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            UpdateStatus = $"Spawn Timer {Updates.CurrentVersion}. Couldn't check for updates: {ex.Message}";
            if (manual) Banner(AlertKind.Info, "Couldn't check for updates", ex.Message);
        }
    }

    private bool AutoInstallOn => Settings.Updates.InstallAutomatically && !Updates.IsDevBuild;

    /// <summary>
    /// Installs a found update on its own when that won't interrupt anything: the game isn't running, the item lookup
    /// is closed, and you didn't decline this version's admin prompt. Called after each check and when the game closes.
    /// </summary>
    private async Task<bool> TryAutoInstallAsync()
    {
        if (!AutoInstallOn || _availableUpdate is not { } update || _updateBusy || GameRunning
            || (_itemLookup?.IsOpen ?? false) || update.Version.ToString() == _declinedVersion)
            return false;
        await InstallUpdateAsync(automatic: true);
        return true;
    }

    /// <summary>A waiting update installs 20 seconds after the game closes (time to read the session banner) if it's still closed.</summary>
    private void InstallUpdateAfterGameCloses()
    {
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        delay.Tick += async (_, _) =>
        {
            delay.Stop();
            await TryAutoInstallAsync();
        };
        delay.Start();
    }

    /// <summary>Download + verify the installer, run it silently (it relaunches the app), and exit.</summary>
    public Task InstallUpdateAsync() => InstallUpdateAsync(automatic: false);

    private async Task InstallUpdateAsync(bool automatic)
    {
        if (_availableUpdate is not { } update || _updateBusy) return;
        _updateBusy = true;
        RefreshUpdateMenu();
        try
        {
            Banner(AlertKind.Info, $"Updating to v{update.Version}", "Downloading and verifying the installer…");
            var setup = await Updates.DownloadAsync(update, CancellationToken.None);
            SettingsStore.Save(Settings);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
                {
                    UseShellExecute = true, // lets Windows show the admin prompt
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // admin prompt declined
            {
                if (automatic) _declinedVersion = update.Version.ToString(); // don't ask again for this version this run
                Banner(AlertKind.Info, "Update postponed", "The admin prompt was declined. Tray ▸ Install update when you're ready.");
                return;
            }
            Shutdown(); // the installer replaces the exe and starts the new version
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or Win32Exception)
        {
            UpdateStatus = $"Spawn Timer {Updates.CurrentVersion}. Update to v{update.Version} failed: {ex.Message}";
            Banner(AlertKind.Info, "Update failed", ex.Message);
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateMenu();
        }
    }

    private void RefreshUpdateMenu()
    {
        if (_updateMenuItem == null) return;
        _updateMenuItem.Visible = _availableUpdate != null;
        _updateMenuItem.Enabled = !_updateBusy;
        _updateMenuItem.Text = _updateBusy ? "Updating…" : $"Install update v{_availableUpdate?.Version}";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tick?.Stop();
        _ledger?.Dispose();
        Sales?.Dispose();
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
        _itemLookup?.ApplySettings();
        UpdateOverlayVisibility();

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
            _ledger.IssuesFound += OnLedgerIssues;
            _ = BackfillSalesAsync();
        }

        _lastMeterRefresh = DateTime.MinValue;
        OnTick();
        if (save) SettingsStore.Save(Settings);
    }

    public void RestartTimer(SpawnTimerState timer)
    {
        _activeIndex = Math.Max(0, Timers.IndexOf(timer));
        timer.Restart(DateTime.UtcNow, DateTime.UtcNow);
        SettingsStore.Save(Settings);
        OnTick();
    }

    /// <summary>Back to READY and forget the running countdown.</summary>
    public void ClearTimer(SpawnTimerState timer)
    {
        timer.Reset(DateTime.UtcNow);
        SettingsStore.Save(Settings);
        OnTick();
    }

    /// <summary>
    /// Deletes a timer right away (no confirmation: nothing may pull focus from the game).
    /// A removed auto-created named timer is remembered so the next kill doesn't recreate it.
    /// </summary>
    public void RemoveTimer(TimerDef def)
    {
        var detail = "Removed with its camp history.";
        if (def.AutoCreated)
        {
            if (!NameMatcher.MatchesAny(def.Name, Settings.Named.NoTimerNames))
            {
                Settings.Named.NoTimerNames = string.IsNullOrWhiteSpace(Settings.Named.NoTimerNames)
                    ? def.Name
                    : Settings.Named.NoTimerNames.TrimEnd() + Environment.NewLine + def.Name;
            }
            detail = "Won't be recreated on the next kill. To undo, delete it from Settings ▸ Alerts ▸ \"Don't create timers for\".";
        }
        Settings.Timers.Remove(def);
        ApplySettings();
        Banner(AlertKind.Info, $"Timer removed: {def.Name}", detail);
    }

    /// <summary>Starts a new session from now (meter button, tray and Settings).</summary>
    public void ResetSession()
    {
        Session.Reset();
        _lastMeterRefresh = DateTime.MinValue;
        OnTick();
        Banner(AlertKind.Info, "Session restarted", $"Coin, corpses and items count from {DateTime.Now:t}.");
    }

    /// <summary>Stops every running/paused timer and sets it back to READY (camp history is kept). True if any changed.</summary>
    private bool ResetAllTimers()
    {
        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var t in Timers)
        {
            if (t.Phase == TimerPhase.Idle && t.Def.LastCycleStartUtc == null) continue;
            t.Reset(now);
            changed = true;
        }
        if (changed)
        {
            SettingsStore.Save(Settings);
            OnTick();
        }
        return changed;
    }

    /// <summary>Starts a new session at <paramref name="start"/> and rebuilds it from the Ledger entries since then.</summary>
    private void StartNewSession(DateTimeOffset start, string reason, bool announce = true)
    {
        Session.Reset(start);
        foreach (var e in _ledgerHistory) Session.Apply(e); // earlier entries only update party state
        _lastMeterRefresh = DateTime.MinValue;
        if (announce)
            Banner(AlertKind.Info, "New session", $"Started {reason} ({start.LocalDateTime:t}). Coin and corpses count from here.");
    }

    /// <summary>True while Monsters &amp; Memories is running (as of the last check).</summary>
    public bool GameRunning { get; private set; }

    private bool _gameStateKnown;

    /// <summary>
    /// Tracks whether the game (mnm.exe) is running, which shows or hides the overlays.
    /// The session follows the game: a launch starts a new session from the launch time (also when Spawn
    /// Timer starts while the game is already running); closing the game ends the session, freezing its totals.
    /// </summary>
    private void CheckGameState()
    {
        var running = false;
        DateTime? gameStart = null;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("mnm"))
        {
            running = true;
            try
            {
                if (gameStart == null || p.StartTime < gameStart) gameStart = p.StartTime;
            }
            // StartTime is denied when the game runs as administrator and Spawn Timer doesn't.
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            finally
            {
                p.Dispose();
            }
        }

        var firstLook = !_gameStateKnown;
        _gameStateKnown = true;
        var wasRunning = GameRunning;
        GameRunning = running;
        if (firstLook || running != wasRunning) UpdateOverlayVisibility();
        if (wasRunning && !running && _availableUpdate != null) InstallUpdateAfterGameCloses();

        if (!Settings.Meter.AutoNewSession) return;

        if (running)
        {
            // Unreadable launch time: treat the first sighting as the launch.
            var started = gameStart ?? _seenGameStart ?? DateTime.Now;
            if (started == _seenGameStart && !Session.IsEnded) return;
            _seenGameStart = started;
            var launch = new DateTimeOffset(started);
            // A new launch (or the first look at an already-running game) starts the session at the launch time.
            if (firstLook || Session.IsEnded || launch > Session.Start.AddMinutes(1))
                StartNewSession(launch, "when the game launched", announce: !firstLook);
        }
        else
        {
            if (!Session.IsEnded)
            {
                Session.Stop(DateTimeOffset.Now);
                _lastMeterRefresh = DateTime.MinValue;
                var timersReset = Settings.ResetTimersWhenGameCloses && ResetAllTimers();
                if (wasRunning)
                    Banner(AlertKind.Info, "Game closed",
                        $"Session ended: {CoinFormat.Format(Session.LootCoinCp)} looted, {Session.Kills} corpses." + (timersReset ? " Timers reset." : ""));
            }
        }
    }

    /// <summary>
    /// From the Settings window (owner given): Save As dialog, then offer to open the folder.
    /// From the tray (no owner): no dialogs at all. Saves to Documents\Spawn Timer Sessions and shows a banner,
    /// so you never have to leave the game.
    /// </summary>
    public void ExportSession(Window? owner = null)
    {
        var interactive = owner != null;
        if (Session.Events.Count == 0)
        {
            var reason = Settings.Ledger.Enabled
                ? "Nothing recorded yet this session. Loot something first."
                : "Session data comes from the game's Ledger. Turn on Settings ▸ Game data first.";
            if (interactive) MessageBox.Show(owner!, reason, "Export session");
            else Banner(AlertKind.Info, "Nothing to export", reason);
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

        string path;
        if (interactive)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export session to CSV",
                Filter = "CSV file (*.csv)|*.csv",
                InitialDirectory = folder,
                FileName = SessionExport.DefaultFileName(Session),
                AddExtension = true,
                DefaultExt = ".csv",
            };
            if (dialog.ShowDialog(owner) != true) return;
            path = dialog.FileName;
        }
        else
        {
            // Unique name so a second export in the same minute doesn't fail on an open/locked file.
            path = System.IO.Path.Combine(folder, $"MnM session {Session.Start.LocalDateTime:yyyy-MM-dd HHmm} (saved {DateTime.Now:HHmmss}).csv");
        }

        string mobsPath;
        try
        {
            mobsPath = SessionExport.Write(Session, path, Sales == null ? null : Sales.UnitPrice);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            var msg = $"Couldn't write the file: {ex.Message}. If it's open in Excel, close it and try again.";
            if (interactive) MessageBox.Show(owner!, msg, "Export session");
            else Banner(AlertKind.Info, "Export failed", msg);
            return;
        }

        if (!interactive)
        {
            Banner(AlertKind.Info, "Session exported",
                $"{Session.Events.Count} events, {Session.Mobs.Count} mobs → Documents\\Spawn Timer Sessions\\{System.IO.Path.GetFileName(path)}");
            return;
        }

        var open = MessageBox.Show(owner!,
            $"Exported {Session.Events.Count} events and {Session.Mobs.Count} mobs:\n\n{path}\n{mobsPath}\n\nOpen the folder?",
            "Export session", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
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
            t.ShowControls = unlocked;
        }

        if (now - _lastMeterRefresh >= TimeSpan.FromSeconds(1) && _meterOverlay is { IsVisible: true })
        {
            _lastMeterRefresh = now;
            _meterOverlay.Refresh(Session, Settings.Meter, Settings.Ledger.Enabled, LedgerStatus,
                Sales == null ? null : Sales.UnitPrice);
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
            state.ResumeSavedCycle(DateTime.UtcNow);
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
        Bind(h.ItemSearch, () => _itemLookup?.Toggle(LookupMode.Items));
        Bind(h.MobSearch, () => _itemLookup?.Toggle(LookupMode.Mobs));

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

    /// <summary>While the item lookup is open: hotkeys still work (incl. closing it), every other key goes to the box.</summary>
    private bool CaptureKey(int vk, System.Windows.Input.ModifierKeys mods)
    {
        var hotkey = new Hotkey(mods, System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk));
        if (OnGlobalKey(hotkey)) return true;
        return _itemLookup?.HandleKey(vk, mods) ?? false;
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

    private void OpenSalesDb()
    {
        try
        {
            Sales = SalesDb.Open();
            // New prices re-value the meter's loot and the lookup cards.
            Sales.PricesChanged += () => Dispatcher.InvokeAsync(() => _lastMeterRefresh = DateTime.MinValue);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException
                                       or UnauthorizedAccessException or DllNotFoundException or TypeInitializationException)
        {
            Sales = null;
            _salesStatus = ex.Message;
            // Runs before the overlays exist; show the banner once startup is done.
            var message = $"Item values are off this run: {ex.Message}";
            Dispatcher.BeginInvoke(() => Banner(AlertKind.Info, "Sales database unavailable", message), DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Once per run: import every merchant sale already in the Ledger files (all characters) in the background.</summary>
    private async Task BackfillSalesAsync()
    {
        if (Sales == null || _salesBackfillStarted) return;
        _salesBackfillStarted = true;
        _salesStatus = "reading your Ledger history…";
        try
        {
            var added = await Sales.BackfillAsync(LedgerWatcher.GameDataRoot);
            _salesStatus = added > 0 ? $"{added} sales imported from your Ledger history" : "";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or UnauthorizedAccessException)
        {
            _salesStatus = "history import failed: " + ex.Message;
        }
        _lastMeterRefresh = DateTime.MinValue;
    }

    private void OnLedgerIssues(IReadOnlyList<LedgerIssue> issues)
    {
        foreach (var (title, message) in LedgerHealth.Record(issues, _appStart))
            Banner(AlertKind.Info, title, message);
    }

    private void OnLedgerEvents(IReadOnlyList<LedgerEvent> events)
    {
        var now = DateTime.UtcNow;
        LedgerHealth.Observe(events);
        try
        {
            Sales?.Import(events);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            _salesStatus = "couldn't save a sale: " + ex.Message;
        }
        var historyChanged = false;
        foreach (var e in events)
        {
            // Anyone acting in or named by the ledger is a player, never a named mob.
            if (e.Actor.Length > 0) _knownPlayers.Add(e.Actor);
            if (e.Owner.Length > 0) _knownPlayers.Add(e.Owner);
            if (!string.IsNullOrEmpty(e.Subject)) _knownPlayers.Add(e.Subject);

            // Long break with no loot: this loot starts a new session.
            var idle = TimeSpan.FromMinutes(Math.Max(5, Settings.Meter.NewSessionAfterIdleMinutes));
            // Measured from the last loot, or from the session start if nothing's been looted yet (e.g. the game was
            // left running overnight). Breaks found while replaying history at startup restart quietly.
            var sinceActivity = e.Time - (Session.LastLootTime ?? Session.Start);
            if (Settings.Meter.AutoNewSession && e.Kind is LedgerKind.ItemLoot or LedgerKind.CoinLoot
                && e.Time >= Session.Start && !Session.IsEnded && sinceActivity > idle)
            {
                StartNewSession(e.Time.AddSeconds(-1), "after a break with no loot", announce: e.Time >= _appStart);
            }

            _ledgerHistory.Add(e);
            Session.Apply(e);

            if (e.Kind is not (LedgerKind.ItemLoot or LedgerKind.CoinLoot)) continue;

            // History replayed on startup: no alerts or restarts, but remember when each named was last looted.
            if (e.Time < _appStart)
            {
                historyChanged |= BackfillNamedSeen(e);
                continue;
            }

            if (e.Kind == LedgerKind.ItemLoot && Settings.DropAlerts.Enabled && !string.IsNullOrEmpty(e.Item)
                && (!Settings.DropAlerts.MineOnly || e.IsMine)
                && NameMatcher.MatchesAny(e.Item, Settings.DropAlerts.ItemNames))
            {
                ShowDropAlert(e.Item, e.Corpse, e.IsMine ? "You" : e.Actor, forceSound: false);
            }

            if (e.Corpse == null) continue;
            if (Settings.Named.Enabled && NamedDetector.IsNamed(e.Corpse, Settings.Named, _knownPlayers))
                OnNamedLooted(e);
            foreach (var timer in Timers)
            {
                var kind = timer.TryAutoRestart(e.Corpse, e.Time.UtcDateTime, now);
                if (kind == AutoRestartKind.Placeholder) ShowPlaceholderDown(timer.Def, e.Corpse);
                historyChanged |= kind != AutoRestartKind.None;
            }
        }
        if (historyChanged) SettingsStore.Save(Settings); // camp history survives restarts
        _lastMeterRefresh = DateTime.MinValue;

        // Keep about three days of entries for session rebuilds.
        var cutoff = DateTimeOffset.Now.AddDays(-3);
        if (_ledgerHistory.Count > 0 && _ledgerHistory[0].Time < cutoff) _ledgerHistory.RemoveAll(x => x.Time < cutoff);

        // Zone changes (seen on loot entries, including history replayed at startup) preload that zone's items.
        // The lookup ignores a zone it already has, and retries one whose load failed.
        var zone = events.LastOrDefault(e => e.Zone.Length > 0)?.Zone;
        if (zone != null) _ = _itemLookup?.OnZoneAsync(zone);
    }

    private bool BackfillNamedSeen(LedgerEvent e)
    {
        if (e.Corpse == null) return false;
        var changed = false;
        var at = e.Time.UtcDateTime;
        foreach (var def in Settings.Timers)
        {
            if (!def.MatchesCorpse(e.Corpse)) continue;
            if (!(def.LastNamedSeenUtc >= at))
            {
                def.LastNamedSeenUtc = at;
                changed = true;
            }

            // A kill newer than the saved cycle: run the countdown from it (UP? if it's already past).
            if (def.AutoStart && !(def.LastCycleStartUtc >= at)
                && Timers.FirstOrDefault(t => ReferenceEquals(t.Def, def)) is { Phase: not TimerPhase.Paused } state)
            {
                def.PlaceholderCycles = 0;
                def.LastCycleWasPlaceholder = false;
                state.Restart(at, DateTime.UtcNow);
                changed = true;
            }
        }
        return changed;
    }

    private void ShowPlaceholderDown(TimerDef def, string corpse)
    {
        var next = DateTime.Now.AddSeconds(def.DurationSec);
        Banner(AlertKind.PlaceholderDown, corpse,
            $"{def.Name} spawn point reset · next spawn ~{next:t} could be the named (PH cycle {def.PlaceholderCycles})");
    }

    // ---- Mob lookup → timers ----

    IReadOnlyList<TimerDef> ITimerLinkHost.TimerDefs => Settings.Timers;

    TimerDef? ITimerLinkHost.SelectedTimerDef => ActiveTimer?.Def;

    TimerDef? ITimerLinkHost.LinkMob(MobTimerAction action, string mob, bool isNamed, TimerDef? target)
    {
        var (changed, title, detail) = TimerLinks.Apply(Settings, action, mob, isNamed, target);
        if (changed == null) return null;
        ApplySettings(); // saves, and syncs the runtime timers (a new one shows on the overlay)
        Banner(AlertKind.Info, title, detail);
        return changed;
    }

    private void OnNamedLooted(LedgerEvent e)
    {
        var corpse = e.Corpse!;
        // One alert per kill: a corpse produces several loot lines within seconds.
        if (_lastNamedAlert.TryGetValue(corpse, out var last) && e.Time - last < TimeSpan.FromSeconds(60)) return;
        _lastNamedAlert[corpse] = e.Time;

        var timer = Settings.Timers.FirstOrDefault(t => t.AutoStart && t.MatchesCorpse(corpse));
        if (timer == null && Settings.Named.AutoTimers && !NameMatcher.MatchesAny(corpse, Settings.Named.NoTimerNames))
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

    private async void ShowDropAlert(string item, string? corpse, string looter, bool forceSound)
    {
        var detail = corpse == null ? $"Looted by {looter}" : $"Looted by {looter} from {corpse}";
        if (await PartyUseLineAsync(item) is { } use) detail += Environment.NewLine + use;
        Banner(AlertKind.Drop, item, detail);
        var s = Settings.DropAlerts;
        _alerts.Drop(forceSound ? new DropAlertSettings { Sound = true, Speak = s.Speak } : s, item);
    }

    /// <summary>
    /// "Usable by: you (CLR), Zetherin (PAL) · Not: …" for a drop banner. Zone items are usually cached already;
    /// otherwise the wiki gets 2 seconds before the banner shows without the line.
    /// </summary>
    private async Task<string?> PartyUseLineAsync(string item)
    {
        var myClass = Settings.ItemLookup.YourClass?.Trim() is { Length: > 0 } set ? set : Session.MyClass;
        var members = Session.PartyMembers(DateTimeOffset.Now);
        if (string.IsNullOrEmpty(myClass) && members.Count == 0) return null;

        var details = ItemDb.TryGetCached(item);
        if (details == null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var found = await ItemDb.GetAsync(new[] { item }, cts.Token);
                found.TryGetValue(item, out details);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
        }
        return PartyUse.Describe(details, members, myClass);
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
        if (def.IsSpawnPoint)
        {
            // The spot can respawn as the named or its placeholder; the Ledger can't tell which.
            var detail = def.PlaceholderCycles > 0
                ? $"Named or placeholder · {def.PlaceholderCycles} PH cycle{(def.PlaceholderCycles == 1 ? "" : "s")} since the named"
                : "Could be the named or its placeholder";
            Banner(AlertKind.SpawnDue, def.Name, detail);
            var s = Settings.Named;
            _alerts.Named(forceSound ? new NamedSettings { Sound = true, Speak = s.Speak } : s, $"{def.Name} spawn up");
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
        UpdateOverlayVisibility();
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

    /// <summary>
    /// Timer and meter overlays show only while the game runs (unless that's turned off), and always while unlocked
    /// so they can be moved. Alert banners always show: they're invisible when empty and report e.g. "Game closed".
    /// </summary>
    private void UpdateOverlayVisibility()
    {
        if (_timerOverlay == null) return;
        var show = GameRunning || !Settings.HideOverlaysWhenGameClosed || _editMode;
        SetOverlayVisible(_timerOverlay, Settings.TimerOverlay.Visible && show);
        SetOverlayVisible(_meterOverlay!, Settings.MeterOverlay.Visible && show);
        SetOverlayVisible(_alertOverlay!, Settings.AlertOverlay.Visible);
        _lastMeterRefresh = DateTime.MinValue; // a meter that just appeared refreshes on the next tick
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
        var timersMenu = new Forms.ToolStripMenuItem("Timers");
        menu.Items.Add(timersMenu);
        // Rebuilt each time so it always lists the current timers.
        menu.Opening += (_, _) => BuildTimersMenu(timersMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        _lockMenuItem = new Forms.ToolStripMenuItem("Unlock overlays (move)", null, (_, _) => SetEditMode(!_editMode));
        menu.Items.Add(_lockMenuItem);
        menu.Items.Add("Export session to CSV…", null, (_, _) => ExportSession());
        _updateMenuItem = new Forms.ToolStripMenuItem("Install update", null, async (_, _) => await InstallUpdateAsync())
        {
            Visible = false,
            Font = new Font(Forms.SystemInformation.MenuFont, System.Drawing.FontStyle.Bold),
        };
        menu.Items.Insert(0, _updateMenuItem);
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesNow());
        menu.Items.Add("Restart session", null, (_, _) => ResetSession());
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

    private void BuildTimersMenu(Forms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        if (Timers.Count == 0)
        {
            parent.DropDownItems.Add(new Forms.ToolStripMenuItem("(no timers)") { Enabled = false });
            return;
        }
        foreach (var timer in Timers.ToList())
        {
            var t = timer;
            var item = new Forms.ToolStripMenuItem($"{t.Name}   {t.DisplayText} {t.StatusText}".TrimEnd());
            item.DropDownItems.Add("Restart now", null, (_, _) => RestartTimer(t));
            item.DropDownItems.Add("Clear (back to ready)", null, (_, _) => ClearTimer(t));
            item.DropDownItems.Add(new Forms.ToolStripSeparator());
            item.DropDownItems.Add("Remove timer", null, (_, _) => RemoveTimer(t.Def));
            parent.DropDownItems.Add(item);
        }
    }

    /// <summary>The app icon at the tray's size for this display's scaling (16, 20, 24 or 32 px).</summary>
    private static Icon MakeTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/SpawnTimer.ico"));
        if (resource == null) return SystemIcons.Application;
        using var stream = resource.Stream;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
