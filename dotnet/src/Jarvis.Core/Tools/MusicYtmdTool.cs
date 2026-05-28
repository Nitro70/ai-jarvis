using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Common;

namespace Jarvis.Core.Tools;

// Port of tools/music_ytmd.py. Controls the th-ch/youtube-music desktop app
// through its HTTP API on localhost:{port}. Behavior parity with Python:
//   - Pairing via POST /auth/{clientId}; JWT cached at <installDir>/.ytmd_token
//   - All other calls go to /api/v1/* with an Authorization: Bearer header
//   - On 401, the cached token is deleted so the next call re-pairs
//   - If the app isn't running and exePath is configured, auto-launch and
//     poll until the API answers (45s in Python; same here)
//
// Deviations from Python:
//   1. Search uses YoutubeExplode (same library WebBrowserTool already uses)
//      instead of ytmusicapi. ytmusicapi calls music.youtube.com internal
//      endpoints from Python; .NET has no equivalent. YoutubeExplode hits
//      regular youtube.com but returns videoIds that YT Music accepts, and
//      it's already a project dependency so no new packages are needed.
//      The same Top-Result-Wins behavior is preserved, including the
//      _TITLE_BLOCKLIST filter.
//   2. The seven tool classes share one YtmdClient (passed in via the ctor)
//      rather than module-level globals. The registry constructs YtmdClient
//      once and hands it to each tool. This avoids one HttpClient per tool.

/// <summary>
/// Shared HTTP/auth/launch helper for all YTMD tools. One instance per
/// process, constructed by the bootstrap and passed to every Music*Tool.
/// Mirrors the module-level state in tools/music_ytmd.py
/// (_HOST/_PORT/_BASE_URL/_EXE_PATH/_TOKEN_PATH/_token/_http).
/// </summary>
internal sealed class YtmdClient : IDisposable
{
    private const string ClientId = "jarvis";

    private readonly int _port;
    private readonly string? _exePath;
    private readonly string _tokenPath;
    private readonly ILogger _log;
    private readonly HttpClient _bare;       // no auth, for /auth + alive checks
    private readonly HttpClient _http;       // auth header is set lazily
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;

    public string BaseUrl { get; }

