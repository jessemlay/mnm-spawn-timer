using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpawnTimer.Core;

public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string Tag { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string SetupUrl { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

/// <summary>
/// Checks this repo's latest GitHub release and installs it in place: downloads SpawnTimer-Setup.exe, verifies it
/// against the SHA-256 digest GitHub publishes for the asset, and runs it silently with /RELAUNCH=1 so the
/// installer restarts the app when it's done.
/// </summary>
public static class Updates
{
    public const string Repo = "jessemlay/mnm-spawn-timer";
    private const string SetupAsset = "SpawnTimer-Setup.exe";

    // Static fields initialize in textual order: the version must exist before the client's User-Agent uses it.
    /// <summary>This build's version, e.g. 1.4.0 (CI/dev builds carry a suffix that is ignored here).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>True for local or CI builds (e.g. 1.4.0-ci.12), which shouldn't auto-install over themselves.</summary>
    public static bool IsDevBuild { get; } = ReadInformational().Contains('-');

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SpawnTimer/{CurrentVersion} (+https://github.com/{Repo})");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string ReadInformational() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private static Version ReadCurrentVersion() => ParseVersion(ReadInformational()) ?? new Version(0, 0);

    /// <summary>"v1.4.0", "1.4.0-ci.3+abc" → 1.4.0.</summary>
    public static Version? ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>The latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version == null || version <= CurrentVersion) return null;

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == SetupAsset);
        if (asset.ValueKind != JsonValueKind.Object) return null;

        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        return new UpdateInfo
        {
            Version = version,
            Tag = tag,
            PageUrl = root.GetProperty("html_url").GetString() ?? "",
            SetupUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
            Size = asset.GetProperty("size").GetInt64(),
            Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : "",
        };
    }

    /// <summary>Downloads the installer and checks its size and SHA-256. Throws if anything doesn't match.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct)
    {
        if (update.Sha256.Length != 64)
            throw new InvalidOperationException("The release doesn't publish a SHA-256 digest for the installer, so it can't be verified.");
        var uri = new Uri(update.SetupUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected download location: " + update.SetupUrl);

        var folder = Path.Combine(Path.GetTempPath(), "SpawnTimer-Update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"SpawnTimer-Setup-{update.Version}.exe");

        var bytes = await Http.GetByteArrayAsync(uri, ct);
        if (bytes.LongLength != update.Size)
            throw new InvalidOperationException($"Download size mismatch ({bytes.LongLength} bytes, expected {update.Size}).");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != update.Sha256)
            throw new InvalidOperationException("Download checksum doesn't match GitHub's SHA-256 digest; not installing.");

        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }
}
