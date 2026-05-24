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
            log?.Report("Downloading YouTube Music app installer...");
            await DownloadFileAsync(assetUrl, installerPath, percent, ct);

            log?.Report("Installing YouTube Music (silent, per-user)...");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                // /S = NSIS silent install. Per-user, no admin prompt because
                // the YT Music NSIS script doesn't request elevation.
                Arguments = "/S",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException(
                              "Failed to start YouTube Music installer.");
            await p.WaitForExitAsync(ct);
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
        string url, string destPath, IProgress<double>? percent, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
