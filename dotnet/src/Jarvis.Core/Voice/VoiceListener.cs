using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Voice;

/// <summary>
/// The voice loop. Combines mic capture + Silero VAD + Whisper STT into
/// Python's <c>VoiceListener.wait_for_wake</c> + <c>record_command</c> +
/// always-on follow-up pattern.
///
/// Public surface mirrors Python's: one entry point per loop iteration —
/// caller awaits <see cref="WaitForWakeAsync"/>, then awaits
/// <see cref="RecordCommandAsync"/>. The always-on hybrid loop (re-record
/// with longer giveup after each reply) is built by the caller, same as
/// Python's run_voice_mode.
/// </summary>
public sealed class VoiceListener : IAsyncDisposable
{
    private readonly WhisperStt _wakeModel;
    private readonly WhisperStt _commandModel;
    private readonly SileroVad _vad;
    private readonly MicrophoneCapture _mic;
    private readonly ILogger<VoiceListener> _log;
    private readonly IReadOnlyList<string> _wakeWordsLower;
    private readonly double _silenceEndSeconds;
    private readonly double _maxCommandSeconds;
    private readonly double _commandGiveupSeconds;
    private readonly double _wakeWindowSeconds;
    private readonly double _wakeCheckIntervalSeconds;

    public VoiceListener(
        WhisperStt wakeModel,
        WhisperStt commandModel,
        SileroVad vad,
        MicrophoneCapture mic,
        IEnumerable<string> wakeWords,
        double silenceEndSeconds,
        double maxCommandSeconds,
        double commandGiveupSeconds,
        ILogger<VoiceListener> log,
        double wakeWindowSeconds = 2.0,
        double wakeCheckIntervalSeconds = 0.3)
    {
        _wakeModel = wakeModel;
        _commandModel = commandModel;
        _vad = vad;
        _mic = mic;
        _wakeWordsLower = wakeWords.Select(w => w.ToLowerInvariant()).ToList();
        _silenceEndSeconds = silenceEndSeconds;
        _maxCommandSeconds = maxCommandSeconds;
        _commandGiveupSeconds = commandGiveupSeconds;
        _log = log;
        _wakeWindowSeconds = wakeWindowSeconds;
        _wakeCheckIntervalSeconds = wakeCheckIntervalSeconds;
    }

    /// <summary>
    /// Block until any configured wake-word variant is heard. Emits
    /// VoiceInfo events via <paramref name="events"/> so the UI can render
    /// "🎤 Listening for jarvis...", "...heard: ...", and the no-audio
    /// warning the same way Python's terminal output does.
    /// </summary>
    public async Task WaitForWakeAsync(
        IProgress<JarvisEvent>? events,
        CancellationToken ct)
    {
        events?.Report(new StateChanged(JarvisState.ListeningForWakeWord));
        events?.Report(new VoiceInfo(
            $"🎤 Listening for '{_wakeWordsLower[0]}'...",
            InfoSeverity.Info));

        var sampleRate = MicrophoneCapture.SampleRate;
        var windowSamples = (int)(_wakeWindowSeconds * sampleRate);
        var maxBufferSamples = (int)(sampleRate * (_wakeWindowSeconds + 1.0));
        var buffer = new List<float>(capacity: maxBufferSamples);

        var startTime = Stopwatch.StartNew();
        var lastCheck = TimeSpan.Zero;
        bool everHeardAudio = false;
        bool warnedNoAudio = false;

        await foreach (var chunk in _mic.CaptureAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            // Append, then keep only the trailing maxBuffer samples (cheap-ish
            // — chunks are small, copies happen ~10×/sec).
            buffer.AddRange(chunk);
            if (buffer.Count > maxBufferSamples)
                buffer.RemoveRange(0, buffer.Count - maxBufferSamples);

            if (!everHeardAudio)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (Math.Abs(chunk[i]) > 1e-4f) { everHeardAudio = true; break; }
                }
            }

            // Warn once if we've gone 4+ seconds without any non-zero audio.
            // Means the mic is wrong / muted / no permission.
            if (!warnedNoAudio && !everHeardAudio && startTime.Elapsed.TotalSeconds > 4.0)
            {
                events?.Report(new VoiceInfo(
                    "⚠ No audio detected from your microphone yet — check Windows Sound " +
                    "settings (Input device, mic permission, mute switch).",
                    InfoSeverity.Warning));
                warnedNoAudio = true;
            }

            // Throttle the check loop: only run VAD + Whisper every wake_check_interval.
            if ((startTime.Elapsed - lastCheck).TotalSeconds < _wakeCheckIntervalSeconds)
                continue;
            lastCheck = startTime.Elapsed;

