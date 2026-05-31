// Claude Code subscription backend. Uses the locally-installed `claude.exe`
// CLI so the user pays via their Claude subscription rather than an API key.
//
// Loose port of llm/claude_agent.py from the Python edition. The Python build
// uses the official `claude_agent_sdk` package, which talks to Claude Code
// over an MCP transport. There is no equivalent .NET SDK, so this backend
// shells out to `claude.exe --print --output-format stream-json` and parses
// the JSON Lines on stdout — same end result, different transport.
//
// Behavioral differences from the Python version (documented for parity-
// auditors):
//
//   * No conversation continuity across SendAsync calls. Each user message
//     spawns a fresh `claude` process. The Python SDK uses ClaudeSDKClient
//     which holds a persistent session across the whole Jarvis run; we'd
//     need to plumb --session-id / --continue to match that. Filed as a
//     follow-up; for snappy voice replies this is usually fine since each
//     command is self-contained.
//
//   * Our ToolRegistry is NOT exposed to Claude Code. Claude Code has its
//     own MCP server + built-in tools (Bash, Read, Edit, etc.) and runs
//     them inside its own sandbox. The `tools` constructor argument is
//     accepted-but-ignored to keep the ILlmBackend constructor signature
//     uniform across all three backends. If you want Jarvis's tools to be
//     callable from Claude Code, you'd need to register them as an MCP
//     server via `--mcp-config`. Not done in this first cut.
//
//   * No `effort` / `thinking` knobs. The CLI exposes `--effort` but it's
//     less granular than the SDK's typed config; we default to whatever
//     the CLI does (medium), which gives reasonable latency for voice.
//
//   * Auth: uses the user's existing Claude Code login (OAuth / keychain),
//     exactly like the interactive CLI and the Python SDK. We deliberately
//     do NOT pass --bare (which would force ANTHROPIC_API_KEY-only auth and
//     re-prompt subscription users to log in). See the per-call comment in
//     SendAsync for the full flag rationale.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Tools;
using Jarvis.Setup.NET.Models;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Llm;

/// <summary>
/// Claude Code CLI shell-out backend. Spawns <c>claude.exe</c> per
/// <see cref="SendAsync"/> call, pipes the user message via stdin, parses the
/// stream-json output on stdout, and yields the text blocks as they arrive.
///
/// <para>
/// Known limitations vs the Python <c>claude_agent_sdk</c> backend:
/// no session continuity between turns (each call is a fresh process), and
/// Jarvis's own <see cref="ToolRegistry"/> is not surfaced to Claude Code
/// (which uses its own built-in tools + MCP servers). See the file-level
/// comment for the full breakdown.
/// </para>
/// </summary>
public sealed class ClaudeAgentBackend : ILlmBackend
{
    private readonly string _claudeExe;
    private readonly string? _model;
    private readonly string _systemPrompt;
    private readonly ILogger<ClaudeAgentBackend> _log;
    private readonly int _toolCount;
    private readonly string _installDir;
    private string? _mcpConfigPath;  // lazily written temp file

    public ClaudeAgentBackend(
        InstallConfig cfg,
        string systemPrompt,
        ToolRegistry tools,
        ILogger<ClaudeAgentBackend> log)
    {
        _log = log;
        _systemPrompt = systemPrompt ?? "";
        _toolCount = tools?.AllSchemas.Count ?? 0;
        _installDir = cfg?.InstallDir ?? AppContext.BaseDirectory;

        // The Python backend defaulted to claude-sonnet-4-6 when no model was
        // set. Match that. Empty string => let the CLI pick whatever the user
        // has configured as default (i.e. omit --model entirely).
        var configured = cfg?.Llm?.Model;
        _model = string.IsNullOrWhiteSpace(configured) ? null : configured;

        _claudeExe = LocateClaudeExe()
            ?? throw new FileNotFoundException(
                "Claude Code CLI not found. Install from https://claude.com/code, "
              + "sign in, then restart Jarvis.");

        _log.LogInformation(
            "Claude Agent backend ready (exe={Exe}, model={Model}, tools={Tools})",
            _claudeExe, _model ?? "<cli default>", _toolCount);
    }

