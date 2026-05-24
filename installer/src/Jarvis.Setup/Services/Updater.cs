using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// Applies an update in place. Used by the Settings app's Updates tab.
///
/// The tricky part is the running JarvisSettings.exe. Windows lets you
/// RENAME a running .exe but not OVERWRITE or DELETE it, so we:
///   1. Rename "JarvisSettings.exe" -> "JarvisSettings.exe.old"
///   2. Download the new one as "JarvisSettings.exe"
///   3. Tell the user to restart Settings.
/// On the next Settings startup we delete the leftover .old file.
/// </summary>
public static class Updater
{
    public static string OldSettingsExePath(string installDir) =>
        Path.Combine(installDir, "JarvisSettings.exe.old");

    public static string SettingsExePath(string installDir) =>
        Path.Combine(installDir, "JarvisSettings.exe");

    /// <summary>Delete any leftover .exe.old from a previous update.
    /// Safe to call repeatedly; silent on failure.</summary>
    public static void CleanupOldSettingsExe(string installDir)
    {
        try
        {
            var old = OldSettingsExePath(installDir);
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* the file is probably still locked; try next launch */ }
    }

    public record Result(bool RestartRequired, string Message);

    /// <summary>
    /// Apply <paramref name="release"/> to <paramref name="cfg"/>.InstallDir.
    /// Downloads the source zip, extracts over the install dir, swaps the
    /// running Settings.exe, and updates install-info.json's Version field.
    /// </summary>
    public static async Task<Result> ApplyAsync(
        InstallConfig cfg,
        UpdateChecker.ReleaseInfo release,
        IProgress<string> log,
        IProgress<double> percent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.InstallDir) || !Directory.Exists(cfg.InstallDir))
            throw new DirectoryNotFoundException(
                $"Install dir not found: {cfg.InstallDir}");

        bool settingsReplaced = false;

        // ----- 1. Source code update -----
        if (!string.IsNullOrWhiteSpace(release.SourceZipUrl))
        {
            log.Report($"Downloading Jarvis source ({release.Tag})...");
            var zipPath = Path.Combine(Path.GetTempPath(),
                $"jarvis-update-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadFileAsync(release.SourceZipUrl!, zipPath, percent, ct);
                log.Report("Extracting...");
                ExtractAndFlatten(zipPath, cfg.InstallDir, log);
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        // ----- 2. JarvisSettings.exe self-replace -----
        if (!string.IsNullOrWhiteSpace(release.SettingsExeUrl))
        {
            log.Report("Downloading new Settings app...");
            var newExe = SettingsExePath(cfg.InstallDir) + ".new";
            await DownloadFileAsync(release.SettingsExeUrl!, newExe, percent, ct);

            // Move the currently-running exe out of the way (Windows allows
            // renaming a running exe, just not overwriting or deleting it).
            var curExe = SettingsExePath(cfg.InstallDir);
            var oldExe = OldSettingsExePath(cfg.InstallDir);
            try
            {
                if (File.Exists(oldExe)) File.Delete(oldExe);
            }
            catch { /* leftover from a prior update; whatever */ }

            try
            {
                if (File.Exists(curExe))
                    File.Move(curExe, oldExe);
                File.Move(newExe, curExe);
                settingsReplaced = true;
                log.Report("Settings app updated. Restart required.");
            }
            catch (Exception e)
            {
                log.Report($"(Couldn't swap Settings.exe: {e.Message}. " +
                           "You may need to restart Settings and try again.)");
                try { File.Delete(newExe); } catch { }
            }
        }

        // ----- 3. Update install-info.json's Version -----
        var newVer = UpdateChecker.ParseTag(release.Tag)?.ToString(3) ?? release.Tag;
        cfg.Version = newVer;
        InstallLocator.Save(cfg);
        log.Report($"Updated install-info.json -> Version {newVer}");

        return new Result(
            RestartRequired: settingsReplaced,
            Message: settingsReplaced
                ? $"Updated to {release.Tag}. Restart Jarvis Settings to load the new version."
                : $"Updated to {release.Tag}. Jarvis source refreshed.");
    }

    // ============================================================
    //  shared helpers (basically same as JarvisDownloader, kept
    //  separate so updater is self-contained and doesn't need the
    //  installer-flow-specific code)
    // ============================================================

    private static void ExtractAndFlatten(string zipPath, string destDir, IProgress<string> log)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        // GitHub archive zips wrap everything in "<repo>-<tag>/"; strip it.
        var rootPrefix = archive.Entries
            .Select(e => e.FullName.Split('/').FirstOrDefault())
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .First().Key + "/";

        int count = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.StartsWith(rootPrefix)) continue;

            var rel = entry.FullName.Substring(rootPrefix.Length);
            var target = Path.GetFullPath(Path.Combine(destDir, rel));

            if (!target.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar,
                                   StringComparison.OrdinalIgnoreCase))
                continue;  // zip-slip guard

            // Skip the .exe.old leftover from a previous update.
            if (Path.GetFileName(target).Equals("JarvisSettings.exe.old",
                                                 StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                count++;
            }
            catch (IOException e)
            {
                // Most likely a file currently locked (e.g. the running
                // JarvisSettings.exe — we handle that one via rename above).
                log.Report($"  (skipped {rel}: {e.Message})");
            }
        }
        log.Report($"  extracted {count} files");
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<double>? percent, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisSettings/1.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[131072];
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
