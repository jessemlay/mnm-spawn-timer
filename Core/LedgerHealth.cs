using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SpawnTimer.Core;

/// <summary>
/// Watches for signs that a game patch changed the Ledger format: entry types the app doesn't know, entries or
/// details it can't read, values it relies on going missing. Serious problems raise one banner per kind per run;
/// everything is summarized in Settings ▸ Game data and appended to ledger-health.log. UI thread only.
/// </summary>
public sealed class LedgerHealth
{
    private const long MaxLogBytes = 256 * 1024;

    private readonly Dictionary<(LedgerIssueKind Kind, string Detail), (int Count, DateTime FirstSeen)> _issues = new();
    /// <summary>Banner categories already shown this run: new entry types, and everything else ("format").</summary>
    private readonly HashSet<bool> _bannered = new();
    private readonly Dictionary<string, int> _todayByAction = new(StringComparer.Ordinal);
    private DateTime _today = DateTime.Today;
    private int _entriesRead;

    public static string LogPath => Path.Combine(SettingsStore.CacheFolder, "ledger-health.log");

    public DateTimeOffset? LastEntryTime { get; private set; }
    public int SeriousCount => _issues.Where(i => i.Key.Kind != LedgerIssueKind.NewField).Sum(i => i.Value.Count);

    /// <summary>Counts entries as they arrive, for the status panel.</summary>
    public void Observe(IEnumerable<LedgerEvent> events)
    {
        if (DateTime.Today != _today)
        {
            _today = DateTime.Today;
            _todayByAction.Clear();
        }
        foreach (var e in events)
        {
            _entriesRead++;
            if (LastEntryTime == null || e.Time > LastEntryTime) LastEntryTime = e.Time;
            if (e.Time.LocalDateTime.Date == _today)
                _todayByAction[e.Action] = _todayByAction.GetValueOrDefault(e.Action) + 1;
        }
    }

    /// <summary>
    /// Records newly seen issues. Returns banners to show: the first serious problem of each kind this run,
    /// counting only entries from <paramref name="bannerSince"/> on (old entries already read fine back then).
    /// </summary>
    public List<(string Title, string Message)> Record(IReadOnlyList<LedgerIssue> issues, DateTimeOffset bannerSince)
    {
        var banners = new List<(string, string)>();
        var log = new StringBuilder();
        foreach (var issue in issues)
        {
            var key = (issue.Kind, issue.Detail);
            if (_issues.TryGetValue(key, out var seen)) _issues[key] = (seen.Count + 1, seen.FirstSeen);
            else
            {
                _issues[key] = (1, DateTime.Now);
                log.Append(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {issue.Kind,-13} {issue.Detail}");
                if (issue.Time is { } t) log.Append(CultureInfo.InvariantCulture, $"  (entry {t:yyyy-MM-dd HH:mm:ss})");
                log.AppendLine();
            }
        }
        AppendLog(log.ToString());

        foreach (var group in issues.Where(i => i.Serious && (i.Time == null || i.Time >= bannerSince))
                     .GroupBy(i => i.Kind == LedgerIssueKind.UnknownAction))
        {
            if (!_bannered.Add(group.Key)) continue;
            var example = group.First().Detail;
            var count = group.Count();
            banners.Add(group.Key
                ? ("Ledger: new entry type",
                   $"The game logged {string.Join(", ", group.Select(i => i.Detail).Distinct().Take(3))}, which Spawn Timer doesn't know yet. " +
                   "Totals may be missing something. Details in Settings ▸ Game data.")
                : ("Ledger format changed?",
                   $"{count} {(count == 1 ? "entry" : "entries")} couldn't be fully read (e.g. {example}). " +
                   "Coin or loot may be undercounted. Details in Settings ▸ Game data."));
        }
        return banners;
    }

    /// <summary>Multi-line summary for Settings ▸ Game data.</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            LastEntryTime is { } last
                ? $"Last entry: {last.LocalDateTime:g} ({TimeFormat.Ago(DateTimeOffset.Now - last)})."
                : "No entries read yet.",
        };
        if (_todayByAction.Count > 0)
            lines.Add("Today: " + string.Join(" · ", _todayByAction.OrderByDescending(kv => kv.Value).Select(kv => $"{Label(kv.Key)} {kv.Value}")) + ".");
        var serious = _issues.Where(i => i.Key.Kind != LedgerIssueKind.NewField).ToList();
        if (serious.Count == 0) lines.Add("Format check: OK, every entry read cleanly.");
        else
        {
            lines.Add($"Format check: {serious.Sum(i => i.Value.Count)} entries with problems:");
            lines.AddRange(serious.OrderByDescending(i => i.Value.Count).Take(6).Select(i => $"  • {i.Key.Detail} ×{i.Value.Count}  ({Label(i.Key.Kind)})"));
        }
        var fields = _issues.Keys.Where(k => k.Kind == LedgerIssueKind.NewField).Select(k => k.Detail.Split(": ").Last()).Distinct().ToList();
        if (fields.Count > 0) lines.Add($"New fields seen (harmless so far): {string.Join(", ", fields)}.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Label(LedgerIssueKind kind) => kind switch
    {
        LedgerIssueKind.UnknownAction => "new entry type",
        LedgerIssueKind.BadEntry => "unreadable entry",
        LedgerIssueKind.BadPayload => "unreadable details",
        LedgerIssueKind.MissingData => "missing value",
        _ => "new field",
    };

    private static string Label(string action) => action switch
    {
        "act_13" => "items",
        "act_14" => "coin",
        "act_24" => "sales",
        "act_01" => "level-ups",
        "act_31" or "act_32" or "act_33" or "act_34" or "act_35" => "party",
        "act_16" or "act_20" => "trades",
        _ => action,
    };

    private static void AppendLog(string text)
    {
        if (text.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes) File.Move(LogPath, LogPath + ".old", overwrite: true);
            File.AppendAllText(LogPath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
