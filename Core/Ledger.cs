using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Threading;

namespace SpawnTimer.Core;

public enum LedgerKind { Other, ItemLoot, CoinLoot, VendorSale, LevelUp, Party }

public enum LedgerIssueKind
{
    /// <summary>An action code the app doesn't know (a patch added a new kind of entry).</summary>
    UnknownAction,
    /// <summary>An entry without a readable action or time; it's skipped.</summary>
    BadEntry,
    /// <summary>The entry's details (f03) aren't readable JSON.</summary>
    BadPayload,
    /// <summary>Readable, but a value the app relies on is missing or in a new format (item name, coin, price).</summary>
    MissingData,
    /// <summary>Entries carry a field the app doesn't know (e.g. f10 in 0.30.3). Harmless on its own; logged only.</summary>
    NewField,
}

/// <summary>Something in a Ledger entry the parser couldn't fully read, for the Ledger health check.</summary>
/// <param name="Key">Identifies the entry, so a re-read file doesn't report it twice.</param>
public sealed record LedgerIssue(string Key, LedgerIssueKind Kind, string Detail, DateTimeOffset? Time)
{
    public bool Serious => Kind != LedgerIssueKind.NewField;
}

/// <summary>
/// One entry from the game's Ledger files
/// (%USERPROFILE%\AppData\LocalLow\Niche Worlds Cult\Monsters and Memories\&lt;server&gt;\&lt;character&gt;\Ledger\*.json).
/// The game records loot, coin, vendor sales, level-ups, trades and party changes. It records no damage,
/// healing or kill events, so a looted corpse is the closest available signal for a kill.
/// </summary>
public sealed class LedgerEvent
{
    public string Key { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Action { get; set; } = "";
    public LedgerKind Kind { get; set; }
    public string Zone { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Owner { get; set; } = "";
    /// <summary>Server folder the entry came from (e.g. "tilustra"); set by whoever read the file.</summary>
    public string Server { get; set; } = "";
    public bool IsMine => string.Equals(Actor, Owner, StringComparison.OrdinalIgnoreCase);
    /// <summary>The player named in party and trade entries (f02 "name_…").</summary>
    public string? Subject { get; set; }
    public string? Corpse { get; set; }
    public string? Item { get; set; }
    public int Quantity { get; set; }
    public long CoinTotalCp { get; set; }
    public long? CoinShareCp { get; set; }
    /// <summary>Your coin from this entry (loot share or vendor sale); filled in by <see cref="SessionStats"/>.</summary>
    public long MyCoinCp { get; set; }
    public string? CoinKind { get; set; }
    public int? Level { get; set; }
    public int? PartySize { get; set; }
}

public static class LedgerParser
{
    private static readonly Regex CoinWords = new(@"(\d+)\s*(platinum|gold|silver|copper)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Action codes seen in the Ledger so far (0.30.x); anything else is reported as new.</summary>
    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
    {
        "act_01", "act_02", "act_11", "act_12", "act_13", "act_14", "act_15", "act_16", "act_20", "act_24",
        "act_31", "act_32", "act_33", "act_34", "act_35", "act_36",
    };

    /// <summary>Entry fields seen so far. f10 (0.30.3+) is the actor's class code on party (Social) entries, e.g. "clr", and empty on your own.</summary>
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "f01", "f02", "f03", "f04", "f05", "f06", "f07", "f08", "f09", "f10",
    };

    /// <param name="issues">If given, collects anything the parser couldn't fully read (see <see cref="LedgerIssue"/>).</param>
    public static List<LedgerEvent> Parse(string json, List<LedgerIssue>? issues = null)
    {
        var result = new List<LedgerEvent>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("c01", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            issues?.Add(new LedgerIssue("file|no-c01", LedgerIssueKind.BadEntry, "file has no entry list (c01)", null));
            return result;
        }
        foreach (var entry in list.EnumerateArray())
        {
            var ev = ParseEntry(entry, issues);
            if (ev != null) result.Add(ev);
        }
        return result;
    }

    private static LedgerEvent? ParseEntry(JsonElement e, List<LedgerIssue>? issues)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            issues?.Add(new LedgerIssue("bad|" + e.GetRawText(), LedgerIssueKind.BadEntry, "entry isn't an object", null));
            return null;
        }
        var action = Str(e, "f01");
        var f02 = Str(e, "f02");
        var f03 = Str(e, "f03");
        var time = Str(e, "f04");
        if (action.Length == 0 || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            issues?.Add(new LedgerIssue("bad|" + e.GetRawText(), LedgerIssueKind.BadEntry,
                action.Length == 0 ? "entry has no action (f01)" : $"{action}: unreadable time (f04 \"{time}\")", null));
            return null;
        }

        var zone = Str(e, "f05");
        var ev = new LedgerEvent
        {
            Action = action,
            Time = at,
            Zone = zone.StartsWith("zone_", StringComparison.Ordinal) ? Decode(zone[5..]) : zone,
            Actor = Str(e, "f07"),
            Owner = Str(e, "f09"),
        };
        // Character and Social files can both contain the same entry; dedupe on content, not file.
        ev.Key = string.Join('|', action, time, ev.Actor, f02, f03);
        if (action is "act_16" or "act_20" or "act_31" or "act_32" or "act_33" or "act_34" or "act_35" or "act_36"
            && f02.StartsWith("name_", StringComparison.Ordinal))
            ev.Subject = Decode(f02[5..]);

        if (issues != null)
        {
            if (!KnownActions.Contains(action))
                issues.Add(new LedgerIssue(ev.Key, LedgerIssueKind.UnknownAction, action, at));
            var extra = string.Join(", ", e.EnumerateObject().Select(f => f.Name).Where(n => !KnownFields.Contains(n)));
            if (extra.Length > 0)
                issues.Add(new LedgerIssue(ev.Key + "|fields", LedgerIssueKind.NewField, $"{action}: {extra}", at));
        }

        if (!f03.StartsWith('{'))
        {
            // These always carry details; without them the entry is useless to the meter.
            if (action is "act_13" or "act_14" or "act_24")
                issues?.Add(new LedgerIssue(ev.Key, LedgerIssueKind.BadPayload, $"{action}: no details (f03)", at));
            return ev;
        }
        try
        {
            using var payload = JsonDocument.Parse(f03);
            var d = payload.RootElement;
            switch (action)
            {
                case "act_11": // dropped
                case "act_12":
                case "act_15": // destroyed
                    ev.Item = Str(d, "d04");
                    ev.Quantity = Int(d, "d01") ?? 1;
                    break;
                case "act_16":
                case "act_20":
                    ev.Item = TradeItems(d);
                    ev.CoinTotalCp = ParseCoinCsv(Decode(Str(d, "currency"))) ?? 0;
                    break;
                case "act_13":
                    ev.Kind = LedgerKind.ItemLoot;
                    ev.Corpse = NullIfEmpty(Decode(Str(d, "d02")));
                    ev.Item = Str(d, "d04");
                    ev.Quantity = Int(d, "d01") ?? 1;
                    if (ev.Item.Length == 0) Missing("item name (d04)");
                    break;
                case "act_14":
                    ev.Kind = LedgerKind.CoinLoot;
                    ev.Corpse = NullIfEmpty(Decode(Str(d, "d13")));
                    // Use the coin breakdown (d12). The d11 "total" disagrees with it whenever silver is present
                    // (1 silver 8 copper is logged as 18), so it's only a fallback.
                    var breakdown = ParseCoinCsv(Decode(Str(d, "d12")));
                    if (breakdown == null) Missing("coin breakdown (d12)");
                    ev.CoinTotalCp = breakdown ?? Int(d, "d11") ?? 0;
                    ev.CoinKind = Decode(Str(d, "d14"));
                    ev.CoinShareCp = ParseCoinCsv(Decode(Str(d, "d15")));
                    break;
                case "act_24":
                    ev.Kind = LedgerKind.VendorSale;
                    ev.Item = Str(d, "d04");
                    ev.Quantity = Int(d, "d01") ?? 1;
                    var priceText = Decode(Str(d, "d03"));
                    ev.CoinTotalCp = ParseCoinWords(priceText);
                    if (ev.Item.Length == 0) Missing("item name (d04)");
                    if (!CoinWords.IsMatch(priceText)) Missing("sale price (d03)");
                    break;
                case "act_01":
                    ev.Kind = LedgerKind.LevelUp;
                    ev.Level = Int(d, "d21");
                    break;
                case "act_31":
                case "act_32":
                case "act_33":
                case "act_34":
                case "act_35":
                    ev.Kind = LedgerKind.Party;
                    ev.PartySize = Int(d, "d62");
                    break;
            }
        }
        catch (JsonException)
        {
            // Unknown payload shape; keep the bare event.
            issues?.Add(new LedgerIssue(ev.Key, LedgerIssueKind.BadPayload, $"{action}: details (f03) aren't readable", at));
        }
        return ev;

        void Missing(string what) => issues?.Add(new LedgerIssue(ev.Key, LedgerIssueKind.MissingData, $"{action}: {what}", at));
    }

