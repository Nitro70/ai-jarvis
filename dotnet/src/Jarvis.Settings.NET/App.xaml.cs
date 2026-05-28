using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Jarvis.Settings.NET;

/// <summary>
/// WPF app entry point with single-instance enforcement. Mirrors the
/// Python-edition Settings app's mutex+activate logic but uses a distinct
/// mutex name so the .NET edition can run alongside the Python edition's
/// Settings without one blocking the other.
/// </summary>
public partial class App : Application
{
    // Local\ scope confines the mutex to the current user session.
    // Distinct from BOTH the Python edition's Jarvis.Settings mutex and
    // the .NET edition's main-app mutex (Jarvis.NET.SingleInstance.v1).
    private const string MutexName = @"Local\Jarvis.NET.Settings.SingleInstance.v1";
    private static Mutex? _instanceMutex;
    private static bool _ownsMutex;

    [DllImport("user32.dll")]   private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]   private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]   private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]   private static extern bool IsWindowVisible(IntPtr hWnd);
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        bool ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                ownsMutex = _instanceMutex.WaitOne(0, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
        }

        if (!ownsMutex)
        {
            var activated = TryActivateExisting();
            if (activated)
            {
                Shutdown();
                return;
            }
            try
            {
                if (_instanceMutex!.WaitOne(2000, exitContext: false))
                    _ownsMutex = true;
            }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            if (!_ownsMutex)
            {
                Shutdown();
                return;
            }
        }
        else
        {
            _ownsMutex = true;
        }

        base.OnStartup(e);
    }

    private static bool TryActivateExisting()
    {
        Process current;
        try { current = Process.GetCurrentProcess(); }
        catch { return false; }

        Process[] others;
        try
        {
            others = Process.GetProcessesByName(current.ProcessName)
                .Where(p => p.Id != current.Id)
                .ToArray();
        }
        catch { return false; }

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
            return true;
        }

        foreach (var p in others)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
            catch { /* permissions / already exited / whatever */ }
        }
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsMutex && _instanceMutex != null)
                _instanceMutex.ReleaseMutex();
        }
        catch { }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            _ownsMutex = false;
        }
        base.OnExit(e);
    }
}
