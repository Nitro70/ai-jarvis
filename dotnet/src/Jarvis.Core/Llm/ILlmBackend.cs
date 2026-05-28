using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Llm;

/// <summary>
/// One LLM backend. There's one implementation per supported provider:
///   - <see cref="OpenAiCompatBackend"/> for OpenAI/Groq/xAI/Ollama/LM Studio
///   - ClaudeApiBackend (later phase) for direct Anthropic API
///   - ClaudeAgentBackend (later phase) for shell-out to Claude Code CLI
///
/// Mirrors the Python <c>llm/base.py</c> contract. The backend owns its own
/// conversation history; the orchestrator just feeds user messages in via
/// <see cref="SendAsync"/> and receives streamed assistant tokens out.
///
/// Implementations MUST be cancellation-aware — the user can hit "stop" or
/// close the window mid-stream.
/// </summary>
public interface ILlmBackend : IAsyncDisposable
{
    /// <summary>
    /// Send a user message and stream the assistant's reply back as tokens.
    /// If the backend supports tools (most do unless config.disable_tools=true),
    /// it will execute tool calls internally via the orchestrator's
    /// <see cref="ToolRegistry"/> and continue the conversation until a
    /// non-tool-call response is reached. Tool execution events are reported
    /// via the <paramref name="events"/> sink.
    /// </summary>
    IAsyncEnumerable<string> SendAsync(
        string userText,
        IProgress<JarvisEvent> events,
        CancellationToken ct);
}
