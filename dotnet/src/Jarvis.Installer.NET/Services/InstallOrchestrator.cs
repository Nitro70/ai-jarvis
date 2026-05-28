using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Setup.NET.Models;
using Jarvis.Setup.NET.Services;

namespace Jarvis.Installer.NET.Services;

/// <summary>
/// Top-level install orchestrator for the .NET edition. Drives the steps
/// the progress page shows:
///   1. Download Jarvis-NET.exe
///   2. Download JarvisSettings-NET.exe (soft-fail on 404)
///   3. Write config.yaml + memory.md
///   4. Write install-info.json pointer
///   5. Create Start Menu shortcuts
///   6. Optional YT Music install if user enabled music_ytmd
///
/// Deliberately no Python, no pip, no source-zip extraction — the .NET
/// edition ships pre-built single-file exes.
/// </summary>
public static class InstallOrchestrator
{
    private const string JarvisExeAsset   = "Jarvis-NET.exe";
    private const string SettingsExeAsset = "JarvisSettings-NET.exe";

    public static async Task InstallAsync(
        InstallerState state,
        IProgress<string> log,
        IProgress<double> percent,
        IProgress<string> step,
        CancellationToken ct = default)
    {
        var cfg = state.Config;
        if (string.IsNullOrWhiteSpace(cfg.InstallDir))
            throw new ArgumentException("InstallDir is empty.", nameof(state));

        Directory.CreateDirectory(cfg.InstallDir);

        // -----------------------------------------------------------
        // 1. Download Jarvis-NET.exe (fatal if missing)
        // -----------------------------------------------------------
        step.Report($"Downloading {JarvisExeAsset}");
        log.Report($"Looking up latest release on github.com/Nitro70/ai-jarvis...");
        var jarvisLookup = await GithubReleaseDownloader.FindAssetAsync(JarvisExeAsset, ct);
        if (jarvisLookup.Url == null)
        {
            throw new InvalidOperationException(
                $"Could not locate '{JarvisExeAsset}' in the latest GitHub release " +
                $"({jarvisLookup.Reason}). The .NET edition needs this binary to run. " +
                "Try again once a release including it is published.");
        }
        log.Report($"Found release {jarvisLookup.Tag} — downloading {JarvisExeAsset}...");
        var jarvisDest = Path.Combine(cfg.InstallDir, JarvisExeAsset);
        await GithubReleaseDownloader.DownloadAsync(jarvisLookup.Url, jarvisDest, percent, ct);
        log.Report($"Downloaded to {jarvisDest}");
        percent.Report(0);

        // Stamp the version we just installed onto the pointer; release tags
        // look like "v0.3.0", strip the leading 'v'.
        if (!string.IsNullOrWhiteSpace(jarvisLookup.Tag))
            cfg.Version = jarvisLookup.Tag.TrimStart('v', 'V');

        // -----------------------------------------------------------
        // 2. Download JarvisSettings-NET.exe (soft-fail on 404)
        // -----------------------------------------------------------
        step.Report($"Downloading {SettingsExeAsset}");
        try
        {
            var settingsLookup = await GithubReleaseDownloader.FindAssetAsync(SettingsExeAsset, ct);
            if (settingsLookup.Url == null)
            {
                log.Report($"(Settings app not in this release — {settingsLookup.Reason}. " +
                           "Settings app will be downloaded on first update.)");
            }
            else
            {
                log.Report($"Downloading {SettingsExeAsset}...");
                var settingsDest = Path.Combine(cfg.InstallDir, SettingsExeAsset);
                await GithubReleaseDownloader.DownloadAsync(settingsLookup.Url, settingsDest, percent, ct);
                log.Report($"Downloaded to {settingsDest}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log.Report($"(Settings app download failed: {e.Message}. " +
                       "Settings app will be downloaded on first update.)");
        }
        percent.Report(0);

        // -----------------------------------------------------------
        // 3. Write config.yaml and memory.md
        // -----------------------------------------------------------
        step.Report("Writing config");

        // Honor the user's memory toggle. When disabled, clear the persona
        // pointer so the runtime doesn't try to read a missing file.
        if (state.MemoryEnabled && !string.IsNullOrWhiteSpace(state.MemoryBody))
        {
            cfg.Persona.MemoryFile = "memory.md";
            var memPath = Path.Combine(cfg.InstallDir, "memory.md");
            await File.WriteAllTextAsync(memPath, state.MemoryBody, ct);
            log.Report($"Wrote {memPath}");
        }
        else
        {
            cfg.Persona.MemoryFile = null;
            log.Report("Memory disabled — no memory.md written.");
        }

        // Stamp the YT Music exe path BEFORE writing config so it lands in
        // config.yaml. (The runtime needs it to talk to the API server.)
        // If we know we're about to auto-install, set the expected path now;
        // if we're NOT auto-installing but already had a previous path, keep it.
        if (cfg.Tools.MusicYtmd.Enabled && state.MusicYtmdAutoInstall)
            cfg.Tools.MusicYtmd.ExePath = YtmdInstaller.ExpectedInstallPath;

        // Fallback for the installer's own version stamp if the release lookup
        // didn't give us a tag (offline reinstall, etc.) — use the .exe version.
        if (string.IsNullOrWhiteSpace(cfg.Version))
        {
            cfg.Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);
        }

        new ConfigService().Save(cfg);
        log.Report($"Wrote {Path.Combine(cfg.InstallDir, "config.yaml")}");

        // -----------------------------------------------------------
        // 4. Write install-info.json pointer
        // -----------------------------------------------------------
        step.Report("Saving install pointer");
        InstallLocator.Save(new InstallPointer
        {
            InstallDir = cfg.InstallDir,
            Version    = cfg.Version,
            Runtime    = "dotnet",
        });
        log.Report($"Wrote {InstallLocator.PointerPath}");

        // -----------------------------------------------------------
        // 5. Start Menu shortcuts (best-effort)
        // -----------------------------------------------------------
        step.Report("Creating shortcuts");
        try
        {
            Shortcuts.Create(cfg);
            log.Report("Start Menu shortcuts created under 'Jarvis-NET'.");
        }
        catch (Exception e)
        {
            log.Report($"(shortcuts skipped: {e.Message})");
        }

        // -----------------------------------------------------------
        // 6. Optional YT Music auto-install (soft-fail)
        // -----------------------------------------------------------
        if (cfg.Tools.MusicYtmd.Enabled && state.MusicYtmdAutoInstall)
        {
            step.Report("Installing YouTube Music app");
            try
            {
                var ytPath = await YtmdInstaller.EnsureInstalledAsync(log, percent, ct);
                cfg.Tools.MusicYtmd.ExePath = ytPath;
                // Re-save config so the actual (possibly different) path lands on disk.
                new ConfigService().Save(cfg);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.Report($"(YouTube Music install failed: {e.Message}. " +
                           "Music control tool will be disabled. You can install " +
                           "it manually from https://github.com/th-ch/youtube-music " +
                           "later, then point Jarvis Settings at its .exe.)");
                cfg.Tools.MusicYtmd.Enabled = false;
                cfg.Tools.MusicYtmd.ExePath = null;
                new ConfigService().Save(cfg);
            }
        }

        step.Report("Done");
        log.Report("Installation complete.");
        percent.Report(100);
    }
}