    public YtmdClient(int port, string? exePath, string installDir, ILogger log)
    {
        _port = port;
        _exePath = string.IsNullOrWhiteSpace(exePath) ? null : exePath;
        _tokenPath = Path.Combine(installDir, ".ytmd_token");
        _log = log;
        BaseUrl = $"http://127.0.0.1:{port}";

        _bare = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose()
    {
        _bare.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }

    // ===== Auto-launch + alive checks =====

    private async Task<bool> ApiAliveAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, "/");
            using var resp = await _bare.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForApiAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ApiAliveAsync(TimeSpan.FromSeconds(2), ct))
                return true;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task EnsureAppRunningAsync(CancellationToken ct)
    {
        if (await ApiAliveAsync(TimeSpan.FromMilliseconds(1500), ct))
            return;

        if (_exePath is null || !File.Exists(_exePath))
        {
            throw new InvalidOperationException(
                $"YT Music app isn't reachable at {BaseUrl}. Either start it " +
                "manually, or set tools.music_ytmd.exe_path in config.yaml to " +
                "youtube-music.exe so I can auto-launch it.");
        }

        _log.LogInformation("YTMD not running — launching {Exe}", _exePath);
        Console.WriteLine("Starting YouTube Music app...");

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to launch {_exePath}: {ex.Message}", ex);
        }

        // Python uses 45s; auto-launch on a cold app can take a while.
        if (!await WaitForApiAsync(TimeSpan.FromSeconds(45), ct))
        {
            throw new InvalidOperationException(
                "YT Music started but the API Server didn't come up. Open the " +
                "app -> menu -> Plugins -> enable 'API Server', fully quit " +
                "(right-click tray -> Quit), and try again.");
        }
    }

    private async Task<string> EnsurePairedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_token))
            return _token!;

        if (File.Exists(_tokenPath))
        {
            var cached = File.ReadAllText(_tokenPath).Trim();
            if (cached.Length > 0)
            {
                _token = cached;
                await EnsureAppRunningAsync(ct);
                return _token;
            }
        }

        await EnsureAppRunningAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"A dialog in the YouTube Music app should ask to authorize '{ClientId}'. Click Allow.");
        Console.WriteLine();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/auth/{ClientId}");
        // Pairing waits on a user click; give it the same 120s Python does.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        using var resp = await _bare.SendAsync(req, cts.Token);

        if ((int)resp.StatusCode == 403)
            throw new InvalidOperationException("Pairing denied in the YT Music app.");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("accessToken").GetString();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Pairing response had no accessToken.");

        _token = token;
        File.WriteAllText(_tokenPath, token);
        _log.LogInformation("paired; saved token to {Path}", _tokenPath);
        return token;
    }

    /// <summary>
    /// Send an /api/v1/{path} request. Mirrors Python's _api(): on 401 the
    /// cached token is dropped so the next call re-pairs, then the exception
    /// propagates. Caller wraps in try/catch for friendly error strings.
    /// </summary>
    public async Task<HttpResponseMessage> ApiAsync(
        HttpMethod method,
        string path,
        object? jsonBody,
        CancellationToken ct)
    {
        // Serialize pairing/launch so multiple in-flight tool calls don't
        // each try to launch the app or write the token file.
        await _gate.WaitAsync(ct);
        string token;
        try
        {
            token = await EnsurePairedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        var req = new HttpRequestMessage(method, $"/api/v1{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null)
            req.Content = JsonContent.Create(jsonBody);

        var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 401)
        {
            _log.LogWarning("YTMD token rejected; deleting cache");
            try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); } catch { /* best-effort */ }
            _token = null;
        }
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    public async Task<JsonNode?> ApiJsonAsync(
        HttpMethod method,
        string path,
        object? jsonBody,
        CancellationToken ct)
    {
        using var resp = await ApiAsync(method, path, jsonBody, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body)) return null;
        return JsonNode.Parse(body);
    }

    // ===== Queue introspection (port of _unwrap_renderer / _find_current_index) =====

    private static JsonObject? UnwrapRenderer(JsonNode? item)
    {
        if (item is not JsonObject obj) return null;

        if (obj["playlistPanelVideoWrapperRenderer"] is JsonObject wrapper &&
            wrapper["primaryRenderer"] is JsonObject primary &&
            primary["playlistPanelVideoRenderer"] is JsonObject inner1)
        {
            return inner1;
        }
        if (obj["playlistPanelVideoRenderer"] is JsonObject inner2)
            return inner2;
        if (obj.ContainsKey("videoId"))
            return obj;
        return null;
    }

    private static string? ItemVideoId(JsonNode? item)
    {
        var inner = UnwrapRenderer(item);
        return inner?["videoId"]?.GetValue<string>();
    }

    private static bool ItemIsSelected(JsonNode? item)
    {
        var inner = UnwrapRenderer(item);
        if (inner is null) return false;
        return TryBool(inner["selected"]) || TryBool(inner["playing"]);

        static bool TryBool(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    }

    private static int? FindCurrentIndex(JsonNode? data)
    {
        if (data is not JsonObject obj) return null;
        foreach (var k in new[] { "currentIndex", "selectedIndex", "current_index", "playingIndex" })
        {
            if (obj[k] is JsonValue v && v.TryGetValue<int>(out var i))
                return i;
        }
        if (obj["current"] is JsonObject cur)
        {
            foreach (var k in new[] { "index", "currentIndex" })
            {
                if (cur[k] is JsonValue v && v.TryGetValue<int>(out var i))
                    return i;
            }
        }
        if (obj["items"] is JsonArray items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (ItemIsSelected(items[i])) return i;
            }
        }
        return null;
    }

    public async Task ClearQueueAroundCurrentAsync(CancellationToken ct)
    {
        JsonNode? data;
        try
        {
            data = await ApiJsonAsync(HttpMethod.Get, "/queue", null, ct);
        }
        catch (Exception e)
        {
            _log.LogWarning("GET /queue failed: {Err}", e.Message);
            return;
        }

        if (data is not JsonObject obj || obj["items"] is not JsonArray items || items.Count == 0)
            return;

        var currentIdx = FindCurrentIndex(data);
        if (currentIdx is null)
        {
            _log.LogWarning("can't find current index; queue keys=[{Keys}]",
                string.Join(",", obj.Select(kv => kv.Key)));
            return;
        }

        _log.LogInformation("queue clear: current_idx={Idx}, total={Total}",
            currentIdx, items.Count);

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (i == currentIdx) continue;
            try
            {
                using var _ = await ApiAsync(HttpMethod.Delete, $"/queue/{i}", null, ct);
            }
            catch (HttpRequestException e)
            {
                _log.LogWarning("DELETE /queue/{Idx} failed: {Err}", i, e.Message);
                break;
            }
        }
    }

    // ===== Search (deviation: YoutubeExplode in place of ytmusicapi) =====

    private static readonly string[] TitleBlocklist =
    {
        "karaoke", "instrumental", "tutorial", "lyrics video",
        "8 hour", "10 hour", "1 hour", "sped up", "slowed",
        "nightcore", "reverb",
    };

    public readonly record struct SearchHit(string VideoId, string Title, string Artist);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        var yt = new YoutubeClient();
        var hits = new List<SearchHit>(8);
        await foreach (var r in yt.Search.GetVideosAsync(query, ct))
        {
            var id = r.Id.Value;
            if (string.IsNullOrEmpty(id)) continue;
            var artist = r.Author?.ChannelTitle ?? string.Empty;
            hits.Add(new SearchHit(id, r.Title ?? string.Empty, artist));
            if (hits.Count >= 8) break;
        }
        return FilterResults(hits, query);
    }

    private static IReadOnlyList<SearchHit> FilterResults(IReadOnlyList<SearchHit> results, string query)
    {
        var q = query.ToLowerInvariant();
        var kept = results.Where(r =>
        {
            var t = r.Title.ToLowerInvariant();
            return !TitleBlocklist.Any(term => t.Contains(term) && !q.Contains(term));
        }).ToList();
        return kept.Count > 0 ? kept : results;
    }

    public static string? ItemVideoIdPublic(JsonNode? item) => ItemVideoId(item);
}

