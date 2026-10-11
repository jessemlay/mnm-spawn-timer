using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnTimer.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A user-defined spawn timer (what gets saved).</summary>
public sealed class TimerDef : Observable
{
    private string _name = "Spawn";
    private int _durationSec = 400;
    private int _warnSec = 30;
    private string _restartHotkey = "";
    private bool _autoStart;
    private string _mobNames = "";
    private bool _autoCreated;
    private string _placeholderNames = "";
    private DateTime? _lastNamedSeenUtc;
    private int _placeholderCycles;
    private bool _lastCycleWasPlaceholder;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get => _name; set { if (Set(ref _name, value ?? "")) Raise(nameof(ListLabel)); } }

    public int DurationSec
    {
        get => _durationSec;
        set { if (Set(ref _durationSec, Math.Max(1, value))) Raise(nameof(DurationText)); }
    }

    /// <summary>Editable "m:ss" form of <see cref="DurationSec"/>; invalid input is ignored.</summary>
    [JsonIgnore]
    public string DurationText
    {
        get => TimeFormat.Clock(TimeSpan.FromSeconds(DurationSec));
        set
        {
            if (TimeFormat.TryParseDuration(value, out var seconds)) DurationSec = seconds;
            Raise();
        }
    }

    public int WarnSec { get => _warnSec; set => Set(ref _warnSec, Math.Max(0, value)); }

    /// <summary>Optional hotkey that restarts this specific timer and makes it active.</summary>
    public string RestartHotkey { get => _restartHotkey; set => Set(ref _restartHotkey, value ?? ""); }

    /// <summary>Restart automatically when a corpse matching <see cref="MobNames"/> is looted (needs game data).</summary>
    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }

    /// <summary>One corpse name per line. A trailing * matches any name starting with the text.</summary>
    public string MobNames { get => _mobNames; set => Set(ref _mobNames, value ?? ""); }

    /// <summary>True for timers created automatically when a named mob was looted.</summary>
    public bool AutoCreated
    {
        get => _autoCreated;
        set { if (Set(ref _autoCreated, value)) { Raise(nameof(ListLabel)); Raise(nameof(IsSpawnPoint)); } }
    }

    /// <summary>
    /// Mobs that can spawn in the named's place. Looting one while the spawn window is open restarts the timer.
    /// </summary>
    public string PlaceholderNames
    {
        get => _placeholderNames;
        set { if (Set(ref _placeholderNames, value ?? "")) Raise(nameof(IsSpawnPoint)); }
    }

    // Camp history, saved so it survives restarts.
    public DateTime? LastNamedSeenUtc { get => _lastNamedSeenUtc; set { if (Set(ref _lastNamedSeenUtc, value)) Raise(nameof(HistorySummary)); } }
    public int PlaceholderCycles { get => _placeholderCycles; set { if (Set(ref _placeholderCycles, Math.Max(0, value))) Raise(nameof(HistorySummary)); } }
    public bool LastCycleWasPlaceholder { get => _lastCycleWasPlaceholder; set => Set(ref _lastCycleWasPlaceholder, value); }

    /// <summary>When the current countdown started (named/PH kill or manual restart), so it can resume after an app restart.</summary>
    public DateTime? LastCycleStartUtc { get; set; }

    /// <summary>A spawn point can be the named or its placeholder, so "up" is a maybe.</summary>
    [JsonIgnore]
    public bool IsSpawnPoint => AutoCreated || !string.IsNullOrWhiteSpace(PlaceholderNames);

    [JsonIgnore]
    public string ListLabel => AutoCreated ? $"{Name}  (named)" : Name;

    [JsonIgnore]
    public string HistorySummary
    {
        get
        {
            var seen = LastNamedSeenUtc is { } t ? $"{t.ToLocalTime():g} ({TimeFormat.Ago(DateTime.UtcNow - t)})" : "not seen yet";
            return $"Named last looted: {seen}.  Placeholder cycles since: {PlaceholderCycles}.";
        }
    }

    public bool MatchesCorpse(string corpse) => NameMatcher.MatchesAny(corpse, MobNames, allowCommas: true);

    public bool MatchesPlaceholder(string corpse) => NameMatcher.MatchesAny(corpse, PlaceholderNames, allowCommas: true);

    public void ResetHistory()
    {
        LastNamedSeenUtc = null;
        LastCycleStartUtc = null;
        PlaceholderCycles = 0;
        LastCycleWasPlaceholder = false;
    }
}

public sealed class HotkeySettings
{
    public string Start { get; set; } = "Ctrl+Alt+1";
    public string Stop { get; set; } = "Ctrl+Alt+2";
    public string Restart { get; set; } = "Ctrl+Alt+3";
    public string NextTimer { get; set; } = "Ctrl+Alt+4";
    public string ToggleEdit { get; set; } = "Ctrl+Alt+0";

