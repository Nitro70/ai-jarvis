using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// Reads a subset of config.yaml fields back into an <see cref="InstallConfig"/>.
/// We avoid a full YAML parser dependency; the format we generate is small and
/// stable, so a hand-rolled scanner is fine.
///
/// Why this exists: Settings.exe loads its state from install-info.json,
/// NOT config.yaml. If you edit config.yaml directly (or installed via a
/// flow that wrote one place but not the other), the Settings UI showed
/// stale values. Clicking Save then wrote those stale values back over
/// your real config — silently destroying hand-edits.
///
/// On Settings launch we now <see cref="OverlayAll"/> every UI-writable field
/// from config.yaml on top of the InstallConfig populated from install-info.json,
/// so the UI mirrors what jarvis.py actually loads at runtime.
/// </summary>
public static class ConfigYamlReader
{
    /// <summary>
    /// Overlay every UI-writable section (mode, llm, voice, tools, persona)
    /// from <paramref name="installDir"/>/config.yaml on top of <paramref name="cfg"/>.
    /// No-op if the file is missing or unparseable.
    /// </summary>
    public static void OverlayAll(InstallConfig cfg, string installDir)
    {
        var dict = LoadFlat(installDir);
        if (dict.Count == 0) return;

        // ---- top-level ----
        if (dict.TryGetValue("mode", out var mode) && !string.IsNullOrEmpty(mode))
            cfg.Mode = mode;

        // ---- llm.* ----
        if (dict.TryGetValue("llm.backend", out var b) && !string.IsNullOrEmpty(b))
            cfg.Llm.Backend = b;
        if (dict.TryGetValue("llm.model", out var m) && !string.IsNullOrEmpty(m))
            cfg.Llm.Model = m;
        if (dict.TryGetValue("llm.api_key", out var k) && !string.IsNullOrEmpty(k))
            cfg.Llm.ApiKey = k;
        if (dict.TryGetValue("llm.base_url", out var u) && !string.IsNullOrEmpty(u))
            cfg.Llm.BaseUrl = u;
        if (dict.TryGetValue("llm.max_tokens", out var mt) &&
            int.TryParse(mt, out var mtInt))
            cfg.Llm.MaxTokens = mtInt;
        if (dict.TryGetValue("llm.disable_tools", out var dt))
            cfg.Llm.DisableTools = ParseBool(dt);

        // ---- voice.* ----
        if (dict.TryGetValue("voice.wake_word", out var ww) && !string.IsNullOrEmpty(ww))
            cfg.Voice.WakeWord = ww;
        if (dict.TryGetValue("voice.silence_end_seconds", out var ses) &&
            double.TryParse(ses, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var sesD))
            cfg.Voice.SilenceEndSeconds = sesD;
        if (dict.TryGetValue("voice.max_command_seconds", out var mcs) &&
            int.TryParse(mcs, out var mcsI))
            cfg.Voice.MaxCommandSeconds = mcsI;
        if (dict.TryGetValue("voice.command_giveup_seconds", out var cgs) &&
            int.TryParse(cgs, out var cgsI))
            cfg.Voice.CommandGiveupSeconds = cgsI;
        if (dict.TryGetValue("voice.stt.model", out var sm) && !string.IsNullOrEmpty(sm))
            cfg.Voice.SttModel = sm;
        if (dict.TryGetValue("voice.stt.wake_model", out var wm) && !string.IsNullOrEmpty(wm))
            cfg.Voice.WakeModel = wm;
        if (dict.TryGetValue("voice.tts.enabled", out var te))
            cfg.Voice.TtsEnabled = ParseBool(te);
        if (dict.TryGetValue("voice.tts.voice", out var tv) && !string.IsNullOrEmpty(tv))
            cfg.Voice.TtsVoice = tv;

        // ---- persona.memory_file → Memory.Enabled (Memory.Content comes from
        //      memory.md on disk, not from config.yaml) ----
        if (dict.TryGetValue("persona.memory_file", out var mf))
            cfg.Memory.Enabled = !string.IsNullOrEmpty(mf) && mf != "null" && mf != "~";

        // ---- tools.*.enabled ----
        if (dict.TryGetValue("tools.memory.enabled", out var t1))      cfg.Tools.Memory       = ParseBool(t1);
        if (dict.TryGetValue("tools.system_info.enabled", out var t2)) cfg.Tools.SystemInfo   = ParseBool(t2);
        if (dict.TryGetValue("tools.web_browser.enabled", out var t3)) cfg.Tools.WebBrowser   = ParseBool(t3);
        if (dict.TryGetValue("tools.music_ytmd.enabled", out var t4))  cfg.Tools.MusicYtmd    = ParseBool(t4);
        if (dict.TryGetValue("tools.music_ytmd.exe_path", out var mep) && !string.IsNullOrEmpty(mep))
            cfg.Tools.MusicYtmdExePath = mep;
        if (dict.TryGetValue("tools.windows_apps.enabled", out var t5))  cfg.Tools.WindowsApps  = ParseBool(t5);
        if (dict.TryGetValue("tools.windows_state.enabled", out var t6)) cfg.Tools.WindowsState = ParseBool(t6);
        if (dict.TryGetValue("tools.dangerous_shell.enabled", out var t7))
            cfg.Tools.DangerousShell = ParseBool(t7);
        if (dict.TryGetValue("tools.dangerous_shell.i_understand_the_risks", out var t8))
            cfg.Tools.DangerousShellAck = ParseBool(t8);
    }

