using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpawnTimer.Core;

/// <summary>
/// Fallback source: MnM Archive (mnmarchive.com), a separately hosted fan reference that adapts the M&amp;M Wiki
/// (CC BY-SA 4.0) and adds its own catalog. Used when the wiki is unreachable or lacks a page: one item page per
/// lookup, or a zone page for its mob list. robots.txt allows it; requests are spaced a second apart and cached.
/// </summary>
public static class MnmArchive
{
    public const string Name = "MnM Archive";
    public const string Base = "https://mnmarchive.com";
    public const string Credit = "MnM Archive (from the M&M Wiki, CC BY-SA 4.0)";

    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SpawnTimer/1.6 (personal Monsters & Memories overlay; lookup fallback when the wiki is down; caches results)");
        return client;
    }

    /// <summary>The Archive's page name for a title: lowercase, apostrophes dropped, other punctuation to hyphens.</summary>
    public static string Slug(string title)
    {
        var s = title.Trim().ToLowerInvariant().Replace("'", "").Replace("’", "");
        s = Regex.Replace(s, "[^a-z0-9]+", "-");
        return s.Trim('-');
    }

    public static string ItemUrl(string title) => $"{Base}/items/{Slug(title)}";
    public static string NpcUrl(string name) => $"{Base}/npcs/{Slug(name)}";
    public static string ZoneUrl(string zone) => $"{Base}/zones/{Slug(zone)}";

    /// <summary>The item's Archive page, or null when it has none (404).</summary>
    public static async Task<ItemDetails?> GetItemAsync(string title, CancellationToken ct)
    {
        var url = ItemUrl(title);
        var html = await GetPageAsync(url, ct);
        return html == null ? null : ParseItem(title, html, url);
    }

    /// <summary>The zone's mobs from its Archive page (named mobs with a preview of their drops), or null.</summary>
    public static async Task<ZoneSourceResult?> GetZoneAsync(string zone, CancellationToken ct)
    {
        var html = await GetPageAsync(ZoneUrl(zone), ct);
        return html == null ? null : new ZoneSourceResult { Source = Name, ZoneName = zone, FetchedUtc = DateTime.UtcNow, Mobs = ParseZone(html) };
    }

    /// <summary>Every zone name on the Archive's zone index.</summary>
    public static async Task<List<string>> ZoneNamesAsync(CancellationToken ct)
    {
        var html = await GetPageAsync($"{Base}/zones", ct) ?? "";
        return Links(MainContent(html), "zones").Select(l => l.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string?> GetPageAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + Spacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---------------- parsing ----------------

    private static readonly Regex Tooltip = new("<article class=\"game-tooltip\"[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Field = new(@"<b>\s*([^<:]+?)\s*:\s*</b>(.*?)(?=<b>|</div>|<span|&nbsp;)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Stat = new("<span data-item-stat=\"([^\"]+)\"[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DroppedRow = new(@"<strong>s*Droppeds*</strong>s*<span>(.*?)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Anchor = new("<a [^>]*href=\"/(items|npcs|zones)/([^\"#?]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);

    public static ItemDetails? ParseItem(string title, string html, string url)
    {
        var tip = Tooltip.Match(html);
        if (!tip.Success) return null;
        var body = tip.Groups[1].Value;
        var h1 = Regex.Match(html, "<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline);
        var item = new ItemDetails
        {
            Title = title,
            Name = h1.Success ? Text(h1.Groups[1].Value) : title,
            Source = Name,
            SourceUrl = url,
            FetchedUtc = DateTime.UtcNow,
        };

        var flags = Regex.Match(body, "<div class=\"tooltip-flags\">(.*?)</div>", RegexOptions.Singleline);
        var flagText = flags.Success ? Text(flags.Groups[1].Value).ToUpperInvariant() : "";
        item.Magic = flagText.Contains("MAGIC");
        item.Lore = flagText.Contains("LORE");
        item.NoDrop = flagText.Contains("NO DROP") || flagText.Contains("NODROP");

        foreach (Match f in Field.Matches(body))
        {
            var value = Text(f.Groups[2].Value);
            switch (f.Groups[1].Value.Trim().ToUpperInvariant())
            {
                case "SLOT": item.Slot = TitleCase(value); break;
                case "WEAPON DMG": item.Damage = value; break;
                case "ATK DELAY": item.Delay = value; break;
                case "SKILL": item.Skill = value; break;
                case "HANDS": item.Handed = value; break;
                case "WEIGHT": item.Weight = value; break;
                case "SIZE": item.Size = TitleCase(value); break;
                case "CLASS": item.Classes = value; break;
                case "RACE": item.Races = value; break;
                case "EFFECT": item.Effect = value; break;
            }
        }

        foreach (Match s in Stat.Matches(body))
        {
            var key = s.Groups[1].Value;
            if (key == "WEAPON_RATIO") continue;
            var text = Text(s.Groups[2].Value); // "STR: +4"
            var colon = text.IndexOf(':');
            if (colon <= 0) continue;
            var label = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            if (key == "AC") item.Ac = value.TrimStart('+');
            else item.Stats.Add(new[] { label, value });
        }

        var description = Regex.Match(body, "<p class=\"tooltip-description\">(.*?)</p>", RegexOptions.Singleline);
        if (description.Success) item.Description = Text(description.Groups[1].Value);

        // "How to obtain it": each "Dropped" row names the zone, then the NPC. Merchant, quest and crafted rows
        // name NPCs too, so only Dropped rows count.
        foreach (Match row in DroppedRow.Matches(Section(html, "How to obtain it")))
        {
            var zone = "";
            foreach (var link in Links(row.Groups[1].Value, "zones", "npcs"))
            {
                if (link.Kind == "zones") zone = link.Text;
                else
                {
                    var source = zone.Length > 0 ? $"{link.Text} ({zone})" : link.Text;
                    if (!item.DropsFrom.Contains(source, StringComparer.OrdinalIgnoreCase)) item.DropsFrom.Add(source);
                }
            }
        }
        return item;
    }

    public static List<ZoneMob> ParseZone(string html)
    {
        var mobs = new Dictionary<string, ZoneMob>(StringComparer.OrdinalIgnoreCase);
        var main = MainContent(html);

        foreach (Match card in Regex.Matches(main, "<article class=\"notable-npc-card\">(.*?)</article>", RegexOptions.Singleline))
        {
            var c = card.Groups[1].Value;
            var npc = Links(c, "npcs").FirstOrDefault();
            if (npc.Text == null) continue;
            var info = Regex.Match(c, "</h3>\\s*<span>(.*?)</span>", RegexOptions.Singleline);
            var mob = Mob(mobs, npc.Text);
            mob.IsNamed = info.Success && info.Groups[1].Value.Contains("Named", StringComparison.OrdinalIgnoreCase);
            mob.Level = Level(info.Success ? Text(info.Groups[1].Value) : "");
            foreach (var drop in Links(c, "items"))
                if (!mob.Drops.Any(d => d.Name.Equals(drop.Text, StringComparison.OrdinalIgnoreCase)))
                    mob.Drops.Add(new ZoneDrop { Name = drop.Text });
        }

        var others = Regex.Match(main, "<ul class=\"zone-creature-list\">(.*?)</ul>", RegexOptions.Singleline);
        if (others.Success)
            foreach (Match li in Regex.Matches(others.Groups[1].Value, "<li>(.*?)</li>", RegexOptions.Singleline))
            {
                var npc = Links(li.Groups[1].Value, "npcs").FirstOrDefault();
                if (npc.Text == null) continue;
                var level = Regex.Match(li.Groups[1].Value, "<small>(.*?)</small>", RegexOptions.Singleline);
                Mob(mobs, npc.Text).Level ??= Level(level.Success ? Text(level.Groups[1].Value) : "");
            }
        return mobs.Values.ToList();
    }

    private static ZoneMob Mob(Dictionary<string, ZoneMob> mobs, string name)
    {
        if (!mobs.TryGetValue(name, out var mob)) mobs[name] = mob = new ZoneMob { Name = name };
        return mob;
    }

    /// <summary>"Level 28-30 · Named" → "28-30"; "Level unknown" → null.</summary>
    private static string? Level(string text)
    {
        var m = Regex.Match(text, @"Level\s+([0-9][0-9\-+?~ ]*)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string MainContent(string html)
    {
        var start = html.IndexOf("<main", StringComparison.Ordinal);
        var end = html.IndexOf("</main>", StringComparison.Ordinal);
        return start >= 0 && end > start ? html[start..end] : html;
    }

    /// <summary>From a section's &lt;h2&gt; heading to the next section or the end of the main content.</summary>
    private static string Section(string html, string heading)
    {
        var main = MainContent(html);
        var at = main.IndexOf($"<h2>{heading}</h2>", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        var next = main.IndexOf("<section", at, StringComparison.Ordinal);
        return next > at ? main[at..next] : main[at..];
    }

    private static IEnumerable<(string Kind, string Text)> Links(string html, params string[] kinds)
    {
        foreach (Match a in Anchor.Matches(html))
        {
            if (!kinds.Contains(a.Groups[1].Value)) continue;
            var text = Text(a.Groups[3].Value);
            if (text.Length > 0 && !text.StartsWith('+') && !text.StartsWith("View ", StringComparison.Ordinal))
                yield return (a.Groups[1].Value, text);
        }
    }

    private static string Text(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    private static string TitleCase(string upper) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(upper.ToLowerInvariant());
}
