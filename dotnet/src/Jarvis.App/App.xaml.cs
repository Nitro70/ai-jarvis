using System;
using System.IO;
using System.Threading;
using System.Windows;
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

                // Tools — built from config toggles.
                services.AddSingleton<ToolRegistry>(sp =>
                {
                    var log = sp.GetRequiredService<ILoggerFactory>();
                    var tools = new System.Collections.Generic.List<ITool>();
                    if (cfg.Tools.SystemInfo.Enabled)
                    {
                        tools.Add(new CurrentTimeTool(log.CreateLogger<CurrentTimeTool>()));
                        tools.Add(new GetWeatherTool(log.CreateLogger<GetWeatherTool>()));
                    }
                    if (cfg.Tools.Memory.Enabled)
                    {
                        var memPath = Path.Combine(installDir,
                            cfg.Persona.MemoryFile ?? "memory.md");
                        tools.Add(new RememberTool(memPath,
                            log.CreateLogger<RememberTool>()));
                        tools.Add(new ForgetTool(memPath,
                            log.CreateLogger<ForgetTool>()));
                    }
                    if (cfg.Tools.WebBrowser.Enabled)
                    {
                        tools.Add(new OpenUrlTool(log.CreateLogger<OpenUrlTool>()));
                        tools.Add(new PlayYoutubeVideoTool(log.CreateLogger<PlayYoutubeVideoTool>()));
                    }
                    if (cfg.Tools.WindowsApps.Enabled)
                    {
                        tools.Add(new WindowsAppsTool(log.CreateLogger<WindowsAppsTool>()));
                    }
                    if (cfg.Tools.WindowsState.Enabled)
                    {
                        // WindowsStateTool's subagent split this into 6 tools —
                        // minimize/maximize/restore/focus/close/list. Lazy-start the
                        // foreground tracker the first time any of them is invoked.
                        tools.Add(new MinimizeWindowTool(log.CreateLogger<MinimizeWindowTool>()));
                        tools.Add(new MaximizeWindowTool(log.CreateLogger<MaximizeWindowTool>()));
                        tools.Add(new RestoreWindowTool(log.CreateLogger<RestoreWindowTool>()));
                        tools.Add(new FocusWindowTool(log.CreateLogger<FocusWindowTool>()));
                        tools.Add(new CloseWindowTool(log.CreateLogger<CloseWindowTool>()));
                        tools.Add(new ListOpenWindowsTool(log.CreateLogger<ListOpenWindowsTool>()));
                    }
                    if (cfg.Tools.MusicYtmd.Enabled)
                    {
                        // MusicYtmdTool is composite — one parent class that owns the
                        // shared YtmdClient and exposes 7 sub-tools via .Tools.
                        var ytmd = new MusicYtmdTool(
                            cfg.Tools.MusicYtmd.Port,
                            cfg.Tools.MusicYtmd.ExePath,
                            installDir,
                            log.CreateLogger<MusicYtmdTool>());
                        foreach (var t in ytmd.Tools) tools.Add(t);
                    }
                    // Dangerous shell — double-gated. ONLY register if BOTH enabled
                    // AND ack are true. Either alone is not enough.
                    if (DangerousShellTools.IsEnabled(cfg.Tools.DangerousShell))
                    {
                        tools.Add(new RunShellTool(cfg.Tools.DangerousShell,
                            log.CreateLogger<RunShellTool>()));
                        tools.Add(new ReadFileTool(cfg.Tools.DangerousShell,
                            log.CreateLogger<ReadFileTool>()));
                        tools.Add(new WriteFileTool(cfg.Tools.DangerousShell,
                            log.CreateLogger<WriteFileTool>()));
                        tools.Add(new ListDirectoryTool(cfg.Tools.DangerousShell,
                            log.CreateLogger<ListDirectoryTool>()));
                    }
                    return new ToolRegistry(tools);
                });

                // Backend — Phase 1 only ships openai_compat. Other backends
                // (claude_api, claude_agent) are Phase 5 work.
                services.AddSingleton<ILlmBackend>(sp =>
                {
                    if (cfg.Llm.Backend != "openai_compat")
                    {
                        // Don't crash — yield a friendly warning. User can switch
                        // backend in Settings once more backends ship.
                        MessageBox.Show(
                            $"Backend '{cfg.Llm.Backend}' isn't supported in the .NET " +
                            "edition yet (Phase 1 ships openai_compat only). Falling " +
                            "back to openai_compat with whatever you configured.",
                            "Jarvis", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return new OpenAiCompatBackend(
                        cfg,
                        cfg.Persona.SystemPrompt,
                        sp.GetRequiredService<ToolRegistry>(),
                        sp.GetRequiredService<ILogger<OpenAiCompatBackend>>());
                });

                services.AddSingleton<ConversationOrchestrator>();
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
