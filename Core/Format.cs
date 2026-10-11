using System;
using System.Collections.Generic;
using System.Globalization;

namespace SpawnTimer.Core;

public static class TimeFormat
{
    /// <summary>"m:ss", or "h:mm:ss" from one hour up.</summary>
    public static string Clock(TimeSpan t)
    {
        var total = (long)Math.Max(0, Math.Round(t.TotalSeconds));
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>"just now", "8m ago", "1h 12m ago", "3d ago".</summary>
    public static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "just now";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes}m ago";
        if (t < TimeSpan.FromHours(48)) return $"{(int)t.TotalHours}h {t.Minutes}m ago";
        return $"{(int)t.TotalDays}d ago";
    }

    /// <summary>Accepts "400", "6:40" or "1:06:40".</summary>
    public static bool TryParseDuration(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;
        long total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
            total = total * 60 + n;
        }
        if (total <= 0 || total > int.MaxValue) return false;
        seconds = (int)total;
        return true;
    }
}

public static class CoinFormat
{
    // M&M: 100 copper = 1 silver, 100 silver = 1 gold, 100 gold = 1 platinum.
    public const long Silver = 100;
    public const long Gold = 100 * Silver;
    public const long Platinum = 100 * Gold;

    public static long ToCopper(long pp, long gp, long sp, long cp) => pp * Platinum + gp * Gold + sp * Silver + cp;

    public static string Format(long copper)
    {
        if (copper <= 0) return "0c";
        var parts = new List<string>(4);
        long p = copper / Platinum, g = copper / Gold % 100, s = copper / Silver % 100, c = copper % 100;
        if (p > 0) parts.Add($"{p}p");
        if (g > 0) parts.Add($"{g}g");
        if (s > 0) parts.Add($"{s}s");
        if (c > 0) parts.Add($"{c}c");
        return string.Join(" ", parts);
    }
}
