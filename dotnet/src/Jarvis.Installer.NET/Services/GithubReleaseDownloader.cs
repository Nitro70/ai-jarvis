using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Installer.NET.Services;

/// <summary>
/// Pulls binary assets from the latest GitHub release. The .NET edition
/// ships pre-built single-file exes (Jarvis-NET.exe, JarvisSettings-NET.exe);
/// the installer doesn't compile from source.
/// </summary>
public static class GithubReleaseDownloader
{
    private const string RepoOwner = "Nitro70";
    private const string RepoName  = "ai-jarvis";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

    /// <summary>
    /// Result of a release asset lookup. Url is null if the asset doesn't
    /// exist in the latest release — caller decides whether that's fatal
    /// (Jarvis-NET.exe) or soft-fail (JarvisSettings-NET.exe).
    /// </summary>
    public record AssetLookup(string? Url, string? Tag, string? Reason);

    /// <summary>
    /// Look up a named asset in the latest release. Returns AssetLookup with
    /// Url == null and Reason set if the release exists but the asset doesn't,
    /// or if the API returned 404 (no releases yet). Throws on network /
    /// other errors that aren't a missing-asset condition.
    /// </summary>
    public static async Task<AssetLookup> FindAssetAsync(string assetName, CancellationToken ct)
    {
        using var http = NewHttp(TimeSpan.FromSeconds(30));
        using var resp = await http.GetAsync(LatestReleaseApi, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new AssetLookup(null, null, "no releases published yet");

        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag  = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

        if (!root.TryGetProperty("assets", out var assets))
            return new AssetLookup(null, tag, "release has no 'assets' field");

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
            {
                var url = a.GetProperty("browser_download_url").GetString();
                return new AssetLookup(url, tag, null);
            }
        }
        return new AssetLookup(null, tag, $"asset '{assetName}' not in release {tag}");
    }

    /// <summary>
    /// Stream a URL to a destination file path, reporting download progress.
    /// </summary>
    public static async Task DownloadAsync(
        string url, string destPath,
        IProgress<double>? percent,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var http = NewHttp(TimeSpan.FromMinutes(15));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;

        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[262144];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) percent?.Report(100.0 * read / total);
        }
    }

    public static HttpClient NewHttp(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisInstaller-NET/1.0");
        // GitHub API requires Accept for application/vnd.github+json on the
        // /releases endpoints to get stable response shapes.
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