// ===== Tool classes (one per Python get_tools() entry) =====

/// <summary>
/// play_music — search YT Music for a query, replace the current playback
/// with the top filtered result, and clear the rest of the queue. Port of
/// the Python _play_music handler.
/// </summary>
public sealed class PlayMusicTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray { "query" },
    };

    private readonly YtmdClient _client;
    private readonly ILogger<PlayMusicTool> _log;

    internal PlayMusicTool(YtmdClient client, ILogger<PlayMusicTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "play_music",
        Description:
            "Play a song, artist, or album on YouTube Music. Plays the top " +
            "filtered search result, replaces whatever's playing, and clears " +
            "the rest of the queue so only the new song is queued. Call this " +
            "every time the user asks to play something — even if the same " +
            "song appears to already be playing.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var query = (arguments["query"]?.GetValue<string>() ?? string.Empty).Trim();
        if (query.Length == 0) return "No query provided.";

        IReadOnlyList<YtmdClient.SearchHit> results;
        try
        {
            results = await _client.SearchAsync(query, ct);
        }
        catch (Exception e)
        {
            return $"YT Music search failed: {e.Message}";
        }
        if (results.Count == 0) return $"Nothing found for '{query}'.";

        var top = results[0];
        var label = string.IsNullOrEmpty(top.Artist)
            ? top.Title
            : $"{top.Title} by {top.Artist}";

        _log.LogInformation("play_music: query={Query} -> {Label} (videoId={Id})",
            query, label, top.VideoId);

        try
        {
            await _client.ApiAsync(HttpMethod.Post, "/queue", new
            {
                videoId = top.VideoId,
                insertPosition = "INSERT_AFTER_CURRENT_VIDEO",
            }, ct).ContinueWith(t => t.Result.Dispose(), ct);

            // Small grace period before re-reading the queue, same as Python.
            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return "Cancelled."; }

            int? targetIdx = null;
            try
            {
                var data = await _client.ApiJsonAsync(HttpMethod.Get, "/queue", null, ct);
                if (data is JsonObject obj && obj["items"] is JsonArray items)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (YtmdClient.ItemVideoIdPublic(items[i]) == top.VideoId)
                        {
                            targetIdx = i;
                            break;
                        }
                    }
                }
            }
            catch { /* fall through to /next */ }

            if (targetIdx is not null)
            {
                (await _client.ApiAsync(HttpMethod.Patch, "/queue",
                    new { index = targetIdx.Value }, ct)).Dispose();
            }
            else
            {
                (await _client.ApiAsync(HttpMethod.Post, "/next", null, ct)).Dispose();
            }

            await _client.ClearQueueAroundCurrentAsync(ct);
        }
        catch (HttpRequestException e)
        {
            return $"YTMD playback control failed: {e.Message}";
        }
        catch (Exception e)
        {
            _log.LogError(e, "play_music failed");
            return $"Tool error: {e.Message}";
        }

        return $"Playing {label}.";
    }
}

/// <summary>play_pause — toggle pause/resume.</summary>
public sealed class PlayPauseTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly YtmdClient _client;
    private readonly ILogger<PlayPauseTool> _log;

    internal PlayPauseTool(YtmdClient client, ILogger<PlayPauseTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "play_pause",
        Description: "Toggle pause/resume on YouTube Music.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            (await _client.ApiAsync(HttpMethod.Post, "/toggle-play", null, ct)).Dispose();
            return "Toggled play/pause.";
        }
        catch (Exception e)
        {
            _log.LogError(e, "play_pause failed");
            return $"Tool error: {e.Message}";
        }
    }
}

