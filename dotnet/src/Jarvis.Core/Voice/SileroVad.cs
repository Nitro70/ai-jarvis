using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jarvis.Core.Voice;

// Port of the VAD probes in voice/stt.py (`_has_speech` and
// `_last_speech_end_seconds`). Python uses faster_whisper.vad, which
// internally bundles the Silero VAD ONNX model and runs a simple
// threshold-with-min-duration filter on top of it. We do the same here
// against the upstream Silero ONNX so we don't pull in faster-whisper just
// for VAD.
//
// The model is stateful: it takes a 512-sample window (32ms @ 16kHz) plus a
// hidden state [2, 1, 128] and an int64 sample-rate input. The state must
// carry between successive windows of the same utterance.
//
// We deliberately keep the segmentation algorithm dead simple (probability
// >= threshold opens a segment, probability < (threshold - hysteresis) for
// a sustained run closes it, segments shorter than min-duration are dropped)
// — matching the *behaviour* of faster_whisper's bundled VAD, not its exact
// byte-for-byte output. The original requirement was just "did the user say
// anything?", which this answers reliably.

/// <summary>
/// Half-open [StartSample, EndSample) range of speech in a 16 kHz audio buffer.
/// </summary>
public readonly record struct SpeechRange(int StartSample, int EndSample)
{
    public double StartSeconds => StartSample / (double)SileroVad.SampleRate;
    public double EndSeconds => EndSample / (double)SileroVad.SampleRate;
}

