using System;
using System.Collections.Generic;
using System.Linq;

namespace SpawnTimer.Core;

/// <summary>Who in your group can use an item: the wiki's class list against the classes the Ledger shows (f10).</summary>
public static class PartyUse
{
    /// <summary>
    /// "Usable by: you (CLR), Zetherin (PAL) · Not: Kensho (MNK)", or null when the item has no class list or
    /// nobody's class is known. Groupmates whose class hasn't shown up in the Ledger yet are left out.
    /// </summary>
    public static string? Describe(ItemDetails? item, IReadOnlyList<(string Name, string? Class)> members, string? myClass)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Classes)) return null;

        var people = new List<(string Label, string Class)>();
        if (!string.IsNullOrWhiteSpace(myClass)) people.Add(("you", myClass.Trim().ToUpperInvariant()));
        foreach (var (name, cls) in members)
            if (!string.IsNullOrWhiteSpace(cls)) people.Add((name, cls.Trim().ToUpperInvariant()));
        if (people.Count == 0) return null;

        var codes = item.Classes.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (codes.Contains("ALL")) return "Usable by every class";

        var yes = people.Where(p => item.UsableBy(p.Class)).Select(Label).ToList();
        var no = people.Where(p => !item.UsableBy(p.Class)).Select(Label).ToList();
        if (yes.Count == 0) return "No one here can use it: " + string.Join(", ", no);
        return "Usable by: " + string.Join(", ", yes) + (no.Count > 0 ? " · Not: " + string.Join(", ", no) : "");

        static string Label((string Label, string Class) p) => $"{p.Label} ({p.Class})";
    }
}