    /// <summary>When true the game still receives the key press after the timer handles it.</summary>
    public bool PassThrough { get; set; }

    /// <summary>Holding Ctrl+Shift temporarily makes overlays draggable without unlocking them.</summary>
    public bool HoldToMove { get; set; } = true;
}

public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 55;
    public bool HideIdle { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class MeterSettings
{
    public bool ShowZone { get; set; } = true;
    public bool ShowSessionTime { get; set; } = true;
    public bool ShowKills { get; set; } = true;
    public bool ShowKillRate { get; set; }
    public bool ShowCoin { get; set; } = true;
    public bool ShowCoinRate { get; set; } = true;
    public bool ShowItems { get; set; } = true;
    public bool ShowLevels { get; set; } = true;
    public bool ShowTopMobs { get; set; } = true;
    public int TopMobsCount { get; set; } = 5;
    public bool ShowRecentLoot { get; set; } = true;
    public int RecentLootCount { get; set; } = 5;

    /// <summary>Recent loot drops off the meter after this many minutes; 0 = keep showing it.</summary>
    public int RecentLootMinutes { get; set; } = 15;

    /// <summary>Per-hour rates follow the last ~N minutes and fade to 0 when you stop; 0 = whole-session average.</summary>
    public int RateWindowMinutes { get; set; } = 10;
}

public sealed class LedgerSettings
{
    public bool Enabled { get; set; }
    public string FolderOverride { get; set; } = "";
}

public sealed class AlertSettings
{
    public bool SoundOnSpawn { get; set; } = true;
    public bool SoundOnWarning { get; set; } = true;
    public bool SpeakOnSpawn { get; set; }
    public bool BannerOnTimerUp { get; set; } = true;
    public int BannerSeconds { get; set; } = 8;
}

/// <summary>Alert when specific items are looted by you or your party (from the Ledger).</summary>
public sealed class DropAlertSettings
{
    public bool Enabled { get; set; } = true;
    public string ItemNames { get; set; } = "";
    public bool MineOnly { get; set; }
    public bool Sound { get; set; } = true;
    public bool Speak { get; set; }
}

/// <summary>
/// Named-mob tracking. The game logs loot, not spawns, so "spawn due" alerts are predicted: loot time + respawn time.
/// </summary>
public sealed class NamedSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoDetect { get; set; } = true;
    public string WatchNames { get; set; } = "";
    public string IgnoreNames { get; set; } = "";
    public bool AlertOnLoot { get; set; } = true;
    public bool AutoTimers { get; set; } = true;

    /// <summary>Named mobs whose auto-created timer was removed; they still alert but don't get a timer again.</summary>
    public string NoTimerNames { get; set; } = "";

    public int DefaultRespawnSec { get; set; } = 1200;
    public bool Sound { get; set; } = true;
    public bool Speak { get; set; }

    [JsonIgnore]
    public string DefaultRespawnText
    {
        get => TimeFormat.Clock(TimeSpan.FromSeconds(DefaultRespawnSec));
        set
        {
            if (TimeFormat.TryParseDuration(value, out var seconds)) DefaultRespawnSec = seconds;
        }
    }
}

public sealed class AppSettings
{
    public ObservableCollection<TimerDef> Timers { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public OverlaySettings TimerOverlay { get; set; } = new();
    public OverlaySettings MeterOverlay { get; set; } = new() { Visible = false, Scale = 0.9 };
    public MeterSettings Meter { get; set; } = new();
    public LedgerSettings Ledger { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public DropAlertSettings DropAlerts { get; set; } = new();
    public NamedSettings Named { get; set; } = new();
    public OverlaySettings AlertOverlay { get; set; } = new() { BackgroundOpacity = 75 };
    public bool FirstRun { get; set; } = true;
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpawnTimer");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
        }
        catch (Exception)
        {
            // Keep the unreadable file for inspection and start fresh.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch (IOException) { }
        }

        settings ??= new AppSettings();
        settings.Timers ??= new ObservableCollection<TimerDef>();
        settings.Hotkeys ??= new HotkeySettings();
        settings.TimerOverlay ??= new OverlaySettings();
        settings.MeterOverlay ??= new OverlaySettings { Visible = false };
        settings.Meter ??= new MeterSettings();
        settings.Ledger ??= new LedgerSettings();
        settings.Alerts ??= new AlertSettings();
        settings.DropAlerts ??= new DropAlertSettings();
        settings.Named ??= new NamedSettings();
        settings.AlertOverlay ??= new OverlaySettings { BackgroundOpacity = 75 };
        if (settings.Timers.Count == 0) settings.Timers.Add(new TimerDef());

        // Guard against duplicate ids from hand-edited files.
        var ids = new HashSet<Guid>();
        foreach (var t in settings.Timers.Where(t => !ids.Add(t.Id))) t.Id = Guid.NewGuid();

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
