using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

using Jarvis.Setup.NET.Models;

namespace Jarvis.Core.Tools;

// Port of tools/dangerous_shell.py — opt-in shell + file access tools.
//
// The Python module refuses to load unless BOTH config.enabled and
// config.i_understand_the_risks are true. In the .NET port we mirror that
// via the static DangerousShellTools.IsEnabled(config) helper that the App's
// DI registration consults — the tool classes themselves do not gate (a
// registered tool is assumed valid), matching the pattern of the other
// ITool classes in this project. We also keep a defensive constructor-level
// assertion so a misconfigured DI wiring fails loudly instead of silently
// exposing shell access.
//
// All four tools share a single config struct (shell + timeout +
// max_output_chars) so we take DangerousShellConfig in the ctor of each.
//
// As with MemoryTool / SystemInfoTool we put all the related ITool classes
// in one file since they share a source module on the Python side.

/// <summary>
/// Static helpers for the dangerous_shell tool module. The
/// <see cref="IsEnabled"/> check mirrors the Python <c>get_tools(config)</c>
/// guard: registration only proceeds if BOTH <c>enabled</c> and
/// <c>i_understand_the_risks</c> are true.
/// </summary>
public static class DangerousShellTools
{
    /// <summary>
    /// Python-parity gate. Both flags must be true or the module refuses
    /// to load. Caller (the App's DI bootstrap) is expected to consult
    /// this before constructing the ITool instances.
    /// </summary>
    public static bool IsEnabled(DangerousShellConfig cfg)
    {
        if (cfg is null) return false;
        return cfg.Enabled && cfg.IUnderstandTheRisks;
    }

    /// <summary>
    /// Best-effort: are we running with elevated privileges? Windows-only
    /// in this build (TargetFramework is net8.0-windows). Returns false on
    /// any failure, matching the Python fallback.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Truncate <paramref name="text"/> to <paramref name="maxChars"/>,
    /// appending a "[truncated, total N chars]" footer when shortened.
    /// Same format as the Python <c>_truncate</c>.
    /// </summary>
    internal static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars)
            + $"\n... [truncated, total {text.Length} chars]";
    }
}

/// <summary>
/// Run a single shell command via powershell, cmd, or bash and return its
/// stdout/stderr. Mirrors the Python <c>run_shell</c> tool. Has full system
/// access at whatever privilege Jarvis is running with.
/// </summary>
public sealed class RunShellTool : ITool
{
    private readonly DangerousShellConfig _cfg;
    private readonly ILogger<RunShellTool> _log;

    public RunShellTool(DangerousShellConfig cfg, ILogger<RunShellTool> log)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        // Defensive: if DI ever wires this up without the double-gate the
        // user expected, fail loudly. Registration should have filtered.
        if (!DangerousShellTools.IsEnabled(cfg))
            throw new InvalidOperationException(
                "RunShellTool constructed but dangerous_shell is not fully " +
                "enabled (need both enabled=true and " +
                "i_understand_the_risks=true).");
        _cfg = cfg;
        _log = log;

        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] =
                        $"Single command line for {_cfg.Shell}. For " +
                        "PowerShell, semicolons separate statements; " +
                        "for bash, use && / ;",
                },
            },
            ["required"] = new JsonArray { "command" },
        };

        Schema = new ToolSchema(
            Name: "run_shell",
            Description:
                $"Run a shell command via {_cfg.Shell} and return its " +
                "stdout/stderr. Has full system access at the privilege " +
                "level Jarvis is running with. Use sparingly and only " +
                "when the user clearly wants a shell action. Commands " +
                $"have a {_cfg.TimeoutSeconds}-second timeout.",
            Parameters: parameters);
    }

    public ToolSchema Schema { get; }

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var command = (arguments["command"]?.GetValue<string>() ?? string.Empty).Trim();
        if (command.Length == 0) return "No command provided.";
        _log.LogWarning("run_shell: {Command}", command);

        ProcessStartInfo psi;
        var shell = (_cfg.Shell ?? "powershell").ToLowerInvariant();
        switch (shell)
        {
            case "powershell":
                psi = new ProcessStartInfo("powershell.exe");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(command);
                break;
            case "cmd":
                psi = new ProcessStartInfo("cmd.exe");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
                break;
            default:
                // bash / sh / etc.
                psi = new ProcessStartInfo(shell);
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;
        }

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"Shell not found: {ex.Message}";
        }
        catch (FileNotFoundException ex)
        {
            return $"Shell not found: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Failed to run command: {ex.Message}";
        }

        // Read both pipes concurrently so a large stream on one doesn't
        // block the child waiting on the other.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var timeout = TimeSpan.FromSeconds(_cfg.TimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                // Caller cancelled — kill child and propagate.
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }
            // Timeout path.
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return $"Command timed out after {_cfg.TimeoutSeconds}s and was killed.";
        }
        catch (Exception ex)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return $"Failed to run command: {ex.Message}";
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var exitCode = proc.ExitCode;

        var parts = new System.Collections.Generic.List<string>(2);
        if (stdout.Trim().Length > 0) parts.Add(stdout.TrimEnd());
        if (stderr.Trim().Length > 0) parts.Add($"[stderr]\n{stderr.TrimEnd()}");
        var combined = parts.Count > 0
            ? string.Join("\n\n", parts)
            : $"(no output, exit code {exitCode})";
        if (exitCode != 0)
            combined += $"\n\n[exit code {exitCode}]";

        return DangerousShellTools.Truncate(combined, _cfg.MaxOutputChars);
    }
}

