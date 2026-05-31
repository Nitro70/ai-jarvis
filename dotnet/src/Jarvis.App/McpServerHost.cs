using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Jarvis.Setup.NET.Services;
using Microsoft.Extensions.Logging;

namespace Jarvis.App;

/// <summary>
/// Headless MCP-server entry point. Runs WITHOUT any WPF involvement — no
/// Application object, no Dispatcher, no window, no taskbar entry. Invoked
/// from <see cref="Program.Main"/> the moment "--mcp-server" is seen on the
/// command line, before WPF would otherwise spin up.
///
/// Serves the Jarvis ToolRegistry over stdio MCP (JSON-RPC) so the
/// claude_agent backend's claude.exe child can call Jarvis's tools.
/// </summary>
public static class McpServerHost
{
    /// <summary>
    /// Returns true if the args request MCP-server mode. If so, the caller
    /// should run <see cref="Run"/> and exit without starting WPF.
    /// </summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, "--mcp-server", StringComparison.OrdinalIgnoreCase));

    /// <summary>Run the MCP server until stdin closes. Returns a process exit code.</summary>
    public static int Run(string[] args)
    {
        try
        {
            // Optional "--install-dir <path>" so the server loads the same
            // config.yaml the UI uses. Falls back to the pointer file / exe dir.
            string? installDirArg = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--install-dir", StringComparison.OrdinalIgnoreCase))
                {
                    installDirArg = args[i + 1];
                    break;
                }
            }

            var installDir = installDirArg;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                installDir = InstallLocator.LoadExisting()?.InstallDir;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                installDir = AppContext.BaseDirectory;

            var cfg = new ConfigService().Load(installDir);

            // Log to stderr only — stdout is the JSON-RPC channel and must stay clean.
            using var lf = LoggerFactory.Create(b => b
                .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
                .SetMinimumLevel(LogLevel.Warning));

            var registry = ToolBuilder.Build(cfg, installDir, lf);
            var server = new Jarvis.Core.Mcp.McpStdioServer(
                registry, lf.CreateLogger<Jarvis.Core.Mcp.McpStdioServer>());

            // Explicit UTF-8 standard streams. As a WinExe there's no console,
            // but the redirected pipes the claude CLI handed us still work via
            // Console.OpenStandardInput/Output. No BOM on output.
            using var stdin = new StreamReader(
                Console.OpenStandardInput(), new UTF8Encoding(false));
            using var stdout = new StreamWriter(
                Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

            using var cts = new CancellationTokenSource();
            server.RunAsync(stdin, stdout, cts.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[jarvis-mcp] fatal: {ex}"); } catch { }
            return 1;
        }
    }
}