/// <summary>next_track — skip forward.</summary>
public sealed class NextTrackTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly YtmdClient _client;
    private readonly ILogger<NextTrackTool> _log;

    internal NextTrackTool(YtmdClient client, ILogger<NextTrackTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "next_track",
        Description: "Skip to the next track on YouTube Music.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            (await _client.ApiAsync(HttpMethod.Post, "/next", null, ct)).Dispose();
            return "Skipped to next track.";
        }
        catch (Exception e)
        {
            _log.LogError(e, "next_track failed");
            return $"Tool error: {e.Message}";
        }
    }
}

/// <summary>previous_track — go back.</summary>
public sealed class PreviousTrackTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly YtmdClient _client;
    private readonly ILogger<PreviousTrackTool> _log;

    internal PreviousTrackTool(YtmdClient client, ILogger<PreviousTrackTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "previous_track",
        Description: "Go back to the previous track on YouTube Music.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            (await _client.ApiAsync(HttpMethod.Post, "/previous", null, ct)).Dispose();
            return "Went back to previous track.";
        }
        catch (Exception e)
        {
            _log.LogError(e, "previous_track failed");
            return $"Tool error: {e.Message}";
        }
    }
}

/// <summary>
/// volume — up/down/set/mute. Mirrors Python _volume exactly, including the
/// default step of 10 and clamp to [0, 100].
/// </summary>
public sealed class VolumeTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["direction"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray { "up", "down", "set", "mute" },
            },
            ["amount"] = new JsonObject { ["type"] = "integer" },
        },
        ["required"] = new JsonArray { "direction" },
    };

    private readonly YtmdClient _client;
    private readonly ILogger<VolumeTool> _log;

    internal VolumeTool(YtmdClient client, ILogger<VolumeTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "volume",
        Description:
            "Adjust YouTube Music volume. direction is 'up', 'down', " +
            "'set', or 'mute'. For up/down, amount is the step (default 10). " +
            "For set, amount is the target volume 0-100.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var direction = (arguments["direction"]?.GetValue<string>() ?? string.Empty)
            .Trim().ToLowerInvariant();
        int? amount = null;
        if (arguments["amount"] is JsonValue av && av.TryGetValue<int>(out var ai))
            amount = ai;

        if (direction == "mute")
        {
            try
            {
                (await _client.ApiAsync(HttpMethod.Post, "/toggle-mute", null, ct)).Dispose();
                return "Toggled mute.";
            }
            catch (Exception e)
            {
                _log.LogError(e, "mute failed");
                return $"Tool error: {e.Message}";
            }
        }

        try
        {
            if (direction == "up" || direction == "down")
            {
                var step = amount ?? 10;
                var data = await _client.ApiJsonAsync(HttpMethod.Get, "/volume", null, ct);
                var current = 50;
                if (data is JsonObject obj && obj["state"] is JsonValue sv &&
                    sv.TryGetValue<int>(out var cur))
                {
                    current = cur;
                }
                var delta = direction == "up" ? step : -step;
                var next = Math.Max(0, Math.Min(100, current + delta));
                (await _client.ApiAsync(HttpMethod.Post, "/volume",
                    new { volume = next }, ct)).Dispose();
                return $"Volume {direction} to {next}.";
            }
            if (direction == "set")
            {
                var next = Math.Max(0, Math.Min(100, amount ?? 50));
                (await _client.ApiAsync(HttpMethod.Post, "/volume",
                    new { volume = next }, ct)).Dispose();
                return $"Volume set to {next}.";
            }
            return $"Unknown direction '{direction}' — use up/down/set/mute.";
        }
        catch (HttpRequestException e)
        {
            return $"Volume change failed: {e.Message}";
        }
        catch (Exception e)
        {
            _log.LogError(e, "volume failed");
            return $"Tool error: {e.Message}";
        }
    }
}

