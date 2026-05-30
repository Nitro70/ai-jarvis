using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace Jarvis.Core.Voice;

// Port of the Python `edge-tts` library's communicate.py + drm.py to C#.
//
// Microsoft does not publish an official SDK for the Edge browser's
// "Read Aloud" TTS endpoint, so we reverse-engineer the WebSocket
// protocol the same way edge-tts does. Both clients impersonate the
// Edge "Read Aloud" Chromium extension.
//
// Protocol shape (request side):
//   1. Generate a Sec-MS-GEC token (SHA256 of windows-FILETIME-ticks
//      rounded to 5 minutes + a hardcoded TrustedClientToken).
//   2. Open WebSocket to speech.platform.bing.com with Edge UA + the
//      chrome-extension Origin header.
//   3. Send a `Path:speech.config` text frame that selects the output
//      codec (24kHz 48kbps mono mp3).
//   4. Send a `Path:ssml` text frame with the SSML to synthesize.
//
// Protocol shape (response side):
//   - Binary frames. Each frame is `[2-byte BE header length][header
//     bytes][body bytes]`. Headers are HTTP-style `Key:Value\r\n` lines.
//   - Several JSON metadata frames first (turn.start, response,
//     audio.metadata).
//   - Then one or more `Path:audio` frames whose body is raw MP3.
//   - Then a `Path:turn.end` frame signals EOF — close the socket.
//
// Things that have historically bitten the Python lib (DO NOT repeat):
//   - Token must be regenerated PER CONNECTION (not cached) — server
//     rejects stale tokens with a 401-style close.
//   - Ticks must be rounded down to a 5-minute boundary (300 s), and
//     the hash input is decimal digits with no leading zeros.
//   - SSML must escape `&`, `<`, `>` in the user's text or the server
//     returns a generic 400 close with no helpful body.
//   - Text-frame requests use `\r\n` between header lines, NOT `\n`.

public sealed class EdgeTts : IDisposable
{
    // Public so callers/tests can verify protocol values match Python.
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string SecMsGecVersion = "1-130.0.2849.68";
    public const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    private const string WssUrlBase =
        "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1" +
        "?TrustedClientToken=" + TrustedClientToken;

    // Edge on Windows. The major version is mirrored in SecMsGecVersion.
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    private const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";

    // .NET DateTime.Ticks is already 100-ns since 0001-01-01. The Windows
    // FILETIME epoch is 1601-01-01. We need the FILETIME tick value:
    //     filetime_ticks = (utc_now - 1601-01-01).TotalSeconds * 1e7
    // which equals utc_now.ToFileTimeUtc() — but we also need to round to
    // 5-minute boundaries BEFORE converting to ticks. So we work in seconds.
    private static readonly DateTime WinEpoch =
        new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ILogger<EdgeTts> _log;

    public EdgeTts(ILogger<EdgeTts> log)
    {
        _log = log ?? NullLogger<EdgeTts>.Instance;
    }

    /// <summary>
    /// Generate the per-connection Sec-MS-GEC token.
    /// Algorithm: ticks = (utc_now - 1601-01-01).TotalSeconds; round down
    /// to nearest 300 s; multiply by 1e7 to get 100-ns intervals;
    /// SHA256(decimal-string-of-ticks + TrustedClientToken); uppercase hex.
    /// </summary>
    public static string GenerateSecMsGec()
    {
        var nowUtc = DateTime.UtcNow;
        var secondsSinceWinEpoch = (long)Math.Floor((nowUtc - WinEpoch).TotalSeconds);
        // Round down to nearest 5-minute boundary.
        secondsSinceWinEpoch -= secondsSinceWinEpoch % 300;
        // Convert to 100-ns FILETIME ticks.
        var ticks = (long)secondsSinceWinEpoch * 10_000_000L;

        var input = ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + TrustedClientToken;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(input));
        return Convert.ToHexString(hash); // already uppercase
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("voice is required (e.g. 'en-GB-RyanNeural')", nameof(voice));

        var token = GenerateSecMsGec();
        var url = $"{WssUrlBase}&Sec-MS-GEC={token}&Sec-MS-GEC-Version={SecMsGecVersion}" +
                  $"&ConnectionId={Guid.NewGuid():N}";

        using var ws = new ClientWebSocket();
        // ClientWebSocket forbids setting User-Agent/Origin via the public
        // SetRequestHeader on some older runtimes; on net8.0 both are allowed.
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        ws.Options.SetRequestHeader("Origin", Origin);
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");

