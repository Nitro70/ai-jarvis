// OpenAI-compatible backend. Talks to anything speaking the OpenAI Chat
// Completions API: OpenAI itself, xAI (Grok), Groq, OpenRouter, Ollama,
// LM Studio, etc. Configure base_url + api_key + model in config.yaml.
//
// Direct port of llm/openai_compat.py from the Python edition. The behavior
// MUST stay parity-equivalent — a user can flip between the Python build and
// the .NET build pointing at the same config.yaml and get the same answers.

using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Tools;
using Jarvis.Setup.NET.Models;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace Jarvis.Core.Llm;

/// <summary>
/// OpenAI Chat Completions–compatible backend. Streams tokens, executes tool
/// calls against the orchestrator's <see cref="ToolRegistry"/>, and surfaces
/// friendly error strings on common failure modes (auth, rate limit, timeout,
/// connection refused, bad request, other HTTP errors).
/// </summary>
public sealed class OpenAiCompatBackend : ILlmBackend
{
    private readonly ChatClient _chat;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly ToolRegistry _tools;
    private readonly IReadOnlyList<ChatTool>? _toolDefs;
    private readonly ILogger<OpenAiCompatBackend> _log;
    private readonly string _baseUrl;

    // History is owned by the backend. The orchestrator just feeds user
    // strings in; the backend remembers everything for the whole session.
    // Mirrors the Python self._history: list[dict] approach. Mutated during
    // SendAsync — see the checkpoint/rollback pattern there.
    private readonly List<ChatMessage> _history = new();

    public OpenAiCompatBackend(
        InstallConfig cfg,
        string systemPrompt,
        ToolRegistry tools,
        ILogger<OpenAiCompatBackend> log)
    {
        _log = log;
        _tools = tools;

        var llm = cfg.Llm;
        _baseUrl = string.IsNullOrWhiteSpace(llm.BaseUrl)
            ? "https://api.openai.com/v1"
            : llm.BaseUrl!;
        _model = string.IsNullOrWhiteSpace(llm.Model) ? "gpt-4o-mini" : llm.Model;
        _maxTokens = llm.MaxTokens > 0 ? llm.MaxTokens : 1024;

        // Dummy api_key fallback. Local backends (Ollama, LM Studio) ignore
        // the key entirely, so users typically leave it blank in config.yaml.
        // The OpenAI SDK requires a non-empty credential — passing "not-needed"
        // satisfies that without leaking a real key. If they pointed at real
        // OpenAI without setting one, the request will 401 and they'll get the
        // friendly "My API key was rejected" string below. Matches Python:
        //     api_key = llm_cfg.get("api_key") or "not-needed"
        var apiKey = string.IsNullOrWhiteSpace(llm.ApiKey) ? "not-needed" : llm.ApiKey!;

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(_baseUrl) };
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        _chat = client.GetChatClient(_model);

        // Some local models (llama2 base, older Mistral via Ollama, etc.) don't
        // support OpenAI-style function calling. Sending tools to them either
        // silently drops the calls or errors with "model does not support
        // tools". Setting disable_tools: true in config.yaml's llm: section
        // makes Jarvis run in chat-only mode — replies still work, but it
        // can't call play_music / open_app / etc.
        if (llm.DisableTools)
        {
            _log.LogInformation("disable_tools=true in config — running chat-only, no tool calls");
            _toolDefs = null;
        }
        else
        {
            var defs = new List<ChatTool>();
            foreach (var schema in tools.AllSchemas)
            {
                // The OpenAI SDK takes the JSON Schema for parameters as raw
                // BinaryData. Round-trip our JsonObject through string so the
                // SDK can re-parse it on the wire.
                var paramJson = schema.Parameters.ToJsonString();
                defs.Add(ChatTool.CreateFunctionTool(
                    functionName: schema.Name,
                    functionDescription: schema.Description,
                    functionParameters: BinaryData.FromString(paramJson)));
            }
            _toolDefs = defs.Count > 0 ? defs : null;
        }

