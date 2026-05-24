using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Services;

/// <summary>
/// "Test connection" button on the API key page. Sends a tiny request to the
/// configured backend and reports success / a human-readable error.
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

    // ---------------------------------------------------------------
    // claude_agent: just verify Claude Code CLI is installed.
    // ---------------------------------------------------------------
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

    // ---------------------------------------------------------------
    // claude_api: 1-token completion on /v1/messages.
    // ---------------------------------------------------------------
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

    // ---------------------------------------------------------------
    // openai_compat: 1-token completion on /chat/completions.
    // ---------------------------------------------------------------
    private static async Task<Result> TestOpenAiCompat(LlmConfig cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
            return new Result(false, "Base URL is empty.");

        // Local servers (Ollama, LM Studio, llama.cpp, etc.) don't use API
        // keys. Only require one for remote URLs. Matches the Python
        // openai_compat backend's "not-needed" fallback.
        var isLocal = cfg.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                   || cfg.BaseUrl.Contains("127.0.0.1")
                   || cfg.BaseUrl.Contains("0.0.0.0")
                   || cfg.BaseUrl.Contains("://[::1]");
        if (!isLocal && string.IsNullOrWhiteSpace(cfg.ApiKey))
            return new Result(false, "API key is empty (required for non-local URLs).");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // Send a bearer token if we have one; local servers usually ignore it.
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
