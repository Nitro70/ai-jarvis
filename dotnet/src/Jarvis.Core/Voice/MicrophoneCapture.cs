using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jarvis.Core.Voice;

// Port of the microphone-capture portion of voice/stt.py — specifically the
// sd.InputStream usage in wait_for_wake / record_command, plus check_microphone
// and list_input_devices. Whisper expects 16 kHz mono float32, so this class's
// job is to take whatever format the actual audio endpoint gives us (commonly
// 44.1/48 kHz, 16-bit or float, stereo) and hand the caller normalized
// 16 kHz mono float32 frames.
//
// WASAPI is Windows-only, hence the [SupportedOSPlatform] attribute. The Python
// version uses sounddevice (cross-platform via PortAudio); on .NET we lean on
// NAudio's WasapiCapture directly because PortAudio bindings on .NET aren't
// well-maintained.
//
// Format conversion strategy: WasapiCapture.WaveFormat reflects the device's
// shared-mode mix format. Setting it to a different rate/channel count is
// allowed but the WASAPI API mostly ignores it for capture — the device gives
// us its native format and we resample in-process. We always build a
// MediaFoundationResampler from the device format → 16 kHz mono IEEE float, so
// downstream code never has to think about it. If the device is already
// 16 kHz mono float, the resampler is effectively a passthrough.

