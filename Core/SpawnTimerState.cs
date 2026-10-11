using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SpawnTimer.Core;

public enum TimerPhase { Idle, Running, Paused }

public enum TimerLook { Idle, Running, Warning, Expired, Paused }

public enum AutoRestartKind { None, Named, Placeholder }

/// <summary>Runtime state of one timer. Counts past zero ("overtime") until stopped or restarted.</summary>
public sealed class SpawnTimerState : Observable
{
    private static readonly TimeSpan AutoRestartDebounce = TimeSpan.FromSeconds(30);

    private TimerPhase _phase = TimerPhase.Idle;
    private DateTime _endUtc;
    private TimeSpan _pausedRemaining;
    private bool _warned;
    private bool _fired;
    private DateTime _lastAutoUtc = DateTime.MinValue;
    private string _lastAutoCorpse = "";

    private string _displayText = "";
    private string _statusText = "";
    private string _historyText = "";
    private double _progress = 1;
    private TimerLook _look = TimerLook.Idle;
    private bool _isActive;
    private bool _showInOverlay = true;

    public SpawnTimerState(TimerDef def)
    {
        Def = def;
        Def.PropertyChanged += OnDefChanged;
        Tick(DateTime.UtcNow);
    }

    public TimerDef Def { get; }
    public string Name => Def.Name;
    public TimerPhase Phase => _phase;

