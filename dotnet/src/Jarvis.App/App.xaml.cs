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
        // NOTE: --mcp-server mode is handled entirely in Program.Main BEFORE
        // WPF starts (so the MCP child has no window / no taskbar entry). By
        // the time we reach OnStartup we're always the real GUI app.

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
