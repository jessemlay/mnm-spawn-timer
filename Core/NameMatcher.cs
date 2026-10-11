using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnTimer.Core;

/// <summary>
/// Case-insensitive name lists, one pattern per line: "Exact Name", "prefix*", "*suffix" or "*contains*".
/// </summary>
public static class NameMatcher
{
    public static IEnumerable<string> Patterns(string? list, bool allowCommas = false)
    {
        if (string.IsNullOrWhiteSpace(list)) return Enumerable.Empty<string>();
        var separators = allowCommas ? new[] { '\r', '\n', ',' } : new[] { '\r', '\n' };
        return list.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && p != "*");
    }

    public static bool MatchesAny(string? value, string? list, bool allowCommas = false) =>
        !string.IsNullOrEmpty(value) && Patterns(list, allowCommas).Any(p => Matches(value, p));

    public static bool Matches(string value, string pattern)
    {
        var starts = pattern.StartsWith('*');
        var ends = pattern.EndsWith('*');
        var core = pattern.Trim('*').Trim();
        if (core.Length == 0) return false;
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        return (starts, ends) switch
        {
            (true, true) => value.Contains(core, ic),
            (false, true) => value.StartsWith(core, ic),
            (true, false) => value.EndsWith(core, ic),
            _ => value.Equals(core, ic),
        };
    }
}

/// <summary>Decides whether a corpse belongs to a named mob.</summary>
public static class NamedDetector
{
    public static bool IsNamed(string corpse, NamedSettings settings, IReadOnlySet<string> knownPlayers)
    {
        if (string.IsNullOrWhiteSpace(corpse)) return false;
        if (NameMatcher.MatchesAny(corpse, settings.IgnoreNames)) return false;
        if (NameMatcher.MatchesAny(corpse, settings.WatchNames)) return true;
        if (!settings.AutoDetect) return false;

        // Common mobs are "a ..."/"an ..."; named mobs are capitalized ("General Overlord Bloodynose").
        // Exclude players (seen as actors or party members), their corpses and pets.
        if (!char.IsUpper(corpse[0])) return false;
        if (knownPlayers.Contains(corpse)) return false;
        if (corpse.Contains("corpse", StringComparison.OrdinalIgnoreCase)) return false;
        if (corpse.Contains("'s ", StringComparison.Ordinal) || corpse.Contains("`s ", StringComparison.Ordinal)) return false;
        return true;
    }
}
