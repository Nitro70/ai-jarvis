using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Setup.Services;

/// <summary>
/// Silently downloads and installs th-ch/youtube-music (the YouTube Music
/// desktop app whose HTTP API Jarvis's music_ytmd tool talks to).
///
/// The app's installer is an NSIS exe — `/S` is the silent flag. Default
/// per-user install location is:
///   %LocalAppData%\Programs\YouTube Music\YouTube Music.exe
/// which is what we set as `tools.music_ytmd.exe_path` in config.yaml.
///
/// We do NOT and CANNOT auto-enable the "API Server" plugin inside the
/// app — that's a manual click in the app's Settings UI. The Done page
/// of the installer reminds the user.
/// </summary>
public static class YtmdInstaller
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/th-ch/youtube-music/releases/latest";

    public static string ExpectedInstallPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "YouTube Music", "YouTube Music.exe");

    /// <summary>If YT Music is already installed at the expected path, return that.
    /// Otherwise download + run the latest installer silently and return the
    /// new path. Throws on failure.</summary>
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
            // ----- Download with size + speed feedback -----
            log?.Report("Downloading YouTube Music app (typically 100-200 MB)...");
            var dlStart = DateTime.UtcNow;
            long lastBytes = 0;
            double lastPct = 0;
            var lastLog = DateTime.UtcNow;
            var wrappedPct = new Progress<(double pct, long bytes, long total)>(t =>
            {
                percent?.Report(t.pct);
                // Throttle log lines: only every 2 seconds or every 10%.
                var now = DateTime.UtcNow;
                if ((now - lastLog).TotalSeconds < 2 && t.pct - lastPct < 10) return;
                var elapsed = (now - dlStart).TotalSeconds;
                var speed = elapsed > 0 ? (t.bytes - 0) / elapsed / (1024 * 1024) : 0;
                var remaining = speed > 0 ? (t.total - t.bytes) / (1024 * 1024) / speed : 0;
                log?.Report(
                    $"  downloading {t.bytes / 1_000_000} / {t.total / 1_000_000} MB " +
                    $"({t.pct:F0}%, {speed:F1} MB/s, ~{remaining:F0}s left)");
                lastLog = now;
                lastPct = t.pct;
                lastBytes = t.bytes;
            });
            await DownloadFileAsync(assetUrl, installerPath, wrappedPct, ct);
            log?.Report($"  downloaded in {(DateTime.UtcNow - dlStart).TotalSeconds:F0}s");

            // ----- Silent install with heartbeat -----
            log?.Report("Installing YouTube Music (silent, ~20-40 seconds)...");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                // /S = NSIS silent install. Per-user, no admin prompt.
                Arguments = "/S",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException(
                              "Failed to start YouTube Music installer.");

            // Heartbeat — print a dot-line every 5 seconds so the user can
            // tell the install isn't frozen.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                var t0 = DateTime.UtcNow;
                while (!heartbeatCts.IsCancellationRequested)
                {
                    try { await Task.Delay(5000, heartbeatCts.Token); }
                    catch { break; }
                    log?.Report($"  ...still installing ({(DateTime.UtcNow - t0).TotalSeconds:F0}s)");
                }
            }, heartbeatCts.Token);

            await p.WaitForExitAsync(ct);
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { }

            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"YouTube Music installer exited with code {p.ExitCode}.");
        }
        finally
        {
            try { File.Delete(installerPath); } catch { }
        }

        // NSIS silent installs are async — the installer process exits before
        // file copies finish in some versions. Wait briefly for the exe to
        // actually appear.
        for (int i = 0; i < 20 && !File.Exists(ExpectedInstallPath); i++)
            await Task.Delay(500, ct);

        if (!File.Exists(ExpectedInstallPath))
            throw new InvalidOperationException(
                $"YouTube Music installer reported success but the exe was " +
                $"not found at {ExpectedInstallPath}. " +
                "It may have installed to a non-default location.");

        // electron-builder's NSIS auto-launches the app post-install (it's
        // baked into the installer template — no flag to suppress). Kill it
        // so it doesn't sit running uninvited; Jarvis (or the user) will
        // start it when actually needed.
        KillYouTubeMusicProcesses(log);

        log?.Report($"YouTube Music installed at {ExpectedInstallPath}");
        return ExpectedInstallPath;
    }

    private static async Task<string> FindWindowsInstallerAssetAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisInstaller/1.0");
        using var resp = await http.GetAsync(LatestReleaseApi, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var assets = doc.RootElement.GetProperty("assets");

        // th-ch/youtube-music ships several Windows assets per release. Recent
        // releases (3.11.0) look like:
        //   YouTube-Music-3.11.0.exe              <- full bundled NSIS, what we want
        //   YouTube-Music-Web-Setup-3.11.0.exe    <- needs system WebView2, smaller
        //   YouTube-Music-3.11.0-portable.exe     <- skip, not what we install
        //   YouTube-Music-3.11.0-arm64.exe        <- skip, wrong arch
        //   YouTube-Music-3.11.0-ia32.exe         <- skip, 32-bit
        // Score everything, prefer the full bundled NSIS on x64.
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
            if (name.Contains("web")) score -= 50;  // Web variant needs WebView2; prefer full
            if (name.Contains("x64")) score += 10;  // explicit x64 if labelled

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

    private static async Task DownloadFileAsync(
        string url, string destPath,
        IProgress<(double pct, long bytes, long total)>? progress,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JarvisInstaller/1.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[262144];  // 256 KB chunks for fewer report callbacks
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((100.0 * read / total, read, total));
        }
    }

    private static void KillYouTubeMusicProcesses(IProgress<string>? log)
    {
        try
        {
            // The Electron app's exe name is "YouTube Music" (with space).
            // GetProcessesByName takes the name without .exe.
            var procs = Process.GetProcessesByName("YouTube Music");
            if (procs.Length == 0) return;
            log?.Report($"  closing {procs.Length} auto-launched YouTube Music process(es)...");
            foreach (var proc in procs)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                    }
                }
                catch (Exception e)
                {
                    log?.Report($"  (couldn't close PID {proc.Id}: {e.Message})");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception e)
        {
            log?.Report($"  (post-install cleanup skipped: {e.Message})");
        }
    }
}
