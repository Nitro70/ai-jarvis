using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Tools;

// Port of tools/windows_state.py. The Python module exposes six tools
// (minimize_window, maximize_window, restore_window, focus_window,
// close_window, list_open_windows) backed by user32.dll. We mirror that one-
// to-one as six ITool classes. All Win32 plumbing lives in WindowsStateInterop
// (P/Invoke + window enumeration + fuzzy lookup), and ForegroundTracker keeps
// the "last user-focused window that isn't us" so 'this'/'current' resolves to
// whatever the user was looking at before alt-tabbing to the Jarvis console.
//
// Deviation from Python: instead of an asyncio poll loop at 100-300ms, we use
// SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT) which is
// event-driven and doesn't need a pump (OUTOFCONTEXT hooks dispatch via the
// thread's message queue when present, but the hook itself is registered on
// any thread; we run a dedicated background thread with a message loop so the
// callback fires regardless of whether the host process pumps messages).

#region Interop + shared helpers

internal static class WindowsStateInterop
{
    public const int SW_MAXIMIZE = 3;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Matches the Python _THIS_KEYWORDS set: any of these names targets the
    // last user-focused (non-Jarvis) window via ForegroundTracker.
    public static readonly HashSet<string> ThisKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "this window", "current", "current window",
        "active", "active window", "the active window", "it",
    };

    public static string WindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return string.Empty;
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var buf = new StringBuilder(length + 1);
        GetWindowText(hWnd, buf, length + 1);
        return buf.ToString().Trim();
    }

    public static List<(IntPtr hWnd, string title)> EnumVisibleWindows()
    {
        var results = new List<(IntPtr, string)>();
        // The delegate must stay alive for the duration of EnumWindows; passing
        // it directly is fine because EnumWindows is synchronous and we hold
        // the local reference throughout the call.
        EnumWindowsProc cb = (h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = WindowTitle(h);
            if (!string.IsNullOrEmpty(title))
                results.Add((h, title));
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return results;
    }

    /// <summary>
    /// Resolve a user-supplied window name to (hwnd, title). Mirrors Python's
    /// _find_window: 'this'/'current' → ForegroundTracker, then exact match,
    /// then shortest substring match, then difflib close-match (we approximate
    /// with a Ratcliff/Obershelp-style ratio).
    /// </summary>
    public static (IntPtr hWnd, string title)? FindWindow(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (ThisKeywords.Contains(name))
        {
            var hwnd = ForegroundTracker.Current;
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
                return (hwnd, WindowTitle(hwnd));
            return null;
        }

        var windows = EnumVisibleWindows();
        if (windows.Count == 0) return null;

        // Exact match (case-insensitive).
        var exact = windows.FirstOrDefault(w =>
            string.Equals(w.title, name, StringComparison.OrdinalIgnoreCase));
        if (exact.hWnd != IntPtr.Zero) return exact;

        // Shortest substring match — Python uses min(..., key=len(title)) so
        // the tightest match wins (e.g. "Discord" beats "1 - Discord - some
        // long suffix" when both contain 'discord').
        var subs = windows
            .Where(w => w.title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (subs.Count > 0)
            return subs.OrderBy(w => w.title.Length).First();

        // Fuzzy fallback. Python's difflib uses cutoff=0.55 with a SequenceMatcher
        // ratio. We approximate with a normalized longest-common-subsequence-ish
        // score (longest common substring / max len). It's not identical to
        // difflib but works fine for window-title matching.
        var best = (score: 0.0, idx: -1);
        for (var i = 0; i < windows.Count; i++)
        {
            var score = FuzzyRatio(name, windows[i].title);
            if (score > best.score)
                best = (score, i);
        }
        if (best.score >= 0.55 && best.idx >= 0)
            return windows[best.idx];

        return null;
    }

    private static double FuzzyRatio(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        // Longest common substring length, normalized by 2*lcs / (|a|+|b|) —
        // same formula difflib.SequenceMatcher.ratio() uses when there are no
        // duplicate matches, which is the common case for short names.
        var m = a.Length;
        var n = b.Length;
        var dp = new int[n + 1];
        var lcs = 0;
        for (var i = 1; i <= m; i++)
        {
            var prev = 0;
            for (var j = 1; j <= n; j++)
            {
                var temp = dp[j];
                if (a[i - 1] == b[j - 1])
                {
                    dp[j] = prev + 1;
                    if (dp[j] > lcs) lcs = dp[j];
                }
                else
                {
                    dp[j] = 0;
                }
                prev = temp;
            }
        }
        return 2.0 * lcs / (m + n);
    }
}

#endregion

#region Foreground tracker

/// <summary>
/// Tracks the most recent foreground window that isn't owned by our own
/// process. Used so 'this'/'current' in the windows_state tools resolves to
/// whatever the user was looking at before alt-tabbing to the Jarvis console.
///
/// Implementation: SetWinEventHook with EVENT_SYSTEM_FOREGROUND +
/// WINEVENT_OUTOFCONTEXT. We host the hook on a dedicated STA-ish background
/// thread that runs a Win32 message pump — OUTOFCONTEXT hooks are delivered
/// to the hooking thread's message queue, so the pump is required.
/// </summary>
public static class ForegroundTracker
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private static IntPtr _current;
    private static int _started; // 0 = not started, 1 = started (Interlocked)
    private static readonly uint _ownPid = (uint)Process.GetCurrentProcess().Id;

    // Hold a strong reference to the delegate so the GC can't collect it
    // while the native hook still points at it.
    private static WinEventDelegate? _callbackKeepAlive;

    /// <summary>HWND of the most recent non-Jarvis foreground window, or IntPtr.Zero.</summary>
    public static IntPtr Current => _current;

    /// <summary>Title of the most recent non-Jarvis foreground window (live lookup).</summary>
    public static string CurrentTitle => WindowsStateInterop.WindowTitle(_current);

    /// <summary>
    /// Idempotently start the tracker. Safe to call from any thread; the hook
    /// and its message pump live on a dedicated background thread.
    /// </summary>
    public static void EnsureStarted(ILogger? log = null)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;

        // Seed with whatever's currently in the foreground (if not us) so the
        // first 'this' call doesn't return nothing.
        var initial = WindowsStateInterop.GetForegroundWindow();
        if (initial != IntPtr.Zero)
        {
            WindowsStateInterop.GetWindowThreadProcessId(initial, out var pid);
            if (pid != _ownPid && !string.IsNullOrEmpty(WindowsStateInterop.WindowTitle(initial)))
                _current = initial;
        }

        var thread = new Thread(() => HookThread(log))
        {
            IsBackground = true,
            Name = "Jarvis.ForegroundTracker",
        };
        thread.Start();
        log?.LogInformation("foreground tracker started (own pid={Pid})", _ownPid);
    }

    private static void HookThread(ILogger? log)
    {
        _callbackKeepAlive = OnForegroundChanged;
        var hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callbackKeepAlive,
            0, 0, WINEVENT_OUTOFCONTEXT);

        if (hook == IntPtr.Zero)
        {
            log?.LogWarning("SetWinEventHook returned NULL — foreground tracker disabled");
            return;
        }

        try
        {
            // Pump messages so OUTOFCONTEXT callbacks fire. GetMessage returns
            // 0 on WM_QUIT, -1 on error.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnhookWinEvent(hook);
        }
    }

    private static void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // idObject == OBJID_WINDOW (0) for top-level window changes; ignore
        // child/menu/etc. events.
        if (hwnd == IntPtr.Zero || idObject != 0) return;
        try
        {
            WindowsStateInterop.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownPid) return;
            if (string.IsNullOrEmpty(WindowsStateInterop.WindowTitle(hwnd))) return;
            _current = hwnd;
        }
        catch
        {
            // Swallow — hook callbacks must not throw into native code.
        }
    }
}

