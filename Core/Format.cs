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
    // 10 copper = 1 silver, 10 silver = 1 gold, 10 gold = 1 platinum.
    public static string Format(long copper)
    {
        if (copper <= 0) return "0c";
        var parts = new List<string>(4);
        long p = copper / 1000, g = copper / 100 % 10, s = copper / 10 % 10, c = copper % 10;
        if (p > 0) parts.Add($"{p}p");
        if (g > 0) parts.Add($"{g}g");
        if (s > 0) parts.Add($"{s}s");
        if (c > 0) parts.Add($"{c}c");
        return string.Join(" ", parts);
    }
}
