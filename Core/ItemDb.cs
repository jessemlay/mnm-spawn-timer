using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpawnTimer.Core;

/// <summary>One item's stats, parsed from its {{ItemBox}} on the M&M Wiki (Miraheze, CC BY-SA 4.0).</summary>
public sealed class ItemDetails
{
    public string Title { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Handed { get; set; } = "";
    public string Skill { get; set; } = "";
    public string Ac { get; set; } = "";
    public string Damage { get; set; } = "";
    public string Delay { get; set; } = "";
    /// <summary>Ordered (label, value) pairs: STR +2, HP +10, MR +3…</summary>
    public List<string[]> Stats { get; set; } = new();
    public string Effect { get; set; } = "";
    public string Classes { get; set; } = "";
    public string Races { get; set; } = "";
    public string Weight { get; set; } = "";
    public string Size { get; set; } = "";
    public bool Magic { get; set; }
    public bool Lore { get; set; }
    public bool NoDrop { get; set; }
    public string Description { get; set; } = "";
    /// <summary>"Mob (Zone)" entries from the page's drop list.</summary>
    public List<string> DropsFrom { get; set; } = new();
    public DateTime FetchedUtc { get; set; }

    public bool UsableBy(string classCode)
    {
        if (string.IsNullOrWhiteSpace(classCode) || Classes.Length == 0) return true;
        var codes = Classes.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return codes.Contains("ALL") || codes.Contains(classCode.Trim().ToUpperInvariant());
    }
}

/// <summary>
/// Item search data from the M&M Wiki: an index of every item page (pages using {{ItemBox}}) and per-item
/// stats, fetched on demand, 50 pages per request, cached on disk for 7 days.
/// </summary>
public static class ItemDb
{
    private const string WikiApi = "https://monstersandmemories.miraheze.org/w/api.php";
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, ItemDetails> Details = new(StringComparer.OrdinalIgnoreCase);
    private static List<string>? _index;
    private static bool _detailsLoaded;

    private static string Folder => Path.Combine(SettingsStore.CacheFolder, "item-cache");
    private static string IndexPath => Path.Combine(Folder, "v1-index.json");
    private static string DetailsPath => Path.Combine(Folder, "v1-items.json");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SpawnTimer/1.5 (personal Monsters & Memories overlay; item lookup; caches results)");
        return client;
    }

    /// <summary>The item's page on the M&M Wiki, e.g. https://monstersandmemories.miraheze.org/wiki/Hadhraman%27s_Maul.</summary>
    public static string WikiUrl(string title) =>
        "https://monstersandmemories.miraheze.org/wiki/" + Uri.EscapeDataString(title.Trim().Replace(' ', '_'));

    /// <summary>All item page titles (cached 7 days). Null until loaded.</summary>
    public static IReadOnlyList<string>? Index => _index;

    public static async Task<IReadOnlyList<string>> LoadIndexAsync(bool refresh, CancellationToken ct)
    {
        if (_index != null && !refresh) return _index;
        if (!refresh && File.Exists(IndexPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexPath) < CacheAge)
        {
            try
            {
                _index = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(IndexPath, ct));
                if (_index is { Count: > 0 }) return _index;
            }
            catch (JsonException) { }
        }

        var titles = new List<string>();
        string? cont = null;
        do
        {
            var url = $"{WikiApi}?action=query&list=embeddedin&eititle=Template:ItemBox&einamespace=0&eilimit=500&format=json&formatversion=2"
                      + (cont != null ? "&eicontinue=" + Uri.EscapeDataString(cont) : "");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            titles.AddRange(doc.RootElement.GetProperty("query").GetProperty("embeddedin").EnumerateArray()
                .Select(p => p.GetProperty("title").GetString() ?? "").Where(t => t.Length > 0));
            cont = doc.RootElement.TryGetProperty("continue", out var c) && c.TryGetProperty("eicontinue", out var ec)
                ? ec.GetString() : null;
            if (cont != null) await Task.Delay(300, ct); // be gentle with the wiki
        } while (cont != null);

