using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Tools;

// Port of tools/memory.py + core/memory.py. The memory file is plain markdown
// owned by the user. Jarvis only writes under a "# Learned" section it
// creates and manages; user-written sections above are never touched.
//
// We deliberately split into two ITool classes (RememberTool, ForgetTool) so
// the ToolRegistry can register them separately, matching the Python tool
// shape (one Tool per function). The file-format logic lives in the private
// helper MemoryFile so both share one implementation.

/// <summary>
/// Append a single fact to the user's long-term memory file under the
/// "# Learned" section. Idempotent: re-remembering a known fact is a no-op.
/// Mirrors the Python <c>remember</c> tool in <c>tools/memory.py</c>.
/// </summary>
public sealed class RememberTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["fact"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] =
                    "A single concise fact, in plain English. Write it from " +
                    "the user's perspective ('I prefer X') or in the third " +
                    "person ('The user prefers X').",
            },
        },
        ["required"] = new JsonArray { "fact" },
    };

    private readonly string _path;
    private readonly ILogger<RememberTool> _log;

    public RememberTool(string memoryFilePath, ILogger<RememberTool> log)
    {
        _path = memoryFilePath;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "remember",
        Description:
            "Save a fact about the user (or about a preference, routine, or " +
            "recurring detail) to long-term memory. Call this when the user " +
            "shares something worth remembering across sessions — their name, " +
            "preferences, recurring tasks, important dates, etc. — even if " +
            "they don't explicitly say 'remember'. Idempotent: re-remembering " +
            "a known fact is a no-op.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var fact = (arguments["fact"]?.GetValue<string>() ?? string.Empty).Trim();
            if (fact.Length == 0)
                return Task.FromResult("No fact provided.");

            var appended = MemoryFile.AppendFact(_path, fact, _log);
            return Task.FromResult(appended
                ? $"Saved: {fact}"
                : "I already know that — skipped.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "remember failed");
            return Task.FromResult($"Tool error: {ex.Message}");
        }
    }
}

/// <summary>
/// Remove bullets from the "# Learned" section whose text contains a given
/// substring (case-insensitive). Never touches the user's hand-written
/// sections. Mirrors the Python <c>forget</c> tool.
/// </summary>
public sealed class ForgetTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["matching"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Substring to match against memory entries.",
            },
        },
        ["required"] = new JsonArray { "matching" },
    };

    private readonly string _path;
    private readonly ILogger<ForgetTool> _log;

    public ForgetTool(string memoryFilePath, ILogger<ForgetTool> log)
    {
        _path = memoryFilePath;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "forget",
        Description:
            "Remove facts from long-term memory whose text matches a " +
            "substring (case-insensitive). Only removes from the Learned " +
            "section — never touches the user's hand-written sections.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var sub = (arguments["matching"]?.GetValue<string>() ?? string.Empty).Trim();
            if (sub.Length == 0)
                return Task.FromResult("Need a substring to match against.");

            var removed = MemoryFile.RemoveFactsMatching(_path, sub, _log);
            if (removed == 0)
                return Task.FromResult($"Nothing in the Learned section matched '{sub}'.");
            return Task.FromResult($"Removed {removed} fact{(removed == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "forget failed");
            return Task.FromResult($"Tool error: {ex.Message}");
        }
    }
}

/// <summary>
/// Shared file-format logic for the memory.md file. Kept private to this file
/// since RememberTool and ForgetTool are the only consumers. Port of
/// <c>core/memory.py</c>.
/// </summary>
internal static class MemoryFile
{
    private const string LearnedHeader = "# Learned";

    // Matches HTML comments (multi-line). Used by the dedupe check so
    // commented-out example facts in memory.example.md don't count as matches.
    private static readonly Regex HtmlComment =
        new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Append a fact under the "# Learned" header, creating the file or the
    /// section if missing. Returns true if appended, false if the fact was
    /// already present (case-insensitive substring match against visible
    /// content). Adds a trailing period if the fact has none.
    /// </summary>
    public static bool AppendFact(string path, string fact, ILogger log)
    {
        fact = fact.Trim();
        if (fact.Length == 0) return false;
        if (!fact.EndsWith(".", StringComparison.Ordinal)) fact += ".";

        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        // Dedupe against visible content (strip HTML comments first so
        // placeholder example facts don't trigger false positives).
        var visible = HtmlComment.Replace(existing, string.Empty).ToLowerInvariant();
        var probe = fact.TrimEnd('.').ToLowerInvariant();
        if (visible.Contains(probe))
        {
            log.LogInformation("fact already known, not appending: {Fact}", fact);
            return false;
        }

        string updated;
        if (existing.Contains(LearnedHeader, StringComparison.Ordinal))
        {
            if (!existing.EndsWith("\n", StringComparison.Ordinal))
                existing += "\n";
            updated = existing + $"- {fact}\n";
        }
        else
        {
            // Trim trailing whitespace from existing, then start a fresh
            // Learned section with a blank line above and below the header.
            updated = existing.TrimEnd() + $"\n\n{LearnedHeader}\n\n- {fact}\n";
        }

        // Make sure the directory exists before writing.
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, updated);
        log.LogInformation("appended fact to {Path}: {Fact}", path, fact);
        return true;
    }

    /// <summary>
    /// Remove every "- ..." bullet under the "# Learned" section whose text
    /// contains <paramref name="substring"/> (case-insensitive). Returns the
    /// number removed. Headers at any level (# or ##) end the Learned section
    /// unless the new header is also "Learned".
    /// </summary>
    public static int RemoveFactsMatching(string path, string substring, ILogger log)
    {
        if (!File.Exists(path)) return 0;
        var sub = substring.Trim().ToLowerInvariant();
        if (sub.Length == 0) return 0;

        var lines = File.ReadAllText(path).Split('\n');
        var kept = new List<string>(lines.Length);
        var removed = 0;
        var inLearned = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            // Any markdown header line either enters or exits the Learned
            // section. Match Python: check "# " or "## " prefix on the
            // stripped line, then compare the header text.
            if (trimmed.StartsWith("# ", StringComparison.Ordinal) ||
                trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                var headerText = trimmed.TrimStart('#').Trim();
                inLearned = string.Equals(headerText, "Learned",
                    StringComparison.OrdinalIgnoreCase);
            }

            if (inLearned &&
                line.TrimStart().StartsWith("- ", StringComparison.Ordinal) &&
                line.ToLowerInvariant().Contains(sub))
            {
                removed++;
                continue;
            }

            kept.Add(line);
        }

        if (removed > 0)
        {
            // Python writes "\n".join(...) + "\n". The split('\n') above
            // already preserved the trailing-newline empty string if present,
            // so re-joining with "\n" and adding a final "\n" matches Python.
            File.WriteAllText(path, string.Join("\n", kept) + "\n");
            log.LogInformation("removed {Count} facts matching {Sub} from {Path}",
                removed, substring, path);
        }
        return removed;
    }
}
