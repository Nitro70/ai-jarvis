using System;

namespace Jarvis.Core;

/// <summary>
/// Events surfaced by Jarvis.Core. The UI subscribes via
/// <see cref="ConversationOrchestrator.Events"/> and renders them.
///
/// Designed as a discriminated union (sealed records inheriting from a
/// common abstract base) so the UI can pattern-match exhaustively.
/// </summary>
public abstract record JarvisEvent;

/// <summary>State machine snapshot — UI uses this to drive the status bar.</summary>
public enum JarvisState
{
    Idle,
    ListeningForWakeWord,
    ListeningForCommand,
    Transcribing,
    AwaitingReply,
    Speaking,
}

public sealed record StateChanged(JarvisState State) : JarvisEvent;

/// <summary>The user spoke or typed something — this is the full message.</summary>
public sealed record UserMessage(string Text) : JarvisEvent;

/// <summary>A single token streamed from the LLM. UI appends to current Jarvis line.</summary>
public sealed record JarvisToken(string Token) : JarvisEvent;

/// <summary>The full Jarvis reply has been received. Token stream is complete.</summary>
public sealed record JarvisReplyComplete(string FullText) : JarvisEvent;

public enum InfoSeverity { Info, Warning, Error }

/// <summary>
/// Side-channel info from the voice loop or backend (e.g. "Listening for
/// jarvis...", "...heard: 'something'", "No audio detected"). Rendered as
/// muted italic lines in the chat log.
/// </summary>
public sealed record VoiceInfo(string Message, InfoSeverity Severity) : JarvisEvent;

/// <summary>A tool was invoked. UI can render as a small inline chip.</summary>
public sealed record ToolInvoked(string ToolName, string ArgumentsJson) : JarvisEvent;

/// <summary>The most recent tool call returned. Result is short — most tools
/// keep it under a few hundred chars.</summary>
public sealed record ToolResult(string ToolName, string Result) : JarvisEvent;

/// <summary>The backend failed in a user-actionable way. UserMessage is the
/// friendly version; Exception is the raw cause for the log.</summary>
public sealed record BackendError(string UserMessage, Exception? Exception) : JarvisEvent;
