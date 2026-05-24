using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Setup.Services;

/// <summary>
/// Downloads the Jarvis source from GitHub and extracts it to the install dir.
/// </summary>
public static class JarvisDownloader
{
    private const string RepoOwner = "Nitro70";
    private const string RepoName  = "ai-jarvis";
    private const string Branch    = "main";

    private static string ZipUrl =>
        $"https://github.com/{RepoOwner}/{RepoName}/archive/refs/heads/{Branch}.zip";

    /// <summary>
    /// Download the repo zip and extract it into <paramref name="installDir"/>,
    /// flattening the GitHub-injected top-level folder (e.g. "ai-jarvis-main/").
    /// Returns the directory the zip extracted into.
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        string installDir,
        IProgress<string>? log = null,
        IProgress<double>? percent = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(installDir);

        var tempZip = Path.Combine(Path.GetTempPath(), $"jarvis-{Guid.NewGuid():N}.zip");
        try
        {
            log?.Report("Downloading Jarvis from GitHub...");
            await DownloadFileAsync(ZipUrl, tempZip, percent, ct);

            log?.Report("Extracting...");
            ExtractAndFlatten(tempZip, installDir);
            log?.Report($"Extracted to {installDir}");
            return installDir;
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private static void ExtractAndFlatten(string zipPath, string destDir)
    {
        // The repo zip from GitHub has a single top-level dir like
        // "ai-jarvis-main/...". We strip that prefix so files land directly
        // in destDir.
        using var archive = ZipFile.OpenRead(zipPath);
        var rootPrefix = archive.Entries
            .Select(e => e.FullName.Split('/').FirstOrDefault())
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .First().Key + "/";

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;          // directory
            if (!entry.FullName.StartsWith(rootPrefix)) continue;

            var rel = entry.FullName.Substring(rootPrefix.Length);
            var target = Path.GetFullPath(Path.Combine(destDir, rel));

            // Guard against zip-slip.
            if (!target.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar,
                                   StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Download JarvisSettings.exe from the latest GitHub release and drop it
    /// next to jarvis.py. Returns true on success. Treats "no release yet" /
    /// "asset missing" as soft failures (logs and returns false), since the
    /// installer can complete without it — the user just won't have the
    /// Settings GUI until the next release.
    /// </summary>
    public static async Task<bool> TryDownloadSettingsAppAsync(
        string installDir,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var dest = Path.Combine(installDir, "JarvisSettings.exe");
        var url  = $"https://github.com/{RepoOwner}/{RepoName}/releases/latest/download/JarvisSettings.exe";
        try
        {
            log?.Report("Downloading Jarvis Settings app...");
            await DownloadFileAsync(url, dest, null, ct);
            log?.Report($"Settings app installed at {dest}");
            return true;
        }
        catch (HttpRequestException e)
        {
            log?.Report($"(Settings app not available yet — {e.Message}. Skipping.)");
            return false;
        }
        catch (Exception e)
        {
            log?.Report($"(Failed to download Settings app: {e.Message}. Skipping.)");
            return false;
        }
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<double>? percent, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisInstaller/1.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) percent?.Report(100.0 * read / total);
        }
    }
}
