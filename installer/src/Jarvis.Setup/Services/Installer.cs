using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// Top-level install orchestrator. Drives the four steps the progress page
/// shows: Python, source download, pip install, write config.
/// </summary>
public static class Installer
{
    public static async Task InstallAsync(
        InstallConfig cfg,
        IProgress<string> log,
        IProgress<double> percent,
        IProgress<string> step,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.InstallDir))
            throw new ArgumentException("InstallDir is empty.", nameof(cfg));

        // 1. Python
        step.Report("Locating Python");
        var python = PythonInstaller.FindExisting();
        if (python == null)
        {
            step.Report("Installing Python 3.12");
            python = await PythonInstaller.InstallAsync(log, percent, ct);
        }
        else
        {
            log.Report($"Found existing Python: {python}");
        }

        // 2. Download Jarvis source
        step.Report("Downloading Jarvis");
        await JarvisDownloader.DownloadAndExtractAsync(cfg.InstallDir, log, percent, ct);

        // 2b. Download the Settings GUI (best-effort — may 404 before the first release).
        await JarvisDownloader.TryDownloadSettingsAppAsync(cfg.InstallDir, log, ct);

        // 3. pip install
        step.Report("Installing Python packages");
        var reqFile = ChooseRequirementsFile(cfg);
        var reqPath = Path.Combine(cfg.InstallDir, reqFile);
        if (!File.Exists(reqPath))
            throw new FileNotFoundException($"Requirements file not found: {reqPath}");
        await PythonInstaller.RunPipInstallAsync(python, cfg.InstallDir, reqFile, log, ct);

        // 3b. Optional: install th-ch/youtube-music desktop app for the
        //     music_ytmd tool. Soft-fail — if the YT Music install errors we
        //     still finish the Jarvis install, just with the tool disabled.
        if (cfg.Tools.MusicYtmd && cfg.Tools.MusicYtmdAutoInstall)
        {
            step.Report("Installing YouTube Music app");
            try
            {
                cfg.Tools.MusicYtmdExePath =
                    await YtmdInstaller.EnsureInstalledAsync(log, percent, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.Report($"(YouTube Music install failed: {e.Message}. " +
                           "Music control tool will be disabled. You can install " +
                           "it manually from https://github.com/th-ch/youtube-music " +
                           "later, then point Jarvis Settings at its .exe.)");
                cfg.Tools.MusicYtmd = false;
            }
        }

        // 4. Write config + memory + install-info pointer
        step.Report("Writing config");
        // Stamp the installed version from the installer's assembly so the
        // Settings update tab can compare it against the latest GitHub release.
        cfg.Version ??= typeof(Installer).Assembly.GetName().Version?.ToString(3);
        ConfigYamlWriter.Write(cfg);
        InstallLocator.Save(cfg);

        // 5. Create Start Menu shortcuts (best-effort)
        step.Report("Creating shortcuts");
        try { Shortcuts.Create(cfg, python); }
        catch (Exception e) { log.Report($"(shortcuts skipped: {e.Message})"); }

        step.Report("Done");
        log.Report("Installation complete.");
    }

    /// <summary>Pick the smallest requirements file that satisfies the user's choices.</summary>
    public static string ChooseRequirementsFile(InstallConfig cfg)
    {
        // Voice mode needs voice deps. Music needs music. Always need core + LLM.
        // Easiest correct answer: install everything. It's a one-time cost.
        return "requirements-all.txt";
    }
}