#endregion

#region Tool classes — one per Python Tool() entry

// Shared JSON Schema: {"name": "string"} required. Matches Python _name_schema.
internal static class WindowsStateSchemas
{
    public static JsonObject NameSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray { "name" },
    };

    public static JsonObject EmptySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    public static string GetName(JsonObject arguments, string fallback = "this")
    {
        var raw = arguments["name"]?.GetValue<string>();
        var trimmed = (raw ?? string.Empty).Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }
}

public sealed class MinimizeWindowTool : ITool
{
    private readonly ILogger<MinimizeWindowTool> _log;
    public MinimizeWindowTool(ILogger<MinimizeWindowTool> log) { _log = log; }

    public ToolSchema Schema { get; } = new(
        Name: "minimize_window",
        Description:
            "Minimize a window. Pass 'this' or 'current' to minimize whatever " +
            "window the user was last looking at (excludes the JARVIS terminal). " +
            "Otherwise a fuzzy app/window name like 'Discord' or 'Chrome'.",
        Parameters: WindowsStateSchemas.NameSchema());

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        ForegroundTracker.EnsureStarted(_log);
        var name = WindowsStateSchemas.GetName(arguments);
        var found = WindowsStateInterop.FindWindow(name);
        if (found is null) return Task.FromResult($"Can't find a window matching '{name}'.");
        WindowsStateInterop.ShowWindow(found.Value.hWnd, WindowsStateInterop.SW_MINIMIZE);
        return Task.FromResult($"Minimized {found.Value.title}.");
    }
}

public sealed class MaximizeWindowTool : ITool
{
    private readonly ILogger<MaximizeWindowTool> _log;
    public MaximizeWindowTool(ILogger<MaximizeWindowTool> log) { _log = log; }

