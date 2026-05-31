using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Mcp;

/// <summary>
/// A minimal Model Context Protocol (MCP) server over stdio. Exposes a
/// <see cref="ToolRegistry"/> as MCP tools so Claude Code (the claude_agent
/// backend) can call Jarvis's own tools — play_music, open_app, etc.
///
/// This is the C# equivalent of the Python edition's
/// <c>create_sdk_mcp_server(name="jarvis", tools=...)</c>. The Python build
/// runs the MCP server in-process via the SDK; we run it as a separate
/// process (Jarvis-NET.exe launched with --mcp-server) that the claude CLI
/// connects to via --mcp-config.
///
/// Transport: newline-delimited JSON-RPC 2.0 over stdin/stdout, per the MCP
/// stdio transport spec. One JSON message per line, UTF-8, no embedded
/// newlines in a message.
/// </summary>
public sealed class McpStdioServer
{
    private readonly ToolRegistry _tools;
    private readonly ILogger _log;
    private const string ServerName = "jarvis";
    private const string ServerVersion = "1.0.0";
    // Fallback protocol version if the client doesn't send one. We echo the
    // client's version back when it does, which is the most compatible move.
    private const string DefaultProtocolVersion = "2024-11-05";

    public McpStdioServer(ToolRegistry tools, ILogger log)
    {
        _tools = tools;
        _log = log;
    }

    /// <summary>
    /// Run the read/dispatch/write loop until stdin closes (EOF) or the token
    /// is cancelled. Reads from <paramref name="input"/>, writes responses to
    /// <paramref name="output"/>. Both should be the process's redirected
    /// stdin/stdout when invoked by the claude CLI.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct)
    {
        _log.LogInformation("MCP stdio server starting ({Count} tools)", _tools.AllSchemas.Count);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;  // pipe closed
            }
            if (line is null) break;            // EOF — client disconnected
            if (line.Length == 0) continue;     // keep-alive blank line

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException e)
            {
                _log.LogWarning(e, "MCP: unparseable line: {Line}", Trunc(line, 200));
                continue;  // can't even parse — skip (can't form a proper error reply without an id)
            }
            if (request is not JsonObject reqObj) continue;

            // Notifications have no id and expect no response.
            var hasId = reqObj.TryGetPropertyValue("id", out var idNode);
            var method = reqObj["method"]?.GetValue<string>();

            if (method is null)
            {
                if (hasId) await WriteErrorAsync(output, idNode, -32600, "Invalid Request: no method", ct);
                continue;
            }

            // notifications/* and other id-less messages: process side effects, no reply.
            if (!hasId)
            {
                _log.LogDebug("MCP notification: {Method}", method);
                continue;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        await HandleInitializeAsync(output, idNode, reqObj, ct);
                        break;
                    case "tools/list":
                        await HandleToolsListAsync(output, idNode, ct);
                        break;
                    case "tools/call":
                        await HandleToolsCallAsync(output, idNode, reqObj, ct);
                        break;
                    case "ping":
                        await WriteResultAsync(output, idNode, new JsonObject(), ct);
                        break;
                    default:
                        await WriteErrorAsync(output, idNode, -32601, $"Method not found: {method}", ct);
                        break;
                }
            }
            catch (Exception e)
            {
                _log.LogError(e, "MCP: handler for {Method} threw", method);
                await WriteErrorAsync(output, idNode, -32603, $"Internal error: {e.Message}", ct);
            }
        }
        _log.LogInformation("MCP stdio server stopped");
    }

    private async Task HandleInitializeAsync(TextWriter output, JsonNode? id, JsonObject req, CancellationToken ct)
    {
        // Echo the client's protocolVersion when present — maximizes compat.
        var clientProto = req["params"]?["protocolVersion"]?.GetValue<string>();
        var proto = string.IsNullOrEmpty(clientProto) ? DefaultProtocolVersion : clientProto;

        var result = new JsonObject
        {
            ["protocolVersion"] = proto,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),  // we support tools (list + call)
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
        await WriteResultAsync(output, id, result, ct);
    }

    private async Task HandleToolsListAsync(TextWriter output, JsonNode? id, CancellationToken ct)
    {
        var toolsArray = new JsonArray();
        foreach (var schema in _tools.AllSchemas)
        {
            toolsArray.Add(new JsonObject
            {
                ["name"] = schema.Name,
                ["description"] = schema.Description,
                // MCP calls it inputSchema; our ToolSchema.Parameters IS the
                // JSON Schema object. Deep-clone so we don't reparent a node.
                ["inputSchema"] = schema.Parameters.DeepClone(),
            });
        }
        await WriteResultAsync(output, id, new JsonObject { ["tools"] = toolsArray }, ct);
    }

    private async Task HandleToolsCallAsync(TextWriter output, JsonNode? id, JsonObject req, CancellationToken ct)
    {
        var name = req["params"]?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            await WriteErrorAsync(output, id, -32602, "Invalid params: missing tool name", ct);
            return;
        }
        if (!_tools.TryGet(name, out var tool) || tool is null)
        {
            await WriteErrorAsync(output, id, -32602, $"Unknown tool: {name}", ct);
            return;
        }

        // arguments is an object (may be absent for no-arg tools).
        var argsNode = req["params"]?["arguments"];
        var args = argsNode is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();

        string resultText;
        bool isError = false;
        try
        {
            resultText = await tool.InvokeAsync(args, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log.LogError(e, "MCP: tool {Tool} threw", name);
            resultText = $"Tool error: {e.Message}";
            isError = true;
        }

        // MCP tools/call result shape: { content: [{type:"text", text:...}], isError? }
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = resultText ?? "" },
            },
            ["isError"] = isError,
        };
        await WriteResultAsync(output, id, result, ct);
    }

    // ===================================================================
    //  JSON-RPC framing helpers
    // ===================================================================
    private static async Task WriteResultAsync(TextWriter output, JsonNode? id, JsonNode result, CancellationToken ct)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
        await WriteLineAsync(output, msg, ct);
    }

    private static async Task WriteErrorAsync(TextWriter output, JsonNode? id, int code, string message, CancellationToken ct)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        await WriteLineAsync(output, msg, ct);
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private static async Task WriteLineAsync(TextWriter output, JsonObject msg, CancellationToken ct)
    {
        var json = msg.ToJsonString(Compact);
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await output.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally { WriteLock.Release(); }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
}
