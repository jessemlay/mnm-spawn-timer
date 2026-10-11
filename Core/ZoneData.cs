using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpawnTimer.Core;

public sealed class ZoneDrop
{
    public string Name { get; set; } = "";
    public string? Slot { get; set; }
}

public sealed class ZoneMob
{
    public string Name { get; set; } = "";
    public bool IsNamed { get; set; }
    public string? Level { get; set; }
    public List<ZoneDrop> Drops { get; set; } = new();
}

/// <summary>One zone's mobs and drops as reported by one source.</summary>
public sealed class ZoneSourceResult
{
    public string Source { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public DateTime FetchedUtc { get; set; }
    public List<ZoneMob> Mobs { get; set; } = new();
}

/// <summary>A zone as known to each source (either id can be missing).</summary>
public sealed class ZoneRef
{
    public string Name { get; set; } = "";
    public string? WikiTitle { get; set; }
    public string? DropsSlug { get; set; }

    public string Key => ZoneNames.Normalize(Name);

    public override string ToString()
    {
        var where = (WikiTitle != null, DropsSlug != null) switch
        {
            (true, true) => "wiki + mnmdrops",
            (true, false) => "wiki",
            _ => "mnmdrops",
        };
        return $"{Name}   ({where})";
    }
}

public static class ZoneNames
{
    // Ledger zone codes that don't normalize to the zone's display name.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wyrmsbanetomb"] = "tombofthelastwyrmsbane",
        ["nightharborw"] = "nightharbor",
        ["nightharbore"] = "nightharbor",
        ["evergrove"] = "evershadeweald",
    };

    /// <summary>Lowercase letters and digits only: "Tel Ekir" / "tel-ekir" / "telekir" all become "telekir".</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Finds the zone matching a Ledger zone code such as "telekir" or "nightharborw".</summary>
    public static ZoneRef? MatchLedgerZone(string? ledgerCode, IEnumerable<ZoneRef> zones)
    {
        var code = Normalize(ledgerCode);
        if (code.Length == 0) return null;
        if (Aliases.TryGetValue(code, out var alias)) code = alias;
        var list = zones.ToList();
        return list.FirstOrDefault(z => z.Key == code)
               ?? list.Where(z => z.Key.Length >= 5 && code.StartsWith(z.Key, StringComparison.Ordinal))
                   .OrderByDescending(z => z.Key.Length).FirstOrDefault();
    }

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "a", "an", "and", "in", "on", "to", "for", "with", "at", "from" };

    /// <summary>"SHIELD OF THE PROTECTOR" to "Shield of the Protector"; leaves mixed-case text alone.</summary>
    public static string Tidy(string text)
    {
        text = text.Trim();
        if (text.Any(char.IsLower)) return text;
        var words = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()).Split(' ');
        for (var i = 1; i < words.Length; i++)
            if (SmallWords.Contains(words[i])) words[i] = words[i].ToLowerInvariant();
        return string.Join(' ', words);
    }

    /// <summary>Mob identity across sources: ignores case, punctuation and a leading "a"/"an"/"the".</summary>
    public static string MobKey(string name)
    {
        var trimmed = name.Trim();
        foreach (var article in new[] { "a ", "an ", "the " })
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { trimmed = trimmed[article.Length..]; break; }
        return Normalize(trimmed);
    }
}

/// <summary>An item row in the import dialog, merged across sources.</summary>
public sealed class ZoneItemRow : Observable
{
    private bool _selected;

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Item { get; init; } = "";
    public string Slot { get; init; } = "";
    public string DroppedBy { get; init; } = "";
    public bool FromNamed { get; init; }
    public string Sources { get; init; } = "";
    public bool AlreadyAlerting { get; init; }

    public string NamedText => FromNamed ? "Named" : "";
}

public static class ZoneMerge
{
    // Crafting materials and vendor junk; only used to decide the default tick when the slot is unknown.
    private static readonly string[] MaterialWords =
    {
        "scraps", "chips", "meat", "tooth", "teeth", "fang", "ear", "eye", "wing", "silk", "pelt",
        "venom", "sac", "bone", "egg", "leg", "tail", "dust", "head of",
    };

    /// <summary>Distinct mobs (across sources) that have at least one listed drop.</summary>
    public static int CountMobsWithDrops(IEnumerable<ZoneSourceResult> results) =>
        results.SelectMany(r => r.Mobs).Where(m => m.Drops.Count > 0).Select(m => ZoneNames.MobKey(m.Name)).Distinct().Count();

    public static List<ZoneItemRow> BuildRows(IEnumerable<ZoneSourceResult> results, string existingAlertList)
    {
        var items = new Dictionary<string, (string Name, string? Slot, Dictionary<string, string> Mobs, bool Named, SortedSet<string> Sources)>();
        foreach (var result in results)
        {
            foreach (var mob in result.Mobs)
            {
                foreach (var drop in mob.Drops)
                {
                    var key = ZoneNames.Normalize(drop.Name);
                    if (key.Length == 0) continue;
                    if (!items.TryGetValue(key, out var row))
                    {
                        row = (drop.Name, drop.Slot, new Dictionary<string, string>(), false,
                            new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                    // Prefer the first source's spelling of names (the wiki is read first).
                    row.Mobs.TryAdd(ZoneNames.MobKey(mob.Name), mob.Name);
                    row.Sources.Add(result.Source);
                    items[key] = (row.Name, row.Slot ?? drop.Slot, row.Mobs, row.Named || mob.IsNamed, row.Sources);
                }
            }
        }

        return items.Values
            .Select(v =>
            {
                var already = NameMatcher.MatchesAny(v.Name, existingAlertList);
                return new ZoneItemRow
                {
                    Item = v.Name,
                    Slot = v.Slot ?? "",
                    DroppedBy = string.Join(", ", v.Mobs.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                    FromNamed = v.Named,
                    Sources = string.Join(" + ", v.Sources),
                    AlreadyAlerting = already,
                    Selected = !already && IsLikelyGood(v.Name, v.Slot, v.Named),
                };
            })
            .OrderByDescending(r => r.FromNamed)
            .ThenBy(r => r.DroppedBy, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Default tick: gear from named mobs (known slot, or a name that doesn't look like a material).</summary>
    public static bool IsLikelyGood(string item, string? slot, bool fromNamed)
    {
        if (!fromNamed) return false;
        if (!string.IsNullOrWhiteSpace(slot)) return true;
        var lower = item.ToLowerInvariant();
        return !MaterialWords.Any(w => lower.EndsWith(w, StringComparison.Ordinal) || lower.StartsWith(w, StringComparison.Ordinal));
    }
}