            // Not enough audio buffered yet.
            if (buffer.Count < sampleRate * 0.7) continue;

            // Slice the most recent windowSamples.
            var window = buffer.Count >= windowSamples
                ? buffer.GetRange(buffer.Count - windowSamples, windowSamples).ToArray()
                : buffer.ToArray();

            if (!_vad.HasSpeech(window)) continue;

            string text;
            try
            {
                text = await _wakeModel.TranscribeAsync(window, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _log.LogWarning(e, "wake-word transcribe failed; skipping window");
                continue;
            }

            if (string.IsNullOrWhiteSpace(text)) continue;
            _log.LogDebug("wake-listen heard: {Text}", text);

            var textLower = text.ToLowerInvariant();
            if (_wakeWordsLower.Any(w => textLower.Contains(w)))
            {
                _log.LogInformation("wake word detected in: {Text}", text);
                return;
            }
            // Echo what Whisper heard so the user can tell their mic is working
            // and what (if anything) was missing in the wake-word match.
            events?.Report(new VoiceInfo($"   …heard: {text}", InfoSeverity.Info));
        }
    }

    /// <summary>
    /// Record from the mic until silence-after-speech, hard cap, or giveup.
    /// Returns the raw samples (16kHz mono float). Caller passes to Whisper
    /// for transcription.
    /// </summary>
    /// <param name="giveupSeconds">
    /// Override for the "no speech detected, give up" timeout. Used by the
    /// always-on hybrid follow-up loop (typically 30s instead of the 6s
    /// default for the initial post-wake command).
    /// </param>
    public async Task<float[]> RecordCommandAsync(
        IProgress<JarvisEvent>? events,
        double? giveupSeconds,
        CancellationToken ct)
    {
        events?.Report(new StateChanged(JarvisState.ListeningForCommand));

        var sampleRate = MicrophoneCapture.SampleRate;
        var maxSamples = (int)(_maxCommandSeconds * sampleRate);
        var effectiveGiveup = giveupSeconds ?? _commandGiveupSeconds;
        var buffer = new List<float>(capacity: maxSamples);
        bool speechEver = false;
        var start = Stopwatch.StartNew();
        var lastCheck = TimeSpan.Zero;
        const double checkIntervalSeconds = 0.25;

        await foreach (var chunk in _mic.CaptureAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            buffer.AddRange(chunk);

            // Hard cap: never record more than max_command_seconds.
            if (start.Elapsed.TotalSeconds > _maxCommandSeconds)
            {
                _log.LogInformation("command recording: max duration");
                break;
            }

            if ((start.Elapsed - lastCheck).TotalSeconds < checkIntervalSeconds)
                continue;
            lastCheck = start.Elapsed;

            var duration = buffer.Count / (double)sampleRate;
            if (duration < 0.5) continue;

            var window = buffer.ToArray();
            var lastSpeechEnd = _vad.LastSpeechEndSeconds(window);
            if (lastSpeechEnd == null)
            {
                if (start.Elapsed.TotalSeconds > effectiveGiveup)
                {
                    _log.LogInformation("command recording: no speech in {Elapsed}s",
                        start.Elapsed.TotalSeconds);
                    break;
                }
                continue;
            }

            speechEver = true;
            var silence = duration - lastSpeechEnd.Value;
            if (silence >= _silenceEndSeconds)
            {
                _log.LogInformation("command recording: silence {Silence:F2}s ≥ {End:F2}s",
                    silence, _silenceEndSeconds);
                // Trim to the end of the last detected speech + 300ms tail.
                var endSample = (int)((lastSpeechEnd.Value + 0.3) * sampleRate);
                if (endSample > buffer.Count) endSample = buffer.Count;
                return buffer.GetRange(0, endSample).ToArray();
            }
        }

        if (!speechEver) return Array.Empty<float>();
        return buffer.ToArray();
    }

    /// <summary>
    /// Transcribe the audio returned by <see cref="RecordCommandAsync"/>.
    /// Caller is expected to emit <see cref="JarvisState.Transcribing"/>
    /// before invoking (so the UI status bar updates before the slow call).
    /// </summary>
    public async Task<string> TranscribeCommandAsync(float[] audio, CancellationToken ct)
    {
        if (audio.Length == 0) return string.Empty;
        return await _commandModel.TranscribeAsync(audio, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _mic.Dispose();
        _vad.Dispose();
        await _wakeModel.DisposeAsync();
        if (!ReferenceEquals(_wakeModel, _commandModel))
            await _commandModel.DisposeAsync();
    }
}