    public string DisplayText { get => _displayText; private set => Set(ref _displayText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Camp history for spawn-point timers, e.g. "after PH · named 1h 12m ago · 3 PH cycles".</summary>
    public string HistoryText { get => _historyText; private set => Set(ref _historyText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public TimerLook Look { get => _look; private set => Set(ref _look, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public bool ShowInOverlay { get => _showInOverlay; set => Set(ref _showInOverlay, value); }

    public event Action<SpawnTimerState>? Warned;
    public event Action<SpawnTimerState>? Expired;

    private TimeSpan Duration => TimeSpan.FromSeconds(Def.DurationSec);

    /// <summary>Idle: start full. Paused: resume. Running: no change.</summary>
    public void Start(DateTime nowUtc)
    {
        switch (_phase)
        {
            case TimerPhase.Running:
                return;
            case TimerPhase.Paused:
                _endUtc = nowUtc + _pausedRemaining;
                break;
            default:
                _endUtc = nowUtc + Duration;
                _warned = _fired = false;
                break;
        }
        _phase = TimerPhase.Running;
        Tick(nowUtc);
    }

    /// <summary>Running: pause. Paused or idle: reset to full (so pressing Stop twice resets).</summary>
    public void Stop(DateTime nowUtc)
    {
        if (_phase == TimerPhase.Running)
        {
            _pausedRemaining = _endUtc - nowUtc;
            _phase = TimerPhase.Paused;
        }
        else
        {
            _phase = TimerPhase.Idle;
            Def.LastCycleStartUtc = null; // reset by the user: don't resume it after a restart
        }
        Tick(nowUtc);
    }

    /// <summary>Run a full countdown measured from <paramref name="startUtc"/> (may be slightly in the past).</summary>
    public void Restart(DateTime startUtc, DateTime nowUtc)
    {
        Def.LastCycleStartUtc = startUtc;
        _endUtc = startUtc + Duration;
        var remaining = _endUtc - nowUtc;
        _warned = remaining.TotalSeconds <= Def.WarnSec;
        _fired = remaining <= TimeSpan.Zero;
        _phase = TimerPhase.Running;
        Tick(nowUtc);
    }

    /// <summary>
    /// Ledger-driven restart. The named restarts the spawn point and resets the placeholder count. A placeholder
    /// restarts it only while the spawn window is open (timer at zero or idle). The Ledger has no locations, so
    /// this keeps same-named mobs elsewhere from resetting a countdown that's still running.
    /// </summary>
    public AutoRestartKind TryAutoRestart(string corpse, DateTime eventUtc, DateTime nowUtc)
    {
        if (!Def.AutoStart) return AutoRestartKind.None;

        if (Def.MatchesCorpse(corpse))
        {
            // Repeat loot lines from the same corpse.
            if (string.Equals(corpse, _lastAutoCorpse, StringComparison.OrdinalIgnoreCase)
                && eventUtc - _lastAutoUtc < AutoRestartDebounce)
                return AutoRestartKind.None;

            _lastAutoCorpse = corpse;
            _lastAutoUtc = eventUtc;
            Restart(eventUtc, nowUtc);
            Def.LastNamedSeenUtc = eventUtc;
            Def.PlaceholderCycles = 0;
            Def.LastCycleWasPlaceholder = false;
            return AutoRestartKind.Named;
        }

        if (Def.MatchesPlaceholder(corpse) && SpawnWindowOpen(eventUtc))
        {
            // Restarting closes the window, so further loot lines from this corpse are ignored automatically.
            _lastAutoCorpse = corpse;
            _lastAutoUtc = eventUtc;
            Restart(eventUtc, nowUtc);
            Def.PlaceholderCycles++;
            Def.LastCycleWasPlaceholder = true;
            return AutoRestartKind.Placeholder;
        }

        return AutoRestartKind.None;
    }

    private bool SpawnWindowOpen(DateTime atUtc) => _phase switch
    {
        TimerPhase.Idle => true,
        TimerPhase.Running => _endUtc <= atUtc,
        _ => false, // paused by the user
    };

    public void Tick(DateTime nowUtc)
    {
        var remaining = _phase switch
        {
            TimerPhase.Running => _endUtc - nowUtc,
            TimerPhase.Paused => _pausedRemaining,
            _ => Duration,
        };

        if (_phase == TimerPhase.Running)
        {
            if (!_warned && Def.WarnSec > 0 && remaining > TimeSpan.Zero && remaining.TotalSeconds <= Def.WarnSec)
            {
                _warned = true;
                Warned?.Invoke(this);
            }
            if (!_fired && remaining <= TimeSpan.Zero)
            {
                _fired = true;
                Expired?.Invoke(this);
            }
        }

        DisplayText = remaining > TimeSpan.Zero
            ? TimeFormat.Clock(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)))
            : "+" + TimeFormat.Clock(TimeSpan.FromSeconds(Math.Floor(-remaining.TotalSeconds)));

        Progress = _phase == TimerPhase.Idle
            ? 1
            : Math.Clamp(remaining.TotalSeconds / Math.Max(1, Def.DurationSec), 0, 1);

        Look = _phase switch
        {
            TimerPhase.Idle => TimerLook.Idle,
            TimerPhase.Paused => TimerLook.Paused,
            _ when remaining <= TimeSpan.Zero => TimerLook.Expired,
            _ when remaining.TotalSeconds <= Def.WarnSec => TimerLook.Warning,
            _ => TimerLook.Running,
        };

        StatusText = Look switch
        {
            TimerLook.Idle => "READY",
            TimerLook.Paused => "PAUSED",
            TimerLook.Expired => Def.IsSpawnPoint ? "UP?" : "UP!",
            _ => "",
        };

        HistoryText = Def.IsSpawnPoint ? BuildHistory(nowUtc, remaining) : "";
    }

    private string BuildHistory(DateTime nowUtc, TimeSpan remaining)
    {
        // Short enough to fit under the bar: "PH cycle 2 · named 47m ago".
        var parts = new List<string>(2);
        if (Def.PlaceholderCycles > 0)
            parts.Add(Def.LastCycleWasPlaceholder && _phase == TimerPhase.Running && remaining > TimeSpan.Zero
                ? $"PH cycle {Def.PlaceholderCycles}"
                : $"{Def.PlaceholderCycles} PH since named");
        parts.Add(Def.LastNamedSeenUtc is { } seen ? $"named {TimeFormat.Ago(nowUtc - seen)}" : "named not seen yet");
        return string.Join(" · ", parts);
    }

    /// <summary>Resume a spawn-point countdown saved before the app closed (no alerts for time already passed).</summary>
    public void ResumeSavedCycle(DateTime nowUtc)
    {
        if (!Def.IsSpawnPoint || Def.LastCycleStartUtc is not { } start) return;
        if (nowUtc - start > TimeSpan.FromHours(24)) return;
        Restart(start, nowUtc);
    }

    public void Detach() => Def.PropertyChanged -= OnDefChanged;

    private void OnDefChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimerDef.Name)) Raise(nameof(Name));
        else Tick(DateTime.UtcNow);
    }
}
