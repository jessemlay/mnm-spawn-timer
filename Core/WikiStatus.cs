using System;

namespace SpawnTimer.Core;

/// <summary>
/// Remembers that the M&amp;M Wiki (Miraheze) just failed, so lookups go straight to saved copies and MnM Archive
/// for a couple of minutes instead of waiting on the wiki every time. Shared by items, zones and mob pictures.
/// </summary>
public static class WikiStatus
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    private static long _downUntilTicks;

    /// <summary>The wiki failed within the last couple of minutes.</summary>
    public static bool LikelyDown => DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref _downUntilTicks);

    public static void Failed() => System.Threading.Interlocked.Exchange(ref _downUntilTicks, (DateTime.UtcNow + RetryAfter).Ticks);

    public static void Succeeded() => System.Threading.Interlocked.Exchange(ref _downUntilTicks, 0);
}
