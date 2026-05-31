using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Setup.NET.Models;

namespace Jarvis.Settings.NET.Services;

/// <summary>
/// "Test connection" button on the AI backend tab. Sends a tiny request to
/// the configured backend and reports success / a human-readable error.
/// Adapted from the Python edition's ApiTester. The .NET edition only ships
/// openai_compat today (claude_agent / claude_api land in v0.5.0); we still
/// support the test buttons for all three so the UI is forward-compatible.
/// </summary>
public static class ApiTester
{
    public record Result(bool Ok, string Message);

    public static async Task<Result> TestAsync(LlmConfig cfg, CancellationToken ct = default)
    {
        try
        {
            return cfg.Backend switch
            {
                "claude_agent"   => TestClaudeAgent(),
                "claude_api"     => await TestClaudeApi(cfg, ct),
                "openai_compat"  => await TestOpenAiCompat(cfg, ct),
                _                => new Result(false, $"Unknown backend: {cfg.Backend}"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return new Result(false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static Result TestClaudeAgent()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return new Result(false, "Could not start `claude`.");
            p.WaitForExit(5000);
            if (p.ExitCode == 0)
            {
                var v = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
                return new Result(true, $"Claude Code found: {v}");
            }
            return new Result(false, "`claude --version` failed. Is Claude Code installed and on PATH?");
        }
        catch (Exception e)
        {
            return new Result(false,
                $"Claude Code (`claude`) not found on PATH. Install from https://claude.com/code — {e.Message}");
        }
    }

    private static async Task<Result> TestClaudeApi(LlmConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            return new Result(false, "API key is empty.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("x-api-key", cfg.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var body = new
        {
            model = cfg.Model,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "hi" } },
        };

        using var resp = await http.PostAsJsonAsync("https://api.anthropic.com/v1/messages", body, ct);
        if (resp.IsSuccessStatusCode)
            return new Result(true, $"OK ({cfg.Model})");

        var err = await resp.Content.ReadAsStringAsync(ct);
        return new Result(false, $"HTTP {(int)resp.StatusCode}: {Truncate(err, 250)}");
    }

    private static async Task<Result> TestOpenAiCompat(LlmConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
            return new Result(false, "Base URL is empty.");

        var isLocal = cfg.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                   || cfg.BaseUrl.Contains("127.0.0.1")
                   || cfg.BaseUrl.Contains("0.0.0.0")
                   || cfg.BaseUrl.Contains("://[::1]");
        if (!isLocal && string.IsNullOrWhiteSpace(cfg.ApiKey))
            return new Result(false, "API key is empty (required for non-local URLs).");

        // Local servers (Ollama especially) load the model on first request,
        // which can take 30-40s on a cold start. Use a generous timeout for
        // local URLs; remote APIs answer fast.
        var timeout = isLocal ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(20);
        using var http = new HttpClient { Timeout = timeout };
        var key = string.IsNullOrWhiteSpace(cfg.ApiKey) ? "not-needed" : cfg.ApiKey;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var body = new
        {
            model = cfg.Model,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "hi" } },
        };

        var url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        try
        {
            using var resp = await http.PostAsJsonAsync(url, body, ct);
            if (resp.IsSuccessStatusCode)
                return new Result(true, $"OK ({cfg.Model} via {cfg.BaseUrl})");

            var err = await resp.Content.ReadAsStringAsync(ct);
            return new Result(false, $"HTTP {(int)resp.StatusCode}: {Truncate(err, 250)}");
        }
        catch (HttpRequestException e) when (isLocal)
        {
            return new Result(false,
                $"Couldn't reach {cfg.BaseUrl}. Is your local server (Ollama / LM Studio) running? " +
                $"({e.Message})");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}