    /// <summary>The game base64-encodes names; fall back to the raw text when it isn't valid base64.</summary>
    public static string Decode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 4 != 0) return value ?? "";
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return text.Any(c => char.IsControl(c) || c == '�') ? value : text;
        }
        catch (FormatException)
        {
            return value;
        }
    }

    /// <summary>Human-readable item list for trade entries: "Ration x10, Water Flask x10".</summary>
    private static string? TradeItems(JsonElement d)
    {
        if (!d.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var name = Str(item, "name");
            var qty = Int(item, "quantity") ?? 1;
            if (name.Length > 0) parts.Add(qty > 1 ? $"{name} x{qty}" : name);
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>A short label for an entry's action, for exports.</summary>
    public static string Describe(LedgerEvent e) => e.Action switch
    {
        "act_01" => "Level up",
        "act_11" => "Dropped item",
        "act_12" => "Item (act_12)",
        "act_13" => "Looted item",
        "act_14" => "Looted coin",
        "act_15" => "Destroyed item",
        "act_16" => "Trade given",
        "act_20" => "Trade received",
        "act_24" => "Sold to vendor",
        "act_31" => "Party created",
        "act_32" => "Party join",
        "act_33" => "Party disband",
        "act_34" => "Party leave",
        "act_35" => "Party leader change",
        "act_36" => "Party invite",
        _ => e.Action,
    };

    /// <summary>"pp,gp,sp,cp" to copper. Denominations aren't normalized (e.g. "0,0,1,12"), so convert each.</summary>
    public static long? ParseCoinCsv(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4) return null;
        var n = new long[4];
        for (var i = 0; i < 4; i++)
            if (!long.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return CoinFormat.ToCopper(n[0], n[1], n[2], n[3]);
    }

    /// <summary>"0 platinum 1 gold 2 silver 3 copper" to copper.</summary>
    public static long ParseCoinWords(string text)
    {
        long total = 0;
        foreach (Match m in CoinWords.Matches(text))
        {
            var n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value.ToLowerInvariant() switch
            {
                "platinum" => n * CoinFormat.Platinum,
                "gold" => n * CoinFormat.Gold,
                "silver" => n * CoinFormat.Silver,
                _ => n,
            };
        }
        return total;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}

/// <summary>
/// Polls the most recently active character's Ledger folder once a second and raises new entries on the UI thread.
/// Read-only: files are opened with full sharing so the game is never blocked.
/// </summary>
public sealed class LedgerWatcher : IDisposable
{
    private static readonly TimeSpan DirRescan = TimeSpan.FromSeconds(30);

    private readonly Dispatcher _ui;
    private readonly string _override;
    private readonly DateTimeOffset _since;
    private readonly Timer _timer;
    private readonly Dictionary<string, (DateTime Write, long Length)> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new();
    private readonly HashSet<string> _seenIssues = new();
    /// <summary>Files that failed to parse, with the stamp and how many polls in a row (mid-write reads fail once).</summary>
    private readonly Dictionary<string, ((DateTime, long) Stamp, int Fails)> _badReads = new(StringComparer.OrdinalIgnoreCase);
    private const int UnreadableAfter = 10;
    private string? _dir;
    private string _server = "";
    private DateTime _nextDirScan = DateTime.MinValue;
    private int _busy;
    private volatile bool _disposed;

    public LedgerWatcher(string overridePath, DateTimeOffset since, Dispatcher ui)
    {
        _override = overridePath.Trim();
        _since = since;
        _ui = ui;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public static string GameDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Niche Worlds Cult", "Monsters and Memories");

    public string OverridePath => _override;
    public string Status { get; private set; } = "Starting…";

    public event Action<IReadOnlyList<LedgerEvent>>? EventsArrived;

    /// <summary>Newly seen problems reading the Ledger (each entry reported once), on the UI thread.</summary>
    public event Action<IReadOnlyList<LedgerIssue>>? IssuesFound;

    /// <summary>The server folder name for a Ledger folder (…\&lt;server&gt;\&lt;character&gt;\Ledger).</summary>
    public static string ServerOf(string ledgerDir) => Directory.GetParent(ledgerDir)?.Parent?.Name ?? "";

    private void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_dir == null || DateTime.UtcNow >= _nextDirScan)
            {
                var found = FindLedgerDir();
                _nextDirScan = DateTime.UtcNow + DirRescan;
                if (!string.Equals(found, _dir, StringComparison.OrdinalIgnoreCase))
                {
                    _dir = found;
                    _server = found == null ? "" : ServerOf(found);
                    _stamps.Clear();
                    _badReads.Clear();
                }
            }

            if (_dir == null)
            {
                Status = _override.Length == 0
                    ? $"Game data folder not found ({GameDataRoot})"
                    : $"No Ledger folder found at {_override}";
                return;
            }

            var batch = new List<LedgerEvent>();
            var newIssues = new List<LedgerIssue>();
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                var info = new FileInfo(file);
                var stamp = (info.LastWriteTimeUtc, info.Length);
                if (_stamps.TryGetValue(file, out var old) && old == stamp) continue;

                string text;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    text = reader.ReadToEnd();
                }
                catch (IOException)
                {
                    continue;
                }

                List<LedgerEvent> events;
                var issues = new List<LedgerIssue>();
                try
                {
                    events = LedgerParser.Parse(text, issues);
                }
                catch (JsonException)
                {
                    // Usually caught mid-write: the stamp isn't stored, so it's retried next poll. A file that stays
                    // unreadable with the same stamp is a real format problem.
                    var fails = _badReads.TryGetValue(file, out var bad) && bad.Stamp == stamp ? bad.Fails + 1 : 1;
                    _badReads[file] = (stamp, fails);
                    if (fails == UnreadableAfter)
                        newIssues.Add(new LedgerIssue($"file|{file}|{stamp}", LedgerIssueKind.BadEntry,
                            $"{Path.GetFileName(file)} isn't readable JSON", null));
                    continue;
                }

                _badReads.Remove(file);
                _stamps[file] = stamp;
                foreach (var ev in events)
                {
                    ev.Server = _server;
                    if (_seen.Add(ev.Key) && ev.Time >= _since) batch.Add(ev);
                }
                foreach (var issue in issues)
                    if (_seenIssues.Add(issue.Key + "|" + issue.Kind + "|" + issue.Detail)) newIssues.Add(issue);
            }

            Status = "Watching " + Describe(_dir);
            if (batch.Count > 0)
            {
                batch.Sort((a, b) => a.Time.CompareTo(b.Time));
                _ui.InvokeAsync(() => EventsArrived?.Invoke(batch));
            }
            if (newIssues.Count > 0) _ui.InvokeAsync(() => IssuesFound?.Invoke(newIssues));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private string? FindLedgerDir()
    {
        if (_override.Length > 0)
        {
            if (!Directory.Exists(_override)) return null;
            if (string.Equals(Path.GetFileName(_override.TrimEnd('\\', '/')), "Ledger", StringComparison.OrdinalIgnoreCase))
                return _override;
            var sub = Path.Combine(_override, "Ledger");
            return Directory.Exists(sub) ? sub : null;
        }

        var root = GameDataRoot;
        if (!Directory.Exists(root)) return null;

        // Pick the Ledger folder whose newest file was written most recently (the character being played).
        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var server in Directory.EnumerateDirectories(root))
        {
            foreach (var character in Directory.EnumerateDirectories(server))
            {
                var ledger = Path.Combine(character, "Ledger");
                if (!Directory.Exists(ledger)) continue;
                foreach (var file in Directory.EnumerateFiles(ledger, "*.json"))
                {
                    var t = File.GetLastWriteTimeUtc(file);
                    if (t > bestTime)
                    {
                        bestTime = t;
                        best = ledger;
                    }
                }
            }
        }
        return best;
    }

    private static string Describe(string ledgerDir)
    {
        var character = Directory.GetParent(ledgerDir);
        var server = character?.Parent;
        return server != null && character != null ? $"{server.Name} / {character.Name}" : ledgerDir;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
