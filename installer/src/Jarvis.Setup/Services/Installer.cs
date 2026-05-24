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

        // 4. Write config + memory + install-info pointer
        step.Report("Writing config");
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
