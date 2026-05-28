using System;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core;
using Jarvis.Core.Conversation;
using Jarvis.Core.Voice;
using Jarvis.Setup.NET.Models;
using Microsoft.Extensions.Logging;

namespace Jarvis.App;

/// <summary>
/// Owns the voice loop's lifecycle. Sits between the WPF chat window
/// (which toggles voice on/off via a mic button) and the
/// <see cref="VoiceListener"/> that does the actual mic + Whisper work.
///
/// Hybrid always-on loop matches Python's run_voice_mode in jarvis.py:
///   wait_for_wake() -> record_command() -> handle -> if always_on, loop
///   on record_command() with longer giveup, fall back to wait_for_wake()
///   after silence_seconds.
/// </summary>
public sealed class VoiceController : IAsyncDisposable
{
    private readonly InstallConfig _cfg;
    private readonly ConversationOrchestrator _orchestrator;
    private readonly ILogger<VoiceController> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IProgress<JarvisEvent> _events;
    private readonly string _installDir;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private VoiceListener? _listener;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public VoiceController(
        InstallConfig cfg,
        ConversationOrchestrator orchestrator,
        ILoggerFactory loggerFactory,
        IProgress<JarvisEvent> events,
        string installDir)
    {
        _cfg = cfg;
        _orchestrator = orchestrator;
        _log = loggerFactory.CreateLogger<VoiceController>();
        _loggerFactory = loggerFactory;
        _events = events;
        _installDir = installDir;
    }

    /// <summary>
    /// Spin up the voice loop. Idempotent — calling while already running
    /// is a no-op.
    /// </summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();

        _events.Report(new VoiceInfo(
            "Loading voice models (Whisper + Silero VAD) — first run downloads ~150 MB...",
            InfoSeverity.Info));

        try
        {
            // Load STT models + VAD + mic. These run in parallel where the
            // operations are independent (model files for wake + command are
            // different downloads).
            var statusLog = new Progress<string>(s =>
                _events.Report(new VoiceInfo(s, InfoSeverity.Info)));

            var wakeTask = WhisperStt.LoadAsync(
                _cfg.Voice.Stt.WakeModel,
                _loggerFactory.CreateLogger<WhisperStt>(),
                statusLog,
                _cts.Token);
            var cmdTask = WhisperStt.LoadAsync(
                _cfg.Voice.Stt.Model,
                _loggerFactory.CreateLogger<WhisperStt>(),
                statusLog,
                _cts.Token);
            var vadTask = SileroVad.LoadAsync(
                _loggerFactory.CreateLogger<SileroVad>(),
                statusLog,
                _cts.Token);

            await Task.WhenAll(wakeTask, cmdTask, vadTask);

            var wake = await wakeTask;
            var cmd = await cmdTask;
            var vad = await vadTask;
            var mic = new MicrophoneCapture(_loggerFactory.CreateLogger<MicrophoneCapture>());

            // Quick mic sanity check — same diagnostic value as Python's
            // check_microphone() at startup.
            var (peak, deviceName) = await mic.CheckHealthAsync(0.5, _cts.Token);
            if (peak <= 0)
            {
                _events.Report(new VoiceInfo(
                    $"⚠ Default input device '{deviceName}' produced silence. " +
                    "Check Windows Settings → System → Sound → Input.",
                    InfoSeverity.Warning));
            }
            else
            {
                _events.Report(new VoiceInfo(
                    $"✓ Microphone OK: '{deviceName}' (peak {peak:F3})",
                    InfoSeverity.Info));
            }

            _listener = new VoiceListener(
                wakeModel: wake,
                commandModel: cmd,
                vad: vad,
                mic: mic,
                wakeWords: _cfg.Voice.WakeWordVariants,
                silenceEndSeconds: _cfg.Voice.SilenceEndSeconds,
                maxCommandSeconds: _cfg.Voice.MaxCommandSeconds,
                commandGiveupSeconds: _cfg.Voice.CommandGiveupSeconds,
                log: _loggerFactory.CreateLogger<VoiceListener>());

            // Warm up the command model in the background so the first
            // post-wake transcription isn't slow.
            _ = Task.Run(async () =>
            {
                try { await cmd.WarmUpAsync(_cts.Token); }
                catch { /* warmup is best-effort */ }
            }, _cts.Token);

            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "voice startup failed");
            _events.Report(new BackendError(
                $"Voice mode failed to start: {ex.Message}", ex));
            await DisposeAsync();
        }
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_loopTask != null) await _loopTask; }
        catch (OperationCanceledException) { /* expected */ }
        _events.Report(new StateChanged(JarvisState.Idle));
        _events.Report(new VoiceInfo("(voice mode stopped)", InfoSeverity.Info));
    }

    /// <summary>The main loop. Wake → command(s) → loop.</summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        if (_listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _listener.WaitForWakeAsync(_events, ct);
                _events.Report(new VoiceInfo("✨ Yes, sir?", InfoSeverity.Info));

                bool isFollowUp = false;
                while (!ct.IsCancellationRequested)
                {
                    var giveup = isFollowUp ? _cfg.Voice.FollowUpSeconds : (double?)null;
                    if (isFollowUp)
                    {
                        _events.Report(new VoiceInfo(
                            $"🎙️  Listening for follow-up (~{(int)_cfg.Voice.FollowUpSeconds}s)...",
                            InfoSeverity.Info));
                    }
                    else
                    {
                        _events.Report(new VoiceInfo(
                            "🎙️  Listening for your command...",
                            InfoSeverity.Info));
                    }

                    var audio = await _listener.RecordCommandAsync(_events, giveup, ct);
                    if (audio.Length == 0)
                    {
                        _events.Report(new VoiceInfo(
                            isFollowUp
                                ? $"(quiet — back to listening for '{_cfg.Voice.WakeWord}'.)"
                                : "(I didn't catch anything — going back to listening.)",
                            InfoSeverity.Info));
                        break;
                    }

                    _events.Report(new StateChanged(JarvisState.Transcribing));
                    _events.Report(new VoiceInfo("⌛ Transcribing...", InfoSeverity.Info));
                    var text = await _listener.TranscribeCommandAsync(audio, ct);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _events.Report(new VoiceInfo("(silence — try again)", InfoSeverity.Info));
                        if (isFollowUp) break;  // give up; back to wake
                        continue;
                    }

                    // Hand to the conversation orchestrator. It emits UserMessage
                    // + JarvisToken events that the UI already knows how to render.
                    await _orchestrator.SendAsync(text, ct);

                    if (_cfg.Voice.AlwaysOn)
                    {
                        isFollowUp = true;
                        continue;
                    }
                    break;  // default: one command per wake
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "voice loop iteration failed");
                _events.Report(new VoiceInfo(
                    $"(voice error: {ex.Message} — restarting wake-word loop)",
                    InfoSeverity.Warning));
                await Task.Delay(1000, ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { }
        if (_listener != null)
        {
            await _listener.DisposeAsync();
            _listener = null;
        }
        _cts?.Dispose();
        _cts = null;
    }
}