        _index = titles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            Directory.CreateDirectory(Folder);
            await File.WriteAllTextAsync(IndexPath, JsonSerializer.Serialize(_index), ct);
        }
        catch (IOException) { }
        return _index;
    }

    public static ItemDetails? TryGetCached(string title)
    {
        EnsureDetailsLoaded();
        lock (Details) return Details.TryGetValue(title, out var d) && DateTime.UtcNow - d.FetchedUtc < CacheAge ? d : null;
    }

    /// <summary>(mob, zone, item) for every "drops from" entry of every cached item.</summary>
    public static List<(string Mob, string Zone, string Item)> CachedDropSources()
    {
        EnsureDetailsLoaded();
        var list = new List<(string, string, string)>();
        lock (Details)
            foreach (var (title, d) in Details)
                foreach (var source in d.DropsFrom)
                {
                    var (mob, zone) = SplitSource(source);
                    list.Add((mob, zone, title));
                }
        return list;
    }

    /// <summary>"Mob (Zone)" → ("Mob", "Zone"); "Mob" → ("Mob", "").</summary>
    public static (string Mob, string Zone) SplitSource(string source)
    {
        var i = source.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 && source.EndsWith(')') ? (source[..i], source[(i + 2)..^1]) : (source, "");
    }

    /// <summary>Stats for the given item titles; fetches anything not cached (50 per request).</summary>
    public static async Task<Dictionary<string, ItemDetails>> GetAsync(IEnumerable<string> titles, CancellationToken ct)
    {
        EnsureDetailsLoaded();
        var wanted = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, ItemDetails>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var t in wanted)
        {
            if (TryGetCached(t) is { } d) result[t] = d;
            else missing.Add(t);
        }
        if (missing.Count == 0) return result;

        await Gate.WaitAsync(ct);
        try
        {
            foreach (var batch in missing.Chunk(50))
            {
                var url = $"{WikiApi}?action=query&prop=revisions&rvprop=content&rvslots=main&redirects=1&format=json&formatversion=2&titles="
                          + Uri.EscapeDataString(string.Join("|", batch));
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
                var query = doc.RootElement.GetProperty("query");

                // Map normalized/redirected titles back to what was asked for.
                var asked = batch.ToDictionary(b => b, b => b, StringComparer.OrdinalIgnoreCase);
                foreach (var key in new[] { "normalized", "redirects" })
                {
                    if (!query.TryGetProperty(key, out var arr)) continue;
                    foreach (var m in arr.EnumerateArray())
                    {
                        var from = m.GetProperty("from").GetString() ?? "";
                        var to = m.GetProperty("to").GetString() ?? "";
                        if (asked.TryGetValue(from, out var original)) asked[to] = original;
                    }
                }

                foreach (var page in query.GetProperty("pages").EnumerateArray())
                {
                    if (!page.TryGetProperty("revisions", out var revs)) continue;
                    var title = page.GetProperty("title").GetString() ?? "";
                    var text = revs[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? "";
                    var details = Parse(title, text);
                    lock (Details)
                    {
                        Details[title] = details;
                        if (asked.TryGetValue(title, out var original)) Details[original] = details;
                    }
                    result[asked.TryGetValue(title, out var o) ? o : title] = details;
                }
                if (missing.Count > 50) await Task.Delay(300, ct);
            }
            SaveDetails();
        }
        finally
        {
            Gate.Release();
        }
        return result;
    }

    private static readonly (string Key, string Label)[] StatFields =
    {
        ("str", "STR"), ("sta", "STA"), ("agi", "AGI"), ("dex", "DEX"), ("wis", "WIS"), ("int", "INT"), ("cha", "CHA"),
        ("hp", "HP"), ("mana", "MANA"), ("hp_regen", "HP REGEN"), ("mana_regen", "MANA REGEN"), ("haste", "HASTE"),
        ("mr", "MR"), ("fr", "FR"), ("cr", "CR"), ("pr", "PR"), ("dr", "DR"),
    };

    public static ItemDetails Parse(string title, string wikitext)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = TemplateBody(wikitext, "ItemBox");
        if (body != null)
        {
            // Pages differ: one field per line or several on a line ("| agi = | dex = 1"). Split on top-level "|".
            foreach (var part in SplitTopLevel(body))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part[..eq].Trim();
                if (key.Length > 0 && Regex.IsMatch(key, "^[A-Za-z_0-9-]+$")) fields[key] = Clean(part[(eq + 1)..]);
            }
        }
        string F(string key) => fields.TryGetValue(key, out var v) ? v : "";
        bool Flag(string key) => F(key).Equals("true", StringComparison.OrdinalIgnoreCase) || F(key).Equals("yes", StringComparison.OrdinalIgnoreCase);

        var item = new ItemDetails
        {
            Title = title,
            Name = F("item_name").Length > 0 ? F("item_name") : title,
            Slot = ZoneNames.Tidy(F("slot")),
            Handed = F("handed"),
            Skill = F("skill"),
            Ac = F("ac"),
            Damage = F("dmg"),
            Delay = F("delay"),
            Effect = F("effect"),
            Classes = F("class").ToUpperInvariant(),
            Races = F("race").ToUpperInvariant(),
            Weight = F("weight"),
            Size = ZoneNames.Tidy(F("size")),
            Magic = Flag("magic"),
            Lore = F("lore").Length > 0 && !F("lore").Equals("false", StringComparison.OrdinalIgnoreCase),
            NoDrop = Flag("nodrop"),
            Description = F("item_stats"),
            FetchedUtc = DateTime.UtcNow,
        };
        foreach (var (key, label) in StatFields)
        {
            var v = F(key).Replace(" ", "");
            if (v.Length == 0 || v == "0" || v == "+0") continue;
            if (!v.StartsWith('+') && !v.StartsWith('-') && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "+" + v;
            item.Stats.Add(new[] { label, v });
        }

        // {{Itempage |dropsfrom = [[Zone]] * [[Mob]] …}}: zone lines without "*", mob lines with "*".
        var drops = Regex.Match(wikitext, @"\|\s*dropsfrom\s*=(.*?)(\n\s*\||\}\})", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (drops.Success)
        {
            var zone = "";
            foreach (var raw in drops.Groups[1].Value.Split('\n'))
            {
                var line = raw.Trim();
                var link = Regex.Match(line, @"\[\[([^\]|#]+)");
                if (!link.Success) continue;
                var name = link.Groups[1].Value.Trim();
                if (line.StartsWith('*')) item.DropsFrom.Add(zone.Length > 0 ? $"{name} ({zone})" : name);
                else zone = name;
            }
        }
        return item;
    }

    /// <summary>The text between "{{Name" and its matching "}}" (nested templates/links allowed), or null.</summary>
    private static string? TemplateBody(string text, string name)
    {
        var start = Regex.Match(text, @"\{\{\s*" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase);
        if (!start.Success) return null;
        var depth = 1;
        for (var i = start.Index + start.Length; i < text.Length - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{') { depth++; i++; }
            else if (text[i] == '}' && text[i + 1] == '}')
            {
                depth--;
                if (depth == 0) return text[(start.Index + start.Length)..i];
                i++;
            }
        }
        return text[(start.Index + start.Length)..];
    }

    /// <summary>Splits template parameters on "|" that aren't inside [[links]] or {{templates}}.</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var startAt = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (i + 1 < body.Length && (body[i] == '[' && body[i + 1] == '[' || body[i] == '{' && body[i + 1] == '{')) { depth++; i++; }
            else if (i + 1 < body.Length && (body[i] == ']' && body[i + 1] == ']' || body[i] == '}' && body[i + 1] == '}')) { depth = Math.Max(0, depth - 1); i++; }
            else if (body[i] == '|' && depth == 0)
            {
                yield return body[startAt..i];
                startAt = i + 1;
            }
        }
        yield return body[startAt..];
    }

    private static string Clean(string value) =>
        WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(value, @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1"), "<[^>]+>", "")).Trim();

    private static void EnsureDetailsLoaded()
    {
        if (_detailsLoaded) return;
        _detailsLoaded = true;
        try
        {
            if (!File.Exists(DetailsPath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, ItemDetails>>(File.ReadAllText(DetailsPath));
            if (saved == null) return;
            lock (Details)
                foreach (var (k, v) in saved) Details[k] = v;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    private static void SaveDetails()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string json;
            lock (Details) json = JsonSerializer.Serialize(Details);
            File.WriteAllText(DetailsPath, json);
        }
        catch (IOException) { }
    }
}

/// <summary>Ranks item titles for the search box: current-zone items first, then prefix, word-start and substring matches.</summary>
public static class ItemSearch
{
    public static List<string> Find(string query, IReadOnlyList<string> index, IReadOnlySet<string> zoneItems, int max)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return zoneItems.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(max).ToList();

        var scored = new List<(string Title, int Score)>();
        foreach (var title in index.Concat(zoneItems.Where(z => !index.Contains(z, StringComparer.OrdinalIgnoreCase))))
        {
            var s = Score(title, q);
            if (s <= 0) continue;
            if (zoneItems.Contains(title)) s += 1000;
            scored.Add((title, s));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Title.Length).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>300 prefix, 200 word start, 100 anywhere, 0 no match.</summary>
    public static int Score(string title, string q)
    {
        if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 300;
        var idx = title.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        return idx > 0 && (title[idx - 1] == ' ' || title[idx - 1] == '-' || title[idx - 1] == '\'') ? 200 : 100;
    }

    /// <summary>The rest of the best prefix match, for ghost-text autocomplete ("" if none).</summary>
    public static string Completion(string query, IEnumerable<string> ranked)
    {
        if (query.Length == 0) return "";
        var best = ranked.FirstOrDefault(t => t.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        return best == null ? "" : best[query.Length..];
    }
}

/// <summary>A mob for the mob lookup, with what it drops.</summary>
public sealed class MobInfo
{
    public string Name { get; init; } = "";
    public string Zone { get; set; } = "";
    public string? Level { get; set; }
    public bool IsNamed { get; set; }
    /// <summary>From the current zone's drop list (ranked first, listed when the box is empty).</summary>
    public bool InZone { get; set; }
    public List<ZoneDrop> Drops { get; } = new();

    public void AddDrop(string item, string? slot)
    {
        if (item.Length == 0) return;
        var existing = Drops.FirstOrDefault(d => d.Name.Equals(item, StringComparison.OrdinalIgnoreCase));
        if (existing == null) Drops.Add(new ZoneDrop { Name = item, Slot = slot });
        else if (string.IsNullOrEmpty(existing.Slot)) existing.Slot = slot;
    }
}

/// <summary>
/// Mob lookup: the current zone's mobs (from its wiki drop list) plus every mob named in a cached item's
/// "drops from" list, so mobs elsewhere show up once you've looked up (or preloaded) their items.
/// </summary>
public static class MobSearch
{
    public static List<MobInfo> Build(IEnumerable<ZoneMob> zoneMobs, string zoneName,
        IEnumerable<(string Mob, string Zone, string Item)> cachedSources)
    {
        var byKey = new Dictionary<string, MobInfo>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, MobInfo>(StringComparer.OrdinalIgnoreCase);
        static string Key(string mob, string zone) => mob + "|" + ZoneNames.Normalize(zone);

        foreach (var m in zoneMobs)
        {
            if (m.Name.Length == 0) continue;
            if (!byKey.TryGetValue(Key(m.Name, zoneName), out var mob))
            {
                mob = new MobInfo { Name = m.Name, Zone = zoneName, InZone = true };
                byKey[Key(m.Name, zoneName)] = mob;
                byName.TryAdd(m.Name, mob);
            }
            mob.IsNamed |= m.IsNamed;
            if (string.IsNullOrEmpty(mob.Level)) mob.Level = m.Level;
            foreach (var d in m.Drops) mob.AddDrop(d.Name, d.Slot);
        }

        foreach (var (name, zone, item) in cachedSources)
        {
            if (name.Length == 0) continue;
            // No zone on the item page: attach to a known mob of that name rather than listing it twice.
            if (!byKey.TryGetValue(Key(name, zone), out var mob) && !(zone.Length == 0 && byName.TryGetValue(name, out mob)))
            {
                mob = new MobInfo { Name = name, Zone = zone };
                byKey[Key(name, zone)] = mob;
                byName.TryAdd(name, mob);
            }
            mob.AddDrop(item, ItemDb.TryGetCached(item)?.Slot);
        }
        return byKey.Values.ToList();
    }

    /// <summary>Empty query: the current zone's mobs, named first. Otherwise ranked like items, current zone first.</summary>
    public static List<MobInfo> Find(string query, IReadOnlyList<MobInfo> mobs, int max)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return mobs.Where(m => m.InZone).OrderByDescending(m => m.IsNamed)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        return mobs.Select(m => (Mob: m, Score: ItemSearch.Score(m.Name, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score + (x.Mob.InZone ? 1000 : 0))
            .ThenBy(x => x.Mob.Name.Length).ThenBy(x => x.Mob.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Mob).Take(max).ToList();
    }
}
