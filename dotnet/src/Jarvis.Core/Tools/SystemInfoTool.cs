using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Tools;

// Port of tools/system_info.py. Two simple tools with no external setup:
//   current_time — formatted local date/time
//   get_weather  — wttr.in lookup (curl User-Agent gets the terse format)
//
// As with MemoryTool we put both ITool classes in one file since they share
// a source module on the Python side.

/// <summary>
/// Return the current local time and date as a human-readable string.
/// Mirrors the Python <c>current_time</c> tool.
/// </summary>
public sealed class CurrentTimeTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly ILogger<CurrentTimeTool> _log;

    public CurrentTimeTool(ILogger<CurrentTimeTool> log)
    {
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "current_time",
        Description: "Get the current local time and date.",
        Parameters: _parameters);

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        // Match the Python format exactly:
        //   "Monday, January 1, 2026 at 3:45 PM"
        // {DayName}, {MonthName} {Day}, {Year} at {Hour12}:{Minute:00} {AM/PM}
        var formatted = DateTime.Now.ToString(
            "dddd, MMMM d, yyyy 'at' h:mm tt",
            CultureInfo.InvariantCulture);
        return Task.FromResult(formatted);
    }
}

/// <summary>
/// Look up current weather via wttr.in. Empty location → wttr's IP geolocation.
/// Mirrors the Python <c>get_weather</c> tool.
/// </summary>
public sealed class GetWeatherTool : ITool
{
    private static readonly JsonObject _parameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["location"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "City, region, or empty for auto",
            },
        },
        ["required"] = new JsonArray { "location" },
    };

    // One HttpClient for the process. wttr.in returns a different (HTML-ish)
    // format for browsers, so we always send a curl User-Agent.
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly ILogger<GetWeatherTool> _log;

    public GetWeatherTool(ILogger<GetWeatherTool> log)
    {
        _log = log;
    }

    public ToolSchema Schema { get; } = new(
        Name: "get_weather",
        Description:
            "Get current weather for a location. Pass an empty string to " +
            "use the user's approximate location (by IP). Powered by wttr.in.",
        Parameters: _parameters);

    public async Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        var location = (arguments["location"]?.GetValue<string>() ?? string.Empty).Trim();
        // Python uses urllib.parse.quote_plus which encodes spaces as '+'.
        // Uri.EscapeDataString uses %20; we swap to '+' to match.
        var encoded = Uri.EscapeDataString(location).Replace("%20", "+");
        var fmt = "%l:+%c+%t,+feels+%f,+wind+%w,+humidity+%h";
        var url = $"https://wttr.in/{encoded}?format={fmt}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("curl/8");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Trim().Replace("+", " ");
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "wttr.in lookup failed for {Location}", location);
            return $"Weather lookup failed: {ex.Message}";
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces timeouts as TaskCanceledException (with no
            // user cancellation). If the caller actually cancelled, rethrow
            // so the orchestrator sees it; otherwise treat as a timeout.
            if (ct.IsCancellationRequested) throw;
            _log.LogWarning(ex, "wttr.in lookup timed out for {Location}", location);
            return $"Weather lookup failed: {ex.Message}";
        }
    }
}
