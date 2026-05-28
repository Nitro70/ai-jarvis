using System;
using System.IO;
using Jarvis.Setup.NET.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Jarvis.Setup.NET.Services;

/// <summary>
/// Reads + writes <see cref="InstallConfig"/> to a config.yaml file. Uses
/// YamlDotNet for full parsing — the Python edition installer's hand-rolled
/// reader was scoped to credential overlay, the runtime needs full schema
/// support including nested blocks (voice.stt, voice.tts, tools.*).
///
/// Both Load and Save are tolerant of missing fields — anything absent in
/// the file keeps its default from <see cref="InstallConfig"/>.
/// </summary>
public class ConfigService
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Load config.yaml from <paramref name="installDir"/>. If the file
    /// doesn't exist, returns a default config (caller decides whether to
    /// prompt the user to set it up). Throws on malformed YAML — that's a
    /// bug the user needs to see, not silently swallow.
    /// </summary>
    public InstallConfig Load(string installDir)
    {
        var path = Path.Combine(installDir, "config.yaml");
        if (!File.Exists(path))
        {
            return new InstallConfig { InstallDir = installDir };
        }

        var yaml = File.ReadAllText(path);
        var cfg = Deserializer.Deserialize<InstallConfig>(yaml) ?? new InstallConfig();
        cfg.InstallDir = installDir;
        return cfg;
    }

    /// <summary>
    /// Write <paramref name="cfg"/> back to config.yaml. Preserves the
    /// install dir; everything else is serialized.
    /// </summary>
    public void Save(InstallConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.InstallDir))
            throw new ArgumentException("InstallDir is empty.", nameof(cfg));
        var path = Path.Combine(cfg.InstallDir, "config.yaml");
        var yaml = Serializer.Serialize(cfg);
        File.WriteAllText(path, yaml);
    }
}
