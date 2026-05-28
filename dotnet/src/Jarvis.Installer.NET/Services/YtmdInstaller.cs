using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Installer.NET.Services;

/// <summary>
/// Silently downloads and installs th-ch/youtube-music (the YouTube Music
/// desktop app whose HTTP API Jarvis's music_ytmd tool talks to).
///
/// Mirrors the Python edition installer's logic 1:1 — NSIS silent install
/// via /S flag, default per-user install location, heartbeat + auto-kill
/// of the auto-launched app process.
/// </summary>
public static class YtmdInstaller
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/th-ch/youtube-music/releases/latest";

    public static string ExpectedInstallPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "YouTube Music", "YouTube Music.exe");

    public static async Task<string> EnsureInstalledAsync(
        IProgress<string>? log = null,
        IProgress<double>? percent = null,
        CancellationToken ct = default)
    {
        if (File.Exists(ExpectedInstallPath))
        {
            log?.Report($"YouTube Music already installed at {ExpectedInstallPath}");
            return ExpectedInstallPath;
        }

        log?.Report("Looking up latest YouTube Music release...");
        var assetUrl = await FindWindowsInstallerAssetAsync(ct);

        var installerPath = Path.Combine(
            Path.GetTempPath(), $"jarvis-ytmd-{Guid.NewGuid():N}.exe");

        try
        {
            log?.Report("Downloading YouTube Music app (typically 100-200 MB)...");
            var dlStart = DateTime.UtcNow;
            await GithubReleaseDownloader.DownloadAsync(assetUrl, installerPath, percent, ct);
            log?.Report($"  downloaded in {(DateTime.UtcNow - dlStart).TotalSeconds:F0}s");

            log?.Report("Installing YouTube Music (silent — the app may briefly flash open; we'll close it automatically)...");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/S",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException(
                              "Failed to start YouTube Music installer.");

            using var bgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var heartbeat = Task.Run(async () =>
            {
                var t0 = DateTime.UtcNow;
                while (!bgCts.IsCancellationRequested)
                {
                    try { await Task.Delay(5000, bgCts.Token); }
                    catch { break; }
                    log?.Report($"  ...still installing ({(DateTime.UtcNow - t0).TotalSeconds:F0}s)");
                }
            }, bgCts.Token);

            var appKiller = Task.Run(async () =>
            {
                int killedCount = 0;
                while (!bgCts.IsCancellationRequested)
                {
                    try { await Task.Delay(500, bgCts.Token); }
                    catch { break; }
                    var killed = KillYouTubeMusicProcesses(silent: true);
                    if (killed > 0)
                    {
                        killedCount += killed;
                        log?.Report($"  closed auto-launched YouTube Music ({killedCount} so far)");
                    }
                }
            }, bgCts.Token);

            await p.WaitForExitAsync(ct);

            await Task.Delay(500, ct);
            KillYouTubeMusicProcesses(silent: false, log: log);

            bgCts.Cancel();
            try { await Task.WhenAll(heartbeat, appKiller); } catch { }

            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"YouTube Music installer exited with code {p.ExitCode}.");
        }
        finally
        {
            try { File.Delete(installerPath); } catch { }
        }

        for (int i = 0; i < 20 && !File.Exists(ExpectedInstallPath); i++)
            await Task.Delay(500, ct);

        if (!File.Exists(ExpectedInstallPath))
            throw new InvalidOperationException(
                $"YouTube Music installer reported success but the exe was " +
                $"not found at {ExpectedInstallPath}. " +
                "It may have installed to a non-default location.");

        log?.Report($"YouTube Music installed at {ExpectedInstallPath}");
        return ExpectedInstallPath;
    }

    private static async Task<string> FindWindowsInstallerAssetAsync(CancellationToken ct)
    {
        using var http = GithubReleaseDownloader.NewHttp(TimeSpan.FromSeconds(30));
        using var resp = await http.GetAsync(LatestReleaseApi, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var assets = doc.RootElement.GetProperty("assets");

        // Same scoring logic as the Python edition installer:
        //   YouTube-Music-3.11.0.exe              <- full bundled NSIS, best
        //   YouTube-Music-Web-Setup-3.11.0.exe    <- web variant, needs WebView2
        //   *-portable.exe / *-arm64.exe / *-ia32.exe  <- skip
        string? best = null;
        int bestScore = -1;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = (asset.GetProperty("name").GetString() ?? "").ToLowerInvariant();
            if (!name.EndsWith(".exe")) continue;
            if (name.Contains("arm")) continue;
            if (name.Contains("ia32")) continue;
            if (name.Contains("portable")) continue;
            if (!name.StartsWith("youtube-music-")) continue;

            int score = 100;
            if (name.Contains("web")) score -= 50;
            if (name.Contains("x64")) score += 10;

            if (score > bestScore)
            {
                bestScore = score;
                best = asset.GetProperty("browser_download_url").GetString();
            }
        }

        if (best == null)
            throw new InvalidOperationException(
                "Could not find a Windows Setup.exe in th-ch/youtube-music's " +
                "latest release. The release naming convention may have changed.");
        return best;
    }

    private static int KillYouTubeMusicProcesses(bool silent = false, IProgress<string>? log = null)
    {
        int killed = 0;
        try
        {
            var procs = Process.GetProcessesByName("YouTube Music");
            if (procs.Length == 0) return 0;
            if (!silent)
                log?.Report($"  closing {procs.Length} YouTube Music process(es)...");
            foreach (var proc in procs)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                        killed++;
                    }
                }
                catch (Exception e)
                {
                    if (!silent) log?.Report($"  (couldn't close PID {proc.Id}: {e.Message})");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception e)
        {
            if (!silent) log?.Report($"  (kill sweep skipped: {e.Message})");
        }
        return killed;
    }
}
