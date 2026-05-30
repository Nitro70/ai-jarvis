// Direct Anthropic Messages API backend. Talks to api.anthropic.com using
// a user-supplied API key from console.anthropic.com. Hand-rolled HttpClient
// — Anthropic does not ship an official .NET SDK and the third-party ones
// are either abandoned or pull large transitive dependency trees we don't
// need for a single endpoint.
//
// Direct port of llm/claude_api.py from the Python edition. Behavior must
// stay parity-equivalent — a user can flip between the Python build and the
// .NET build pointing at the same config.yaml and get the same answers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Anthropic Messages API backend. Sends non-streaming POSTs to
/// /v1/messages, runs the tool round-trip loop manually, and surfaces
/// friendly error strings on common failure modes (auth, rate limit,
/// timeout, connection refused, bad request, other HTTP errors).
/// </summary>
public sealed class ClaudeApiBackend : ILlmBackend
{
    // Default endpoint + version. Anthropic versions are date strings and
    // the docs say to pin one explicitly — every supported model takes
    // 2023-06-01 today and there's no reason to upgrade until a breaking
    // change forces us.
    private const string ApiBase = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly string _system;
    private readonly ToolRegistry _tools;
    private readonly JsonArray? _toolDefs;  // null => omit "tools" entirely
    private readonly ILogger<ClaudeApiBackend> _log;

    // History is owned by the backend, same pattern as OpenAiCompatBackend.
    // Each entry is a Messages-API message: { role, content } where content
    // is either a plain string OR a JsonArray of content blocks (text /
    // tool_use / tool_result). We store JsonObjects so we can serialize the
    // whole history straight back to the API on each round-trip.
    private readonly List<JsonObject> _history = new();

    public ClaudeApiBackend(
        InstallConfig cfg,
        string systemPrompt,
        ToolRegistry tools,
        ILogger<ClaudeApiBackend> log)
    {
        _log = log;
        _tools = tools;

        var llm = cfg.Llm;
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            // Matches Python: ValueError on construction. Anthropic has no
            // local-model fallback so a missing key is unrecoverable.
            throw new ArgumentException(
                "claude_api backend requires llm.api_key in config "
              + "(use ${ANTHROPIC_API_KEY} to read from env)");
        }
        _apiKey = llm.ApiKey!;
        _model = string.IsNullOrWhiteSpace(llm.Model) ? "claude-sonnet-4-6" : llm.Model;
        _maxTokens = llm.MaxTokens > 0 ? llm.MaxTokens : 1024;
        _system = systemPrompt;

        _http = new HttpClient { BaseAddress = new Uri(ApiBase) };
        // The auth + version headers are required on every call — set them
        // on the client once instead of decorating each request.
        _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        // Same disable_tools toggle as the OpenAI-compat backend. Anthropic
        // models all support tools so this is mostly here for parity / debug.
        if (llm.DisableTools)
        {
            _log.LogInformation("disable_tools=true in config — running chat-only, no tool calls");
            _toolDefs = null;
        }
        else
        {
            var defs = new JsonArray();
            foreach (var schema in tools.AllSchemas)
            {
                // Anthropic's tool shape: { name, description, input_schema }.
                // input_schema is plain JSON Schema, exactly the same object
                // we hand to OpenAI as `parameters`. Deep-clone so we don't
                // accidentally re-parent the shared JsonNode under two trees.
                defs.Add(new JsonObject
                {
                    ["name"] = schema.Name,
                    ["description"] = schema.Description,
                    ["input_schema"] = schema.Parameters.DeepClone(),
                });
            }
            _toolDefs = defs.Count > 0 ? defs : null;
        }

