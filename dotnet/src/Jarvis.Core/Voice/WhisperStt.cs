using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace Jarvis.Core.Voice;

// Port of voice/stt.py's transcribe() + jarvis.py's _load_whisper_with_recovery.
//
// Two key differences from the Python edition:
//
//  1) faster-whisper (Python) uses CTranslate2 models from
//     "Systran/faster-whisper-{size}". whisper.cpp / Whisper.net uses GGML
//     models from "ggerganov/whisper.cpp" — a different on-disk format
//     entirely, so we cannot share the Python cache. We download our own
//     ggml-{size}.bin alongside (not into) the Python cache.
//
//  2) Python uses huggingface_hub for download + caching. Whisper.net has no
//     such helper, so we do it by hand with HttpClient streaming so the user
//     gets live progress instead of a long silent stall.
//
// Cache-recovery rationale: if a previous run was Ctrl+C'd mid-download, the
// partial .bin file on disk corrupts the next WhisperFactory.FromPath() call.
// Detect that, delete, retry once. Same recipe as Python's
// _load_whisper_with_recovery.

/// <summary>
/// Speech-to-text wrapper around Whisper.net (whisper.cpp bindings). Owns
/// the GGML model file (download + cache) and a long-lived WhisperFactory +
/// WhisperProcessor so transcriptions don't pay model-load cost per call.
///
/// English-only (<c>WithLanguage("en")</c>) — matches the Python edition,
/// which hard-codes <c>language="en"</c> for both wake-word and command
/// transcription.
/// </summary>
public sealed class WhisperStt : IAsyncDisposable
{
    // Whisper expects 16 kHz mono float32 PCM in [-1.0, 1.0]. Same constant
    // as voice/stt.py SAMPLE_RATE.
    public const int SampleRate = 16000;

