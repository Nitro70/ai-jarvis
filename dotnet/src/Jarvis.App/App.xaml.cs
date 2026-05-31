using System;
using System.IO;
using System.Threading;
using System.Windows;
using Jarvis.Core;
using Jarvis.Core.Conversation;
using Jarvis.Core.Llm;
using Jarvis.Core.Tools;
using Jarvis.Setup.NET.Models;
using Jarvis.Setup.NET.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jarvis.App;

public partial class App : Application
{
    /// <summary>Per-user mutex prevents two instances of the .NET edition.
    /// Separate name from the Python edition's so both editions CAN coexist.</summary>
    private const string MutexName = @"Local\Jarvis.NET.SingleInstance.v1";
    private static Mutex? _instanceMutex;
    private static bool _ownsMutex;

    public IHost? Host { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // --mcp-server mode: instead of showing the WPF window, run as a
        // headless MCP stdio server exposing Jarvis's tools to Claude Code.
        // The claude_agent backend launches us this way (via --mcp-config) so
        // Claude Code can call play_music / open_app / etc. Must run BEFORE
        // the single-instance mutex (this is a short-lived child process of
        // the claude CLI and must NOT collide with a running UI instance).
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--mcp-server", StringComparison.OrdinalIgnoreCase))
            {
                string? cfgDir = null;
                if (i + 2 < e.Args.Length &&
                    string.Equals(e.Args[i + 1], "--install-dir", StringComparison.OrdinalIgnoreCase))
                    cfgDir = e.Args[i + 2];
                int code = RunMcpServerBlocking(cfgDir);
                Shutdown(code);
                return;
            }
        }

        // Single-instance check FIRST — no point spinning up DI if we're a duplicate.
        _instanceMutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        try { _ownsMutex = createdNew || _instanceMutex.WaitOne(0, exitContext: false); }
        catch (AbandonedMutexException) { _ownsMutex = true; }

