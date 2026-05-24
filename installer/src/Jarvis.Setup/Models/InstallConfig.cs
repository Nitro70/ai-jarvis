using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Setup.Models;

/// <summary>
/// Single source of truth for everything the user configures.
/// Persisted two ways:
///   1. As config.yaml inside the install dir (consumed by jarvis.py).
///   2. As install-info.json in %LocalAppData%\Jarvis (so Settings.exe
///      can find the install + reload the user's choices to pre-fill the
///      tabs).
/// </summary>
public class InstallConfig
{
    public string InstallDir { get; set; } = "";

    /// <summary>"voice" or "text".</summary>
    public string Mode { get; set; } = "voice";

    public LlmConfig Llm { get; set; } = new();
    public VoiceConfig Voice { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public ToolsConfig Tools { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static InstallConfig FromJson(string json)
        => JsonSerializer.Deserialize<InstallConfig>(json, JsonOpts) ?? new InstallConfig();

    public static InstallConfig? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return FromJson(File.ReadAllText(path));
        }
        catch { return null; }
    }
}

public class LlmConfig
{
    /// <summary>"claude_agent", "claude_api", or "openai_compat".</summary>
    public string Backend { get; set; } = "claude_agent";

    public string Model { get; set; } = "claude-sonnet-4-6";

    /// <summary>Required for claude_api and openai_compat.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Required for openai_compat. e.g. "https://api.groq.com/openai/v1".</summary>
    public string? BaseUrl { get; set; }

    public int MaxTokens { get; set; } = 1024;
}

public class VoiceConfig
{
    public string WakeWord { get; set; } = "jarvis";

    public List<string> WakeWordVariants { get; set; } = new()
    {
        "jarvis", "jervis", "jervice", "service", "travis", "harvest",
    };

    public double SilenceEndSeconds { get; set; } = 2.5;
    public int MaxCommandSeconds { get; set; } = 30;
    public int CommandGiveupSeconds { get; set; } = 6;

    public string SttModel { get; set; } = "base.en";
    public string WakeModel { get; set; } = "tiny.en";

    public bool TtsEnabled { get; set; } = true;
    public string TtsVoice { get; set; } = "en-GB-RyanNeural";
}

public class MemoryConfig
{
    /// <summary>Free-text body the user fills in. Goes into memory.md as-is.</summary>
    public string Content { get; set; } = "";

    /// <summary>If false, no memory.md is written and persona.memory_file is null.</summary>
    public bool Enabled { get; set; } = true;
}

public class ToolsConfig
{
    public bool Memory { get; set; } = true;
    public bool SystemInfo { get; set; } = true;
    public bool WebBrowser { get; set; } = true;
    public bool MusicYtmd { get; set; } = false;
    public bool WindowsApps { get; set; } = true;
    public bool WindowsState { get; set; } = true;

    public bool DangerousShell { get; set; } = false;
    public bool DangerousShellAck { get; set; } = false;
}