/// <summary>
/// ONNX Runtime wrapper around the upstream Silero VAD model
/// (<see href="https://github.com/snakers4/silero-vad"/>).
///
/// Mirrors the cheap speech-probe used by <c>voice/stt.py</c>: only run
/// Whisper on chunks that actually contain speech, so CPU stays near idle
/// when nothing's happening.
/// </summary>
public sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16000;

    // Silero VAD ONNX shape constants.
    private const int WindowSamples = 512;       // 32 ms @ 16 kHz
    private const int StateRank0 = 2;
    private const int StateRank2 = 128;

    // Hysteresis between "open" and "close" thresholds, matching Silero's
    // recommended default. Probability must drop this far below `threshold`
    // (and stay there) before we close a speech segment.
    private const double CloseThresholdHysteresis = 0.15;

    // ~96 ms of below-threshold audio (3 windows) before we close a segment.
    // Smaller → choppy segmentation across word boundaries; larger → trailing
    // silence gets attached to speech, which inflates `LastSpeechEndSeconds`.
    private const int MinSilenceWindowsToClose = 3;

    private const string DownloadUrl =
        "https://github.com/snakers4/silero-vad/raw/master/files/silero_vad.onnx";

    private readonly InferenceSession _session;
    private readonly ILogger<SileroVad> _log;

    private SileroVad(InferenceSession session, ILogger<SileroVad> log)
    {
        _session = session;
        _log = log;
    }

    /// <summary>
    /// Download the Silero ONNX model on first use (cached under
    /// <c>%USERPROFILE%/.cache/jarvis-net/</c>) and open an ONNX Runtime
    /// session over it. If the cached file is corrupt the loader deletes and
    /// re-downloads once before giving up — same recovery pattern as the
    /// Python Whisper helper.
    /// </summary>
    public static async Task<SileroVad> LoadAsync(
        ILogger<SileroVad> log,
        IProgress<string>? statusLog = null,
        CancellationToken ct = default)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "jarvis-net");
        Directory.CreateDirectory(cacheDir);
        var modelPath = Path.Combine(cacheDir, "silero_vad.onnx");

        // First attempt: download if missing, try to open.
        await EnsureModelDownloadedAsync(modelPath, statusLog, log, ct).ConfigureAwait(false);
        try
        {
            return OpenSession(modelPath, log);
        }
        catch (Exception ex)
        {
            // Corrupt download — wipe and try once more, matching the Python
            // helper's behaviour for bad faster-whisper model files.
            log.LogWarning(ex, "Silero VAD model at {Path} failed to load; deleting and retrying", modelPath);
            statusLog?.Report("Silero VAD model corrupt; re-downloading...");
            try { File.Delete(modelPath); } catch { /* best-effort */ }

            await EnsureModelDownloadedAsync(modelPath, statusLog, log, ct).ConfigureAwait(false);
            return OpenSession(modelPath, log);
        }
    }

    private static async Task EnsureModelDownloadedAsync(
        string modelPath,
        IProgress<string>? statusLog,
        ILogger<SileroVad> log,
        CancellationToken ct)
    {
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length > 0)
            return;

        statusLog?.Report($"Downloading Silero VAD model (~1.7 MB) to {modelPath}...");
        log.LogInformation("downloading Silero VAD model to {Path}", modelPath);

        using var http = new HttpClient
        {
            // The file is ~1.7 MB; a generous timeout still catches dead links.
            Timeout = TimeSpan.FromMinutes(2),
        };

        // Write to a temp file in the same dir, then atomic-rename so a
        // mid-download abort never leaves a half-written model that the
        // next run would try to open.
        var tmp = modelPath + ".part";
        try
        {
            await using (var src = await http.GetStreamAsync(DownloadUrl, ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            File.Move(tmp, modelPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }

        statusLog?.Report("Silero VAD model downloaded.");
    }

    private static SileroVad OpenSession(string modelPath, ILogger<SileroVad> log)
    {
        // VAD is tiny (~1.7 MB, runs in ~microseconds per window). Threading
        // overhead easily exceeds the inference cost, so pin to a single op
        // thread — matches Silero's own recommendation.
        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
        };
        try
        {
            var session = new InferenceSession(modelPath, sessionOptions);
            return new SileroVad(session, log);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns the list of detected speech segments, in audio order.
    /// </summary>
    /// <remarks>
    /// Mirrors the contract of <c>faster_whisper.vad.get_speech_timestamps</c>
    /// as used in <c>voice/stt.py</c> (threshold 0.5, min duration 200ms).
    /// Returns an empty list for buffers shorter than ~250ms.
    /// </remarks>
    public IReadOnlyList<SpeechRange> GetSpeechTimestamps(
        ReadOnlySpan<float> audio,
        double threshold = 0.5,
        int minSpeechDurationMs = 200)
    {
        // Match Python's early return in _has_speech: nothing useful to say
        // about a sub-250ms clip; Silero needs at least a few windows of
        // context to settle.
        if (audio.Length < SampleRate / 4)
            return Array.Empty<SpeechRange>();

        var minSpeechSamples = (int)(minSpeechDurationMs / 1000.0 * SampleRate);
        var closeThreshold = Math.Max(0.0, threshold - CloseThresholdHysteresis);

        // Hidden state, carried window-to-window. Shape [2, 1, 128] of float.
        // (Older Silero builds called these h/c separately; the current ONNX
        // model bundles them into a single tensor.)
        var state = new float[StateRank0 * 1 * StateRank2];
        var windowBuffer = new float[WindowSamples];

        var segments = new List<SpeechRange>();
        int? speechStart = null;
        int silenceRun = 0;
        int? tentativeEnd = null;

        // Stride by exactly one window; the upstream model is trained for
        // contiguous 512-sample chunks at 16 kHz.
        var numWindows = audio.Length / WindowSamples;
        for (int w = 0; w < numWindows; w++)
        {
            var offset = w * WindowSamples;
            audio.Slice(offset, WindowSamples).CopyTo(windowBuffer);

            var prob = RunWindow(windowBuffer, state);

            if (prob >= threshold)
            {
                speechStart ??= offset;
                silenceRun = 0;
                tentativeEnd = null;
            }
            else if (speechStart is int startSample)
            {
                // Track the first below-threshold window as the candidate
                // end; only commit it once silence persists.
                tentativeEnd ??= offset;
                if (prob < closeThreshold)
                {
                    silenceRun++;
                    if (silenceRun >= MinSilenceWindowsToClose)
                    {
                        var endSample = tentativeEnd ?? offset;
                        if (endSample - startSample >= minSpeechSamples)
                            segments.Add(new SpeechRange(startSample, endSample));
                        speechStart = null;
                        silenceRun = 0;
                        tentativeEnd = null;
                    }
                }
                else
                {
                    // In the hysteresis band: not speech, not committed
                    // silence. Don't accumulate silenceRun.
                    silenceRun = 0;
                }
            }
        }

        // Flush an open segment that ran to the end of the buffer.
        if (speechStart is int openStart)
        {
            var endSample = audio.Length;
            if (endSample - openStart >= minSpeechSamples)
                segments.Add(new SpeechRange(openStart, endSample));
        }

        return segments;
    }

    /// <summary>
    /// True if any speech segment is detected. Equivalent to Python's
    /// <c>_has_speech()</c> in <c>voice/stt.py</c>.
    /// </summary>
    public bool HasSpeech(
        ReadOnlySpan<float> audio,
        double threshold = 0.5,
        int minSpeechDurationMs = 200)
    {
        // We could short-circuit on the first detected segment for speed,
        // but the segmentation pass is already cheap (~1 ms per second of
        // audio on a laptop CPU) and reusing one code path keeps behaviour
        // identical between HasSpeech / LastSpeechEndSeconds / GetSpeechTimestamps.
        return GetSpeechTimestamps(audio, threshold, minSpeechDurationMs).Count > 0;
    }

    /// <summary>
    /// End timestamp (seconds) of the last detected speech segment, or
    /// <c>null</c> if no speech. Equivalent to Python's
    /// <c>_last_speech_end_seconds()</c> in <c>voice/stt.py</c>.
    /// </summary>
    public double? LastSpeechEndSeconds(
        ReadOnlySpan<float> audio,
        double threshold = 0.5,
        int minSpeechDurationMs = 200)
    {
        var segs = GetSpeechTimestamps(audio, threshold, minSpeechDurationMs);
        if (segs.Count == 0) return null;
        return segs[^1].EndSeconds;
    }

    // Runs the ONNX model on one 512-sample window. Mutates `state` in place
    // with the new hidden state, returns the speech probability for this
    // window. Kept private — callers should go through the segmentation API
    // so they get consistent thresholding behaviour.
    private float RunWindow(float[] window, float[] state)
    {
        var inputTensor = new DenseTensor<float>(window, new[] { 1, WindowSamples });
        var stateTensor = new DenseTensor<float>(state, new[] { StateRank0, 1, StateRank2 });
        var srTensor = new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor),
        };

        using var results = _session.Run(inputs);

        float prob = 0f;
        foreach (var r in results)
        {
            // Output names in the current Silero ONNX are "output" (prob)
            // and "stateN" (new state). We match by tensor shape rather than
            // name so a future model rename doesn't silently break us.
            if (r.Value is not DenseTensor<float> t) continue;
            if (t.Dimensions.Length == 2 && t.Dimensions[0] == 1 && t.Dimensions[1] == 1)
            {
                prob = t.GetValue(0);
            }
            else if (t.Dimensions.Length == 3 &&
                     t.Dimensions[0] == StateRank0 &&
                     t.Dimensions[1] == 1 &&
                     t.Dimensions[2] == StateRank2)
            {
                // Copy the updated hidden state back into our carry buffer.
                var span = t.Buffer.Span;
                span.CopyTo(state);
            }
        }
        return prob;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
