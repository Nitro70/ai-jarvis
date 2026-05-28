using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Setup.NET.Services;

/// <summary>
/// Locator for the .NET edition. Pointer at %LocalAppData%\Jarvis-NET\
/// install-info.json — deliberately different from the Python edition's
/// %LocalAppData%\Jarvis\ so both editions can coexist on one machine
/// without stepping on each other.
/// </summary>
public static class InstallLocator
{
    public static string PointerDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jarvis-NET");

    public static string PointerPath =>
        Path.Combine(PointerDir, "install-info.json");

    /// <summary>Default install dir for the .NET edition.</summary>
    public static string DefaultInstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Jarvis-NET");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Try to read the install pointer. Returns null if it doesn't exist
    /// or can't be parsed.
    /// </summary>
    public static InstallPointer? LoadExisting()
    {
        try
        {
            if (!File.Exists(PointerPath)) return null;
            var json = File.ReadAllText(PointerPath);
            return JsonSerializer.Deserialize<InstallPointer>(json, JsonOpts);
        }
        catch { return null; }
    }

    public static void Save(InstallPointer info)
    {
        Directory.CreateDirectory(PointerDir);
        File.WriteAllText(PointerPath, JsonSerializer.Serialize(info, JsonOpts));
    }
}

/// <summary>
/// What we persist about an install. Deliberately small — config.yaml in
/// the install dir holds the real settings; this is just the breadcrumb
/// so Settings.exe / Updates checker can find the install location and
/// know which version is on disk.
/// </summary>
public class InstallPointer
{
    public string InstallDir { get; set; } = "";
    public string? Version { get; set; }
    public string Runtime { get; set; } = "dotnet";
    public DateTime? LastLaunched { get; set; }
}