    /// <summary>Legacy alias for callers updated before <see cref="OverlayAll"/>
    /// existed. Identical behavior.</summary>
    [Obsolete("Use OverlayAll — overlays every section, not just llm.")]
    public static void OverlayLlmCreds(InstallConfig cfg, string installDir)
        => OverlayAll(cfg, installDir);

    // ==================================================================
    //  Generic dotted-key YAML scanner.
    //  Returns a flat Dictionary keyed by joined-with-dots path, e.g.
    //    "llm.api_key"  -> "sk-..."
    //    "voice.stt.model" -> "base.en"
    //    "tools.dangerous_shell.enabled" -> "false"
    //  Skips: list items (- foo), multi-line literal blocks (key: |),
    //  blank lines, comments.
    // ==================================================================

    private static Dictionary<string, string> LoadFlat(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return new(StringComparer.Ordinal);
        var path = Path.Combine(installDir, "config.yaml");
        if (!File.Exists(path))
            return new(StringComparer.Ordinal);
        string text;
        try { text = File.ReadAllText(path); }
        catch { return new(StringComparer.Ordinal); }
        return ScanYaml(text);
    }

    internal static Dictionary<string, string> ScanYaml(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Stack of (indent_level, key) for every open mapping above the current line.
        var stack = new List<(int indent, string key)>();
        // When we hit a `key: |` (literal block scalar), skip body lines until
        // we return to the parent indent level. We don't ingest literal blocks.
        int? skipUntilIndentLE = null;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Track indent on non-blank lines.
            if (line.Length == 0) continue;
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            var trimmed = line.Substring(indent);

            // Inside a literal block? Skip until we dedent back to or above the
            // block's parent indent.
            if (skipUntilIndentLE.HasValue)
            {
                if (indent > skipUntilIndentLE.Value)
                    continue;
                skipUntilIndentLE = null;
                // fall through and re-process this line as a normal entry
            }

            if (trimmed.StartsWith("#"))   continue;        // pure comment
            if (trimmed.StartsWith("- "))  continue;        // list item, ignore
            if (trimmed == "-")            continue;

            // Pop stack to current indent level.
            while (stack.Count > 0 && stack[^1].indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            var match = Regex.Match(trimmed,
                @"^([a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(.*)$");
            if (!match.Success) continue;
            var key    = match.Groups[1].Value;
            var rawVal = match.Groups[2].Value;

            // Block start ("key:" with no value, OR "key: |" literal block).
            if (string.IsNullOrEmpty(rawVal))
            {
                stack.Add((indent, key));
                continue;
            }
            if (rawVal.StartsWith("|") || rawVal.StartsWith(">"))
            {
                // Literal/folded scalar block — its body lives at indent > current.
                // We don't ingest these (system_prompt is the only such field
                // and the UI doesn't edit it). Skip until dedent.
                skipUntilIndentLE = indent;
                continue;
            }

            // Leaf value.
            var val = StripCommentAndUnquote(rawVal);
            var dotted = stack.Count == 0
                ? key
                : string.Join(".", stack.Select(s => s.key)) + "." + key;
            result[dotted] = val;
        }
        return result;
    }

    /// <summary>
    /// YAML spec: '#' starts an inline comment only when preceded by whitespace
    /// (or it's the start of the line). So `api_key: sk-proj-a#b#c` is the
    /// full value `sk-proj-a#b#c`, NOT truncated at the first '#'.
    /// </summary>
    internal static string StripCommentAndUnquote(string rawVal)
    {
        var s = rawVal;

        // Strip inline comment only on UNQUOTED values, applying the
        // whitespace-before-# rule.
        bool isQuoted = (s.StartsWith("\"") || s.StartsWith("'"));
        if (!isQuoted)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1])))
                {
                    s = s.Substring(0, i).TrimEnd();
                    break;
                }
            }
        }

        // Unquote.
        if (s.Length >= 2 &&
            ((s.StartsWith("\"") && s.EndsWith("\"")) ||
             (s.StartsWith("'")  && s.EndsWith("'"))))
        {
            s = s.Substring(1, s.Length - 2)
                 .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return s;
    }

    private static bool ParseBool(string s) =>
        s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("yes",  StringComparison.OrdinalIgnoreCase) ||
        s == "1";
}