/// <summary>now_playing — current title + artist.</summary>
public sealed class NowPlayingTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly YtmdClient _client;
    private readonly ILogger<NowPlayingTool> _log;

    internal NowPlayingTool(YtmdClient client, ILogger<NowPlayingTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "now_playing",
        Description: "Get the title and artist of the song currently playing.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        JsonNode? data;
        try
        {
            data = await _client.ApiJsonAsync(HttpMethod.Get, "/song", null, ct);
        }
        catch (HttpRequestException e)
        {
            return $"Couldn't read current song: {e.Message}";
        }
        catch (Exception e)
        {
            _log.LogError(e, "now_playing failed");
            return $"Tool error: {e.Message}";
        }

        var obj = data as JsonObject;
        var title = obj?["title"]?.GetValue<string>() ?? "(unknown title)";
        var artist = obj?["artist"]?.GetValue<string>() ?? "(unknown artist)";
        return $"{title} by {artist}";
    }
}

/// <summary>clear_queue — empty queue but keep the current song.</summary>
public sealed class ClearQueueTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly YtmdClient _client;
    private readonly ILogger<ClearQueueTool> _log;

    internal ClearQueueTool(YtmdClient client, ILogger<ClearQueueTool> log)
    {
        _client = client;
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "clear_queue",
        Description:
            "Empty the YouTube Music queue but keep the current song playing. " +
            "Use when the user asks to 'clear the queue' or 'start fresh'.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            await _client.ClearQueueAroundCurrentAsync(ct);
            return "Queue cleared — just the current song left.";
        }
        catch (HttpRequestException e)
        {
            return $"Couldn't clear queue: {e.Message}";
        }
        catch (Exception e)
        {
            _log.LogError(e, "clear_queue failed");
            return $"Tool error: {e.Message}";
        }
    }
}

/// <summary>
/// Bootstrap/factory for the seven YT Music ITool instances. Constructs one
/// shared <see cref="YtmdClient"/> and exposes the resulting tool set via
/// <see cref="Tools"/>. The composition root constructs this once, then feeds
/// <c>Tools</c> into the <see cref="ToolRegistry"/>.
/// </summary>
public sealed class MusicYtmdTool : IDisposable
{
    private readonly YtmdClient _client;

    public IReadOnlyList<ITool> Tools { get; }

    public MusicYtmdTool(int port, string? exePath, string installDir, ILogger<MusicYtmdTool> log)
    {
        _client = new YtmdClient(port, exePath, installDir, log);

        // We only have one ILogger here, so reuse it across the tool ctors via
        // a tiny adapter. The category will all be "MusicYtmdTool" — fine for
        // a tightly-coupled tool family.
        Tools = new ITool[]
        {
            new PlayMusicTool(_client, new RelogAdapter<PlayMusicTool>(log)),
            new PlayPauseTool(_client, new RelogAdapter<PlayPauseTool>(log)),
            new NextTrackTool(_client, new RelogAdapter<NextTrackTool>(log)),
            new PreviousTrackTool(_client, new RelogAdapter<PreviousTrackTool>(log)),
            new VolumeTool(_client, new RelogAdapter<VolumeTool>(log)),
            new NowPlayingTool(_client, new RelogAdapter<NowPlayingTool>(log)),
            new ClearQueueTool(_client, new RelogAdapter<ClearQueueTool>(log)),
        };
    }

    /// <summary>
    /// Alternate factory if the caller has a full <see cref="ILoggerFactory"/>
    /// available and wants per-tool log categories.
    /// </summary>
    public static IReadOnlyList<ITool> Create(
        int port, string? exePath, string installDir, ILoggerFactory loggerFactory)
    {
        var client = new YtmdClient(
            port, exePath, installDir,
            loggerFactory.CreateLogger("Jarvis.Tools.MusicYtmd"));

        return new ITool[]
        {
            new PlayMusicTool(client, loggerFactory.CreateLogger<PlayMusicTool>()),
            new PlayPauseTool(client, loggerFactory.CreateLogger<PlayPauseTool>()),
            new NextTrackTool(client, loggerFactory.CreateLogger<NextTrackTool>()),
            new PreviousTrackTool(client, loggerFactory.CreateLogger<PreviousTrackTool>()),
            new VolumeTool(client, loggerFactory.CreateLogger<VolumeTool>()),
            new NowPlayingTool(client, loggerFactory.CreateLogger<NowPlayingTool>()),
            new ClearQueueTool(client, loggerFactory.CreateLogger<ClearQueueTool>()),
        };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Forwards an <see cref="ILogger"/> to per-class <see cref="ILogger{T}"/>
    /// consumers so the single-logger constructor overload can hand the same
    /// underlying logger to every child tool. The log category stays
    /// whatever the caller supplied (typically "MusicYtmdTool"); this is fine
    /// for a tightly-coupled tool family.
    /// </summary>
    private sealed class RelogAdapter<T> : ILogger<T>
    {
        private readonly ILogger _inner;
        public RelogAdapter(ILogger inner) { _inner = inner; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
