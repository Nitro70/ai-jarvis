using System;

namespace Jarvis.App;

/// <summary>
/// Real entry point (set via &lt;StartupObject&gt; in the csproj). Routes
/// "--mcp-server" to the headless MCP host BEFORE any WPF/Application code
/// runs — that's what guarantees the MCP child process has no window and no
/// taskbar entry (WPF never initializes). Everything else flows into the
/// normal WPF App.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpServerHost.IsRequested(args))
            return McpServerHost.Run(args);

        // Normal GUI startup. App is the WPF Application; its generated
        // InitializeComponent + Run live in App.g.cs. We construct + run it
        // by hand here since we've taken over Main.
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
