using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnTimer.Core;

/// <summary>What the mob lookup can do to a timer for the highlighted mob.</summary>
public enum MobTimerAction { Restart, Placeholder, NewTimer, RemoveRestart, RemovePlaceholder }

/// <summary>The app side of the mob lookup's timer actions (the lookup lives in the UI layer).</summary>
public interface ITimerLinkHost
{
    IReadOnlyList<TimerDef> TimerDefs { get; }

    /// <summary>The ▶ selected timer.</summary>
    TimerDef? SelectedTimerDef { get; }

    /// <summary>Applies the action, saves and confirms with a banner. Returns the timer it changed or created.</summary>
    TimerDef? LinkMob(MobTimerAction action, string mob, bool isNamed, TimerDef? target);
}

/// <summary>Edits the one-name-per-line mob lists on timers (restart names, placeholder names).</summary>
public static class TimerLinks
{
    private static IEnumerable<string> Lines(string? list) =>
        (list ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>The exact name is on its own line (wildcards like "a skeletal*" don't count; they can't be removed per mob).</summary>
    public static bool HasExact(string? list, string name) =>
        Lines(list).Any(l => l.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Add(string? list, string name)
    {
        if (HasExact(list, name)) return list ?? "";
        var lines = Lines(list).ToList();
        lines.Add(name.Trim());
        return string.Join(Environment.NewLine, lines);
    }

    public static string Remove(string? list, string name) =>
        string.Join(Environment.NewLine, Lines(list).Where(l => !l.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Applies a mob lookup action to the settings: a new timer (set up like the automatic named timers), or adding/
    /// removing the mob as a restart name or placeholder of <paramref name="target"/>. A mob is never both for one
    /// timer. Returns the timer changed or created (null if nothing applied) and a banner title and detail.
    /// </summary>
    public static (TimerDef? Changed, string Title, string Detail) Apply(
        AppSettings settings, MobTimerAction action, string mob, bool isNamed, TimerDef? target)
    {
        switch (action)
        {
            case MobTimerAction.NewTimer:
                if (settings.Timers.Any(x => x.MatchesCorpse(mob))) return (null, "", "");
                var def = new TimerDef
                {
                    Name = mob,
                    DurationSec = settings.Named.DefaultRespawnSec,
                    WarnSec = 60,
                    AutoStart = true,
                    MobNames = mob,
                    AutoCreated = isNamed,
                };
                settings.Timers.Add(def);
                settings.Named.NoTimerNames = Remove(settings.Named.NoTimerNames, mob); // it was "removed" before
                return (def, "Timer added",
                    $"{mob}: {TimeFormat.Clock(TimeSpan.FromSeconds(def.DurationSec))}, restarts when it's looted. Change the time in Settings ▸ Timers.");
            case MobTimerAction.Restart when target != null:
                target.MobNames = Add(target.MobNames, mob);
                target.PlaceholderNames = Remove(target.PlaceholderNames, mob);
                target.AutoStart = true;
                return (target, "Restart mob added", $"Looting {mob} restarts {target.Name}.");
            case MobTimerAction.Placeholder when target != null:
                target.PlaceholderNames = Add(target.PlaceholderNames, mob);
                target.MobNames = Remove(target.MobNames, mob);
                return (target, "Placeholder added",
                    $"{mob} is a placeholder for {target.Name}: looting it while that timer is at zero starts a new cycle.");
            case MobTimerAction.RemoveRestart when target != null:
                target.MobNames = Remove(target.MobNames, mob);
                return (target, "Restart mob removed", $"{mob} no longer restarts {target.Name}.");
            case MobTimerAction.RemovePlaceholder when target != null:
                target.PlaceholderNames = Remove(target.PlaceholderNames, mob);
                return (target, "Placeholder removed", $"{mob} is no longer a placeholder for {target.Name}.");
            default:
                return (null, "", "");
        }
    }

    /// <summary>
    /// The timer the lookup's actions should apply to for <paramref name="mob"/>: one it's already linked to, else a
    /// camp timer for a named mob in the same zone (the usual home for placeholders), else the selected timer.
    /// </summary>
    public static TimerDef? DefaultTarget(MobInfo mob, IReadOnlyList<MobInfo> knownMobs, IReadOnlyList<TimerDef> timers, TimerDef? selected)
    {
        var linked = timers.FirstOrDefault(t => t.MatchesCorpse(mob.Name) || t.MatchesPlaceholder(mob.Name));
        if (linked != null) return linked;
        var zoneNamed = knownMobs.Where(m => m.IsNamed && m.Name != mob.Name
                                             && string.Equals(m.Zone, mob.Zone, StringComparison.OrdinalIgnoreCase)).ToList();
        var camp = timers.FirstOrDefault(t => zoneNamed.Any(n => t.MatchesCorpse(n.Name)));
        return camp ?? selected ?? timers.FirstOrDefault();
    }
}
