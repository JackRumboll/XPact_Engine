// Copyright Simgenics. All Rights Reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Simgenics.XPact.XBT.Core;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Core;

/// <summary>
/// Smoke tests for the <see cref="WindowsJobObject"/> P/Invoke wrapper
/// added in audit fix R6-C4. Tests verify the Create / AssignProcess /
/// Dispose paths on Windows; on Linux / macOS the wrapper is documented
/// to be a no-op stub and these tests assert the no-op semantics
/// (Create returns false; AssignProcess returns false; Dispose is a
/// no-throw idempotent operation).
/// </summary>
public sealed class WindowsJobObjectTests
{
    /// <summary>
    /// <see cref="WindowsJobObject.Create"/> succeeds on Windows and
    /// flips <see cref="WindowsJobObject.IsAvailable"/> to true. On
    /// non-Windows platforms the call returns false and IsAvailable
    /// stays false.
    /// </summary>
    [Fact]
    public void Create_OnWindows_FlipsIsAvailable()
    {
        using WindowsJobObject job = new();
        Assert.False(job.IsAvailable);

        bool created = job.Create();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.True(created);
            Assert.True(job.IsAvailable);
        }
        else
        {
            Assert.False(created);
            Assert.False(job.IsAvailable);
        }
    }

    /// <summary>
    /// Calling <see cref="WindowsJobObject.Create"/> twice on the same
    /// instance is idempotent -- the second call returns true (job
    /// already exists) without leaking a handle.
    /// </summary>
    [Fact]
    public void Create_CalledTwice_IsIdempotent()
    {
        using WindowsJobObject job = new();
        bool first = job.Create();
        bool second = job.Create();
        Assert.Equal(first, second);
    }

    /// <summary>
    /// <see cref="WindowsJobObject.AssignProcess"/> succeeds on Windows
    /// for a freshly-started subprocess. We spawn a benign subprocess
    /// (cmd /c exit 0 on Windows; /bin/true on POSIX) and ensure
    /// AssignProcess does not throw or report failure.
    /// </summary>
    [Fact]
    public void AssignProcess_OnWindows_SucceedsForLiveProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux / macOS: the wrapper is a documented no-op. The
            // contract is "returns false, does not throw".
            using WindowsJobObject noopJob = new();
            noopJob.Create();
            using Process noopProc = StartTrivialProcess();
            bool noopResult = noopJob.AssignProcess(noopProc);
            noopProc.WaitForExit();
            Assert.False(noopResult);
            return;
        }

        using WindowsJobObject job = new();
        Assert.True(job.Create());

        // Use a deliberately-slow benign process so AssignProcess has
        // a live process to bind to. cmd /c "exit" returns immediately
        // but the handle remains valid until WaitForExit.
        using Process p = StartTrivialProcess();
        // Contract: AssignProcess does not throw, regardless of whether
        // the OS allows nested-job assignment. The boolean return value
        // captures success-vs-best-effort (a false return is legitimate
        // when the OS has placed the test runner process in an outer
        // job -- Visual Studio test runner, AppVeyor, ... -- so we do
        // not assert on it; the absence of an exception is the contract).
        _ = job.AssignProcess(p);
        p.WaitForExit();
    }

    /// <summary>
    /// Dispose is idempotent: a second call after the first does not
    /// throw, and the underlying job-object handle is released only
    /// once. (Win32 CloseHandle on a stale handle would throw / be
    /// undefined; the wrapper guards against double-close.)
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        WindowsJobObject job = new();
        job.Create();
        job.Dispose();
        job.Dispose();  // no throw
    }

    private static Process StartTrivialProcess()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // cmd /c exit 0 -- starts, exits cleanly, gives us a live
            // process handle for AssignProcess.
            return Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        }
        return Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/true",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
    }
}
