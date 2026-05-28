using System;
using System.Diagnostics;
using System.IO;
using Jarvis.Setup.NET.Models;

namespace Jarvis.Installer.NET.Services;

/// <summary>
/// Creates Start Menu shortcuts for the .NET edition. Uses PowerShell's
/// COM access to WScript.Shell to avoid taking a NuGet dep on
/// IWshRuntimeLibrary. Distinct shortcut names from the Python edition
/// ("Jarvis (.NET)", "Jarvis Settings (.NET)") so both editions can
/// coexist in the Start Menu under "Jarvis-NET\".
/// </summary>
public static class Shortcuts
{
    public static void Create(InstallConfig cfg)
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Jarvis-NET");
        Directory.CreateDirectory(startMenu);

        var jarvisExe   = Path.Combine(cfg.InstallDir, "Jarvis-NET.exe");
        var settingsExe = Path.Combine(cfg.InstallDir, "JarvisSettings-NET.exe");

        if (File.Exists(jarvisExe))
        {
            MakeShortcut(Path.Combine(startMenu, "Jarvis (.NET).lnk"),
                         jarvisExe, cfg.InstallDir,
                         "Start Jarvis (.NET edition).");
        }
        if (File.Exists(settingsExe))
        {
            MakeShortcut(Path.Combine(startMenu, "Jarvis Settings (.NET).lnk"),
                         settingsExe, cfg.InstallDir,
                         "Edit Jarvis (.NET) configuration.");
        }
    }

    private static void MakeShortcut(string lnkPath, string target, string workingDir, string description)
    {
        var ps = $@"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Esc(lnkPath)}');" +
                 $@"$s.TargetPath='{Esc(target)}';" +
                 $@"$s.WorkingDirectory='{Esc(workingDir)}';" +
                 $@"$s.Description='{Esc(description)}';" +
                 $@"$s.Save()";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
