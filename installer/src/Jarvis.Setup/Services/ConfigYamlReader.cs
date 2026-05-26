using System;
using System.IO;
using System.Text.RegularExpressions;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// Reads back a *subset* of config.yaml fields. We don't want a full YAML
/// parser dependency, and we don't need round-tripping — just enough to
/// stop the Settings UI from showing stale values when the user edited
/// config.yaml by hand.
///
/// Why this exists: Settings.exe loads its state from install-info.json,
/// NOT config.yaml. If you put your API key into config.yaml directly
/// (or installed via a path that wrote one place but not the other),
/// the Settings UI showed an empty api_key. Clicking Save then wrote
/// config.yaml back with that empty key and silently nuked your real one.
///
/// This reader fixes the round-trip: on launch, overlay sensitive fields
/// from config.yaml on top of install-info.json so the UI shows what jarvis
/// actually uses at runtime.
/// </summary>
public static class ConfigYamlReader
{
    /// <summary>Overlay the LLM-section credentials (api_key, base_url)
    /// from <paramref name="installDir"/>/config.yaml onto <paramref name="cfg"/>.
    /// No-op if the file doesn't exist or doesn't parse. Other fields are
    /// untouched; the user can still flip backend / model from the UI.</summary>
    public static void OverlayLlmCreds(InstallConfig cfg, string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return;
        var path = Path.Combine(installDir, "config.yaml");
        if (!File.Exists(path)) return;

        string text;
        try { text = File.ReadAllText(path); }
        catch { return; }

        var fields = ReadLlmBlock(text);
        if (fields.TryGetValue("api_key", out var key) && !string.IsNullOrEmpty(key))
            cfg.Llm.ApiKey = key;
        if (fields.TryGetValue("base_url", out var url) && !string.IsNullOrEmpty(url))
            cfg.Llm.BaseUrl = url;
        // backend + model are also worth syncing - if the user manually
        // pointed config.yaml at a different provider, the UI should reflect
        // that rather than stamping back to whatever install-info.json said.
        if (fields.TryGetValue("backend", out var backend) && !string.IsNullOrEmpty(backend))
            cfg.Llm.Backend = backend;
        if (fields.TryGetValue("model", out var model) && !string.IsNullOrEmpty(model))
            cfg.Llm.Model = model;
    }

    /// <summary>
    /// Hand-rolled scanner that pulls "key: value" lines from inside the
    /// `llm:` mapping. Stops when it hits the next top-level key or EOF.
    /// Quoted values get unquoted; literal "null" is treated as missing.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, string> ReadLlmBlock(string yaml)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        bool inLlm = false;

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // Top-level key starts at column 0, has no leading whitespace.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith("#"))
            {
                inLlm = line.TrimEnd().Equals("llm:", StringComparison.Ordinal);
                continue;
            }
            if (!inLlm) continue;
            if (line.TrimStart().StartsWith("#")) continue;  // comment

            var m = Regex.Match(line, @"^\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(.*?)\s*$");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Value;

            // Strip inline comments (yaml `# comment` after a value, but
            // ONLY when not inside quotes).
            if (!(val.StartsWith("\"") || val.StartsWith("'")))
            {
                var hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash).TrimEnd();
            }

            // Unquote
            if (val.Length >= 2 &&
                ((val.StartsWith("\"") && val.EndsWith("\"")) ||
                 (val.StartsWith("'")  && val.EndsWith("'"))))
            {
                val = val.Substring(1, val.Length - 2)
                         .Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            if (val == "null" || val == "~") continue;
            result[key] = val;
        }
        return result;
    }
}
