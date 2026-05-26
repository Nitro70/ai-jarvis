using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Setup.Services;

/// <summary>
/// Hits the GitHub Releases API to find out what the latest tagged release
/// is, so the Settings app's Updates tab can tell the user whether an
/// update is available.
/// </summary>
public static class UpdateChecker
{
    public const string RepoOwner = "Nitro70";
    public const string RepoName  = "ai-jarvis";

    private static string LatestReleaseApi =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public record ReleaseInfo(
        string Tag,
        string Name,
        string Body,
        string HtmlUrl,
        string? SettingsExeUrl,
        string? SourceZipUrl);

    public static async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisSettings/1.0");
        using var resp = await http.GetAsync(LatestReleaseApi, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var url  = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

        string? settingsExe = null;
        string? sourceZip = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var aName = asset.GetProperty("name").GetString() ?? "";
                var aUrl  = asset.GetProperty("browser_download_url").GetString();
                if (aName.Equals("JarvisSettings.exe", StringComparison.OrdinalIgnoreCase))
                    settingsExe = aUrl;
            }
        }
        // GitHub always exposes the source-as-zipball via a fixed URL pattern.
        sourceZip = $"https://github.com/{RepoOwner}/{RepoName}/archive/refs/tags/{tag}.zip";

        return new ReleaseInfo(tag, name, body, url, settingsExe, sourceZip);
    }

    /// <summary>
    /// Parses a tag like "v0.1.5" or "0.1.5" into a System.Version. Returns
    /// null if it can't.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        // Strip pre-release suffix ("-beta.1" etc.) since System.Version
        // doesn't accept it.
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static bool IsNewer(string latest, string? installed)
    {
        var L = ParseTag(latest);
        if (L == null) return false;
        // Treat the broken "1.0.0" sentinel as unknown. Pre-0.1.12 installer
        // stamped this string into every install-info.json regardless of the
        // actual release (Jarvis.Setup.dll's default assembly version was
        // 1.0.0.0 because no <Version> was set on it). Without this guard
        // affected users would see "up to date" forever even when a real
        // release like 0.1.12 is available.
        if (installed == "1.0.0" || installed == "1.0.0.0") return true;
        var I = ParseTag(installed);
        if (I == null) return true; // unknown installed -> assume newer wins
        return L > I;
    }
}