/// <summary>
/// Captures microphone audio via WASAPI shared-mode and emits 16 kHz mono
/// float32 chunks suitable for Whisper. Mirrors the InputStream usage in the
/// Python <c>voice/stt.py</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneCapture : IDisposable
{
    public const int SampleRate = 16000;

    private readonly ILogger<MicrophoneCapture> _log;
    private readonly MMDeviceEnumerator _enumerator;

    // Active capture state — only one CaptureAsync stream may be running at a
    // time. We don't bother with locks because callers shouldn't be racing
    // this; Dispose just stops whatever's currently running.
    private WasapiCapture? _capture;
    private MediaFoundationResampler? _resampler;
    private BufferedWaveProvider? _inputBuffer;
    private Channel<float[]>? _channel;
    private bool _disposed;

    public MicrophoneCapture(ILogger<MicrophoneCapture> log)
    {
        _log = log;
        _enumerator = new MMDeviceEnumerator();
    }

    /// <summary>
    /// Friendly name of the default communications input device, or null if
    /// no input device is available / WASAPI enumeration fails.
    /// </summary>
    public string? DefaultInputDeviceName
    {
        get
        {
            try
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(
                    DataFlow.Capture, Role.Communications);
                return dev.FriendlyName;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "no default input device");
                return null;
            }
        }
    }

    /// <summary>
    /// Enumerate active input devices. The "index" is just the position in the
    /// returned list — WASAPI doesn't expose stable numeric indices the way
    /// PortAudio does, but the position is good enough for diagnostic output.
    /// </summary>
    public IReadOnlyList<(int Index, string Name)> ListInputDevices()
    {
        var result = new List<(int, string)>();
        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture, DeviceState.Active);
            for (var i = 0; i < devices.Count; i++)
            {
                using var d = devices[i];
                result.Add((i, d.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "failed to enumerate input devices");
        }
        return result;
    }

    /// <summary>
    /// Capture briefly from the default input device and return the peak
    /// |amplitude| in [0, 1] plus the device name. A live mic returns a
    /// non-zero peak even in 'silence' (self-noise). A muted/disabled/wrong
    /// device returns ~0. Mirrors Python's <c>check_microphone(duration)</c>.
    /// </summary>
    public async Task<(float Peak, string DeviceName)> CheckHealthAsync(
        double durationSeconds = 0.5, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var name = DefaultInputDeviceName ?? "(unknown)";

        // Use a private capture pipeline rather than CaptureAsync so the
        // health check doesn't interfere with (or get interfered with by) a
        // concurrent streaming capture.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds + 2.0));

        float peak = 0f;
        var done = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopAt = DateTime.UtcNow.AddSeconds(durationSeconds);

        WasapiCapture? capture = null;
        MediaFoundationResampler? resampler = null;
        BufferedWaveProvider? input = null;
        try
        {
            (capture, input, resampler) = BuildPipeline();

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                input!.AddSamples(e.Buffer, 0, e.BytesRecorded);
                DrainAndUpdatePeak(resampler!, ref peak);

                if (DateTime.UtcNow >= stopAt)
                    done.TrySetResult(true);
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    done.TrySetException(e.Exception);
                else
                    done.TrySetResult(true);
            };

            // Push StartRecording onto a background thread to avoid the
            // sync-context / apartment-state issues that can hang WASAPI
            // capture when called from a UI thread.
            await Task.Run(() => capture.StartRecording(), cts.Token)
                .ConfigureAwait(false);

            using (cts.Token.Register(() => done.TrySetResult(false)))
            {
                await done.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            try { capture?.StopRecording(); } catch { /* ignore */ }
            resampler?.Dispose();
            capture?.Dispose();
            // BufferedWaveProvider has no Dispose.
            _ = input;
        }

        return (peak, name);
    }

    /// <summary>
    /// Stream mic samples as 16 kHz mono float32 chunks. Each yielded array is
    /// one DataAvailable callback's worth of samples (typically 10-50 ms).
    /// Stops when the cancellation token is signalled or <see cref="Dispose"/>
    /// is called. Caller is responsible for accumulating chunks into a rolling
    /// buffer (see VoiceListener for the Python equivalent).
    /// </summary>
    public async IAsyncEnumerable<float[]> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_capture != null)
            throw new InvalidOperationException(
                "MicrophoneCapture is already streaming; only one CaptureAsync at a time.");

        var (capture, input, resampler) = BuildPipeline();
        var channel = Channel.CreateUnbounded<float[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        _capture = capture;
        _inputBuffer = input;
        _resampler = resampler;
        _channel = channel;

        capture.DataAvailable += (_, e) =>
        {
            try
            {
                if (e.BytesRecorded <= 0) return;
                input.AddSamples(e.Buffer, 0, e.BytesRecorded);

                // Drain the resampler in whatever-sized chunks it gives us.
                // 16 kHz mono float = 16000 samples/sec * 4 bytes = 64 KB/s,
                // so a 50 ms chunk is ~3200 bytes. Read in 8 KB blocks to
                // amortize the per-Read overhead.
                var staging = new byte[8192];
                while (true)
                {
                    int read = resampler.Read(staging, 0, staging.Length);
                    if (read <= 0) break;
                    int sampleCount = read / sizeof(float);
                    var floats = new float[sampleCount];
                    Buffer.BlockCopy(staging, 0, floats, 0, read);
                    channel.Writer.TryWrite(floats);
                    if (read < staging.Length) break;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "mic DataAvailable handler failed");
                channel.Writer.TryComplete(ex);
            }
        };

        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                _log.LogWarning(e.Exception, "WASAPI capture stopped with error");
            channel.Writer.TryComplete(e.Exception);
        };

        // Background-thread StartRecording — see CheckHealthAsync for why.
        await Task.Run(() => capture.StartRecording(), ct).ConfigureAwait(false);

        // Make sure the channel completes when the caller's cancellation
        // token fires, even if the WASAPI callback never produces another
        // sample (e.g. the device disappeared).
        using var ctReg = ct.Register(() =>
        {
            try { capture.StopRecording(); } catch { /* ignore */ }
            channel.Writer.TryComplete();
        });

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct)
                .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            // Clear streaming state so a subsequent CaptureAsync can start.
            // Dispose() may also race in here — both paths converge on
            // disposing the same objects, which is idempotent on NAudio.
            try { capture.StopRecording(); } catch { /* ignore */ }
            try { resampler.Dispose(); } catch { /* ignore */ }
            try { capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
            _resampler = null;
            _inputBuffer = null;
            _channel = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _capture?.StopRecording(); } catch { /* ignore */ }
        try { _channel?.Writer.TryComplete(); } catch { /* ignore */ }
        try { _resampler?.Dispose(); } catch { /* ignore */ }
        try { _capture?.Dispose(); } catch { /* ignore */ }
        try { _enumerator.Dispose(); } catch { /* ignore */ }

        _capture = null;
        _resampler = null;
        _inputBuffer = null;
        _channel = null;
    }

    /// <summary>
    /// Build a fresh capture → buffered-input → resampler pipeline for the
    /// default communications input device. The resampler always outputs
    /// 16 kHz mono IEEE float, regardless of what the device gives us.
    /// </summary>
    private (WasapiCapture Capture, BufferedWaveProvider Input,
        MediaFoundationResampler Resampler) BuildPipeline()
    {
        var device = _enumerator.GetDefaultAudioEndpoint(
            DataFlow.Capture, Role.Communications);

        // Don't set capture.WaveFormat — WASAPI shared-mode capture honors the
        // device's mix format and silently ignores our request. Better to take
        // what it gives us and resample explicitly so we know exactly what
        // happens.
        var capture = new WasapiCapture(device);
        var deviceFormat = capture.WaveFormat;
        _log.LogDebug("WASAPI capture device {Name} format: {Rate}Hz {Channels}ch {Bits}-bit {Encoding}",
            device.FriendlyName, deviceFormat.SampleRate, deviceFormat.Channels,
            deviceFormat.BitsPerSample, deviceFormat.Encoding);

        // BufferedWaveProvider sits between the WASAPI callback (which hands
        // us raw device-format bytes) and the MediaFoundationResampler (which
        // wants an IWaveProvider it can pull from). A 2-second buffer gives us
        // plenty of slack for short stalls without hiding real underruns.
        var input = new BufferedWaveProvider(deviceFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        var resampler = new MediaFoundationResampler(input, targetFormat)
        {
            // Quality 60 = highest. Mic audio is low bandwidth so the cost is
            // trivial, and dropping below default can introduce audible
            // aliasing that hurts Whisper accuracy.
            ResamplerQuality = 60,
        };

        return (capture, input, resampler);
    }

    /// <summary>
    /// Pull whatever's available from the resampler right now and update
    /// <paramref name="peak"/> with the new max |sample|. Used by
    /// CheckHealthAsync; CaptureAsync has its own inline drain because it
    /// also needs to publish the floats to the channel.
    /// </summary>
    private static void DrainAndUpdatePeak(
        MediaFoundationResampler resampler, ref float peak)
    {
        var staging = new byte[8192];
        var floats = new float[staging.Length / sizeof(float)];
        while (true)
        {
            int read = resampler.Read(staging, 0, staging.Length);
            if (read <= 0) break;
            int sampleCount = read / sizeof(float);
            Buffer.BlockCopy(staging, 0, floats, 0, read);
            for (int i = 0; i < sampleCount; i++)
            {
                var a = floats[i] < 0 ? -floats[i] : floats[i];
                if (a > peak) peak = a;
            }
            if (read < staging.Length) break;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MicrophoneCapture));
    }
}

