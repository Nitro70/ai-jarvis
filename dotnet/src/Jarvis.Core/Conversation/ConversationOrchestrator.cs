using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jarvis.Core.Llm;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Conversation;

/// <summary>
/// Top-level conversation driver. Owns the backend and exposes a unified
/// event stream to consumers (the WPF chat window subscribes here).
///
/// Mirrors the Python <c>run_text_mode</c> + <c>run_voice_mode</c> loops in
/// jarvis.py, but doesn't itself know about voice — that's layered in by a
/// future VoiceLoop class in Phase 3 that calls <see cref="SendAsync"/> for
/// each transcribed command. For Phase 1 (text only) the WPF input box
/// calls SendAsync directly when the user hits Enter.
/// </summary>
public sealed class ConversationOrchestrator : IAsyncDisposable
{
    private readonly ILlmBackend _backend;
    private readonly ITtsSink? _tts;
    private readonly ILogger<ConversationOrchestrator> _log;
    private readonly Channel<JarvisEvent> _events = Channel.CreateUnbounded<JarvisEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ConversationOrchestrator(
        ILlmBackend backend,
        ILogger<ConversationOrchestrator> log,
        ITtsSink? tts = null)
    {
        _backend = backend;
        _log = log;
        _tts = tts;
    }

    /// <summary>Consume all events emitted by the conversation.</summary>
    public IAsyncEnumerable<JarvisEvent> Events => _events.Reader.ReadAllAsync();

    /// <summary>
    /// External producers (the voice loop, lifecycle hooks, etc.) can push
    /// events into the same stream the UI subscribes to via this hook. Fire
    /// and forget; never throws — if the channel is closed (during shutdown)
    /// the event is silently dropped.
    /// </summary>
    public void Emit(JarvisEvent ev)
    {
        try { _events.Writer.TryWrite(ev); } catch { /* shutting down */ }
    }

    /// <summary>
    /// Send one user message and pump the resulting events. Returns when the
    /// reply is complete (or errored). UI calls this from the input handler.
    /// </summary>
    public async Task SendAsync(string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;

        _log.LogInformation("USER: {Text}", userText);
        await _events.Writer.WriteAsync(new UserMessage(userText), ct);
        await _events.Writer.WriteAsync(new StateChanged(JarvisState.AwaitingReply), ct);

        // Events progress sink — backends use this to surface tool calls,
        // tool results, errors. We forward to the same channel.
        var progress = new Progress<JarvisEvent>(async e =>
        {
            try { await _events.Writer.WriteAsync(e, ct); }
            catch (ChannelClosedException) { /* shutting down */ }
        });

        var buffer = new System.Text.StringBuilder();
        try
        {
            await foreach (var token in _backend.SendAsync(userText, progress, ct))
            {
                buffer.Append(token);
                await _events.Writer.WriteAsync(new JarvisToken(token), ct);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("send cancelled by user");
            await _events.Writer.WriteAsync(
                new VoiceInfo("(cancelled)", InfoSeverity.Info), ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "backend send failed");
            await _events.Writer.WriteAsync(
                new BackendError("Something went wrong talking to the LLM. Check jarvis.log.", ex),
                ct);
        }
        finally
        {
            var full = buffer.ToString().Trim();
            if (!string.IsNullOrEmpty(full))
            {
                _log.LogInformation("JARVIS: {Reply}", full);
                await _events.Writer.WriteAsync(new JarvisReplyComplete(full), ct);

                // TTS — fire-and-forget'ish: we await so Speaking state is
                // accurate, but a TTS failure must NOT cancel the conversation.
                // EdgeTtsSink already swallows non-cancellation exceptions
                // internally; we still guard here so any future ITtsSink that
                // forgets to do that doesn't crash the loop.
                if (_tts != null)
                {
                    await _events.Writer.WriteAsync(new StateChanged(JarvisState.Speaking), ct);
                    try
                    {
                        await _tts.SpeakAsync(full, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "TTS playback failed (continuing)");
                    }
                }
            }
            await _events.Writer.WriteAsync(new StateChanged(JarvisState.Idle), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _backend.DisposeAsync();
    }
}
