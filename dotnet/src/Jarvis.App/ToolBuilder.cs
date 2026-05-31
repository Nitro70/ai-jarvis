using System.Collections.Generic;
using System.IO;
using Jarvis.Core.Tools;
using Jarvis.Setup.NET.Models;
using Microsoft.Extensions.Logging;

namespace Jarvis.App;

/// <summary>
/// Builds the <see cref="ToolRegistry"/> from config toggles. Shared by the
/// main app's DI graph AND the --mcp-server mode (which exposes the same
/// tools to Claude Code), so the two can never drift out of sync.
/// </summary>
public static class ToolBuilder
{
    public static ToolRegistry Build(InstallConfig cfg, string installDir, ILoggerFactory log)
    {
        var tools = new List<ITool>();

        if (cfg.Tools.SystemInfo.Enabled)
        {
            tools.Add(new CurrentTimeTool(log.CreateLogger<CurrentTimeTool>()));
            tools.Add(new GetWeatherTool(log.CreateLogger<GetWeatherTool>()));
        }
        if (cfg.Tools.Memory.Enabled)
        {
            var memPath = Path.Combine(installDir, cfg.Persona.MemoryFile ?? "memory.md");
            tools.Add(new RememberTool(memPath, log.CreateLogger<RememberTool>()));
            tools.Add(new ForgetTool(memPath, log.CreateLogger<ForgetTool>()));
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
            tools.Add(new MinimizeWindowTool(log.CreateLogger<MinimizeWindowTool>()));
            tools.Add(new MaximizeWindowTool(log.CreateLogger<MaximizeWindowTool>()));
            tools.Add(new RestoreWindowTool(log.CreateLogger<RestoreWindowTool>()));
            tools.Add(new FocusWindowTool(log.CreateLogger<FocusWindowTool>()));
            tools.Add(new CloseWindowTool(log.CreateLogger<CloseWindowTool>()));
            tools.Add(new ListOpenWindowsTool(log.CreateLogger<ListOpenWindowsTool>()));
        }
        if (cfg.Tools.MusicYtmd.Enabled)
        {
            var ytmd = new MusicYtmdTool(
                cfg.Tools.MusicYtmd.Port,
                cfg.Tools.MusicYtmd.ExePath,
                installDir,
                log.CreateLogger<MusicYtmdTool>());
            foreach (var t in ytmd.Tools) tools.Add(t);
        }
        if (DangerousShellTools.IsEnabled(cfg.Tools.DangerousShell))
        {
            tools.Add(new RunShellTool(cfg.Tools.DangerousShell, log.CreateLogger<RunShellTool>()));
            tools.Add(new ReadFileTool(cfg.Tools.DangerousShell, log.CreateLogger<ReadFileTool>()));
            tools.Add(new WriteFileTool(cfg.Tools.DangerousShell, log.CreateLogger<WriteFileTool>()));
            tools.Add(new ListDirectoryTool(cfg.Tools.DangerousShell, log.CreateLogger<ListDirectoryTool>()));
        }

        return new ToolRegistry(tools);
    }
}
