// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests;

/// <summary>
/// Round-3 audit-fix verification suite. Each section maps 1:1 to an
/// audit finding from the Round-3 dispatch: C1 (CppDependencyCache)
/// lives in <c>Tests/ActionGraph/CppDependencyCacheTests.cs</c>; M2-M7
/// live here.
/// </summary>
/// <remarks>
/// This class is in the <see cref="ToolchainSelfHashCollection"/> serial
/// collection because the M2 tests mutate
/// <see cref="ToolchainSelfHash"/>'s process-wide override slot (via
/// <c>__SetForTesting(null)</c> to force the genuine compute path).
/// See that collection's remarks for the full rationale.
/// </remarks>
[Collection(nameof(ToolchainSelfHashCollection))]
public sealed class AuditRound3Tests : IDisposable
{
    private readonly string _scratchDir;

    public AuditRound3Tests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.AuditRound3",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    // ====================================================================
    // M2 -- ToolchainSelfHash now hashes the XBT.Core assembly file
    // rather than Process.MainModule. Under `dotnet test` the process'
    // MainModule is testhost.exe, totally unrelated to the XBT logic
    // being tested; the post-R3-M2 implementation hashes the assembly
    // file (.dll) which is the actual XBT logic in every supported
    // scenario.
    // ====================================================================

