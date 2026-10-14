using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpawnTimer.Core;

/// <summary>
/// Small thumbnails of mobs for the mob lookup card: the page image of the mob's M&M Wiki page (Miraheze,
/// CC BY-SA 4.0), fetched once per mob when it's highlighted and cached on disk for 7 days ("no image" too).
/// </summary>
public static class MobImages
{
    private const string WikiApi = "https://monstersandmemories.miraheze.org/w/api.php";
    private const int ThumbWidth = 128;
    private const int MaxBytes = 1024 * 1024;
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, Entry>? _entries;

    private sealed class Entry
    {
        /// <summary>File name in the cache folder, or "" when the page has no image (or no page).</summary>
        public string File { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
    }

    private static string Folder => Path.Combine(SettingsStore.CacheFolder, "mob-images");
    private static string IndexPath => Path.Combine(Folder, "v1-index.json");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SpawnTimer/1.5 (personal Monsters & Memories overlay; mob lookup thumbnails; caches results)");
        return client;
    }

    /// <summary>
    /// From the cache only: <paramref name="known"/> is false when the mob hasn't been looked up yet (or the cache
    /// is stale); otherwise the result is the image path, or null for a mob without an image.
    /// </summary>
    public static string? TryGetCached(string mob, out bool known)
    {
        var entries = Load();
        lock (entries)
        {
            known = entries.TryGetValue(mob.Trim(), out var e) && DateTime.UtcNow - e.FetchedUtc < CacheAge
                && (e.File.Length == 0 || System.IO.File.Exists(Path.Combine(Folder, e.File)));
            return known && e!.File.Length > 0 ? Path.Combine(Folder, e.File) : null;
        }
    }

    /// <summary>The mob's thumbnail (downloading it if needed), or null if its wiki page has none.</summary>
    public static async Task<string?> GetAsync(string mob, CancellationToken ct)
    {
        mob = mob.Trim();
        if (mob.Length == 0) return null;
        var cached = TryGetCached(mob, out var known);
        if (known) return cached;

        await Gate.WaitAsync(ct);
        try
        {
            cached = TryGetCached(mob, out known);
            if (known) return cached;

            var url = WikiApi + "?action=query&format=json&formatversion=2&redirects=1&prop=pageimages&piprop=thumbnail"
                      + "&pithumbsize=" + ThumbWidth + "&titles=" + Uri.EscapeDataString(mob);
            string? thumb = null;
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct)))
            {
                if (doc.RootElement.TryGetProperty("query", out var q) && q.TryGetProperty("pages", out var pages))
                    foreach (var page in pages.EnumerateArray())
                        if (page.TryGetProperty("thumbnail", out var t) && t.TryGetProperty("source", out var src))
                            thumb = src.GetString();
            }

            var file = "";
            if (thumb != null && IsWikiImageHost(thumb))
            {
                using var resp = await Http.GetAsync(thumb, HttpCompletionOption.ResponseHeadersRead, ct);
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (resp.IsSuccessStatusCode && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && (resp.Content.Headers.ContentLength ?? 0) <= MaxBytes)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length is > 0 and <= MaxBytes)
                    {
                        file = FileName(mob, thumb);
                        Directory.CreateDirectory(Folder);
                        await System.IO.File.WriteAllBytesAsync(Path.Combine(Folder, file), bytes, ct);
                    }
                }
            }

            var entries = Load();
            lock (entries) entries[mob] = new Entry { File = file, FetchedUtc = DateTime.UtcNow };
            Save();
            return file.Length > 0 ? Path.Combine(Folder, file) : null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Only download from the wiki's own image servers.</summary>
    private static bool IsWikiImageHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".wikitide.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".miraheze.org", StringComparison.OrdinalIgnoreCase));

    /// <summary>A safe, stable file name: hash of the mob name plus the image's extension.</summary>
    private static string FileName(string mob, string url)
    {
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")) ext = ".img";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mob.ToLowerInvariant())))[..16];
        return hash + ext;
    }

    private static Dictionary<string, Entry> Load()
    {
        if (_entries != null) return _entries;
        var loaded = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.File.Exists(IndexPath))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(System.IO.File.ReadAllText(IndexPath));
                if (saved != null)
                    foreach (var (k, v) in saved) loaded[k] = v;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return Interlocked.CompareExchange(ref _entries, loaded, null) ?? loaded;
    }

    private static void Save()
    {
        var entries = Load();
        string json;
        lock (entries) json = JsonSerializer.Serialize(entries);
        try
        {
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllText(IndexPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