        _log.LogDebug("EdgeTts connect voice={Voice} chars={Chars}", voice, text.Length);
        await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);

        // ---- Frame 1: speech.config (selects output codec) ----
        var nowIso = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            System.Globalization.CultureInfo.InvariantCulture);
        var configBody =
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":" +
            "{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"" + OutputFormat + "\"}}}}";
        var configFrame =
            "X-Timestamp:" + nowIso + "\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            configBody;
        await SendTextAsync(ws, configFrame, ct).ConfigureAwait(false);

        // ---- Frame 2: SSML ----
        var requestId = Guid.NewGuid().ToString("N");
        var ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            "<voice name='" + voice + "'>" +
            "<prosody pitch='+0Hz' rate='+0%' volume='+0%'>" +
            EscapeXml(text) +
            "</prosody></voice></speak>";
        var ssmlFrame =
            "X-RequestId:" + requestId + "\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            "X-Timestamp:" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
            "Path:ssml\r\n\r\n" +
            ssml;
        await SendTextAsync(ws, ssmlFrame, ct).ConfigureAwait(false);

        // ---- Receive loop ----
        using var audio = new MemoryStream();
        var buffer = new byte[16 * 1024];
        using var frameBuf = new MemoryStream();

        while (ws.State == WebSocketState.Open)
        {
            frameBuf.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    _log.LogDebug("EdgeTts server closed: {Status} {Desc}",
                        res.CloseStatus, res.CloseStatusDescription);
                    goto Done;
                }
                frameBuf.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            var frame = frameBuf.ToArray();

            if (res.MessageType == WebSocketMessageType.Text)
            {
                // Text frames are JSON metadata. We only care about turn.end.
                var asText = Encoding.UTF8.GetString(frame);
                if (asText.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    _log.LogDebug("EdgeTts turn.end (text); bytes={Bytes}", audio.Length);
                    break;
                }
                continue;
            }

            // Binary frame: [u16 BE headerLen][headers ascii][body bytes]
            if (frame.Length < 2)
            {
                _log.LogWarning("EdgeTts binary frame too short ({Len} bytes)", frame.Length);
                continue;
            }
            int headerLen = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0, 2));
            if (2 + headerLen > frame.Length)
            {
                _log.LogWarning("EdgeTts header length {HL} exceeds frame {FL}", headerLen, frame.Length);
                continue;
            }
            var headerText = Encoding.ASCII.GetString(frame, 2, headerLen);
            var bodyOffset = 2 + headerLen;
            var bodyLen = frame.Length - bodyOffset;

            // Headers contain `Path:audio` for MP3 chunks, `Path:turn.end` for EOF.
            // Lines are separated by \r\n; we just substring-search to stay tolerant.
            if (headerText.Contains("Path:audio", StringComparison.Ordinal))
            {
                if (bodyLen > 0)
                    audio.Write(frame, bodyOffset, bodyLen);
            }
            else if (headerText.Contains("Path:turn.end", StringComparison.Ordinal))
            {
                _log.LogDebug("EdgeTts turn.end (binary); bytes={Bytes}", audio.Length);
                break;
            }
            // Other paths (turn.start, response, audio.metadata) we ignore.
        }

    Done:
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort close; server may have already torn down.
        }

        var bytes = audio.ToArray();
        if (bytes.Length == 0)
            _log.LogWarning("EdgeTts produced 0 audio bytes for {Chars} char input", text.Length);
        return bytes;
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private static string EscapeXml(string s)
    {
        // SSML body: minimal XML escaping. Edge's parser is strict.
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        // Nothing persistent to dispose; each Synthesize opens its own socket.
    }
}

/// <summary>
/// Plays MP3 bytes via NAudio. One playback at a time; Stop() is safe at
/// any point, including when nothing is playing.
/// </summary>
public sealed class TtsPlayer : IDisposable
{
    private readonly ILogger<TtsPlayer> _log;
    private readonly object _gate = new();
    private WaveOutEvent? _out;
    private Mp3FileReader? _reader;
    private TaskCompletionSource<bool>? _doneTcs;

    public TtsPlayer(ILogger<TtsPlayer> log)
    {
        _log = log ?? NullLogger<TtsPlayer>.Instance;
    }

    public async Task PlayAsync(byte[] mp3, CancellationToken ct)
    {
        if (mp3 is null || mp3.Length == 0)
        {
            _log.LogDebug("TtsPlayer: empty buffer, nothing to play");
            return;
        }

        // Stop anything currently playing before starting.
        Stop();

        Mp3FileReader reader;
        WaveOutEvent waveOut;
        TaskCompletionSource<bool> tcs;
        try
        {
            reader = new Mp3FileReader(new MemoryStream(mp3, writable: false));
            waveOut = new WaveOutEvent();
            waveOut.Init(reader);
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waveOut.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is not null)
                    _log.LogWarning(args.Exception, "TtsPlayer playback error");
                tcs.TrySetResult(true);
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TtsPlayer: failed to init MP3 decoder/output ({Bytes} bytes)", mp3.Length);
            throw;
        }

        lock (_gate)
        {
            _reader = reader;
            _out = waveOut;
            _doneTcs = tcs;
        }

        waveOut.Play();

        // If ct fires, stop playback; PlaybackStopped then completes the tcs.
        using var ctReg = ct.Register(static state => ((TtsPlayer)state!).Stop(), this);
        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_out, waveOut))
                {
                    _out = null;
                    _reader = null;
                    _doneTcs = null;
                }
            }
            try { waveOut.Dispose(); } catch { }
            try { reader.Dispose(); } catch { }
        }
    }

    public void Stop()
    {
        WaveOutEvent? toStop;
        lock (_gate)
        {
            toStop = _out;
        }
        if (toStop is null) return;
        try { toStop.Stop(); }
        catch (Exception ex) { _log.LogDebug(ex, "TtsPlayer.Stop ignored"); }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Combines EdgeTts + TtsPlayer into an ITtsSink for the orchestrator.
/// Synthesize and play happen sequentially; SpeakAsync returns when
/// playback completes (or ct fires).
/// </summary>
public sealed class EdgeTtsSink : ITtsSink, IDisposable
{
    private readonly string _voice;
    private readonly EdgeTts _tts;
    private readonly TtsPlayer _player;
    private readonly ILogger<EdgeTtsSink> _log;

    public EdgeTtsSink(string voice, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("voice is required", nameof(voice));
        _voice = voice;
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        _log = lf.CreateLogger<EdgeTtsSink>();
        _tts = new EdgeTts(lf.CreateLogger<EdgeTts>());
        _player = new TtsPlayer(lf.CreateLogger<TtsPlayer>());
    }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        byte[] mp3;
        try
        {
            mp3 = await _tts.SynthesizeAsync(text, _voice, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EdgeTtsSink: synthesis failed; skipping playback");
            return;
        }

        if (mp3.Length == 0) return;
        await _player.PlayAsync(mp3, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _player.Dispose();
        _tts.Dispose();
    }
}