/// <summary>
/// Read any file on disk. Mirrors the Python <c>read_file</c> tool — UTF-8
/// with a lenient fallback when bytes don't decode cleanly, truncated to
/// the configured size cap.
/// </summary>
public sealed class ReadFileTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Absolute or ~-relative path.",
            },
        },
        ["required"] = new JsonArray { "path" },
    };

    private readonly DangerousShellConfig _cfg;
    private readonly ILogger<ReadFileTool> _log;

    public ReadFileTool(DangerousShellConfig cfg, ILogger<ReadFileTool> log)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!DangerousShellTools.IsEnabled(cfg))
            throw new InvalidOperationException(
                "ReadFileTool constructed but dangerous_shell is not fully enabled.");
        _cfg = cfg;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "read_file",
        Description:
            "Read the contents of any file on disk. Returns the file's " +
            "text (truncated if very large). Use for inspecting configs, " +
            "logs, source files, etc.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var raw = (arguments["path"]?.GetValue<string>() ?? string.Empty).Trim();
        if (raw.Length == 0) return Task.FromResult("No path provided.");

        var path = ExpandUser(raw);
        _log.LogWarning("read_file: {Path}", path);

        if (!File.Exists(path) && !Directory.Exists(path))
            return Task.FromResult($"File not found: {path}");
        if (Directory.Exists(path))
            return Task.FromResult($"That's a directory, not a file: {path}");

        string text;
        try
        {
            // Python tries strict utf-8 first, then falls back to
            // bytes.decode("utf-8", errors="replace"). .NET's
            // File.ReadAllText defaults to UTF-8 with a permissive
            // decoder (invalid bytes become U+FFFD), so a single call
            // matches the fallback behaviour.
            var utf8Replace = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: false);
            text = File.ReadAllText(path, utf8Replace);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to read: {ex.Message}");
        }

        if (text.Length == 0) return Task.FromResult("(empty file)");
        return Task.FromResult(DangerousShellTools.Truncate(text, _cfg.MaxOutputChars));
    }

    private static string ExpandUser(string path)
    {
        // Python's Path.expanduser: "~" or "~/foo" → user home.
        if (path == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path.Substring(2));
        }
        return path;
    }

    // Exposed for sibling tools (write_file, list_directory) to share.
    internal static string ExpandUserPath(string path) => ExpandUser(path);
}