        if (!_ownsMutex)
        {
            MessageBox.Show(
                "Jarvis is already running. Look in the system tray.",
                "Jarvis", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Locate install dir via the .NET-edition pointer file, falling back to
        // the .exe's directory (works for portable / dev-builds).
        var pointer = InstallLocator.LoadExisting();
        var installDir = pointer?.InstallDir;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            installDir = AppContext.BaseDirectory;

        // Load config.yaml (or get sensible defaults if it doesn't exist yet).
        var configService = new ConfigService();
        InstallConfig cfg;
        try
        {
            cfg = configService.Load(installDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't parse config.yaml in {installDir}:\n\n{ex.Message}\n\n" +
                "Open JarvisSettings-NET.exe to reconfigure, or fix the file by hand.",
                "Jarvis — config error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // DI container — Microsoft.Extensions.Hosting gives us logging +
        // service resolution. The conversation + backend get composed here so
        // MainWindow just resolves what it needs.
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(cfg);
                services.AddSingleton(configService);
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
                // TODO(phase 2): wire NLog or Serilog file sink to
                // <installDir>/<cfg.Logging.File>. Default Hosting console
                // logger is a no-op for WinExe (no console), so for now log
                // events are visible only via debugger output.

                // Tools — built from config toggles via the shared ToolBuilder
                // (same factory the --mcp-server mode uses, so they can't drift).
                services.AddSingleton<ToolRegistry>(sp =>
                    ToolBuilder.Build(cfg, installDir, sp.GetRequiredService<ILoggerFactory>()));

                // Backend — switch on cfg.Llm.Backend. All three Python-edition
                // backends now have C# ports as of v1.0.0.
                services.AddSingleton<ILlmBackend>(sp =>
                {
                    var systemPrompt = cfg.Persona.SystemPrompt;
                    var tools = sp.GetRequiredService<ToolRegistry>();
                    var lf = sp.GetRequiredService<ILoggerFactory>();
                    return cfg.Llm.Backend switch
                    {
                        "claude_agent" => new ClaudeAgentBackend(
                            cfg, systemPrompt, tools,
                            lf.CreateLogger<ClaudeAgentBackend>()),
                        "claude_api" => new ClaudeApiBackend(
                            cfg, systemPrompt, tools,
                            lf.CreateLogger<ClaudeApiBackend>()),
                        // Default for openai_compat AND any unknown backend value
                        // (so a typo in config.yaml doesn't crash the app — falls
                        // back to the most permissive option).
                        _ => new OpenAiCompatBackend(
                            cfg, systemPrompt, tools,
                            lf.CreateLogger<OpenAiCompatBackend>()),
                    };
                });

                // TTS sink — only register if enabled in config. When absent,
                // ConversationOrchestrator's ITtsSink? param stays null and the
                // reply just doesn't get spoken.
                if (cfg.Voice.Tts.Enabled)
                {
                    services.AddSingleton<ITtsSink>(sp =>
                        new Jarvis.Core.Voice.EdgeTtsSink(
                            cfg.Voice.Tts.Voice,
                            sp.GetRequiredService<ILoggerFactory>()));
                }

                services.AddSingleton<ConversationOrchestrator>(sp => new ConversationOrchestrator(
                    sp.GetRequiredService<ILlmBackend>(),
                    sp.GetRequiredService<ILogger<ConversationOrchestrator>>(),
                    sp.GetService<ITtsSink>()));
                services.AddSingleton<VoiceController>(sp =>
                {
                    var orch = sp.GetRequiredService<ConversationOrchestrator>();
                    // VoiceController emits events through the orchestrator's
                    // shared event channel so the UI sees them on the same
                    // stream as chat events. Fire-and-forget IProgress.
                    var events = new Progress<JarvisEvent>(orch.Emit);
                    return new VoiceController(
                        cfg, orch,
                        sp.GetRequiredService<ILoggerFactory>(),
                        events,
                        installDir);
                });
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // Stamp our version into the pointer file (or create the pointer fresh
        // if this is a first-run portable launch with no install-info.json).
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "0.0.0";
            var info = pointer ?? new InstallPointer { InstallDir = installDir };
            info.Version = version;
            info.Runtime = "dotnet";
            info.LastLaunched = DateTime.UtcNow;
            InstallLocator.Save(info);
        }
        catch { /* non-fatal — pointer is informational */ }

        var window = Host.Services.GetRequiredService<MainWindow>();
        window.Show();

        base.OnStartup(e);
    }

    /// <summary>
    /// Headless MCP-server entry point. Loads config from the given install
    /// dir (or the pointer / exe dir if null), builds the same ToolRegistry
    /// the UI uses, and serves MCP over stdin/stdout until the parent (the
    /// claude CLI) closes the pipe. Returns a process exit code.
    /// </summary>
    private static int RunMcpServerBlocking(string? installDirArg)
    {
        try
        {
            var installDir = installDirArg;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                installDir = InstallLocator.LoadExisting()?.InstallDir;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                installDir = AppContext.BaseDirectory;

            var cfg = new ConfigService().Load(installDir);

            // Minimal logger factory — log to stderr so it never corrupts the
            // stdout JSON-RPC stream. (Console logger writes to stderr by
            // default in this configuration would still be risky, so we keep
            // it quiet: warnings+ only, and the MCP server logs via it.)
            using var lf = LoggerFactory.Create(b => b
                .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
                .SetMinimumLevel(LogLevel.Warning));

            var registry = ToolBuilder.Build(cfg, installDir, lf);
            var server = new Jarvis.Core.Mcp.McpStdioServer(
                registry, lf.CreateLogger<Jarvis.Core.Mcp.McpStdioServer>());

            // Explicit UTF-8 standard streams. A WPF WinExe has no console, but
            // Console.OpenStandardInput/Output still return the redirected pipes
            // the claude CLI gave us. No BOM on output.
            using var stdin = new StreamReader(
                Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var stdout = new StreamWriter(
                Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = false };

            using var cts = new CancellationTokenSource();
            server.RunAsync(stdin, stdout, cts.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            // Last-ditch: write to stderr so the failure is at least visible in
            // claude --debug output. Never write to stdout (would corrupt MCP).
            try { Console.Error.WriteLine($"[jarvis-mcp] fatal: {ex}"); } catch { }
            return 1;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsMutex && _instanceMutex != null)
                _instanceMutex.ReleaseMutex();
        }
        catch { }
        _instanceMutex?.Dispose();
        Host?.Dispose();
        base.OnExit(e);
    }
}
