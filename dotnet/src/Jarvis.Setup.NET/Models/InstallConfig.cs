using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Jarvis.Setup.NET.Models;

/// <summary>
/// In-memory representation of config.yaml for the .NET edition. Mirrors
/// the schema used by the Python edition exactly so config files written
/// by either installer/settings UI can be read by either runtime — at
/// runtime, only one edition reads any given config.yaml (separate install
/// dirs), but the schema match means we don't have to translate.
///
/// Field names use YAML-style snake_case via [YamlMember(Alias=...)]
/// attributes; C# property names stay PascalCase.
/// </summary>
public class InstallConfig
{
    [YamlIgnore]
    public string InstallDir { get; set; } = "";

    [YamlIgnore]
    public string? Version { get; set; }

    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = "voice";

    [YamlMember(Alias = "llm")]
    public LlmConfig Llm { get; set; } = new();

    [YamlMember(Alias = "voice")]
    public VoiceConfig Voice { get; set; } = new();

    [YamlMember(Alias = "persona")]
    public PersonaConfig Persona { get; set; } = new();

    [YamlMember(Alias = "tools")]
    public ToolsConfig Tools { get; set; } = new();

    [YamlMember(Alias = "logging")]
    public LoggingConfig Logging { get; set; } = new();
}

public class LlmConfig
{
    [YamlMember(Alias = "backend")]
    public string Backend { get; set; } = "openai_compat";

    [YamlMember(Alias = "model")]
    public string Model { get; set; } = "gpt-4o-mini";

    [YamlMember(Alias = "api_key")]
    public string? ApiKey { get; set; }

    [YamlMember(Alias = "base_url")]
    public string? BaseUrl { get; set; }

    [YamlMember(Alias = "max_tokens")]
    public int MaxTokens { get; set; } = 1024;

    [YamlMember(Alias = "disable_tools")]
    public bool DisableTools { get; set; } = false;
}

public class VoiceConfig
{
    [YamlMember(Alias = "wake_word")]
    public string WakeWord { get; set; } = "jarvis";

    [YamlMember(Alias = "wake_word_variants")]
    public List<string> WakeWordVariants { get; set; } = new()
    {
        "jarvis", "jervis", "jervice", "service", "travis", "harvest",
    };

    [YamlMember(Alias = "silence_end_seconds")]
    public double SilenceEndSeconds { get; set; } = 2.5;

    [YamlMember(Alias = "max_command_seconds")]
    public int MaxCommandSeconds { get; set; } = 30;

    [YamlMember(Alias = "command_giveup_seconds")]
    public int CommandGiveupSeconds { get; set; } = 6;

    [YamlMember(Alias = "stt")]
    public SttConfig Stt { get; set; } = new();

    [YamlMember(Alias = "tts")]
    public TtsConfig Tts { get; set; } = new();

    [YamlMember(Alias = "always_on")]
    public bool AlwaysOn { get; set; } = false;

    [YamlMember(Alias = "follow_up_seconds")]
    public double FollowUpSeconds { get; set; } = 30;
}

public class SttConfig
{
    [YamlMember(Alias = "model")]
    public string Model { get; set; } = "base.en";

    [YamlMember(Alias = "wake_model")]
    public string WakeModel { get; set; } = "tiny.en";
}

public class TtsConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "voice")]
    public string Voice { get; set; } = "en-GB-RyanNeural";
}

public class PersonaConfig
{
    [YamlMember(Alias = "memory_file")]
    public string? MemoryFile { get; set; } = "memory.md";

    [YamlMember(Alias = "system_prompt")]
    public string SystemPrompt { get; set; } = "";

    /// <summary>
    /// The system prompt to actually use: the configured one if non-blank,
    /// otherwise the built-in default. The .NET edition's installer doesn't
    /// always write a system_prompt, so without this fallback Jarvis would
    /// run with NO persona AND no tool-usage guidance — which makes the LLM
    /// describe what it would do instead of calling tools, and pick the wrong
    /// tool (e.g. play_youtube_video / browser instead of play_music / the
    /// YouTube Music app).
    /// </summary>
    public string EffectiveSystemPrompt =>
        string.IsNullOrWhiteSpace(SystemPrompt) ? Default : SystemPrompt;

    /// <summary>Built-in default — mirrors the Python edition's prompt.</summary>
    public const string Default = """
You are JARVIS — a personal voice assistant inspired by Tony Stark's AI.

This is a fully voice conversation. The user SPEAKS to you (their speech
is transcribed by Whisper) and HEARS your replies (synthesized by TTS).
Behave accordingly:
- Expect missing punctuation, occasional mistranscriptions, homophones.
  Interpret charitably; only ask for clarification when meaning is truly
  ambiguous.
- Reply in natural spoken English: no markdown, no bullet points, no
  code blocks, no headings, no asterisks for emphasis.
- Read numbers, units, and symbols the way you'd say them.
  "Three p.m." not "3:00 PM". "Fifty percent" not "50%".
- Keep replies short and useful.

Personality:
- Dry British wit, understated, occasionally cheeky.
- Address the user as "sir" (or "ma'am" if corrected).
- Confident, not sycophantic. Push back politely when the user is wrong.
- When you don't know something, say so plainly.

Tool usage:
- When asked to play music, open an app, change a window, etc. — ACTUALLY
  CALL THE TOOL. Don't just say you would.
- To play a SONG, ARTIST, or ALBUM, use the play_music tool (YouTube Music
  app) — NOT play_youtube_video (which opens the browser). Only use
  play_youtube_video for non-music videos like tutorials or clips.
- After a tool call, give a brief one-line spoken confirmation.
- Never read URLs, video IDs, or AppIDs aloud.
""";
}

public class ToolsConfig
{
    [YamlMember(Alias = "memory")]
    public ToolToggle Memory { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "system_info")]
    public ToolToggle SystemInfo { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "web_browser")]
    public ToolToggle WebBrowser { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "windows_apps")]
    public ToolToggle WindowsApps { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "windows_state")]
    public ToolToggle WindowsState { get; set; } = new() { Enabled = true };

    [YamlMember(Alias = "music_ytmd")]
    public MusicYtmdConfig MusicYtmd { get; set; } = new();

    [YamlMember(Alias = "dangerous_shell")]
    public DangerousShellConfig DangerousShell { get; set; } = new();
}

public class ToolToggle
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;
}

public class MusicYtmdConfig : ToolToggle
{
    // Off by default — requires the th-ch/youtube-music app installed
    // separately. Matches the Python edition's config.example.yaml.
    public MusicYtmdConfig() { Enabled = false; }

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 26538;

    [YamlMember(Alias = "exe_path")]
    public string? ExePath { get; set; }
}

public class DangerousShellConfig : ToolToggle
{
    // Off by default — this is the DANGER ZONE (full shell + file access).
    // Double-gated: both Enabled AND IUnderstandTheRisks must be true. Neither
    // should ever default to on. Matches the Python edition.
    public DangerousShellConfig() { Enabled = false; }

    [YamlMember(Alias = "i_understand_the_risks")]
    public bool IUnderstandTheRisks { get; set; } = false;

    [YamlMember(Alias = "shell")]
    public string Shell { get; set; } = "powershell";

    [YamlMember(Alias = "timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 60;

    [YamlMember(Alias = "max_output_chars")]
    public int MaxOutputChars { get; set; } = 4000;
}

public class LoggingConfig
{
    [YamlMember(Alias = "file")]
    public string File { get; set; } = "jarvis.log";

    [YamlMember(Alias = "mirror_to_terminal")]
    public bool MirrorToTerminal { get; set; } = false;
}