    /// <summary>
    /// Path to the running Jarvis-NET.exe — used as the MCP server command so
    /// Claude Code can call Jarvis's tools via <c>Jarvis-NET.exe --mcp-server</c>.
    /// </summary>
    private static string? JarvisExePath()
    {
        try
        {
            var p = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        }
        catch { }
        // Fallback: look next to this assembly.
        try
        {
            var guess = Path.Combine(AppContext.BaseDirectory, "Jarvis-NET.exe");
            if (File.Exists(guess)) return guess;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Write (once) a temp mcp-config.json that registers Jarvis's tools as an
    /// MCP server. Returns the path, or null if we can't expose tools (no
    /// tools enabled, or we can't locate Jarvis-NET.exe — in which case the
    /// backend falls back to chat-only).
    /// </summary>
    private string? EnsureMcpConfig()
    {
        if (_toolCount == 0) return null;
        if (_mcpConfigPath is not null && File.Exists(_mcpConfigPath)) return _mcpConfigPath;

        var exe = JarvisExePath();
        if (exe is null)
        {
            _log.LogWarning("Can't locate Jarvis-NET.exe for the MCP bridge — claude_agent will be chat-only.");
            return null;
        }

        // { "mcpServers": { "jarvis": { "command": <exe>, "args": [...] } } }
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["jarvis"] = new JsonObject
                {
                    ["command"] = exe,
                    ["args"] = new JsonArray("--mcp-server", "--install-dir", _installDir),
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), "jarvis-mcp-config.json");
        try
        {
            File.WriteAllText(path, config.ToJsonString(), new UTF8Encoding(false));
            _mcpConfigPath = path;
            _log.LogInformation("Wrote MCP config exposing {Count} tools to Claude Code: {Path}",
                _toolCount, path);
            return path;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Failed to write MCP config — claude_agent will be chat-only.");
            return null;
        }
    }

    /// <summary>
    /// Look up <c>claude.exe</c> on PATH via <c>where</c> (Windows builtin,
    /// always present). Returns the first hit, or null if nothing matched.
    /// We resolve it at construction so we fail fast with a friendly error
    /// rather than per-message.
    /// </summary>
    private static string? LocateClaudeExe()
    {
        try
        {
            var psi = new ProcessStartInfo("where", "claude")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            if (p.ExitCode != 0) return null;

            // `where` prints one path per line — the first match wins. There
            // can be several entries (a .npm-global wrapper plus the native
            // build) and the first is what the user's shell would pick.
            foreach (var line in stdout.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && File.Exists(trimmed))
                {
                    return trimmed;
                }
            }
            return null;
        }
        catch (Win32Exception)
        {
            // `where` itself somehow missing. Almost impossible on modern
            // Windows but we handle it gracefully — fall back to PATH search.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<string> SendAsync(
        string userText,
        IProgress<JarvisEvent> events,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Build the argv. `--print` (a.k.a. -p) is non-interactive mode.
        // `--output-format stream-json` makes the CLI emit one JSON object
        // per line; the CLI insists on `--verbose` when stream-json is paired
        // with --print, so we set both.
        //
        // CRITICAL: we do NOT pass `--bare`. The earlier version did, and
        // `--bare` explicitly disables OAuth + keychain credential reads
        // ("Anthropic auth is strictly ANTHROPIC_API_KEY or apiKeyHelper") —
        // which meant a logged-in Claude subscription user got prompted to log
        // in again because the CLI couldn't see their existing OAuth session.
        // Without --bare the CLI reads the same credentials the interactive
        // CLI and the Python claude_agent_sdk use, so the user's existing
        // login Just Works with no re-auth.
        //
        // To keep the per-turn process snappy + chat-only WITHOUT touching
        // auth, we instead use targeted flags:
        //   --tools ""              disable all built-in tools (Bash/Edit/Read/
        //                           etc.) — this backend is conversational only
        //                           (Jarvis's own ToolRegistry isn't exposed to
        //                           Claude Code), and disabling tools also means
        //                           no permission prompts can hang a -p run.
        //   --strict-mcp-config     with no --mcp-config, this loads ZERO MCP
        //                           servers — skips the (often slow) attempts to
        //                           connect the user's configured MCP servers.
        //   --no-session-persistence don't write session files for throwaway
        //                           voice turns.
        //
        // We do NOT pass the user message as a positional arg because it can
        // contain shell-unsafe characters in many languages. Instead the prompt
        // comes in via stdin and `--print` reads it.
        var psi = new ProcessStartInfo
        {
            FileName = _claudeExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Run from a neutral dir so the CLI doesn't auto-discover a stray
            // CLAUDE.md from whatever the app's working directory happens to be.
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");                 // disable built-in tools (Bash/Edit/Read/...)
        psi.ArgumentList.Add("--no-session-persistence");
        // Suppress the user's personal Claude Code environment so it doesn't
        // leak into Jarvis's replies: --setting-sources "" loads no settings.json
        // (no hooks), --disable-slash-commands loads no skills/plugins. Without
        // these, a user's SessionStart hooks + superpowers plugin injected
        // "I'll check for any relevant skills..." into every Jarvis reply.
        psi.ArgumentList.Add("--setting-sources");
        psi.ArgumentList.Add("");
        psi.ArgumentList.Add("--disable-slash-commands");

        // Expose Jarvis's tools to Claude Code via an MCP bridge (the C#
        // equivalent of the Python edition's create_sdk_mcp_server). When set,
        // Claude Code can call play_music / open_app / etc. --strict-mcp-config
        // means ONLY our jarvis server loads (not the user's other MCP servers),
        // and --allowed-tools pre-approves the jarvis tools so they run without
        // a permission prompt in headless -p mode.
        var mcpConfig = EnsureMcpConfig();
        if (mcpConfig is not null)
        {
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(mcpConfig);
            psi.ArgumentList.Add("--strict-mcp-config");
            psi.ArgumentList.Add("--allowed-tools");
            psi.ArgumentList.Add("mcp__jarvis__*");
        }
        else
        {
            // No tools to expose — keep MCP off entirely (skip the user's
            // configured MCP servers too, for speed + isolation).
            psi.ArgumentList.Add("--strict-mcp-config");
        }

        if (_model is not null)
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_model);
        }
        if (!string.IsNullOrEmpty(_systemPrompt))
        {
            psi.ArgumentList.Add("--system-prompt");
            psi.ArgumentList.Add(_systemPrompt);
        }

        Process? proc = null;
        string? spawnError = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                spawnError = "Couldn't start Claude Code: Process.Start returned null.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to spawn claude.exe");
            spawnError = $"Couldn't start Claude Code: {ex.Message}";
        }

        if (spawnError is not null)
        {
            events.Report(new BackendError(spawnError, null));
            yield return spawnError;
            yield break;
        }

        // Stream lines and yield text. We can't yield from inside a try/catch
        // block, so we delegate the IO + parsing to a helper that buffers and
        // returns one of three outcomes:
        //   * a list of text chunks to yield (success)
        //   * a friendly error string (network / parse / non-zero exit)
        //   * cancellation (we kill the process and rethrow)
        await foreach (var emission in StreamProcess(proc!, userText, ct).ConfigureAwait(false))
        {
            // Each emission is either text-to-yield or a terminal error.
            if (emission.IsError)
            {
                events.Report(new BackendError(emission.Text, null));
                yield return emission.Text;
                yield break;
            }
            if (!string.IsNullOrEmpty(emission.Text))
            {
                yield return emission.Text;
            }
        }
    }

    /// <summary>
    /// Read-and-parse loop. Yields <see cref="Emission"/> records as it
    /// progresses — text chunks for assistant text blocks, then optionally a
    /// terminal error if the process exited non-zero or stdout was malformed.
    /// </summary>
    private async IAsyncEnumerable<Emission> StreamProcess(
        Process proc,
        string userText,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Hook cancellation to a Kill so a ctrl-C / stop-button tears the
        // CLI down promptly. CancellationTokenRegistration is disposed at
        // the end of the method.
        using var killReg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* race with normal exit — fine */ }
        });

        // Write the prompt to stdin in a fire-and-forget Task so we can start
        // pulling output immediately. The CLI happily buffers stdin while it
        // starts up, so order doesn't strictly matter, but we close stdin as
        // soon as the prompt is in so the CLI sees EOF and proceeds.
        var stdinTask = Task.Run(async () =>
        {
            try
            {
                await proc.StandardInput.WriteAsync(userText.AsMemory(), ct).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "stdin write to claude.exe failed (process may have exited)");
            }
            finally
            {
                try { proc.StandardInput.Close(); } catch { /* already closed */ }
            }
        }, ct);

        // Drain stderr in parallel so a chatty CLI can't deadlock us by
        // filling the stderr pipe buffer while we're blocked on stdout.
        var stderrBuf = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    stderrBuf.AppendLine(line);
                }
            }
            catch { /* process tore down mid-read */ }
        });

        // Track whether we've already emitted a result-line text. The CLI
        // sometimes echoes the final answer once as the assistant message
        // and again in the {"type":"result"} line — we don't want to double-
        // print, so we skip the result-line text once we've seen at least
        // one assistant text block. If we got NO assistant text but the
        // result line carries text (rare, but happens for one-line replies
        // when the assistant message is filtered), we fall back to it.
        var emittedAnyText = false;
        string? deferredResultText = null;
        bool resultIsError = false;
        string? resultErrorMessage = null;

        // Stream stdout line by line. Each line is a complete JSON object.
        // We tolerate the occasional non-JSON line (e.g. a startup warning
        // the CLI prints to stdout instead of stderr) by ignoring it rather
        // than aborting the whole stream.
        var parseError = false;

        await foreach (var parsed in ReadJsonLines(proc.StandardOutput, ct).ConfigureAwait(false))
        {
            if (parsed is null)
            {
                // Couldn't parse this line. Skip and keep going — fail only
                // if we hit EOF without ever getting valid output.
                continue;
            }

            var type = parsed.RootElement.TryGetProperty("type", out var typeEl)
                ? typeEl.GetString()
                : null;

            if (type == "assistant")
            {
                // {"type":"assistant","message":{"content":[{...},{...}]}}
                // We only care about text blocks; thinking/tool_use blocks
                // get logged and dropped because they're not user-visible
                // text in a voice-reply context.
                if (parsed.RootElement.TryGetProperty("message", out var msgEl) &&
                    msgEl.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in contentEl.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var blockTypeEl)) continue;
                        if (blockTypeEl.GetString() != "text") continue;
                        if (!block.TryGetProperty("text", out var textEl)) continue;
                        var text = textEl.GetString();
                        if (string.IsNullOrEmpty(text)) continue;
                        emittedAnyText = true;
                        yield return new Emission(text, IsError: false);
                    }
                }
            }
            else if (type == "result")
            {
                // {"type":"result","is_error":bool,"result":"...","subtype":"..."}
                // This is the final line. Capture it for fallback if no
                // assistant text was emitted, and check for error status.
                if (parsed.RootElement.TryGetProperty("is_error", out var errEl) &&
                    errEl.ValueKind is JsonValueKind.True)
                {
                    resultIsError = true;
                    if (parsed.RootElement.TryGetProperty("result", out var rEl))
                    {
                        resultErrorMessage = rEl.GetString();
                    }
                }
                else if (parsed.RootElement.TryGetProperty("result", out var rEl))
                {
                    deferredResultText = rEl.GetString();
                }
            }
            // Everything else (system/init, hooks, rate_limit_event, etc.) is
            // bookkeeping noise — ignore.

            parsed.Dispose();
        }

        // stdout closed. Wait for the process to actually exit so we can
        // read ExitCode, then make sure stderr drain finished.
        try
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* already exited */ }
        try { await stderrTask.ConfigureAwait(false); } catch { }
        try { await stdinTask.ConfigureAwait(false); } catch { }

        // Cancellation check AFTER WaitForExit — if the user cancelled, the
        // process was killed and we should report nothing (the orchestrator
        // is tearing down anyway). Throw so the IAsyncEnumerable surfaces
        // cancellation rather than a misleading "Claude Code exited" string.
        ct.ThrowIfCancellationRequested();

        // Result-line error trumps exit-code error — its message is usually
        // more informative ("api_error: rate limit" vs "exit 1").
        if (resultIsError)
        {
            var msg = string.IsNullOrEmpty(resultErrorMessage)
                ? "Claude Code reported an error. Check jarvis.log."
                : $"Claude Code error: {resultErrorMessage}";
            yield return new Emission(msg, IsError: true);
            yield break;
        }

        if (proc.ExitCode != 0)
        {
            // Truncate stderr — some failures dump megabytes of stack trace.
            var stderr = stderrBuf.ToString().Trim();
            if (stderr.Length > 200) stderr = stderr.Substring(0, 200) + "...";
            var msg = string.IsNullOrEmpty(stderr)
                ? $"Claude Code exited with code {proc.ExitCode}. Check jarvis.log."
                : $"Claude Code exited with error: {stderr}";
            _log.LogError("claude.exe exit code {Code}, stderr: {Stderr}",
                proc.ExitCode, stderrBuf.ToString());
            yield return new Emission(msg, IsError: true);
            yield break;
        }

        // Fallback: if no assistant text streamed (because the CLI filtered
        // it or we missed it), emit the result-line text so the user isn't
        // staring at an empty reply.
        if (!emittedAnyText && !string.IsNullOrEmpty(deferredResultText))
        {
            yield return new Emission(deferredResultText, IsError: false);
            yield break;
        }

        // If we got NEITHER assistant text NOR a result line, something is
        // wrong with the output format — surface as parse error.
        if (!emittedAnyText && deferredResultText is null)
        {
            parseError = true;
        }

        if (parseError)
        {
            _log.LogError("claude.exe produced no parseable output. stderr: {Stderr}",
                stderrBuf.ToString());
            yield return new Emission(
                "Got unexpected output from Claude Code. Check jarvis.log.",
                IsError: true);
        }
    }

    /// <summary>
    /// Read <paramref name="reader"/> line-by-line and yield a parsed
    /// <see cref="JsonDocument"/> per line. Lines that aren't valid JSON
    /// yield <c>null</c> — the caller decides whether to ignore or fail.
    /// </summary>
    private static async IAsyncEnumerable<JsonDocument?> ReadJsonLines(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            // ReadLineAsync doesn't accept a CT until .NET 7+, but we already
            // have a kill-on-cancel registration in the outer method, so the
            // underlying stream will close and ReadLineAsync will return.
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Pipe broken because process exited / was killed. Treat as
                // EOF rather than an exception so the outer loop can do its
                // exit-code / cancellation classification.
                yield break;
            }

            if (line is null) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                doc = null;
            }
            yield return doc;
        }
    }

    /// <summary>
    /// Internal helper record so StreamProcess can yield both text chunks
    /// and a terminal "error" without throwing across yield boundaries.
    /// </summary>
    private readonly record struct Emission(string Text, bool IsError);

    public ValueTask DisposeAsync()
    {
        // Nothing persistent to dispose — each SendAsync owns its own process
        // and tears it down before returning.
        return ValueTask.CompletedTask;
    }
}
