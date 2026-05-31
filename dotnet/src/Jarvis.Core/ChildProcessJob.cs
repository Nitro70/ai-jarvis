using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core;

/// <summary>
/// A process-wide Windows Job Object configured with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Any process assigned to it is killed
/// automatically when this process (the one that owns the job handle) exits —
/// including a hard kill via Task Manager, because the OS closes the handle
/// on process termination and that triggers the kill-on-close behavior.
///
/// We use it so that the claude.exe processes the claude_agent backend spawns
/// (and THEIR children — the Jarvis-NET.exe --mcp-server MCP bridge, which
/// inherits the same job) all die when the main Jarvis-NET.exe dies. Without
/// it, a force-killed Jarvis could orphan claude + the MCP server.
///
/// Created lazily + once. Safe no-op on non-Windows (we only target Windows).
/// </summary>
public static class ChildProcessJob
{
    private static readonly object _lock = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _failed;

    /// <summary>
    /// Assign <paramref name="process"/> to the kill-on-close job. Best-effort:
    /// logs and returns on any failure (the backend still kills its own per-turn
    /// process tree explicitly, so this is defense-in-depth for the
    /// force-killed-parent case).
    /// </summary>
    public static void AssignProcess(Process process, ILogger? log = null)
    {
        try
        {
            var job = EnsureJob(log);
            if (job == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                var err = Marshal.GetLastWin32Error();
                // ERROR_ACCESS_DENIED (5) happens if the process is already in
                // a job that disallows nesting — rare on Win10/11 (nested jobs
                // are supported). Non-fatal.
                log?.LogDebug("AssignProcessToJobObject failed (win32={Err}) — relying on per-turn kill", err);
            }
        }
        catch (Exception e)
        {
            log?.LogDebug(e, "ChildProcessJob.AssignProcess failed (non-fatal)");
        }
    }

    private static IntPtr EnsureJob(ILogger? log)
    {
        if (_job != IntPtr.Zero) return _job;
        if (_failed) return IntPtr.Zero;
        lock (_lock)
        {
            if (_job != IntPtr.Zero) return _job;
            if (_failed) return IntPtr.Zero;

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                _failed = true;
                log?.LogDebug("CreateJobObject failed (win32={Err})", Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                {
                    _failed = true;
                    log?.LogDebug("SetInformationJobObject failed (win32={Err})", Marshal.GetLastWin32Error());
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            _job = job;
            log?.LogInformation("Child-process job object created (kill-on-close)");
            return _job;
        }
    }

    // ===================================================================
    //  P/Invoke
    // ===================================================================
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
