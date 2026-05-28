using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.Core.Tools;

/// <summary>
/// Describes a tool as the LLM sees it. <see cref="Parameters"/> is a JSON
/// Schema object (https://json-schema.org/) — same as what the OpenAI /
/// Anthropic APIs expect for function-calling tools.
/// </summary>
public sealed record ToolSchema(
    string Name,
    string Description,
    JsonObject Parameters);

/// <summary>
/// One tool. Mirrors the Python <c>core/tools.py</c> Tool shape: a name, a
/// JSON schema for parameters, and an async invoker that returns a string
/// result (the LLM only ever sees strings).
/// </summary>
public interface ITool
{
    ToolSchema Schema { get; }

    /// <summary>
    /// Execute the tool. <paramref name="arguments"/> matches the schema's
    /// Parameters shape. Returns the string the LLM will see as the tool
    /// result. Should not throw — instead catch and return a clear error
    /// string ("Tool error: ...") so the LLM can react to it. The orchestrator
    /// will surface real exceptions as <see cref="BackendError"/> events.
    /// </summary>
    Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct);
}
