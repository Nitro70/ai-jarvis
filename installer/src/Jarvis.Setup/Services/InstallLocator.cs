using System;
using System.IO;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// Single well-known JSON pointer at %LocalAppData%\Jarvis\install-info.json.
/// The installer writes it on successful install. Settings.exe reads it on
/// launch to find the install dir and pre-fill the form.
/// </summary>
public static class InstallLocator
{
    public static string PointerDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");

    public static string PointerPath =>
        Path.Combine(PointerDir, "install-info.json");

    public static InstallConfig? LoadExisting()
    {
        return InstallConfig.TryLoad(PointerPath);
    }

    public static void Save(InstallConfig cfg)
    {
        Directory.CreateDirectory(PointerDir);
        File.WriteAllText(PointerPath, cfg.ToJson());
    }

    /// <summary>Default install dir if the user doesn't change it.</summary>
    public static string DefaultInstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Jarvis");
}
