using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Common;

namespace Jarvis.Core.Tools;

// Port of tools/web_browser.py. Two tools exposed:
//   - open_url: opens a URL (or bare domain) in the default browser. http/https only.
//   - play_youtube_video: searches YouTube for the top result and opens it.
//
// Dependency substitution vs Python: Python uses yt-dlp for YouTube search;
// .NET uses YoutubeExplode (the package surface that yt-dlp's behavior maps to
// cleanly without needing a python interpreter at runtime). Behavior is
// equivalent: "ytsearch1" → take top result, build a https://www.youtube.com/
// watch?v={id} URL, open it. URL-opening uses Process.Start with
// UseShellExecute=true so the OS's default browser handles the navigation
// (same as Python's webbrowser.open).

/// <summary>
/// Opens a URL (or bare domain) in the user's default browser. Only http/https
/// schemes are accepted; anything else (file://, javascript:, etc.) is refused.
/// </summary>
public sealed class OpenUrlTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["url"] = new JsonObject
            {
                ["type"] = "string",
            },
        },
        ["required"] = new JsonArray { "url" },
    };

    private readonly ILogger<OpenUrlTool> _log;

    public OpenUrlTool(ILogger<OpenUrlTool> log)
    {
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "open_url",
        Description:
            "Open a website in the user's default browser. Accepts a URL " +
            "or bare domain ('github.com'). http/https only.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var url = (arguments["url"]?.GetValue<string>() ?? string.Empty).Trim();
            if (url.Length == 0)
                return Task.FromResult("No URL provided.");

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            // Use Uri.TryCreate to mirror urlparse: we need a scheme and a host.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                return Task.FromResult($"Invalid URL: {url}");

            var scheme = parsed.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
                return Task.FromResult($"Refusing to open URL with scheme '{scheme}'.");

            if (string.IsNullOrEmpty(parsed.Host))
                return Task.FromResult($"Invalid URL: {url}");

            _log.LogInformation("open_url: {Url}", url);
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return Task.FromResult($"Opened {parsed.Host}.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "open_url failed");
            return Task.FromResult($"Tool error: {ex.Message}");
        }
    }
}

/// <summary>
/// Search YouTube (regular YouTube, not YouTube Music) for the top result of a
/// query and open it in the default browser. For music, the LLM should prefer
/// the play_music tool instead.
/// </summary>
public sealed class PlayYoutubeVideoTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
            },
        },
        ["required"] = new JsonArray { "query" },
    };

    private readonly ILogger<PlayYoutubeVideoTool> _log;

    public PlayYoutubeVideoTool(ILogger<PlayYoutubeVideoTool> log)
    {
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "play_youtube_video",
        Description:
            "Search YouTube (NOT YouTube Music) and play the top result " +
            "in the browser. Use for non-music videos: tutorials, clips, " +
            "reviews. For music, prefer the play_music tool.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var query = (arguments["query"]?.GetValue<string>() ?? string.Empty).Trim();
        if (query.Length == 0)
            return "No query provided.";

        YoutubeExplode.Search.VideoSearchResult? top;
        try
        {
            var youtube = new YoutubeClient();
            // YoutubeExplode exposes CollectAsync(count) on IAsyncEnumerable
            // (via YoutubeExplode.Common.BatchItemExtensions) which caps the
            // streamed result count. Equivalent to yt-dlp "ytsearch1:..." —
            // we only need the top hit.
            var results = await youtube.Search.GetVideosAsync(query, ct)
                .CollectAsync(1);
            top = results.FirstOrDefault();
        }
        catch (Exception e)
        {
            return $"YouTube search failed: {e.Message}";
        }

        if (top is null)
            return $"No YouTube results for '{query}'.";

        var videoId = top.Id.Value;
        var title = string.IsNullOrEmpty(top.Title) ? "video" : top.Title;
        if (string.IsNullOrEmpty(videoId))
            return "Top result has no video ID.";

        var url = $"https://www.youtube.com/watch?v={videoId}";
        _log.LogInformation("play_youtube_video: {Query} -> {Title} ({Id})",
            query, title, videoId);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to open youtube url");
            return $"Tool error: {ex.Message}";
        }

        return $"Playing '{title}' on YouTube.";
    }
}
