using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SpawnTimer.Core;

/// <summary>
/// Writes a session as two CSV files: every ledger event, and a per-mob summary (same name + "_mobs").
/// UTF-8 with BOM so Excel shows item names correctly.
/// </summary>
public static class SessionExport
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Spawn Timer Sessions");

    public static string DefaultFileName(SessionStats stats) =>
        $"MnM session {stats.Start.LocalDateTime:yyyy-MM-dd HHmm}.csv";

    public static string MobsPath(string eventsPath) =>
        Path.Combine(Path.GetDirectoryName(eventsPath) ?? "",
            Path.GetFileNameWithoutExtension(eventsPath) + "_mobs" + Path.GetExtension(eventsPath));

    /// <summary>Returns the per-mob summary path.</summary>
    public static string Write(SessionStats stats, string eventsPath)
    {
        WriteEvents(stats, eventsPath);
        var mobsPath = MobsPath(eventsPath);
        WriteMobs(stats, mobsPath);
        return mobsPath;
    }

    private static void WriteEvents(SessionStats stats, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Row(w, "Time", "Zone", "Event", "Who", "Mine", "Corpse", "Item", "Quantity",
            "Coin total (copper)", "My coin (copper)", "My coin", "Detail");

        foreach (var e in stats.Events)
        {
            var detail = e.Kind switch
            {
                LedgerKind.CoinLoot => e.CoinKind,
                LedgerKind.LevelUp => e.Level is { } level ? $"Level {level}" : null,
                LedgerKind.Party => JoinNonEmpty(e.Subject, e.PartySize is { } n ? $"party size {n}" : null),
                _ when e.Action is "act_16" or "act_20" && !string.IsNullOrEmpty(e.Subject) => $"with {e.Subject}",
                _ => e.Subject,
            };
            Row(w,
                e.Time.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
                e.Zone,
                LedgerParser.Describe(e),
                e.Actor,
                e.IsMine ? "Y" : "N",
                e.Corpse,
                e.Item,
                e.Quantity > 0 ? Num(e.Quantity) : null,
                e.CoinTotalCp > 0 ? Num(e.CoinTotalCp) : null,
                e.MyCoinCp > 0 ? Num(e.MyCoinCp) : null,
                e.MyCoinCp > 0 ? CoinFormat.Format(e.MyCoinCp) : null,
                detail);
        }
    }

    private static void WriteMobs(SessionStats stats, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Row(w, "Mob", "Zone", "Corpses looted", "Items looted (party)", "Items looted (me)",
            "My coin (copper)", "My coin", "First seen", "Last seen");

        foreach (var m in stats.Mobs.OrderByDescending(m => m.Corpses).ThenBy(m => m.Name))
        {
            Row(w, m.Name, m.Zone, Num(m.Corpses), Num(m.ItemsLooted), Num(m.MyItemsLooted),
                Num(m.MyCoinCp), CoinFormat.Format(m.MyCoinCp),
                m.FirstSeen.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
                m.LastSeen.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        }

        // Session totals; coin here includes vendor sales, which aren't tied to a mob.
        w.WriteLine();
        Row(w, "SESSION TOTAL", stats.Zone, Num(stats.Kills), "", Num(stats.ItemsLooted),
            Num(stats.CoinCp), CoinFormat.Format(stats.CoinCp),
            stats.Start.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            DateTimeOffset.Now.LocalDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
        Row(w, "Vendor sales", "", "", "", "", Num(stats.SalesCp), CoinFormat.Format(stats.SalesCp), "", "");
        Row(w, "Levels gained", "", Num(stats.LevelsGained), "", "", "", "", "", "");
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string? JoinNonEmpty(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private static void Row(TextWriter w, params string?[] values) =>
        w.WriteLine(string.Join(",", values.Select(Escape)));

    /// <summary>RFC 4180 quoting, plus a leading ' on values a spreadsheet would treat as a formula.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
