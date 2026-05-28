using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Settings.NET.Services;

/// <summary>
/// Hits the GitHub Releases API to find the latest tagged release for the
/// .NET edition. Differs from the Python edition's UpdateChecker in which
/// release assets we look for: the .NET edition ships Jarvis-NET.exe and
/// JarvisSettings-NET.exe rather than the Python edition's
/// JarvisSettings.exe + source zip.
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
        string? JarvisExeUrl);

    public static async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisSettings-NET/1.0");
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
        string? jarvisExe = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var aName = asset.GetProperty("name").GetString() ?? "";
                var aUrl  = asset.GetProperty("browser_download_url").GetString();
                if (aName.Equals("JarvisSettings-NET.exe", StringComparison.OrdinalIgnoreCase))
                    settingsExe = aUrl;
                else if (aName.Equals("Jarvis-NET.exe", StringComparison.OrdinalIgnoreCase))
                    jarvisExe = aUrl;
            }
        }

        return new ReleaseInfo(tag, name, body, url, settingsExe, jarvisExe);
    }

    /// <summary>
    /// Parses a tag like "v0.1.5" or "0.1.5" into a System.Version. Returns
    /// null if it can't.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s.Substring(0, dash);
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static bool IsNewer(string latest, string? installed)
    {
        var L = ParseTag(latest);
        if (L == null) return false;
        if (installed == "1.0.0" || installed == "1.0.0.0") return true;
        var I = ParseTag(installed);
        if (I == null) return true;
        return L > I;
    }
}
