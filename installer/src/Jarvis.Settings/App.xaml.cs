using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Jarvis.Settings;

/// <summary>
/// WPF app entry point with single-instance enforcement.
///
/// Why: previously, double-clicking the Settings icon while an existing
/// instance was still alive (maybe minimized, maybe stuck behind another
/// window, maybe wedged) would just silently launch a second hidden
/// process. From the user's point of view, "clicking it does nothing."
///
/// Now we hold a named Mutex. A second launch finds the existing window
/// and brings it forward; if the existing process is hung with no window
/// (zombie), we kill it so the next click can start fresh.
/// </summary>
public partial class App : Application
{
    // Local\ scope confines the mutex to the current user session - the
    // right scope for a per-user desktop app. A Global\ mutex would also
    // block across different Windows users which we don't want.
    private const string MutexName = @"Local\Jarvis.Settings.SingleInstance.v1";
    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")]   private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]   private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]   private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]   private static extern bool IsWindowVisible(IntPtr hWnd);
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    protected override void OnStartup(StartupEventArgs e)
    {
        // initiallyOwned:false because we just want to take ownership AFTER
        // checking createdNew. WaitOne(0) is a non-blocking acquire.
        _instanceMutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        bool ownsMutex = createdNew;
        if (!createdNew)
        {
            // Mutex existed. Could be a live instance (normal case) or an
            // abandoned mutex from a process that crashed without releasing
            // it (rare but possible). Try to claim it briefly; if we can,
            // we WERE the only living instance after all.
            try
            {
                ownsMutex = _instanceMutex.WaitOne(0, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner crashed; the mutex was abandoned but
                // is now ours. Treat the same as a clean acquisition.
                ownsMutex = true;
            }
        }

        if (!ownsMutex)
        {
            // A real other instance is running. Surface it and exit.
            TryActivateExisting();
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Find any other JarvisSettings.exe processes and bring their main
    /// window forward. If none of them have a visible window (all hung /
    /// background), kill them so the user's next click starts clean.
    /// </summary>
    private static void TryActivateExisting()
    {
        Process current;
        try { current = Process.GetCurrentProcess(); }
        catch { return; }

        Process[] others;
        try
        {
            others = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .ToArray();
        }
        catch { return; }

        // Try to activate any visible window first.
        foreach (var p in others)
        {
            IntPtr handle;
            try { handle = p.MainWindowHandle; }
            catch { continue; }
            if (handle == IntPtr.Zero) continue;
            if (!IsWindowVisible(handle)) continue;

            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
            else                  ShowWindow(handle, SW_SHOW);
            SetForegroundWindow(handle);
            return;
        }

        // No visible window on any sibling - they're zombies. Take them
        // out so the user can re-launch into a working instance. We have
        // to release the mutex too, otherwise the freshly-started copy
        // they'll spawn will see it held by us.
        foreach (var p in others)
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* permissions / already exited / whatever */ }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch { /* ReleaseMutex throws if we never owned it; harmless on exit */ }
        finally { _instanceMutex = null; }
        base.OnExit(e);
    }
}