    // Defense against path-traversal via the model-size string. Same regex as
    // Python's _WHISPER_SIZE_RE in jarvis.py — letters, digits, dot, dash,
    // underscore only. Anything else is either a typo or an attempt to
    // escape the cache dir, and we reject it loudly.
    private static readonly Regex SizeRegex =
        new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    // Approximate on-disk sizes for the GGML model files. Used only for the
    // "Loading Whisper model: base.en (~150 MB, downloads on first use)..."
    // status line so the user has some idea what they're waiting for.
    // (whisper.cpp GGML files are roughly 2x the size of faster-whisper's
    // int8 CT2 weights — these numbers are the GGML ones, not the Python
    // ones from voice/stt.py.)
    private static readonly System.Collections.Generic.Dictionary<string, string> SizeHints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tiny"]     = "~75 MB",  ["tiny.en"]   = "~75 MB",
            ["base"]     = "~150 MB", ["base.en"]   = "~150 MB",
            ["small"]    = "~470 MB", ["small.en"]  = "~470 MB",
            ["medium"]   = "~1.5 GB", ["medium.en"] = "~1.5 GB",
            ["large-v1"] = "~3.0 GB", ["large-v2"]  = "~3.0 GB",
            ["large-v3"] = "~3.0 GB", ["large"]     = "~3.0 GB",
        };

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly ILogger<WhisperStt> _log;
    // Serialize TranscribeAsync calls — WhisperProcessor is not thread-safe
    // and Jarvis can call it from both the wake-word loop and the
    // command-record path. The lock keeps the contract simple: caller can
    // fire calls concurrently from any thread, we'll queue them.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public string ModelSize { get; }
    public string ModelPath { get; }

    private WhisperStt(
        string modelSize, string modelPath,
        WhisperFactory factory, WhisperProcessor processor,
        ILogger<WhisperStt> log)
    {
        ModelSize = modelSize;
        ModelPath = modelPath;
        _factory = factory;
        _processor = processor;
        _log = log;
    }

    /// <summary>
    /// Resolve, download (if missing), and load the GGML model for
    /// <paramref name="modelSize"/>. Throws <see cref="ArgumentException"/>
    /// for malformed size names. Reports progress via
    /// <paramref name="statusLog"/> — both the "Downloading..." line and a
    /// rolling "X / Y MB (Z%)" line during download.
    /// </summary>
    public static async Task<WhisperStt> LoadAsync(
        string modelSize,
        ILogger<WhisperStt> log,
        IProgress<string>? statusLog = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelSize))
            throw new ArgumentException("modelSize must not be empty.", nameof(modelSize));
        if (!SizeRegex.IsMatch(modelSize))
            throw new ArgumentException(
                $"Refusing to load Whisper model with non-canonical name " +
                $"'{modelSize}'. Use a name like 'tiny.en' / 'base.en' / " +
                $"'small.en' / 'medium.en' / 'large-v3'.",
                nameof(modelSize));

        var cacheDir = ResolveCacheDir();
        var modelFile = $"ggml-{modelSize}.bin";
        var modelPath = Path.Combine(cacheDir, modelFile);

        var hint = SizeHints.TryGetValue(modelSize, out var h) ? $" ({h})" : "";
        if (File.Exists(modelPath))
        {
            log.LogInformation("Whisper model {Size} found at {Path}", modelSize, modelPath);
        }
        else
        {
            statusLog?.Report(
                $"Loading Whisper model: {modelSize}{hint} — downloading on first use...");
            statusLog?.Report($"  cache: {cacheDir}");
            await DownloadModelAsync(modelSize, modelPath, statusLog, ct).ConfigureAwait(false);
        }

        // Try to instantiate WhisperFactory; if it throws and we have a cached
        // file, assume it's a corrupt partial download from a previous Ctrl+C,
        // delete + redownload once. Mirrors _load_whisper_with_recovery in
        // jarvis.py.
        WhisperFactory factory;
        try
        {
            factory = WhisperFactory.FromPath(modelPath);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "WhisperFactory.FromPath failed for {Path} — assuming corrupt " +
                "cached model, deleting and retrying download once.", modelPath);
            statusLog?.Report(
                $"  load failed ({ex.GetType().Name}: {ex.Message})");
            statusLog?.Report(
                $"  clearing possibly-corrupt cached model at {modelPath} and retrying...");
            try { File.Delete(modelPath); }
            catch (Exception rmEx)
            {
                log.LogError(rmEx, "Couldn't delete cached model at {Path}", modelPath);
                throw;
            }
            await DownloadModelAsync(modelSize, modelPath, statusLog, ct).ConfigureAwait(false);
            factory = WhisperFactory.FromPath(modelPath);
        }

        WhisperProcessor processor;
        try
        {
            processor = factory.CreateBuilder()
                .WithLanguage("en")
                .Build();
        }
        catch
        {
            factory.Dispose();
            throw;
        }

        log.LogInformation("Whisper model {Size} loaded from {Path}", modelSize, modelPath);
        return new WhisperStt(modelSize, modelPath, factory, processor, log);
    }

    /// <summary>
    /// Transcribe a 16 kHz mono float32 PCM buffer to English text. Returns
    /// the empty string if Whisper produced no output. Concurrent callers
    /// are serialized internally (the underlying processor is not
    /// thread-safe).
    /// </summary>
    public async Task<string> TranscribeAsync(float[] audio, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (audio is null || audio.Length == 0)
            return string.Empty;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var t0 = DateTime.UtcNow;
            var sb = new StringBuilder();
            await foreach (var segment in _processor.ProcessAsync(audio, ct).ConfigureAwait(false))
            {
                sb.Append(segment.Text);
            }
            var text = sb.ToString().Trim();
            _log.LogDebug("transcribed {Seconds:F2}s in {Elapsed:F2}s: {Text}",
                audio.Length / (double)SampleRate,
                (DateTime.UtcNow - t0).TotalSeconds,
                text);
            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Run one transcription on 1 s of silence so whisper.cpp's lazy init
    /// (compute graph setup, mel filter alloc, etc.) happens now instead of
    /// during the first real call. Same intent as <c>warm_up()</c> in
    /// voice/stt.py — without this, the first user-spoken transcription can
    /// stall several seconds and make Jarvis look frozen.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var silence = new float[SampleRate]; // 1 s of zeros
        await TranscribeAsync(silence, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Drain any in-flight call before tearing down the native processor.
        try { await _gate.WaitAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
        try
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _factory.Dispose();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WhisperStt));
    }

    /// <summary>
    /// Cache directory for GGML model files. Honors HF_HUB_CACHE / HF_HOME
    /// so customizing one env var moves Python's and our caches together,
    /// even though we use a different subdirectory layout (we don't sit in
    /// huggingface_hub's snapshots tree — we just colocate near it).
    ///
    /// Layout:
    ///   {hub_root}/models--ggerganov--whisper.cpp/snapshots/main/ggml-{size}.bin
    ///
    /// This matches the huggingface_hub default layout closely enough that
    /// if the user has already manually pulled the GGML model via the
    /// huggingface CLI, we'll find it.
    /// </summary>
    private static string ResolveCacheDir()
    {
        var hubCache = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        if (!string.IsNullOrWhiteSpace(hubCache))
            return Path.Combine(hubCache!,
                "models--ggerganov--whisper.cpp", "snapshots", "main");

        var hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrWhiteSpace(hfHome))
            return Path.Combine(hfHome!, "hub",
                "models--ggerganov--whisper.cpp", "snapshots", "main");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "huggingface", "hub",
            "models--ggerganov--whisper.cpp", "snapshots", "main");
    }

    private static async Task DownloadModelAsync(
        string modelSize, string destPath,
        IProgress<string>? statusLog,
        CancellationToken ct)
    {
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelSize}.bin";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Download to a .partial sibling, then atomically rename. Means a
        // Ctrl+C during download leaves a .partial file (which gets
        // overwritten next attempt) rather than a half-written .bin that
        // looks valid to File.Exists() but crashes WhisperFactory.
        var partial = destPath + ".partial";
        if (File.Exists(partial))
        {
            try { File.Delete(partial); } catch { /* will be overwritten below */ }
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Jarvis-NET/0.2 WhisperStt");

        using var resp = await http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        var totalMb = total > 0 ? total / (1024.0 * 1024.0) : -1.0;

        var fileName = Path.GetFileName(destPath);
        statusLog?.Report(total > 0
            ? $"Downloading {fileName}: 0 / {totalMb:F0} MB (0%)"
            : $"Downloading {fileName}...");

        using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[262144]; // 256 KB chunks, same as YT installer
            long read = 0;
            var lastReportPct = -1;
            var lastReportTime = DateTime.UtcNow;
            int n;
            while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;

                // Report at most every ~2% OR every 1.5s, whichever comes
                // first — avoids flooding the status log on fast networks
                // and avoids dead-air silence on slow ones. Same UX rhythm
                // as the YT Music installer.
                if (statusLog is not null && total > 0)
                {
                    var pct = (int)(100.0 * read / total);
                    var elapsed = (DateTime.UtcNow - lastReportTime).TotalSeconds;
                    if (pct >= lastReportPct + 2 || elapsed >= 1.5)
                    {
                        var readMb = read / (1024.0 * 1024.0);
                        statusLog.Report(
                            $"Downloading {fileName}: {readMb:F0} / {totalMb:F0} MB ({pct}%)");
                        lastReportPct = pct;
                        lastReportTime = DateTime.UtcNow;
                    }
                }
            }
        }

        // Atomic-ish rename. File.Move with overwrite=true so a concurrent
        // run that finished first doesn't make us crash.
        if (File.Exists(destPath))
        {
            try { File.Delete(destPath); } catch { /* Move(..., true) will handle */ }
        }
        File.Move(partial, destPath, overwrite: true);
        statusLog?.Report($"Downloaded {fileName} to {destPath}");
    }
}
