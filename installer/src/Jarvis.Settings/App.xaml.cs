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
    // Tracked so OnExit doesn't ReleaseMutex on a mutex we never owned.
    // ReleaseMutex throws ApplicationException for the non-owner case, and
    // the old code swallowed that — masking a real category error.
    private static bool _ownsMutex;

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
            // A real other instance is alive — try to surface its window.
            // If TryActivateExisting found and activated one, we're done:
            // exit so the user just sees the existing window focused.
            // If it killed zombies instead (no visible window anywhere),
            // it returns false — in that case the live process IS now
            // ours: take the mutex and fall through to start normally so
            // the user's click actually produces a window.
            var activated = TryActivateExisting();
            if (activated)
            {
                Shutdown();
                return;
            }
            // Re-acquire the mutex after the kill swept out the holder.
            try
            {
                if (_instanceMutex!.WaitOne(2000, exitContext: false))
                    _ownsMutex = true;
            }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            if (!_ownsMutex)
            {
                // Couldn't take it even after killing the zombies — give up
                // cleanly rather than running two instances.
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

    /// <summary>
    /// Find any other JarvisSettings.exe processes. Returns true if we
    /// activated an existing visible window (caller should exit). Returns
    /// false if all siblings were zombies — they've been killed and the
    /// caller should fall through and start as the new live instance.
    /// </summary>
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
            return true;
        }

        // No visible window on any sibling — they're zombies. Take them
        // out so we (the caller) can become the new live instance.
        bool killed = false;
        foreach (var p in others)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
                killed = true;
            }
            catch { /* permissions / already exited / whatever */ }
        }
        // Even if we killed nothing (no real siblings), return false so the
        // caller proceeds to acquire the mutex and start fresh.
        _ = killed;
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only ReleaseMutex if we actually owned it. The OS throws
        // ApplicationException ("Object synchronization method was called
        // from an unsynchronized block of code") if you call ReleaseMutex
        // without ownership — silently swallowing that was hiding a real
        // category error.
        try
        {
            if (_ownsMutex && _instanceMutex != null)
                _instanceMutex.ReleaseMutex();
        }
        catch { /* defensive — should not fire now that we gate on _ownsMutex */ }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            _ownsMutex = false;
        }
        base.OnExit(e);
    }
}