    public ToolSchema Schema { get; } = new(
        Name: "maximize_window",
        Description:
            "Maximize a window (fullscreen the app). 'this'/'current' targets " +
            "the user's last-focused window.",
        Parameters: WindowsStateSchemas.NameSchema());

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        ForegroundTracker.EnsureStarted(_log);
        var name = WindowsStateSchemas.GetName(arguments);
        var found = WindowsStateInterop.FindWindow(name);
        if (found is null) return Task.FromResult($"Can't find a window matching '{name}'.");
        WindowsStateInterop.ShowWindow(found.Value.hWnd, WindowsStateInterop.SW_MAXIMIZE);
        return Task.FromResult($"Maximized {found.Value.title}.");
    }
}

public sealed class RestoreWindowTool : ITool
{
    private readonly ILogger<RestoreWindowTool> _log;
    public RestoreWindowTool(ILogger<RestoreWindowTool> log) { _log = log; }

    public ToolSchema Schema { get; } = new(
        Name: "restore_window",
        Description: "Restore a minimized or maximized window to its normal size.",
        Parameters: WindowsStateSchemas.NameSchema());

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        ForegroundTracker.EnsureStarted(_log);
        var name = WindowsStateSchemas.GetName(arguments);
        var found = WindowsStateInterop.FindWindow(name);
        if (found is null) return Task.FromResult($"Can't find a window matching '{name}'.");
        WindowsStateInterop.ShowWindow(found.Value.hWnd, WindowsStateInterop.SW_RESTORE);
        return Task.FromResult($"Restored {found.Value.title}.");
    }
}

public sealed class FocusWindowTool : ITool
{
    private readonly ILogger<FocusWindowTool> _log;
    public FocusWindowTool(ILogger<FocusWindowTool> log) { _log = log; }

    public ToolSchema Schema { get; } = new(
        Name: "focus_window",
        Description:
            "Bring a window to the foreground (alt-tab to it). Use for 'switch " +
            "to X' or 'bring up X'.",
        Parameters: WindowsStateSchemas.NameSchema());

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        ForegroundTracker.EnsureStarted(_log);
        // Python rejects empty / 'this'-style names here because focusing the
        // already-focused window is a no-op the LLM shouldn't get to call.
        var raw = (arguments["name"]?.GetValue<string>() ?? string.Empty).Trim();
        if (raw.Length == 0 || WindowsStateInterop.ThisKeywords.Contains(raw))
            return Task.FromResult("Need a specific window name to focus.");

        var found = WindowsStateInterop.FindWindow(raw);
        if (found is null) return Task.FromResult($"Can't find a window matching '{raw}'.");
        // Restore first in case the target is minimized — SetForegroundWindow
        // alone won't unminimize.
        WindowsStateInterop.ShowWindow(found.Value.hWnd, WindowsStateInterop.SW_RESTORE);
        WindowsStateInterop.SetForegroundWindow(found.Value.hWnd);
        return Task.FromResult($"Switched to {found.Value.title}.");
    }
}

public sealed class CloseWindowTool : ITool
{
    private readonly ILogger<CloseWindowTool> _log;
    public CloseWindowTool(ILogger<CloseWindowTool> log) { _log = log; }

    public ToolSchema Schema { get; } = new(
        Name: "close_window",
        Description:
            "Close a window — equivalent to clicking the X. Apps with unsaved " +
            "work will still prompt to save.",
        Parameters: WindowsStateSchemas.NameSchema());

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        ForegroundTracker.EnsureStarted(_log);
        var name = WindowsStateSchemas.GetName(arguments);
        var found = WindowsStateInterop.FindWindow(name);
        if (found is null) return Task.FromResult($"Can't find a window matching '{name}'.");
        WindowsStateInterop.PostMessage(found.Value.hWnd,
            WindowsStateInterop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return Task.FromResult($"Closed {found.Value.title}.");
    }
}

public sealed class ListOpenWindowsTool : ITool
{
    private readonly ILogger<ListOpenWindowsTool> _log;
    public ListOpenWindowsTool(ILogger<ListOpenWindowsTool> log) { _log = log; }

    public ToolSchema Schema { get; } = new(
        Name: "list_open_windows",
        Description: "List the titles of all currently visible top-level windows.",
        Parameters: WindowsStateSchemas.EmptySchema());

    public Task<string> InvokeAsync(JsonObject arguments, CancellationToken ct)
    {
        ForegroundTracker.EnsureStarted(_log);
        var windows = WindowsStateInterop.EnumVisibleWindows();
        if (windows.Count == 0) return Task.FromResult("No visible windows.");
        // Sort + dedupe titles to match Python's sorted({t for _, t in windows}).
        var titles = windows
            .Select(w => w.title)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);
        return Task.FromResult("Open windows: " + string.Join(", ", titles));
    }
}

#endregion
