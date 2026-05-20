// Copyright Simgenics. All Rights Reserved.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Thin wrapper around the Win32 Job Object API. Used by
/// <c>ParallelExecutor</c> to ensure every subprocess (cl.exe, link.exe,
/// clang.exe, ...) is killed when XBT exits -- normal or crashed.
/// </summary>
/// <remarks>
/// <para>
/// Audit fix R6-C4 (UBT-parity). UBT achieves the same guarantee via
/// <c>FProcessTracking</c>; the .NET CLR exposes the underlying API
/// through P/Invoke.
/// </para>
/// <para>
/// <b>Why this exists.</b> If XBT crashes (segfault in a managed call,
/// kill -9 from the operator, hung mutex, etc.) mid-build, every cl.exe
/// / clang.exe child process is orphaned. The OS does NOT terminate
/// orphans by default on Windows; they continue running with file
/// handles open against the build's intermediate output, blocking the
/// next XBT invocation from cleaning up its temp files. The job object
/// fixes this by binding the child's lifetime to the job handle: when
/// the last handle to the job closes (which happens automatically when
/// XBT exits, normal or otherwise), the OS sends a kill to every
/// process in the job.
/// </para>
/// <para>
/// <b>Flags.</b>
/// </para>
/// <list type="bullet">
///   <item><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the kill-on-close
///   behaviour described above.</item>
///   <item><c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>: a child process can
///   explicitly opt out via <c>CREATE_BREAKAWAY_FROM_JOB</c> at spawn
///   time. Subprocesses that legitimately need to outlive XBT (e.g. a
///   future XPactBuildAccelerator agent) can use this. Compile / link
///   processes do not opt out; they live and die with the build.</item>
/// </list>
/// <para>
/// <b>Platform.</b> Windows-only. On Linux / macOS this type is a
/// thin no-op shell -- POSIX achieves a similar effect via process
/// groups (<c>setpgid</c>) and Phase 2 will wire that up when the
/// Linux/Android toolchains start dispatching long-running children.
/// In Phase 1 the Linux executor is local-only and short-lived, so the
/// no-op is acceptable.
/// </para>
/// </remarks>
public sealed class WindowsJobObject : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private bool _disposed;

    /// <summary>
    /// True when this wrapper holds a live Win32 job object handle.
    /// On Linux / macOS this is always false (the constructor is a
    /// no-op there). Callers gate their <see cref="AssignProcess"/>
    /// calls on <see cref="IsAvailable"/> rather than checking the
    /// platform directly.
    /// </summary>
    public bool IsAvailable => _handle != IntPtr.Zero;

    /// <summary>
    /// Construct an empty wrapper. Call <see cref="Create"/> to allocate
    /// the underlying job object; on a non-Windows host
    /// <see cref="Create"/> is a no-op.
    /// </summary>
    public WindowsJobObject()
    {
    }

    /// <summary>
    /// Allocate a fresh Win32 job object with
    /// <c>KILL_ON_JOB_CLOSE | BREAKAWAY_OK</c> set. Returns true on
    /// success. On non-Windows platforms this is a no-op that returns
    /// false (callers gate their <see cref="AssignProcess"/> usage on
    /// <see cref="IsAvailable"/>).
    /// </summary>
    /// <remarks>
    /// On Windows: <c>CreateJobObject</c> + <c>SetInformationJobObject</c>
    /// with <c>JobObjectExtendedLimitInformation</c>. Failures are
    /// logged via <see cref="Logger.Warning"/> and the wrapper falls
    /// back to its no-op state -- the build continues without job-object
    /// protection rather than aborting.
    /// </remarks>
    public bool Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return CreateWindowsImpl();
    }

    [SupportedOSPlatform("windows")]
    private bool CreateWindowsImpl()
    {
        if (_handle != IntPtr.Zero)
        {
            return true;
        }

        IntPtr h = CreateJobObject(IntPtr.Zero, lpName: null);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Warning(
                $"CreateJobObject failed with Win32 error {err}; XBT child " +
                "processes will not be auto-killed on crash. Build continues.",
                new DiagnosticContext { Action = "job-object-create" });
            return false;
        }

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
        info.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;

        int infoSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(infoSize);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    h,
                    JobObjectInfoClass.JobObjectExtendedLimitInformation,
                    buffer,
                    (uint)infoSize))
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Warning(
                    $"SetInformationJobObject failed with Win32 error {err}; " +
                    "job object created but limit flags not set. Build continues.",
                    new DiagnosticContext { Action = "job-object-set-info" });
                CloseHandle(h);
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _handle = h;
        return true;
    }

    /// <summary>
    /// Assign <paramref name="process"/> to the job. Best-effort: if
    /// the assignment fails (process already exited; nested job objects
    /// confused by an outer enforcer; Wine quirks; ...) the failure is
    /// logged via <see cref="Logger.Warning"/> and the method returns
    /// false. Callers continue with the spawn as if no job were in
    /// effect -- the worst case is that the orphan must be cleaned up
    /// by the next startup sweep.
    /// </summary>
    public bool AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!IsAvailable)
        {
            return false;
        }
        return AssignProcessWindowsImpl(process);
    }

    [SupportedOSPlatform("windows")]
    private bool AssignProcessWindowsImpl(Process process)
    {
        IntPtr ph;
        try
        {
            ph = process.Handle;
        }
        catch (InvalidOperationException)
        {
            // Process is detached or has not been started; we cannot
            // assign it. Caller is on the same code path as a child
            // that exited between Start and Assign; same treatment.
            return false;
        }

        if (!AssignProcessToJobObject(_handle, ph))
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED (5) is common when the OS already
            // placed the process in an outer job that doesn't allow
            // nesting (e.g. Visual Studio's parent job). Log + continue.
            Logger.Warning(
                $"AssignProcessToJobObject failed for pid {process.Id} with " +
                $"Win32 error {err}; the subprocess will not be auto-killed " +
                "on XBT exit. Orphan-temp-file sweep at next startup will " +
                "still clean its outputs.",
                new DiagnosticContext { Action = "job-object-assign" });
            return false;
        }
        return true;
    }

    /// <summary>
    /// Close the job-object handle. Per Win32 documentation, closing the
    /// last handle to a job with <c>KILL_ON_JOB_CLOSE</c> set causes the
    /// OS to terminate every process currently in the job. Idempotent;
    /// safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Finalizer to ensure the job-object handle is closed -- and thus
    /// every assigned subprocess killed -- even if <see cref="Dispose"/>
    /// is not invoked. The CLR's process-exit hook closes file handles
    /// but not raw Win32 handles; we depend on this finalizer firing on
    /// AppDomain unload.
    /// </summary>
    ~WindowsJobObject()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
        }
    }

    // ----- Win32 P/Invoke ----------------------------------------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;

    private enum JobObjectInfoClass
    {
        JobObjectExtendedLimitInformation = 9,
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
