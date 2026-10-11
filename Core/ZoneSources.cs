using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpawnTimer.Core;

/// <summary>
/// Community drop data, fetched only when the user asks, one request at a time, and cached on disk.
/// Sources:
///  - Monsters and Memories Wiki on Miraheze, via the MediaWiki API. Content is CC BY-SA 4.0.
///  - MnM Drops (mnmdrops.com): public HTML pages; robots.txt allows /zones and /mobs. Requests are spaced 1 s apart.
/// monstersandmemories.wiki is deliberately not used: its robots.txt and copyright page forbid bulk scraping/copying.
/// </summary>
public static class ZoneSources
{
    public const string WikiName = "M&M Wiki (Miraheze)";
    public const string DropsName = "MnM Drops";
    public const string Attribution =
        "Drop data: Monsters and Memories Wiki (monstersandmemories.miraheze.org, CC BY-SA 4.0) and MnM Drops (mnmdrops.com). " +
        "Community-maintained, so it may be incomplete or out of date.";

    private const string WikiApi = "https://monstersandmemories.miraheze.org/w/api.php";
    private const string DropsBase = "https://mnmdrops.com";
    private static readonly TimeSpan DropsDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private const int CacheVersion = 2; // bump when parsing changes so stale parsed copies aren't reused

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim DropsGate = new(1, 1);
    private static DateTime _lastDropsRequest = DateTime.MinValue;

