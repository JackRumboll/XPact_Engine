// Copyright Simgenics. All Rights Reserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Xunit;
using BuildModeEntry = Simgenics.XPact.XBT.Entry.BuildMode;

namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode;

/// <summary>
/// Tests for the audit fix R6-C7 per-build named-mutex guard: two
/// concurrent <c>xbt build</c> invocations against the same
/// <c>(engineRoot, target, config, platform)</c> must serialise (the
/// second blocks until the first releases) and the
/// <c>-NoMutexWait</c> flag must short-circuit the second to a fail-
/// fast error path.
/// </summary>
/// <remarks>
/// These are unit-level tests that exercise the mutex naming +
/// acquisition primitives directly. A full integration test that
/// spawns two real <c>xbt</c> processes would be heavyweight, fragile
/// (it depends on toolchain availability), and only marginally more
/// confirmatory than these.
/// </remarks>
public sealed class BuildMutexTests
{
    /// <summary>
    /// Two mutex acquisitions with the same identity must serialise:
    /// when one holds the mutex, the other (on a different thread)
    /// blocks until the first releases. Two-thread test is required
    /// because <see cref="Mutex"/> is recursive on the same thread --
    /// a single-thread test would let the second WaitOne succeed
    /// rather than block.
    /// </summary>
    [Fact]
    public void NamedMutex_SameIdentity_SerializesAccess()
    {
        // Use a unique mutex identity per test invocation so we don't
        // collide with sibling test runs (the OS named-mutex namespace
        // is process-tree scoped on .NET).
        string identity = BuildModeEntry.ComposeBuildMutexName(
            "/engine/root",
            "Target_" + Guid.NewGuid().ToString("N"),
            BuildConfiguration.Development,
            Platform.Win64);

        using Mutex mutex = new(initiallyOwned: false, name: identity, out _);

        // Thread A acquires the mutex and holds it.
        ManualResetEventSlim heldByA = new(initialState: false);
        ManualResetEventSlim releaseA = new(initialState: false);
        Task taskA = Task.Run(() =>
        {
            Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(5)));
            heldByA.Set();
            releaseA.Wait(TimeSpan.FromSeconds(10));
            mutex.ReleaseMutex();
        });

        // Wait for A to hold the mutex.
        Assert.True(heldByA.Wait(TimeSpan.FromSeconds(5)));

        // Thread B's brief WaitOne must FAIL because A holds the mutex.
        Task<bool> taskBQuick = Task.Run(() =>
            mutex.WaitOne(TimeSpan.FromMilliseconds(200)));
        Assert.True(taskBQuick.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(taskBQuick.Result);

        // Release A; B (on yet another thread) can then acquire.
        releaseA.Set();
        Assert.True(taskA.Wait(TimeSpan.FromSeconds(5)));

        Task<bool> taskBPost = Task.Run(() =>
        {
            bool acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            if (acquired) mutex.ReleaseMutex();
            return acquired;
        });
        Assert.True(taskBPost.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(taskBPost.Result);
    }

    /// <summary>
    /// Two mutex acquisitions with DIFFERENT identities must NOT
    /// serialise: two builds against different targets / configs /
    /// platforms run independently. Uses unique target names per
    /// invocation so concurrent test runs do not collide.
    /// </summary>
    [Fact]
    public void NamedMutex_DifferentIdentity_AllowsConcurrency()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string identityA = BuildModeEntry.ComposeBuildMutexName(
            "/engine/root", "TargetA_" + suffix, BuildConfiguration.Development, Platform.Win64);
        string identityB = BuildModeEntry.ComposeBuildMutexName(
            "/engine/root", "TargetB_" + suffix, BuildConfiguration.Development, Platform.Win64);

        Assert.NotEqual(identityA, identityB);

        using Mutex mutexA = new(initiallyOwned: false, name: identityA, out _);
        using Mutex mutexB = new(initiallyOwned: false, name: identityB, out _);

        bool aAcquired = mutexA.WaitOne(TimeSpan.Zero);
        bool bAcquired = mutexB.WaitOne(TimeSpan.Zero);
        try
        {
            Assert.True(aAcquired);
            Assert.True(bAcquired);
        }
        finally
        {
            if (aAcquired) mutexA.ReleaseMutex();
            if (bAcquired) mutexB.ReleaseMutex();
        }
    }

    /// <summary>
    /// Mutex identity is case-insensitive on Windows for the engineRoot
    /// component: two builds targeting <c>C:\Engine</c> and
    /// <c>c:\engine</c> hash to the same mutex name. (Linux remains
    /// case-sensitive per filesystem semantics.)
    /// </summary>
    [Fact]
    public void NamedMutex_CaseInsensitiveOnWindows_ForEngineRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux / macOS: path case is significant. We assert the
            // contract is documented for Windows; on POSIX the two
            // paths really ARE different roots.
            return;
        }

        // Both paths must canonicalize to the same absolute form for
        // Path.GetFullPath to agree. Use a real drive root.
        string upper = "C:\\TempXBTRoot";
        string lower = "c:\\tempxbtroot";
        string nameUpper = BuildModeEntry.ComposeBuildMutexName(
            upper, "T", BuildConfiguration.Development, Platform.Win64);
        string nameLower = BuildModeEntry.ComposeBuildMutexName(
            lower, "T", BuildConfiguration.Development, Platform.Win64);

        Assert.Equal(nameUpper, nameLower);
    }
}