    /// <summary>
    /// The hash is computed against the XBT.Core assembly file on disk.
    /// We assert the hash is a valid 16-char hex (BLAKE3 truncation
    /// discipline) and matches a direct read of the same assembly file.
    /// </summary>
    [Fact]
    public void XbtBinaryHash_HashesXbtCoreAssemblyFile_NotProcessMainModule()
    {
        // Clear any override so we read the genuine compute path.
        ToolchainSelfHash.__SetForTesting(null);

        string actual = ToolchainSelfHash.XbtBinaryHash;

        // 16 hex chars (BLAKE3 truncation, per the engine-wide
        // IoHash-to-string convention).
        Assert.Equal(16, actual.Length);
        Assert.True(actual.All(static c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
            $"Expected 16 hex chars, got '{actual}'");

        // Recompute independently and compare. The assembly we hash is
        // the one carrying ToolchainSelfHash itself (XBT.Core); under
        // `dotnet test` this resolves to the bin/Debug copy of
        // XBT.Core.dll, NOT testhost.exe.
        string assemblyPath = typeof(ToolchainSelfHash).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(assemblyPath), "Assembly.Location must be non-empty under dotnet test");
        using FileStream fs = File.OpenRead(assemblyPath);
        IoHash digest = IoHash.Compute(fs);
        string expected = digest.ToString()[..16];

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The hash path NEVER returns the previous-revision sentinel
    /// <c>"(no-self-hash)"</c>. The R3-M2 fix removed the sentinel
    /// fall-back from the success path; an empty Assembly.Location now
    /// throws instead of silently returning the sentinel. The override
    /// hook still allows tests to inject any value, but the genuine
    /// compute path is sentinel-free under all production-supported
    /// scenarios.
    /// </summary>
    [Fact]
    public void XbtBinaryHash_NeverReturnsSentinelOnSuccessPath()
    {
        ToolchainSelfHash.__SetForTesting(null);
        string actual = ToolchainSelfHash.XbtBinaryHash;
        Assert.NotEqual("(no-self-hash)", actual);
    }

    // ====================================================================
    // M3 -- FileItem.EnsureStat now takes the per-instance lock
    // unconditionally. Under concurrent access the field reads happen
    // under the same monitor that the writes published under, so
    // ARM64 readers cannot observe a torn struct.
    // ====================================================================

    /// <summary>
    /// Stress <see cref="FileItem.Length"/> from many threads against the
    /// same FileItem instance. Each read must return the file's actual
    /// byte count -- a torn read would surface as a mismatched length on
    /// one of the threads.
    /// </summary>
    [Fact]
    public void EnsureStat_ConcurrentReads_AlwaysReturnConsistentLength()
    {
        const int contentLength = 1234;
        string path = Path.Combine(_scratchDir, "stat-stress.bin");
        File.WriteAllBytes(path, new byte[contentLength]);

        FileItem item = FileItem.GetItemByPath(path);

        const int threadCount = 16;
        const int iterations = 200;
        Exception?[] exceptions = new Exception?[threadCount];
        Thread[] threads = new Thread[threadCount];
        using var ready = new ManualResetEventSlim(false);

        for (int t = 0; t < threadCount; t++)
        {
            int threadIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    ready.Wait();
                    for (int i = 0; i < iterations; i++)
                    {
                        long len = item.Length;
                        if (len != contentLength)
                        {
                            throw new InvalidOperationException(
                                $"Torn read: expected {contentLength}, got {len}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions[threadIndex] = ex;
                }
            });
            threads[t].Start();
        }

        ready.Set();
        foreach (Thread th in threads)
        {
            th.Join();
        }

        foreach (Exception? ex in exceptions)
        {
            Assert.Null(ex);
        }
    }

    /// <summary>
    /// Audit fix R3-M3 regression: a fresh FileItem reads its
    /// <see cref="FileItem.LastWriteTimeUtc"/> + <see cref="FileItem.Length"/>
    /// atomically. The values are bound to the file on disk at the
    /// moment of the first call; under concurrent first-access the
    /// pair stays internally consistent (both reflect the same on-disk
    /// snapshot).
    /// </summary>
    [Fact]
    public void EnsureStat_FirstAccess_LengthAndMtimePairConsistent()
    {
        string path = Path.Combine(_scratchDir, "stat-pair.bin");
        File.WriteAllBytes(path, new byte[100]);
        DateTime expectedMtime = File.GetLastWriteTimeUtc(path);

        FileItem item = FileItem.GetItemByPath(path);
        long len = item.Length;
        DateTime mtime = item.LastWriteTimeUtc;

        Assert.Equal(100, len);
        Assert.Equal(expectedMtime, mtime);
    }

    // ====================================================================
    // M4 -- EnsureRandomizeLayoutSeedFile error logging. The Clang
    // toolchain emits the flag against a path that the helper must
    // create; if creation fails (read-only repo, AV lock, ...) the
    // failure must surface as a Logger.Warning, not a silent swallow.
    //
    // Direct exercising of the failure path requires mocking the file
    // system; we instead assert the SUCCESS path (the helper creates a
    // zero-byte file at the canonical path) so the helper is exercised
    // by every test that touches XClangToolChain. The diagnostic
    // emission path is reviewed by code-read; failure injection lives
    // in a future Phase 2 file-system shim.
    // ====================================================================

    /// <summary>
    /// Calling <see cref="XClangToolChain.CompileSource"/> on a writable
    /// repo creates the randomize-layout seed file at the canonical path
    /// and the file is zero bytes. No Logger.Warning is emitted under
    /// the success path (the diagnostic in R3-M4 only fires on IO
    /// failure).
    /// </summary>
    [Fact]
    public void RandomizeLayoutSeedFile_WritableRepo_CreatedEmpty()
    {
        // Build a Clang toolchain bound to our scratch dir as the
        // repo root; the seed-file relative path is hard-coded in the
        // toolchain, so the file lands under _scratchDir.
        string clangBin = Path.Combine(_scratchDir, "fake-clang");
        Directory.CreateDirectory(clangBin);
        string clangExe = Path.Combine(clangBin, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
        File.WriteAllText(clangExe, "stub");

        XClangToolChain toolchain = new(
            clangPath: clangExe,
            clangVersion: "test-18.0.0",
            platform: Platform.Linux,
            repoRoot: _scratchDir);

        ModuleRules module = new() { Name = "M", Tier = ModuleTier.Engine };
        TargetRules target = new()
        {
            Name = "T",
            TargetType = BuildTargetType.Game,
            Platform = Platform.Linux,
            Configuration = BuildConfiguration.Development,
        };

        FileItem source = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.cpp"));
        File.WriteAllText(source.FullPath, "// stub");

        // Triggers EnsureRandomizeLayoutSeedFile internally.
        toolchain.CompileSource(module, target, source, _scratchDir);

        string expectedSeedPath = Path.Combine(
            _scratchDir,
            "Engine", "Source", "Programs", "XBT", "randomize-layout.seed");
        Assert.True(File.Exists(expectedSeedPath),
            $"Expected randomize-layout seed file at {expectedSeedPath}");
        Assert.Equal(0, new FileInfo(expectedSeedPath).Length);
    }

    // ====================================================================
    // M6 -- Reflection-marker 64 KiB cap diagnostic. A file longer than
    // 64 KiB that has no marker in the scanned region is now treated as
    // marker-positive (over-invoke XHT) AND emits an Info-level
    // diagnostic so an operator can investigate.
    // ====================================================================

    /// <summary>
    /// A 100 KiB file with no marker in the scanned region is now
    /// claimed marker-positive (return true) per the R3-M6 fix.
    /// Pre-fix this returned false (silent miss).
    /// </summary>
    [Fact]
    public void FileContainsAny_OverMaxBytesWithNoMarkerInScan_ReportsMarkerPresent()
    {
        // 100 KiB of harmless comment + ASCII content, no marker
        // anywhere. The 64 KiB scan window will see no marker but the
        // file's total length exceeds the cap, so the helper now
        // returns true (over-invoke XHT) instead of false.
        string path = Path.Combine(_scratchDir, "big-no-marker.cpp");
        StringBuilder sb = new(110 * 1024);
        sb.AppendLine("// Long license header without any reflection marker.");
        while (sb.Length < 100 * 1024)
        {
            sb.AppendLine("// padding line content; nothing of note here");
        }
        File.WriteAllText(path, sb.ToString());

        bool result = Simgenics.XPact.XBT.Entry.BuildMode.FileContainsAny(
            path,
            new[] { "XCLASS(", "XSTRUCT(" });

        Assert.True(result, "File over 64 KiB with no marker in scan region must be claimed marker-positive (over-invoke XHT)");
    }

    /// <summary>
    /// A small file with no marker returns false (no over-invocation).
    /// </summary>
    [Fact]
    public void FileContainsAny_UnderMaxBytesWithNoMarker_ReturnsFalse()
    {
        string path = Path.Combine(_scratchDir, "small-no-marker.cpp");
        File.WriteAllText(path, "// no markers here\nint foo() { return 0; }\n");

        bool result = Simgenics.XPact.XBT.Entry.BuildMode.FileContainsAny(
            path,
            new[] { "XCLASS(", "XSTRUCT(" });

        Assert.False(result);
    }

    /// <summary>
    /// A file with a marker in the first KB still returns true even
    /// when the file is large -- the marker hit short-circuits before
    /// the cap check.
    /// </summary>
    [Fact]
    public void FileContainsAny_MarkerInScanRegion_StillReturnsTrue()
    {
        string path = Path.Combine(_scratchDir, "big-with-marker.cpp");
        StringBuilder sb = new(110 * 1024);
        sb.AppendLine("XCLASS(SomeClass)");
        while (sb.Length < 100 * 1024)
        {
            sb.AppendLine("// padding");
        }
        File.WriteAllText(path, sb.ToString());

        bool result = Simgenics.XPact.XBT.Entry.BuildMode.FileContainsAny(
            path,
            new[] { "XCLASS(", "XSTRUCT(" });

        Assert.True(result);
    }

    // ====================================================================
    // M7 -- VerifyNoBannedFlags is now load-bearing. The Clang + MSVC
    // emission paths both call it; a synthetic test that injects a
    // banned flag via a custom toolchain subclass surfaces the
    // exception.
    //
    // The production code paths cannot emit a banned flag today (the
    // helpers that produce the flag list never put -ffast-math /
    // /fp:fast on a sim-path module), so we test the verifier
    // contract directly via reflection on the private method.
    // ====================================================================

    /// <summary>
    /// Direct contract test: a banned flag in the args list on a
    /// SimPath module throws <see cref="ToolchainBannedFlagException"/>.
    /// Uses reflection to invoke the private static
    /// <c>VerifyNoBannedFlags(ModuleRules, IReadOnlyList&lt;string&gt;)</c>
    /// in <c>XClangToolChain</c> so the contract is exercised even
    /// though the production emission path does not produce the
    /// banned flag itself.
    /// </summary>
    [Fact]
    public void VerifyNoBannedFlags_Clang_SimPathBannedFlag_Throws()
    {
        MethodInfo? verify = typeof(XClangToolChain)
            .GetMethod("VerifyNoBannedFlags",
                BindingFlags.Static | BindingFlags.NonPublic,
                new[] { typeof(ModuleRules), typeof(IReadOnlyList<string>) });
        Assert.NotNull(verify);

        ModuleRules simPathModule = new()
        {
            Name = "SimMod",
            Tier = ModuleTier.Engine,
            SimPath = true,
        };
        IReadOnlyList<string> bannedArgs = new[]
        {
            "-c", "-O2", "-ffast-math", // banned -- must surface
        };
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => verify!.Invoke(null, new object[] { simPathModule, bannedArgs }));
        Assert.IsType<ToolchainBannedFlagException>(ex.InnerException);
    }

    /// <summary>
    /// VerifyNoBannedFlags is a no-op on non-SimPath modules: an
    /// otherwise-banned flag passes silently because the determinism
    /// contract only applies to sim-path TUs.
    /// </summary>
    [Fact]
    public void VerifyNoBannedFlags_Clang_NonSimPath_PermitsAnyFlag()
    {
        MethodInfo? verify = typeof(XClangToolChain)
            .GetMethod("VerifyNoBannedFlags",
                BindingFlags.Static | BindingFlags.NonPublic,
                new[] { typeof(ModuleRules), typeof(IReadOnlyList<string>) });
        Assert.NotNull(verify);

        ModuleRules nonSimPath = new()
        {
            Name = "NonSim",
            Tier = ModuleTier.Engine,
            SimPath = false,
        };
        IReadOnlyList<string> wouldBeBannedOnSimPath = new[] { "-ffast-math", "-Ofast" };
        verify!.Invoke(null, new object[] { nonSimPath, wouldBeBannedOnSimPath });
        // No throw -> non-SimPath flow ignores the flags. Pass.
    }

    /// <summary>
    /// MSVC-side equivalent: <c>/fp:fast</c> on a SimPath module
    /// surfaces as a banned-flag exception.
    /// </summary>
    [Fact]
    public void VerifyNoBannedFlags_Msvc_SimPathFpFast_Throws()
    {
        MethodInfo? verify = typeof(XMSVCToolChain)
            .GetMethod("VerifyNoBannedFlags",
                BindingFlags.Static | BindingFlags.NonPublic,
                new[] { typeof(ModuleRules), typeof(IReadOnlyList<string>) });
        Assert.NotNull(verify);

        ModuleRules simPathModule = new()
        {
            Name = "SimMsvc",
            Tier = ModuleTier.Engine,
            SimPath = true,
        };
        IReadOnlyList<string> args = new[] { "/c", "/fp:fast" };
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => verify!.Invoke(null, new object[] { simPathModule, args }));
        Assert.IsType<ToolchainBannedFlagException>(ex.InnerException);
    }