        _log.LogInformation(
            "Anthropic API backend ready (model={Model}, tools={ToolCount})",
            _model, _toolDefs?.Count ?? 0);
    }

    public async IAsyncEnumerable<string> SendAsync(
        string userText,
        IProgress<JarvisEvent> events,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Checkpoint history before mutating it. Same rationale as
        // OpenAiCompatBackend: if anything in the round-trip blows up we
        // restore to here so we don't leave an orphan tool_use without a
        // matching tool_result (the API rejects that on the next call).
        var checkpoint = _history.Count;
        _history.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userText,
        });

        while (true)
        {
            // Build the request body fresh each round-trip. The tools field
            // is omitted entirely when DisableTools=true (Anthropic accepts
            // either absent or empty arrays; absent is the cleaner signal).
            var body = new JsonObject
            {
                ["model"] = _model,
                ["max_tokens"] = _maxTokens,
                ["system"] = _system,
                ["messages"] = CloneHistory(),
            };
            if (_toolDefs is not null)
            {
                body["tools"] = _toolDefs.DeepClone();
            }

            // The send-and-classify dance happens inside this try. We can't
            // yield from inside try/catch in C#, so we buffer the assistant's
            // text blocks + tool_use list, then yield outside.
            JsonNode? responseJson = null;
            string? friendlyError = null;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                using var resp = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var respText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    friendlyError = ClassifyHttpError((int)resp.StatusCode, respText);
                }
                else
                {
                    responseJson = JsonNode.Parse(respText);
                    if (responseJson is null)
                    {
                        friendlyError = "The LLM returned an empty response. Try again.";
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Real user-initiated cancel — roll back and re-raise so the
                // orchestrator's cancel path runs.
                _history.RemoveRange(checkpoint, _history.Count - checkpoint);
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // HttpClient timeout — surfaces as TaskCanceledException
                // (subclass) without our token being tripped.
                _log.LogError(ex, "LLM timeout");
                friendlyError = "The LLM took too long to respond. Try again.";
            }
            catch (HttpRequestException ex)
            {
                // DNS / connection refused / TLS — equivalent to Python's
                // anthropic.APIConnectionError.
                _log.LogError(ex, "LLM network error");
                friendlyError = "I can't reach the LLM server. "
                              + "Check your internet or your network.";
            }
            catch (IOException ex)
            {
                _log.LogError(ex, "LLM stream IO error");
                friendlyError = "I can't reach the LLM server. "
                              + "Check your internet or your network.";
            }
            catch (JsonException ex)
            {
                _log.LogError(ex, "LLM returned unparseable JSON");
                friendlyError = "The LLM returned a malformed response. Try again.";
            }
            catch (Exception ex)
            {
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

            // Parse the assistant content array. Each block is either
            //   { type: "text", text: "..." }
            // or
            //   { type: "tool_use", id: "...", name: "...", input: {...} }
            // We yield text blocks in order and collect tool_use blocks
            // for execution after we've finished iterating.
            var contentArray = responseJson!["content"] as JsonArray ?? new JsonArray();
            var stopReason = responseJson["stop_reason"]?.GetValue<string>();

            // Echo the full assistant response (text + tool_use blocks) back
            // into history verbatim. Anthropic requires the original block
            // structure to be preserved across calls so each tool_use_id
            // can be paired with its matching tool_result on the next turn.
            // DeepClone so the API response object can be GC'd.
            _history.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = contentArray.DeepClone(),
            });

            var toolUses = new List<(string Id, string Name, JsonObject Input)>();
            var textBuf = new StringBuilder();
            foreach (var block in contentArray)
            {
                if (block is not JsonObject obj) continue;
                var type = obj["type"]?.GetValue<string>();
                if (type == "text")
                {
                    var text = obj["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text)) textBuf.Append(text);
                }
                else if (type == "tool_use")
                {
                    var id = obj["id"]?.GetValue<string>() ?? "";
                    var name = obj["name"]?.GetValue<string>() ?? "";
                    var input = obj["input"] as JsonObject ?? new JsonObject();
                    toolUses.Add((id, name, (JsonObject)input.DeepClone()));
                }
                // Other block types (thinking, etc.) are passed through in
                // history via the DeepClone above but ignored for streaming.
            }

            // Yield the buffered text as a single chunk. Same single-yield
            // approach as OpenAiCompatBackend — true per-token streaming
            // would require switching to SSE + a Channel<string>; see the
            // TODO in that file.
            var assistantText = textBuf.ToString();
            if (!string.IsNullOrEmpty(assistantText))
            {
                yield return assistantText;
            }

            // Python: `if response.stop_reason != "tool_use" or not tool_uses: return`
            // We mirror both halves — a model can theoretically end with
            // stop_reason="end_turn" but still ship tool_use blocks, or
            // vice versa; we play it safe and require both.
            if (stopReason != "tool_use" || toolUses.Count == 0)
            {
                yield break;
            }

            // Execute each tool call. Results are collected into a single
            // user message containing an array of tool_result blocks — one
            // per tool_use we got back. Order matches the assistant's
            // tool_use blocks.
            var toolResults = new JsonArray();
            foreach (var use in toolUses)
            {
                events.Report(new ToolInvoked(use.Name, use.Input.ToJsonString()));

                string result;
                bool isError;
                if (!_tools.TryGet(use.Name, out var tool) || tool is null)
                {
                    result = $"Unknown tool: {use.Name}";
                    isError = true;
                }
                else
                {
                    try
                    {
                        result = await tool.InvokeAsync(use.Input, ct).ConfigureAwait(false);
                        isError = false;
                    }
                    catch (OperationCanceledException)
                    {
                        _history.RemoveRange(checkpoint, _history.Count - checkpoint);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // ITool.InvokeAsync should swallow its own errors,
                        // but if one escapes we still want the model to see
                        // a usable result string rather than blowing up the
                        // whole turn.
                        _log.LogError(ex, "tool {Name} threw", use.Name);
                        result = $"Tool error: {ex.Message}";
                        isError = true;
                    }
                }

                events.Report(new ToolResult(use.Name, result));

                // Python builds the result block with `content: str(result)`
                // even when empty. Match that — Anthropic accepts empty
                // strings here without complaint.
                toolResults.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = use.Id,
                    ["content"] = result,
                    ["is_error"] = isError,
                });
            }

            _history.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = toolResults,
            });

            // Loop back so the model can react to the tool results.
        }
    }

    /// <summary>
    /// Deep-clones the history into a fresh JsonArray. We can't hand the
    /// stored JsonObjects directly to the request body because System.Text.Json
    /// nodes can have only one parent — re-using them would detach them from
    /// _history the moment we attached them to the new body.
    /// </summary>
    private JsonArray CloneHistory()
    {
        var arr = new JsonArray();
        foreach (var msg in _history)
        {
            arr.Add(msg.DeepClone());
        }
        return arr;
    }

    /// <summary>
    /// Map an Anthropic HTTP failure to one of the friendly strings the
    /// OpenAI-compat backend uses. Status codes are the same shape — the
    /// Anthropic SDK splits them into AuthenticationError / RateLimitError /
    /// BadRequestError / etc. classes, we classify by code instead.
    /// </summary>
    private string ClassifyHttpError(int status, string responseBody)
    {
        switch (status)
        {
            case 401:
            case 403:
                _log.LogError("LLM auth failed (HTTP {Code}): {Body}", status, responseBody);
                return "My API key was rejected by the LLM. "
                     + "Check api_key in config.yaml and restart me.";
            case 429:
                _log.LogError("LLM rate-limited: {Body}", responseBody);
                return "The LLM provider is rate-limiting me. Try again in a minute.";
            case 400:
            case 404:
            case 422:
                _log.LogError("LLM rejected request (HTTP {Code}): {Body}", status, responseBody);
                return "The LLM rejected my request - the model name or "
                     + "config is probably wrong. Check the log.";
            case 408:
            case 504:
                _log.LogError("LLM timeout (HTTP {Code}): {Body}", status, responseBody);
                return "The LLM took too long to respond. Try again.";
            default:
                _log.LogError("LLM HTTP {Code}: {Body}", status, responseBody);
                return $"The LLM returned HTTP {status}. Check the log for details.";
        }
    }

    public ValueTask DisposeAsync()
    {
        // Mirrors Python's `await self._client.close()` — HttpClient.Dispose
        // releases the underlying socket pool. Wrapped in a try in case the
        // client is already torn down (e.g. on cancellation during startup).
        try { _http.Dispose(); } catch { /* nothing to do */ }
        return ValueTask.CompletedTask;
    }
}
