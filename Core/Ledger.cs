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

    public static List<LedgerEvent> Parse(string json)
    {
        var result = new List<LedgerEvent>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("c01", out var list) || list.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in list.EnumerateArray())
        {
            var ev = ParseEntry(entry);
            if (ev != null) result.Add(ev);
        }
        return result;
    }

    private static LedgerEvent? ParseEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var action = Str(e, "f01");
        var f02 = Str(e, "f02");
        var f03 = Str(e, "f03");
        var time = Str(e, "f04");
        if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;

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
        if (action is "act_16" or "act_20" or "act_31" or "act_32" or "act_33" or "act_34" or "act_35"
            && f02.StartsWith("name_", StringComparison.Ordinal))
            ev.Subject = Decode(f02[5..]);

        if (!f03.StartsWith('{')) return ev;
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
                    break;
                case "act_14":
                    ev.Kind = LedgerKind.CoinLoot;
                    ev.Corpse = NullIfEmpty(Decode(Str(d, "d13")));
                    ev.CoinTotalCp = Int(d, "d11") ?? ParseCoinCsv(Decode(Str(d, "d12"))) ?? 0;
                    ev.CoinKind = Decode(Str(d, "d14"));
                    ev.CoinShareCp = ParseCoinCsv(Decode(Str(d, "d15")));
                    break;
                case "act_24":
                    ev.Kind = LedgerKind.VendorSale;
                    ev.Item = Str(d, "d04");
                    ev.Quantity = Int(d, "d01") ?? 1;
                    ev.CoinTotalCp = ParseCoinWords(Decode(Str(d, "d03")));
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
        }
        return ev;
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
        _ => e.Action,
    };

    /// <summary>"pp,gp,sp,cp" to copper.</summary>
    private static long? ParseCoinCsv(string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 4) return null;
        long total = 0;
        foreach (var p in parts)
        {
            if (!long.TryParse(p.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
            total = total * 10 + n;
        }
        return total;
    }

    /// <summary>"0 platinum 1 gold 2 silver 3 copper" to copper.</summary>
    private static long ParseCoinWords(string text)
    {
        long total = 0;
        foreach (Match m in CoinWords.Matches(text))
        {
            var n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value.ToLowerInvariant() switch
            {
                "platinum" => n * 1000,
                "gold" => n * 100,
                "silver" => n * 10,
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
    private string? _dir;
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
                    _stamps.Clear();
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
                try
                {
                    events = LedgerParser.Parse(text);
                }
                catch (JsonException)
                {
                    continue; // Caught mid-write; the unchanged stamp makes us retry next poll.
                }

                _stamps[file] = stamp;
                foreach (var ev in events)
                {
                    if (_seen.Add(ev.Key) && ev.Time >= _since) batch.Add(ev);
                }
            }

            Status = "Watching " + Describe(_dir);
            if (batch.Count > 0)
            {
                batch.Sort((a, b) => a.Time.CompareTo(b.Time));
                _ui.InvokeAsync(() => EventsArrived?.Invoke(batch));
            }
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