        _history.Add(new SystemChatMessage(systemPrompt));
        _log.LogInformation(
            "OpenAI-compat backend ready (base_url={BaseUrl}, model={Model}, tools={ToolCount})",
            _baseUrl, _model, _toolDefs?.Count ?? 0);
    }

    public async IAsyncEnumerable<string> SendAsync(
        string userText,
        IProgress<JarvisEvent> events,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Take a snapshot of history BEFORE appending anything for this turn.
        // If any LLM call fails — first attempt or in the middle of a multi-
        // step tool round-trip — we restore to here, dropping the half-baked
        // sequence (user + assistant(tool_calls) + tool(result)) rather than
        // leaving it stuck in history confusing every future request.
        //
        // Without this, a single failure after a tool call would leave an
        // orphan "assistant requested tool X" with no "tool X returned Y"
        // alongside it, and the very next user message would get rejected
        // by the API for malformed history.
        var checkpoint = _history.Count;
        _history.Add(new UserChatMessage(userText));

        while (true)
        {
            // Each round-trip builds fresh options — Tools is set inside the
            // ChatCompletionOptions and the SDK reads it per-call.
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = _maxTokens,
            };
            if (_toolDefs is not null)
            {
                foreach (var t in _toolDefs) options.Tools.Add(t);
            }

            // We accumulate content + tool calls from the stream into these.
            // The SDK delivers tool calls piecewise — Index identifies which
            // call a chunk belongs to, then FunctionName arrives on the first
            // chunk for that index and FunctionArgumentsUpdate accumulates
            // across subsequent chunks (JSON is built up token-by-token).
            var contentBuf = new StringBuilder();
            var toolCallNames = new Dictionary<int, string>();
            var toolCallIds = new Dictionary<int, string>();
            var toolCallArgs = new Dictionary<int, StringBuilder>();
            string? friendlyError = null;

            // The whole streaming + error-classification dance happens in this
            // helper. We can't `yield return` from inside a try/catch in C#,
            // so the helper returns the buffered content and any error string
            // and we yield outside the try.
            try
            {
                await foreach (var update in _chat.CompleteChatStreamingAsync(
                    _history, options, ct).ConfigureAwait(false))
                {
                    if (update.ContentUpdate is { Count: > 0 } parts)
                    {
                        foreach (var part in parts)
                        {
                            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrEmpty(part.Text))
                            {
                                contentBuf.Append(part.Text);
                                // Yield each token immediately so the UI can
                                // stream. We have to defer the actual yield to
                                // after the try block — buffer the chunk in a
                                // local and stream after success.
                            }
                        }
                    }

                    if (update.ToolCallUpdates is { Count: > 0 } tcUpdates)
                    {
                        foreach (var tc in tcUpdates)
                        {
                            var idx = tc.Index;
                            if (!string.IsNullOrEmpty(tc.ToolCallId))
                                toolCallIds[idx] = tc.ToolCallId;
                            if (!string.IsNullOrEmpty(tc.FunctionName))
                                toolCallNames[idx] = tc.FunctionName;
                            if (tc.FunctionArgumentsUpdate is { } argDelta && argDelta.ToMemory().Length > 0)
                            {
                                if (!toolCallArgs.TryGetValue(idx, out var sb))
                                {
                                    sb = new StringBuilder();
                                    toolCallArgs[idx] = sb;
                                }
                                sb.Append(argDelta.ToString());
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Real user-initiated cancellation. asyncio.CancelledError in
                // Python bypasses friendly-error handlers — same here. Roll
                // back so we don't poison history, then re-throw so the
                // orchestrator's cancel path runs.
                _history.RemoveRange(checkpoint, _history.Count - checkpoint);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // OperationCanceledException without our token being cancelled
                // means the underlying HttpClient hit its timeout (it raises
                // TaskCanceledException, a subclass). Surface as a friendly
                // timeout message — matches Python's APITimeoutError branch.
                _log.LogError(ex, "LLM timeout ({BaseUrl})", _baseUrl);
                friendlyError = "The LLM took too long to respond. Try again.";
            }
            catch (ClientResultException ex)
            {
                // All OpenAI SDK HTTP failures funnel through this exception.
                // The Python SDK splits them into AuthenticationError /
                // RateLimitError / etc. classes; here we classify by status
                // code on the response. Status == 0 typically means we never
                // got a response (DNS / connection refused / TLS error).
                friendlyError = ClassifyClientError(ex);
            }
            catch (HttpRequestException ex)
            {
                // The HttpClient pipeline raises this for DNS / refused-
                // connection / TLS handshake failures, mirroring Python's
                // APIConnectionError.
                _log.LogError(ex, "LLM network error ({BaseUrl})", _baseUrl);
                friendlyError = "I can't reach the LLM server. "
                              + "Check your internet or the base_url in config.yaml.";
            }
            catch (IOException ex)
            {
                // SSE stream truncation / socket close mid-stream. Treat as
                // a network-class failure.
                _log.LogError(ex, "LLM stream IO error");
                friendlyError = "I can't reach the LLM server. "
                              + "Check your internet or the base_url in config.yaml.";
            }
            catch (Exception ex)
            {
                // Catch-all to match Python's bare-except. Logs the full
                // stack so we can diagnose later from jarvis.log.
                _log.LogError(ex, "LLM unexpected error");
                friendlyError = $"Something went wrong: {ex.Message}";
            }

            if (friendlyError is not null)
            {
                _history.RemoveRange(checkpoint, _history.Count - checkpoint);
                events.Report(new BackendError(friendlyError, null));
                yield return friendlyError;
                yield break;
            }

            // Yield the assistant's content as a single chunk. The streaming
            // loop above buffered it instead of yielding directly because we
            // had to keep the yield outside the try/catch. The caller (and
            // therefore the UI) still sees streaming-style updates because
            // each SendAsync call yields the whole response one round-trip
            // at a time — for tool round-trips, that's still multiple yields.
            //
            // TODO(phase 2): if we want true per-token UI streaming we'll need
            // to refactor this to yield from a Channel<string> populated by a
            // separate task, sidestepping the yield-in-try restriction.
            var assistantText = contentBuf.ToString();
            if (!string.IsNullOrEmpty(assistantText))
            {
                yield return assistantText;
            }

            // Reconstruct the assistant message for history. If there were
            // tool calls we must preserve them verbatim so the next API call
            // sees the matched tool_call_id / tool_result pairs.
            var toolCallList = new List<ChatToolCall>();
            // Iterate by sorted index so calls appear in deterministic order.
            var indices = new List<int>(toolCallNames.Keys);
            indices.Sort();
            foreach (var idx in indices)
            {
                var name = toolCallNames[idx];
                var id = toolCallIds.TryGetValue(idx, out var i) ? i : $"call_{idx}";
                var args = toolCallArgs.TryGetValue(idx, out var sb) ? sb.ToString() : "{}";
                toolCallList.Add(ChatToolCall.CreateFunctionToolCall(
                    id,
                    name,
                    BinaryData.FromString(string.IsNullOrEmpty(args) ? "{}" : args)));
            }

            AssistantChatMessage assistantMsg;
            if (toolCallList.Count > 0)
            {
                // SDK requires the tool-calls overload; content tacks on after.
                assistantMsg = new AssistantChatMessage(toolCallList);
                if (!string.IsNullOrEmpty(assistantText))
                {
                    assistantMsg.Content.Add(ChatMessageContentPart.CreateTextPart(assistantText));
                }
            }
            else
            {
                assistantMsg = new AssistantChatMessage(assistantText);
            }
            _history.Add(assistantMsg);

            if (toolCallList.Count == 0)
            {
                // Plain reply — turn is complete.
                yield break;
            }

            // Execute each tool call and append its result to history. Then
            // loop and call the API again so the model can react to the
            // results. Bounded only by the model deciding to stop calling
            // tools — same as Python.
            foreach (var tc in toolCallList)
            {
                var argsJson = tc.FunctionArguments?.ToString() ?? "{}";
                events.Report(new ToolInvoked(tc.FunctionName, argsJson));

                string result;
                if (!_tools.TryGet(tc.FunctionName, out var tool) || tool is null)
                {
                    result = $"Unknown tool: {tc.FunctionName}";
                }
                else
                {
                    JsonObject argsObj;
                    try
                    {
                        argsObj = string.IsNullOrWhiteSpace(argsJson)
                            ? new JsonObject()
                            : (JsonNode.Parse(argsJson) as JsonObject) ?? new JsonObject();
                    }
                    catch (JsonException)
                    {
                        // Model emitted malformed JSON in arguments. Python
                        // silently falls back to {} — match that behavior so
                        // tools that have all-optional params still fire.
                        argsObj = new JsonObject();
                    }

                    try
                    {
                        result = await tool.InvokeAsync(argsObj, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _history.RemoveRange(checkpoint, _history.Count - checkpoint);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // ITool.InvokeAsync is supposed to return "Tool error:"
                        // strings rather than throw, but if one slips through
                        // we still want the model to see a usable result.
                        _log.LogError(ex, "tool {Name} threw", tc.FunctionName);
                        result = $"Tool error: {ex.Message}";
                    }
                }

                events.Report(new ToolResult(tc.FunctionName, result));
                _history.Add(new ToolChatMessage(tc.Id, result));
            }

            // Loop back around so the model can use the tool results.
        }
    }

    private string ClassifyClientError(ClientResultException ex)
    {
        var status = ex.Status;
        switch (status)
        {
            case 401:
            case 403:
                _log.LogError(ex, "LLM auth failed ({BaseUrl})", _baseUrl);
                return "My API key was rejected by the LLM. "
                     + "Check api_key in config.yaml and restart me.";
            case 429:
                _log.LogError(ex, "LLM rate-limited");
                return "The LLM provider is rate-limiting me. Try again in a minute.";
            case 400:
            case 404:
            case 422:
                _log.LogError(ex, "LLM rejected request (HTTP {Code})", status);
                return "The LLM rejected my request - the model name or "
                     + "config is probably wrong. Check the log.";
            case 408:
            case 504:
                _log.LogError(ex, "LLM timeout (HTTP {Code})", status);
                return "The LLM took too long to respond. Try again.";
            case 0:
                // No HTTP response at all — typically wraps a SocketException
                // for connection refused / DNS failure.
                _log.LogError(ex, "LLM unreachable ({BaseUrl})", _baseUrl);
                return "I can't reach the LLM server. "
                     + "Check your internet or the base_url in config.yaml.";
            default:
                _log.LogError(ex, "LLM HTTP {Code}", status);
                return $"The LLM returned HTTP {status}. Check the log for details.";
        }
    }

    public ValueTask DisposeAsync()
    {
        // The OpenAI SDK's ChatClient holds an HttpClient pipeline managed by
        // the parent OpenAIClient. There's no explicit Close() to call — the
        // process tearing down releases the sockets. Mirrors Python's
        // try/except around client.close() on shutdown.
        return ValueTask.CompletedTask;
    }
}