/// <summary>
/// Write content to a file, creating parent directories as needed.
/// OVERWRITES any existing file. Mirrors the Python <c>write_file</c> tool.
/// </summary>
public sealed class WriteFileTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["content"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray { "path", "content" },
    };

    private readonly DangerousShellConfig _cfg;
    private readonly ILogger<WriteFileTool> _log;

    public WriteFileTool(DangerousShellConfig cfg, ILogger<WriteFileTool> log)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!DangerousShellTools.IsEnabled(cfg))
            throw new InvalidOperationException(
                "WriteFileTool constructed but dangerous_shell is not fully enabled.");
        _cfg = cfg;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "write_file",
        Description:
            "Write content to a file, creating parent directories as " +
            "needed. OVERWRITES the existing file. Use deliberately — " +
            "this can destroy data.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        // Python checks content first, then path — match that order so
        // the error message the LLM sees is identical.
        var contentNode = arguments["content"];
        if (contentNode is null)
            return Task.FromResult("No content provided.");

        var raw = (arguments["path"]?.GetValue<string>() ?? string.Empty).Trim();
        if (raw.Length == 0) return Task.FromResult("No path provided.");

        // Python does str(content) — JSON numbers/bools become their
        // string form. Use ToJsonString() / GetValue<string>() to mirror.
        string content;
        if (contentNode is JsonValue jv && jv.TryGetValue(out string? s) && s is not null)
            content = s;
        else
            content = contentNode.ToJsonString();

        var path = ReadFileTool.ExpandUserPath(raw);
        _log.LogWarning("write_file: {Path} ({Chars} chars)", path, content.Length);

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // UTF-8 without BOM, matching Python's write_text default.
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to write: {ex.Message}");
        }

        return Task.FromResult($"Wrote {content.Length} chars to {path}");
    }
}

/// <summary>
/// List the contents of a directory. Mirrors the Python
/// <c>list_directory</c> tool: directories first, then files, both sorted
/// case-insensitively; each entry shown with a trailing "/" if it's a
/// directory and "  (N bytes)" otherwise.
/// </summary>
public sealed class ListDirectoryTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Defaults to current directory.",
            },
        },
    };

    private readonly DangerousShellConfig _cfg;
    private readonly ILogger<ListDirectoryTool> _log;

    public ListDirectoryTool(DangerousShellConfig cfg, ILogger<ListDirectoryTool> log)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!DangerousShellTools.IsEnabled(cfg))
            throw new InvalidOperationException(
                "ListDirectoryTool constructed but dangerous_shell is not fully enabled.");
        _cfg = cfg;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "list_directory",
        Description: "List the contents of a directory.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var raw = (arguments["path"]?.GetValue<string>() ?? ".").Trim();
        if (raw.Length == 0) raw = ".";

        var path = ReadFileTool.ExpandUserPath(raw);
        _log.LogWarning("list_directory: {Path}", path);

        if (!Directory.Exists(path) && !File.Exists(path))
            return Task.FromResult($"Path does not exist: {path}");
        if (!Directory.Exists(path))
            return Task.FromResult($"Not a directory: {path}");

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(path);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to list: {ex.Message}");
        }

        // Python: sorted(..., key=lambda e: (not e.is_dir(), e.name.lower()))
        // → directories first, then files; each group sorted case-insensitively.
        var sorted = entries
            .Select(p => new
            {
                Path = p,
                Name = Path.GetFileName(p),
                IsDir = Directory.Exists(p),
            })
            .OrderBy(e => e.IsDir ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sorted.Count == 0)
            return Task.FromResult($"(empty directory: {path})");

        var sb = new StringBuilder();
        sb.Append(path).Append(":\n");
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            sb.Append(e.Name);
            if (e.IsDir)
            {
                sb.Append('/');
            }
            else
            {
                try
                {
                    var size = new FileInfo(e.Path).Length;
                    sb.Append("  (").Append(size).Append(" bytes)");
                }
                catch (IOException) { /* match Python's OSError swallow */ }
                catch (UnauthorizedAccessException) { /* same */ }
            }
            if (i < sorted.Count - 1) sb.Append('\n');
        }

        return Task.FromResult(
            DangerousShellTools.Truncate(sb.ToString(), _cfg.MaxOutputChars));
    }
}
