using System;
using System.IO;
using System.Text;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// Creates Start Menu shortcuts. We don't take a COM dep on IWshShell — we
/// write little .cmd launchers and a .url-style shortcut via a PowerShell
/// one-liner (the simplest dep-free path on Windows).
/// </summary>
public static class Shortcuts
{
    public static void Create(InstallConfig cfg, string pythonExe)
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Jarvis");
        Directory.CreateDirectory(startMenu);

        // 1. A run-jarvis.cmd in the install dir that uses the right python.
        var runCmd = Path.Combine(cfg.InstallDir, "run-jarvis.cmd");
        File.WriteAllText(runCmd,
            "@echo off\r\n" +
            $"cd /d \"{cfg.InstallDir}\"\r\n" +
            $"\"{pythonExe}\" jarvis.py %*\r\n",
            new UTF8Encoding(false));

        // 1b. An update.cmd next to it so people can double-click to update
        //     without opening Settings or memorizing the PowerShell one-liner.
        var updateCmd = Path.Combine(cfg.InstallDir, "update.cmd");
        File.WriteAllText(updateCmd,
            "@echo off\r\n" +
            "REM Pulls the latest Jarvis release from GitHub and applies it\r\n" +
            "REM in place. Same logic as the Settings 'Updates' tab.\r\n" +
            "powershell -NoProfile -ExecutionPolicy Bypass -Command " +
            "\"irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/install.ps1 | iex\"\r\n" +
            "pause\r\n",
            new UTF8Encoding(false));

        // 2. Settings launcher (JarvisSettings.exe lives next to jarvis.py
        //    after the installer copies it there — see Installer + GitHub release).
        var settingsExe = Path.Combine(cfg.InstallDir, "JarvisSettings.exe");

        MakeShortcut(Path.Combine(startMenu, "Jarvis.lnk"),
                     runCmd, cfg.InstallDir, "Start Jarvis voice/text assistant.");
        if (File.Exists(settingsExe))
        {
            MakeShortcut(Path.Combine(startMenu, "Jarvis Settings.lnk"),
                         settingsExe, cfg.InstallDir, "Edit Jarvis configuration.");
        }
    }

    private static void MakeShortcut(string lnkPath, string target, string workingDir, string description)
    {
        // Use PowerShell's COM access to WScript.Shell. No NuGet dep needed.
        var ps = $@"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Esc(lnkPath)}');" +
                 $@"$s.TargetPath='{Esc(target)}';" +
                 $@"$s.WorkingDirectory='{Esc(workingDir)}';" +
                 $@"$s.Description='{Esc(description)}';" +
                 $@"$s.Save()";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(10000);
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