    private static string CacheFolder => Path.Combine(SettingsStore.Folder, "zone-cache");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SpawnTimer/1.0 (personal Monsters & Memories overlay; fetches on user request; caches results)");
        return client;
    }

    // ---------------- zone lists ----------------

    public static async Task<List<ZoneRef>> ListZonesAsync(bool useWiki, bool useDrops, bool refresh, CancellationToken ct)
    {
        var merged = new Dictionary<string, ZoneRef>();
        var errors = new List<string>();

        if (useWiki)
        {
            try
            {
                foreach (var title in await Cached("zones-wiki", refresh, () => WikiZoneTitlesAsync(ct)))
                {
                    var zone = Upsert(merged, title);
                    zone.WikiTitle = title;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                errors.Add($"{WikiName}: {ex.Message}");
            }
        }

        if (useDrops)
        {
            try
            {
                foreach (var entry in await Cached("zones-drops", refresh, () => DropsZonesAsync(ct)))
                {
                    var zone = Upsert(merged, entry.Name);
                    zone.DropsSlug = entry.Slug;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                errors.Add($"{DropsName}: {ex.Message}");
            }
        }

        if (merged.Count == 0 && errors.Count > 0) throw new HttpRequestException(string.Join("\n", errors));
        return merged.Values.OrderBy(z => z.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ZoneRef Upsert(Dictionary<string, ZoneRef> zones, string name)
    {
        var key = ZoneNames.Normalize(name);
        if (!zones.TryGetValue(key, out var zone))
        {
            zone = new ZoneRef { Name = name };
            zones[key] = zone;
        }
        return zone;
    }

    // ---------------- zone contents ----------------

    public static async Task<List<ZoneSourceResult>> LoadZoneAsync(
        ZoneRef zone, bool useWiki, bool useDrops, bool refresh, IProgress<string> progress, CancellationToken ct)
    {
        var results = new List<ZoneSourceResult>();
        var errors = new List<string>();

        if (useWiki && zone.WikiTitle != null)
        {
            progress.Report($"Reading {WikiName}…");
            try
            {
                results.Add(await Cached($"wiki-{zone.Key}", refresh, () => WikiZoneAsync(zone.WikiTitle, ct)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                errors.Add($"{WikiName}: {ex.Message}");
            }
        }

        if (useDrops && zone.DropsSlug != null)
        {
            try
            {
                results.Add(await Cached($"drops-{zone.Key}", refresh, () => DropsZoneAsync(zone.DropsSlug, zone.Name, progress, ct)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                errors.Add($"{DropsName}: {ex.Message}");
            }
        }

        if (results.Count == 0 && errors.Count > 0) throw new HttpRequestException(string.Join("\n", errors));
        if (errors.Count > 0) progress.Report("Partly loaded. " + string.Join(" ", errors));
        return results;
    }

    // ---------------- Miraheze wiki (MediaWiki API) ----------------

    private static readonly Regex WikiLink = new(@"\[\[([^\]|#]+)(?:#[^\]|]*)?(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);

    private static async Task<List<string>> WikiZoneTitlesAsync(CancellationToken ct)
    {
        var url = $"{WikiApi}?action=query&list=categorymembers&cmtitle=Category:Zones&cmnamespace=0&cmlimit=200&format=json&formatversion=2";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        return doc.RootElement.GetProperty("query").GetProperty("categorymembers").EnumerateArray()
            .Select(m => m.GetProperty("title").GetString() ?? "")
            .Where(t => t.Length > 0 && !t.StartsWith("Starting City", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static async Task<ZoneSourceResult> WikiZoneAsync(string title, CancellationToken ct)
    {
        var url = $"{WikiApi}?action=query&prop=revisions&rvprop=content&rvslots=main&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(title)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var page = doc.RootElement.GetProperty("query").GetProperty("pages")[0];
        var text = page.TryGetProperty("revisions", out var revs)
            ? revs[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? ""
            : "";
        return new ZoneSourceResult { Source = WikiName, ZoneName = title, FetchedUtc = DateTime.UtcNow, Mobs = ParseWikiZone(text) };
    }

    /// <summary>Parses the "NPCs" table (name, race, class, level, location, known loot, description) and "Items" table (item, drops from, slot).</summary>
    public static List<ZoneMob> ParseWikiZone(string wikitext)
    {
        var mobs = new Dictionary<string, ZoneMob>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in TableRows(Section(wikitext, "NPCs"), out var subHeaders))
        {
            if (row.Count == 0) continue;
            var name = Links(row[0]).FirstOrDefault() ?? Clean(row[0]);
            if (name.Length == 0 || name.StartsWith("Example", StringComparison.OrdinalIgnoreCase)) continue;
            var description = row.Count > 6 ? string.Join(" ", row.Skip(6)) : "";
            var named = description.Contains("Named", StringComparison.OrdinalIgnoreCase)
                        || (!subHeaders.TryGetValue(row, out var header) || !header.Contains("Common", StringComparison.OrdinalIgnoreCase))
                        && !name.StartsWith("a ", StringComparison.Ordinal) && !name.StartsWith("an ", StringComparison.Ordinal);
            var mob = GetMob(mobs, name);
            mob.IsNamed |= named;
            if (row.Count > 3 && Clean(row[3]) is { Length: > 0 } level && level != "-") mob.Level = level;
            if (row.Count > 5)
                foreach (var item in Links(row[5])) AddDrop(mob, item, null);
        }

        foreach (var row in TableRows(Section(wikitext, "Items"), out _))
        {
            if (row.Count < 2) continue;
            var item = Links(row[0]).FirstOrDefault() ?? Clean(row[0]);
            if (item.Length == 0 || item.StartsWith("Example", StringComparison.OrdinalIgnoreCase)) continue;
            var slot = row.Count > 2 ? Clean(row[2]) : "";
            if (slot == "-") slot = "";
            foreach (var mobName in Links(row[1]))
                AddDrop(GetMob(mobs, mobName), item, slot.Length > 0 ? slot : null);
        }

        // Slots learned from the Items table apply to the same item under every mob.
        var slots = mobs.Values.SelectMany(m => m.Drops).Where(d => d.Slot != null)
            .GroupBy(d => ZoneNames.Normalize(d.Name)).ToDictionary(g => g.Key, g => g.First().Slot);
        foreach (var drop in mobs.Values.SelectMany(m => m.Drops))
            drop.Slot ??= slots.GetValueOrDefault(ZoneNames.Normalize(drop.Name));

        return mobs.Values.ToList();
    }

    private static string Section(string text, string heading)
    {
        var m = Regex.Match(text, @"^={2,4}\s*" + Regex.Escape(heading) + @"\s*={2,4}\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var rest = text[(m.Index + m.Length)..];
        var next = Regex.Match(rest, @"^={2,4}[^=].*={2,4}\s*$", RegexOptions.Multiline);
        return next.Success ? rest[..next.Index] : rest;
    }

    /// <summary>Splits wikitable rows into cells; remembers the most recent "!" sub-header seen before each row.</summary>
    private static List<List<string>> TableRows(string table, out Dictionary<List<string>, string> subHeaders)
    {
        var rows = new List<List<string>>();
        subHeaders = new Dictionary<List<string>, string>();
        var currentHeader = "";
        foreach (var chunk in Regex.Split(table, @"^\|-.*$", RegexOptions.Multiline))
        {
            var lines = chunk.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var headerLines = lines.Where(l => l.StartsWith('!')).ToList();
            if (headerLines.Count > 0) currentHeader = string.Join(" ", headerLines);

            var dataLines = lines.Where(l => l.StartsWith('|') && !l.StartsWith("|}") && !l.StartsWith("{|")).ToList();
            if (dataLines.Count == 0) continue;
            var joined = string.Join("||", dataLines.Select(l => l.TrimStart('|')));
            var cells = joined.Split("||").Select(c => c.Trim()).ToList();
            // Drop a leading "style=...|" attribute part if present.
            cells = cells.Select(c => Regex.IsMatch(c, @"^\s*style\s*=") && c.Contains('|') ? c[(c.IndexOf('|') + 1)..].Trim() : c).ToList();
            rows.Add(cells);
            subHeaders[cells] = currentHeader;
        }
        return rows;
    }

    private static IEnumerable<string> Links(string cell) =>
        WikiLink.Matches(cell).Select(m => m.Groups[1].Value.Trim().Replace('_', ' ')).Where(s => s.Length > 0 && !s.StartsWith("File:", StringComparison.OrdinalIgnoreCase));

    private static string Clean(string cell) =>
        WebUtility.HtmlDecode(Regex.Replace(WikiLink.Replace(cell, m => m.Groups[1].Value), "<[^>]+>", "")).Trim();

    // ---------------- MnM Drops (HTML) ----------------

    private static readonly Regex DropsZoneLink = new(@"href=""/zones/([a-z0-9-]+)""[^>]*>\s*<div[^>]*>([^<]+)</div>", RegexOptions.Compiled);
    private static readonly Regex DropsItemCard = new(@"href=""/items/([a-z0-9-]+)""(.*?)</a>", RegexOptions.Compiled | RegexOptions.Singleline);

    public sealed class SlugName
    {
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private static async Task<List<SlugName>> DropsZonesAsync(CancellationToken ct)
    {
        var html = await DropsGetAsync("/zones", ct);
        return DropsZoneLink.Matches(html)
            .Select(m => new SlugName { Slug = m.Groups[1].Value, Name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim() })
            .Where(z => !z.Name.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !z.Name.Equals("Crafted", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(z => z.Slug)
            .ToList();
    }

    private static async Task<ZoneSourceResult> DropsZoneAsync(string slug, string zoneName, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Reading {DropsName} zone page…");
        var html = await DropsGetAsync($"/zones/{slug}", ct);
        var mobLinks = Regex.Matches(html, $@"href=""/mobs/{Regex.Escape(slug)}/([a-z0-9-]+)""[^>]*>\s*<span[^>]*>([^<]+)</span>")
            .Select(m => (Slug: m.Groups[1].Value, Name: WebUtility.HtmlDecode(m.Groups[2].Value).Trim()))
            .DistinctBy(m => m.Slug)
            .ToList();

        var mobs = new List<ZoneMob>();
        for (var i = 0; i < mobLinks.Count; i++)
        {
            var (mobSlug, mobName) = mobLinks[i];
            progress.Report($"Reading {DropsName}: {mobName} ({i + 1}/{mobLinks.Count})…");
            var page = await DropsGetAsync($"/mobs/{slug}/{mobSlug}", ct);
            mobs.Add(ParseDropsMob(page, mobName));
        }
        return new ZoneSourceResult { Source = DropsName, ZoneName = zoneName, FetchedUtc = DateTime.UtcNow, Mobs = mobs };
    }

    public static ZoneMob ParseDropsMob(string html, string mobName)
    {
        var name = ZoneNames.Tidy(mobName);
        var mob = new ZoneMob
        {
            Name = name,
            // MnM Drops title-cases every mob ("A Bloodynose Muralist"), so "A/An ..." marks a common mob.
            IsNamed = !name.StartsWith("A ", StringComparison.Ordinal) && !name.StartsWith("An ", StringComparison.Ordinal),
        };

        var text = Regex.Replace(html, "<[^>]+>", " ");
        var level = Regex.Match(text, @"Level\s+([0-9][0-9\-~+ ]*|Unknown)");
        if (level.Success && !level.Groups[1].Value.StartsWith("Unknown", StringComparison.Ordinal)) mob.Level = level.Groups[1].Value.Trim();

        var start = html.IndexOf("Drop Table", StringComparison.Ordinal);
        if (start < 0) return mob;
        var end = html.IndexOf("Mob Screenshots", start, StringComparison.Ordinal);
        var section = end > start ? html[start..end] : html[start..];

        foreach (Match card in DropsItemCard.Matches(section))
        {
            var body = card.Groups[2].Value;
            var title = Regex.Match(body, @"title=""([^""]+)""");
            var itemName = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value) : card.Groups[1].Value.Replace('-', ' ');
            // Image alt text reads "NAME item card screenshot, slot HEAD" and sometimes appends ", lore: …".
            var slotMatch = Regex.Match(body, @"alt=""[^""]*?, slot ([^"",]+)");
            var slot = slotMatch.Success ? ZoneNames.Tidy(WebUtility.HtmlDecode(slotMatch.Groups[1].Value)) : null;
            AddDrop(mob, ZoneNames.Tidy(itemName), string.IsNullOrWhiteSpace(slot) ? null : slot);
        }
        return mob;
    }

    private static async Task<string> DropsGetAsync(string path, CancellationToken ct)
    {
        await DropsGate.WaitAsync(ct);
        try
        {
            var wait = _lastDropsRequest + DropsDelay - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastDropsRequest = DateTime.UtcNow;
            return await Http.GetStringAsync(DropsBase + path, ct);
        }
        finally
        {
            DropsGate.Release();
        }
    }

    // ---------------- helpers ----------------

    private static ZoneMob GetMob(Dictionary<string, ZoneMob> mobs, string name)
    {
        if (!mobs.TryGetValue(name, out var mob))
        {
            mob = new ZoneMob { Name = name };
            mobs[name] = mob;
        }
        return mob;
    }

    private static void AddDrop(ZoneMob mob, string item, string? slot)
    {
        var existing = mob.Drops.FirstOrDefault(d => string.Equals(d.Name, item, StringComparison.OrdinalIgnoreCase));
        if (existing == null) mob.Drops.Add(new ZoneDrop { Name = item, Slot = slot });
        else existing.Slot ??= slot;
    }

    private static async Task<T> Cached<T>(string key, bool refresh, Func<Task<T>> fetch)
    {
        var path = Path.Combine(CacheFolder, $"v{CacheVersion}-{key}.json");
        if (!refresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < CacheAge)
        {
            try
            {
                var cached = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path));
                if (cached != null) return cached;
            }
            catch (JsonException) { }
        }

        var value = await fetch();
        try
        {
            Directory.CreateDirectory(CacheFolder);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
        }
        catch (IOException) { }
        return value;
    }
}