    /// <summary>
    /// MSVC's <c>/fp:except</c> is also banned on SimPath modules
    /// (environment-sensitive FP exception trapping).
    /// </summary>
    [Fact]
    public void VerifyNoBannedFlags_Msvc_SimPathFpExcept_Throws()
    {
        MethodInfo? verify = typeof(XMSVCToolChain)
            .GetMethod("VerifyNoBannedFlags",
                BindingFlags.Static | BindingFlags.NonPublic,
                new[] { typeof(ModuleRules), typeof(IReadOnlyList<string>) });
        Assert.NotNull(verify);

        ModuleRules simPathModule = new()
        {
            Name = "SimMsvc2",
            Tier = ModuleTier.Engine,
            SimPath = true,
        };
        IReadOnlyList<string> args = new[] { "/c", "/fp:except" };
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => verify!.Invoke(null, new object[] { simPathModule, args }));
        Assert.IsType<ToolchainBannedFlagException>(ex.InnerException);
    }

    // ====================================================================
    // R3-C1 (toolchain side) -- DependencyListFile populated.
    // CompileSource on both toolchains now ships a non-null
    // DependencyListFile pointing at the depfile the cache will parse.
    // ====================================================================

    /// <summary>
    /// Clang's CompileSource now populates
    /// <see cref="IExternalAction.DependencyListFile"/> with the .d file
    /// path (the same path the toolchain emits via <c>-MF</c>).
    /// </summary>
    [Fact]
    public void Clang_CompileSource_PopulatesDependencyListFile()
    {
        string clangBin = Path.Combine(_scratchDir, "fake-clang");
        Directory.CreateDirectory(clangBin);
        string clangExe = Path.Combine(clangBin, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
        File.WriteAllText(clangExe, "stub");

        XClangToolChain toolchain = new(
            clangPath: clangExe,
            clangVersion: "test-18.0.0",
            platform: Platform.Linux,
            repoRoot: _scratchDir);

        ModuleRules module = new() { Name = "M", Tier = ModuleTier.Engine };
        TargetRules target = new()
        {
            Name = "T",
            TargetType = BuildTargetType.Game,
            Platform = Platform.Linux,
            Configuration = BuildConfiguration.Development,
        };
        FileItem source = FileItem.GetItemByPath(Path.Combine(_scratchDir, "DLF.cpp"));
        File.WriteAllText(source.FullPath, "// stub");

        IExternalAction action = toolchain.CompileSource(module, target, source, _scratchDir).Single();

        Assert.NotNull(action.DependencyListFile);
        Assert.EndsWith(".d", action.DependencyListFile!.FullPath);
    }

    // M5 doc-only bump and CppDependencyCache documentation live in
    // XBT.html; they're verified by code-read, not by xUnit tests.
}
