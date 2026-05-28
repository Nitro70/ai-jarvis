using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Setup.NET.Models;
using Jarvis.Setup.NET.Services;

namespace Jarvis.Settings.NET.Services;

/// <summary>
/// Applies a .NET-edition update in place. Used by the Settings app's
/// Updates tab.
///
/// The .NET edition installs two self-contained binaries — Jarvis-NET.exe
/// and JarvisSettings-NET.exe. Windows lets you RENAME a running .exe but
/// not OVERWRITE or DELETE it, so for the Settings binary (which IS the
/// running process) we use the same rename-while-running trick the Python
/// edition uses:
///   1. Rename "JarvisSettings-NET.exe" -> "JarvisSettings-NET.exe.old"
///   2. Download the new one as "JarvisSettings-NET.exe"
///   3. Tell the user to restart Settings.
///   4. On next Settings startup we delete the leftover .old file.
///
/// The main Jarvis-NET.exe is replaced in place. If it happens to be
/// running too (user has the assistant up), the overwrite will fail; we
/// surface a status line rather than abort the whole update.
/// </summary>
public static class Updater
{
    private const string SettingsExeName = "JarvisSettings-NET.exe";
    private const string JarvisExeName   = "Jarvis-NET.exe";

    public static string OldSettingsExePath(string installDir) =>
        Path.Combine(installDir, SettingsExeName + ".old");

    public static string SettingsExePath(string installDir) =>
        Path.Combine(installDir, SettingsExeName);

    public static string JarvisExePath(string installDir) =>
        Path.Combine(installDir, JarvisExeName);

    /// <summary>
    /// Delete any leftover JarvisSettings-NET.exe.old from a previous update.
    /// Safe to call repeatedly; silent on failure.
    /// </summary>
    public static void CleanupOldSettingsExe(string installDir)
    {
        try
        {
            var old = OldSettingsExePath(installDir);
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* still locked; try next launch */ }
    }

    public record Result(bool RestartRequired, string Message);

    public static async Task<Result> ApplyAsync(
        InstallConfig cfg,
        InstallPointer pointer,
        UpdateChecker.ReleaseInfo release,
        IProgress<string> log,
        IProgress<double> percent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.InstallDir) || !Directory.Exists(cfg.InstallDir))
            throw new DirectoryNotFoundException(
                $"Install dir not found: {cfg.InstallDir}");

        bool settingsReplaced = false;

        // ----- 1. Jarvis-NET.exe (main app) -----
        if (!string.IsNullOrWhiteSpace(release.JarvisExeUrl))
        {
            log.Report($"Downloading Jarvis-NET.exe ({release.Tag})...");
            var tmp = JarvisExePath(cfg.InstallDir) + ".new";
            await DownloadFileAsync(release.JarvisExeUrl!, tmp, percent, ct);
            var dest = JarvisExePath(cfg.InstallDir);
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                log.Report("Jarvis-NET.exe updated.");
            }
            catch (Exception e)
            {
                log.Report($"(Couldn't replace Jarvis-NET.exe: {e.Message}. " +
                           "Close the assistant and retry.)");
                try { File.Delete(tmp); } catch { }
            }
        }

        // ----- 2. JarvisSettings-NET.exe self-replace -----
        if (!string.IsNullOrWhiteSpace(release.SettingsExeUrl))
        {
            log.Report("Downloading new Settings app...");
            var newExe = SettingsExePath(cfg.InstallDir) + ".new";
            await DownloadFileAsync(release.SettingsExeUrl!, newExe, percent, ct);

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
        pointer.Version = newVer;
        try { InstallLocator.Save(pointer); } catch { /* best-effort */ }
        log.Report($"Updated install-info.json -> Version {newVer}");

        return new Result(
            RestartRequired: settingsReplaced,
            Message: settingsReplaced
                ? $"Updated to {release.Tag}. Restart Jarvis Settings to load the new version."
                : $"Updated to {release.Tag}.");
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<double>? percent, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisSettings-NET/1.0");
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
