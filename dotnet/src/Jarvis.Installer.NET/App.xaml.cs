using System;
using System.Threading;
using System.Windows;

namespace Jarvis.Installer.NET;

public partial class App : Application
{
    // Distinct from the main Jarvis-NET app mutex
    // (Local\Jarvis.NET.SingleInstance.v1) so the installer can run
    // while the app is also installed and not stomp each other.
    private const string MutexName = "Local\\Jarvis.NET.Installer.SingleInstance.v1";

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Jarvis (.NET) Installer is already running.",
                "Jarvis Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
