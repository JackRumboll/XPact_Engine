// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Toolchain;

/// <summary>
/// Verifies <see cref="XMSVCToolChain"/> emits the documented flag set
/// per Toolchain Contract Rev 13 Section 4.2 (MSVC row) and
/// <c>/Documents/XBT.html</c> Rev 4 Section 19.1 (reproducibility
/// envelope).
/// </summary>
/// <remarks>
/// <para>
/// Tests construct a synthetic <see cref="VCEnvironment"/> so the
/// toolchain code path runs deterministically without depending on a
/// real MSVC install. The flag-emission assertions inspect the
/// generated <see cref="IExternalAction.CommandArguments"/> directly.
/// </para>
/// <para>
/// This class is in the <see cref="ToolchainSelfHashCollection"/> serial
/// collection because tests in it mutate
/// <see cref="Simgenics.XPact.XBT.Core.ToolchainSelfHash"/>'s process-wide
/// override slot (via <c>__SetForTesting</c>). See that collection's
/// remarks for the full rationale.
/// </para>
/// </remarks>
[Collection(nameof(ToolchainSelfHashCollection))]
public sealed class XMSVCToolChainTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly VCEnvironment _env;
    private readonly XMSVCToolChain _toolchain;

    public XMSVCToolChainTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.MSVC",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);

        _env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\VC\Tools\MSVC\14.40\bin\HostX64\x64\cl.exe",
            linkerPath: @"C:\FakeVS\VC\Tools\MSVC\14.40\bin\HostX64\x64\link.exe");
        _toolchain = new XMSVCToolChain(_env, repoRoot: @"C:\repo");
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

    /// <summary>
    /// SimPath module emits /fp:precise (resolved via FPSemantics
    /// auto-promotion in XToolChain.ResolveFPSemantics).
    /// </summary>
    [Fact]
    public void SimPathModule_EmitsFpPrecise()
    {
        ModuleRules module = NewModule(simPath: true);
        TargetRules target = NewTarget();

        var actions = _toolchain.CompileSource(module, target, MakeSource("XPhysics.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("/fp:precise", compile.CommandArguments);
        Assert.DoesNotContain("/fp:fast", compile.CommandArguments);
    }

    /// <summary>
    /// Audit fix C6: Non-SimPath module with FPSemantics.Default emits
    /// NO <c>/fp:</c> flag. The compiler-default <c>/fp:precise</c>
    /// applies; this delegation is the locked Phase 1 policy.
    /// </summary>
    [Fact]
    public void NonSimPathModule_FpDefault_EmitsNoFpFlag()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        var actions = _toolchain.CompileSource(module, target, MakeSource("XRenderer.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.DoesNotContain("/fp:fast", compile.CommandArguments);
        Assert.DoesNotContain("/fp:precise", compile.CommandArguments);
    }

    /// <summary>
    /// Audit fix C6: explicit FPSemantics.Imprecise still emits /fp:fast
    /// when the module opts in. (Default no longer maps to Imprecise.)
    /// </summary>
    [Fact]
    public void NonSimPathModule_FpImprecise_EmitsFpFast()
    {
        ModuleRules module = NewModule(simPath: false, fp: FPSemantics.Imprecise);
        TargetRules target = NewTarget();

        var actions = _toolchain.CompileSource(module, target, MakeSource("XRenderer.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("/fp:fast", compile.CommandArguments);
    }

    /// <summary>
    /// Non-SimPath module with SimdLevel.AVX2 emits /arch:AVX2.
    /// </summary>
    [Fact]
    public void NonSimPathModule_Avx2_EmitsArchAvx2()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.AVX2);
        TargetRules target = NewTarget();

        var actions = _toolchain.CompileSource(module, target, MakeSource("XAvxStuff.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("/arch:AVX2", compile.CommandArguments);
    }

    /// <summary>
    /// SimPath module declaring SimdLevel.AVX is rejected at flag-derivation
    /// time with exit code 41 (banned-flag check on SimPath module).
    /// </summary>
    [Fact]
    public void SimPathModule_DeclaringAvx_RejectedWithExit41()
    {
        ModuleRules module = NewModule(simPath: true, simdLevel: SimdLevel.AVX);
        TargetRules target = NewTarget();

        ToolchainBannedFlagException ex = Assert.Throws<ToolchainBannedFlagException>(
            () => _toolchain.CompileSource(module, target, MakeSource("Bad.cpp"), _scratchDir));
        Assert.Equal(41, ex.ExitCode);
    }

    /// <summary>
    /// Reproducibility envelope (XBT.html Section 19.1 + Contract
    /// Section 2.1, Rev 13.1): every MSVC compile emits
    /// /Brepro + /pathmap:&lt;root&gt;=X:/R; every MSVC link emits
    /// /TIMESTAMP:0 + /BREPRO + /INCREMENTAL:NO + /cgthreads:8.
    /// </summary>
    [Fact]
    public void ReproducibilityFlags_PresentOnEveryCompileAndLink()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        // Compile.
        var compileActions = _toolchain.CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir);
        IExternalAction compile = compileActions.Single();
        Assert.Contains("/Brepro", compile.CommandArguments);
        Assert.Contains(@"/pathmap:C:\repo=X:/R", compile.CommandArguments);

        // Link. The link action emits the full arg list (envelope flags
        // + libpaths + system libs + .obj paths + /OUT) into
        // ResponseFileContents per the unconditional response-file
        // pattern; CommandArguments is empty and ProcessActionRunner
        // appends "@<rsp>" before invoking link.exe.
        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = _toolchain.LinkModule(module, target, new[] { obj }, _scratchDir);
        Assert.NotNull(link.ResponseFileContents);
        Assert.Contains("/BREPRO", link.ResponseFileContents!);
        Assert.Contains("/TIMESTAMP:0", link.ResponseFileContents!);
        Assert.Contains("/INCREMENTAL:NO", link.ResponseFileContents!);
        Assert.Contains("/cgthreads:8", link.ResponseFileContents!);
    }

    /// <summary>
    /// The mock VCEnvironment supplies the compiler path; the constructed
    /// action's CommandPath matches what we passed.
    /// </summary>
    [Fact]
    public void CompileAction_CommandPathMatchesEnvironment()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        var actions = _toolchain.CompileSource(module, target, MakeSource("Trivial.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Equal(_env.CompilerPath, compile.CommandPath);
        // WorkingDirectory is the repo root passed to the toolchain ctor.
        Assert.Equal(@"C:\repo", compile.WorkingDirectory);
        // Already verified via separate test, but cross-check the source is in args:
        Assert.Contains(compile.CommandArguments, a => a.EndsWith("Trivial.cpp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Phase 1.4a: CompileSource emits <c>/I</c> for every entry in the
    /// composite include path list (MSVC + Windows SDK). Module-private
    /// /I flags still come first per the spec; the system includes are
    /// appended in the deterministic VCEnvironment order.
    /// </summary>
    [Fact]
    public void CompileSource_EmitsIncludeFlagsForEnvironmentPaths()
    {
        // Build a sandboxed environment with explicit MSVC + SDK include
        // paths so the assertion does not depend on whatever the host
        // happens to have installed.
        string sdkRoot = Path.Combine(_scratchDir, "FakeSdk");
        foreach (string sub in new[] { "um", "shared", "ucrt", "winrt" })
        {
            Directory.CreateDirectory(Path.Combine(sdkRoot, "Include", "10.0.26100.0", sub));
        }
        foreach (string sub in new[] { "um", "ucrt" })
        {
            Directory.CreateDirectory(Path.Combine(sdkRoot, "Lib", "10.0.26100.0", sub, "x64"));
        }

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\VC\Tools\MSVC\14.40\bin\HostX64\x64\cl.exe",
            linkerPath: @"C:\FakeVS\VC\Tools\MSVC\14.40\bin\HostX64\x64\link.exe",
            msvcIncludePaths: new[] { @"C:\FakeMSVC\include" },
            sdkRoot: sdkRoot);

        XMSVCToolChain toolchain = new(env, repoRoot: @"C:\repo");
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        var actions = toolchain.CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        // Every IncludePaths entry must appear as a /I<path> arg.
        foreach (string inc in env.IncludePaths)
        {
            Assert.Contains($"/I{inc}", compile.CommandArguments);
        }

        // MSVC include comes before any SDK include in the command line.
        int msvcIdx = -1;
        int firstSdkIdx = -1;
        for (int i = 0; i < compile.CommandArguments.Count; i++)
        {
            string arg = compile.CommandArguments[i];
            if (msvcIdx < 0 && arg == @"/IC:\FakeMSVC\include")
            {
                msvcIdx = i;
            }
            if (firstSdkIdx < 0 && arg.Contains(@"\10.0.26100.0\um", StringComparison.Ordinal))
            {
                firstSdkIdx = i;
            }
        }
        Assert.True(msvcIdx >= 0, "MSVC /I flag not found.");
        Assert.True(firstSdkIdx >= 0, "SDK /I flag not found.");
        Assert.True(msvcIdx < firstSdkIdx,
            $"MSVC /I (idx {msvcIdx}) must precede SDK /I (idx {firstSdkIdx}).");
    }

    /// <summary>
    /// Phase 1.4a: LinkModule emits <c>/LIBPATH:</c> for every entry in
    /// the composite library path list PLUS the standard Win32 system
    /// import libs (kernel32, user32, ...). Both must be present on
    /// every link command so a downstream test can spot a missing one
    /// without re-running the linker.
    /// </summary>
    [Fact]
    public void LinkModule_EmitsLibPathFlagsAndDefaultSystemLibs()
    {
        string sdkRoot = Path.Combine(_scratchDir, "FakeSdk");
        foreach (string sub in new[] { "um", "shared", "ucrt", "winrt" })
        {
            Directory.CreateDirectory(Path.Combine(sdkRoot, "Include", "10.0.26100.0", sub));
        }
        foreach (string sub in new[] { "um", "ucrt" })
        {
            Directory.CreateDirectory(Path.Combine(sdkRoot, "Lib", "10.0.26100.0", sub, "x64"));
        }

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\cl.exe",
            linkerPath: @"C:\FakeVS\link.exe",
            msvcLibraryPaths: new[] { @"C:\FakeMSVC\lib\x64" },
            sdkRoot: sdkRoot);

        XMSVCToolChain toolchain = new(env, repoRoot: @"C:\repo");
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = toolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        // The link action's full arg list lives in ResponseFileContents
        // (envelope flags + /LIBPATH + system libs + .obj paths + /OUT)
        // per the unconditional response-file pattern. CommandArguments
        // is empty; ProcessActionRunner appends "@<rsp>" to the visible
        // command line before invoking link.exe.
        Assert.NotNull(link.ResponseFileContents);
        string rsp = link.ResponseFileContents!;

        // Every LibraryPaths entry must appear as a /LIBPATH:<path> arg.
        foreach (string libPath in env.LibraryPaths)
        {
            Assert.Contains($"/LIBPATH:{libPath}", rsp);
        }

        // Default system libs must all be present.
        foreach (string sysLib in new[]
        {
            "kernel32.lib", "user32.lib", "gdi32.lib", "winspool.lib",
            "comdlg32.lib", "advapi32.lib", "shell32.lib", "ole32.lib",
            "oleaut32.lib", "uuid.lib", "odbc32.lib", "odbccp32.lib",
        })
        {
            Assert.Contains(sysLib, rsp);
        }
    }

    /// <summary>
    /// Phase 1.4a: CacheKeyComponents for a compile action MUST include
    /// the MSVC compiler version AND the Windows SDK version. Swapping
    /// either one between two builds (e.g. an MSVC point-release upgrade)
    /// invalidates the cache so the new SDK headers / new MSVC code
    /// generators actually get exercised.
    /// </summary>
    [Fact]
    public void CompileSource_CacheKey_IncludesMsvcAndWinSdkVersion()
    {
        string sdkRootA = Path.Combine(_scratchDir, "SdkA");
        string sdkRootB = Path.Combine(_scratchDir, "SdkB");
        foreach (string root in new[] { sdkRootA, sdkRootB })
        {
            foreach (string sub in new[] { "um", "shared", "ucrt", "winrt" })
            {
                Directory.CreateDirectory(Path.Combine(root, "Include", "10.0.26100.0", sub));
            }
            foreach (string sub in new[] { "um", "ucrt" })
            {
                Directory.CreateDirectory(Path.Combine(root, "Lib", "10.0.26100.0", sub, "x64"));
            }
        }

        VCEnvironment envA = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\cl.exe",
            linkerPath: @"C:\FakeVS\link.exe",
            compilerVersion: "14.40.0.0",
            sdkRoot: sdkRootA);

        VCEnvironment envB = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\cl.exe",
            linkerPath: @"C:\FakeVS\link.exe",
            compilerVersion: "14.41.0.0",          // bumped MSVC version
            sdkRoot: sdkRootB);

        XMSVCToolChain tcA = new(envA, repoRoot: @"C:\repo");
        XMSVCToolChain tcB = new(envB, repoRoot: @"C:\repo");

        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        IExternalAction compileA = tcA.CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();
        IExternalAction compileB = tcB.CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        // The cache key components must reflect the differing MSVC versions.
        Assert.Contains("MsvcVersion=14.40.0.0", compileA.CacheKeyComponents);
        Assert.Contains("MsvcVersion=14.41.0.0", compileB.CacheKeyComponents);
        // And BOTH must include a WinSdkVersion= component (the value
        // here is the same -- 10.0.26100.0 -- but the component MUST be
        // present so a hypothetical SDK swap invalidates the cache).
        Assert.Contains(compileA.CacheKeyComponents, c => c.StartsWith("WinSdkVersion=", StringComparison.Ordinal));
        Assert.Contains(compileB.CacheKeyComponents, c => c.StartsWith("WinSdkVersion=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Phase 1.4a: GeneratePCH also incorporates the composite include
    /// path list so a PCH-generating compile sees the same Windows SDK
    /// headers as the downstream consumer compiles. A PCH built without
    /// SDK paths in its include search would silently shadow the
    /// consumer's symbol resolution and trigger a C1859 (PCH was built
    /// from a different command line) at consumer-compile time.
    /// </summary>
    [Fact]
    public void GeneratePCH_EmitsIncludeFlagsForEnvironmentPaths()
    {
        string sdkRoot = Path.Combine(_scratchDir, "FakeSdk");
        foreach (string sub in new[] { "um", "shared", "ucrt", "winrt" })
        {
            Directory.CreateDirectory(Path.Combine(sdkRoot, "Include", "10.0.26100.0", sub));
        }
        foreach (string sub in new[] { "um", "ucrt" })
        {
            Directory.CreateDirectory(Path.Combine(sdkRoot, "Lib", "10.0.26100.0", sub, "x64"));
        }

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\cl.exe",
            linkerPath: @"C:\FakeVS\link.exe",
            msvcIncludePaths: new[] { @"C:\FakeMSVC\include" },
            sdkRoot: sdkRoot);

        XMSVCToolChain toolchain = new(env, repoRoot: @"C:\repo");

        // Module with PrivatePCHHeaderFile so GeneratePCH executes.
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        string pchOutDir = Path.Combine(_scratchDir, "pchout");
        string pchHeaderPath = Path.Combine(_scratchDir, "pch.h");
        File.WriteAllText(pchHeaderPath, "// PCH header\n");
        FileItem pchHeader = FileItem.GetItemByPath(pchHeaderPath);

        PCHBinding binding = toolchain.GeneratePCH(
            module: module,
            target: target,
            pchHeaderName: "pch.h",
            pchHeaderFile: pchHeader,
            outputDir: pchOutDir);

        // Every IncludePaths entry must appear as a /I<path> arg.
        foreach (string inc in env.IncludePaths)
        {
            Assert.Contains($"/I{inc}", binding.Action.CommandArguments);
        }
    }

    // ===== Audit fix R8-M2 / R8-M3 / R8-M4: cache key strengthening =====

    /// <summary>
    /// Audit fix R8-M2: every MSVC compile action's
    /// CacheKeyComponents contains an <c>EnvelopeFlagsHash=</c>
    /// component. A toolchain upgrade that silently changes any
    /// envelope flag's default rotates this hash and invalidates
    /// the cache.
    /// </summary>
    [Fact]
    public void EnvelopeFlagsHash_AppearsInCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        IExternalAction compile = _toolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains(
            compile.CacheKeyComponents,
            c => c.StartsWith("EnvelopeFlagsHash=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R8-M2: same hash on the link action.
    /// </summary>
    [Fact]
    public void EnvelopeFlagsHash_AppearsInLinkCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();
        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));

        IExternalAction link = _toolchain.LinkModule(module, target, new[] { obj }, _scratchDir);
        Assert.Contains(
            link.CacheKeyComponents,
            c => c.StartsWith("EnvelopeFlagsHash=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R8-M2: two toolchains differing only in their repo
    /// root produce different envelope hashes (the /pathmap= flag
    /// embeds the repo root). This proves the envelope-flag list is
    /// in fact hashed; constant strings alone would alias.
    /// </summary>
    [Fact]
    public void EnvelopeFlagsHash_DiffersByPathmapRepoRoot()
    {
        XMSVCToolChain tcA = new(_env, repoRoot: @"C:\repoA");
        XMSVCToolChain tcB = new(_env, repoRoot: @"C:\repoB");

        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        IExternalAction onA = tcA.CompileSource(module, target, MakeSource("F.cpp"), _scratchDir).Single();
        IExternalAction onB = tcB.CompileSource(module, target, MakeSource("F.cpp"), _scratchDir).Single();

        string hashA = onA.CacheKeyComponents.Single(c => c.StartsWith("EnvelopeFlagsHash="));
        string hashB = onB.CacheKeyComponents.Single(c => c.StartsWith("EnvelopeFlagsHash="));
        Assert.NotEqual(hashA, hashB);
    }

    /// <summary>
    /// Audit fix R8-M3: per-module descriptor hash flows through the
    /// MSVC compile cache key.
    /// </summary>
    [Fact]
    public void DescriptorHash_AppearsInCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        module.ApplyDescriptorContentHash("abcdef0123456789");
        TargetRules target = NewTarget();

        IExternalAction compile = _toolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains("DescriptorHash=abcdef0123456789", compile.CacheKeyComponents);
    }

    /// <summary>
    /// Audit fix R8-M3: a null descriptor hash maps to a stable
    /// sentinel so test paths produce well-formed (and stable)
    /// cache keys.
    /// </summary>
    [Fact]
    public void DescriptorHash_NullValue_UsesStableSentinel()
    {
        ModuleRules module = NewModule(simPath: false);
        Assert.Null(module.DescriptorContentHash);
        TargetRules target = NewTarget();

        IExternalAction compile = _toolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains("DescriptorHash=(no-descriptor)", compile.CacheKeyComponents);
    }

    /// <summary>
    /// Audit fix R8-M4: the XBT binary content hash flows through
    /// the MSVC compile cache key.
    /// </summary>
    [Fact]
    public void XbtBinaryHash_AppearsInCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        IExternalAction compile = _toolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains(
            compile.CacheKeyComponents,
            c => c.StartsWith("XbtBinaryHash=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R8-M4: synthetic switch of the XBT-binary hash via
    /// the test hook rotates the MSVC cache key. The override is
    /// cleared in finally to keep other tests isolated.
    /// </summary>
    [Fact]
    public void XbtBinaryHash_TestOverride_RotatesCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        IExternalAction realKey = _toolchain
            .CompileSource(module, target, MakeSource("F.cpp"), _scratchDir).Single();
        try
        {
            ToolchainSelfHash.__SetForTesting("0000000000000000");
            IExternalAction overridden = _toolchain
                .CompileSource(module, target, MakeSource("F.cpp"), _scratchDir).Single();
            Assert.Contains("XbtBinaryHash=0000000000000000", overridden.CacheKeyComponents);
            Assert.NotEqual(realKey.CommandVersion, overridden.CommandVersion);
        }
        finally
        {
            ToolchainSelfHash.__SetForTesting(null);
        }
    }

    /// <summary>
    /// Two source files with the same basename living in different
    /// subdirectories under the module's source root must produce
    /// distinct .obj paths. Without this, the link action receives
    /// two prerequisite items with identical FullPath, and
    /// <see cref="ExternalAction.Create"/>'s <c>ValidateSorted</c>
    /// throws a duplicate-entry exception that fails the build.
    ///
    /// Real-world trigger:
    /// Engine/Source/Runtime/XCore/Tests/HAL/FAtomicInt32.Tests/CASContention.cpp
    /// vs.
    /// Engine/Source/Runtime/XCore/Tests/HAL/FAtomicInt64.Tests/CASContention.cpp
    /// (and several other duplicate-basename pairs under the
    /// XCore.Tests source tree).
    ///
    /// Fix: the toolchain composes the .obj path under a sub-directory
    /// of <c>outputDir</c> derived from the source file's path
    /// relative to <c>moduleSourceDir</c> -- the UE convention.
    /// </summary>
    [Fact]
    public void CompileSource_DuplicateBasenameInDistinctSubdirs_ProducesDistinctObjPaths()
    {
        ModuleRules module = NewModule();
        TargetRules target = NewTarget();

        string moduleSourceDir = Path.Combine(_scratchDir, "ModSrc");
        string outputDir = Path.Combine(_scratchDir, "Obj");
        Directory.CreateDirectory(outputDir);

        FileItem sourceA = FileItem.GetItemByPath(
            Path.Combine(moduleSourceDir, "SubA", "Same.cpp"));
        FileItem sourceB = FileItem.GetItemByPath(
            Path.Combine(moduleSourceDir, "SubB", "Same.cpp"));

        IExternalAction compileA = _toolchain.CompileSource(
            module, target, sourceA, outputDir,
            pch: null, moduleSourceDir: moduleSourceDir).Single();
        IExternalAction compileB = _toolchain.CompileSource(
            module, target, sourceB, outputDir,
            pch: null, moduleSourceDir: moduleSourceDir).Single();

        string ObjOf(IExternalAction action) => action.ProducedItems
            .First(i => i.FullPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            .FullPath;

        string objA = ObjOf(compileA);
        string objB = ObjOf(compileB);

        Assert.NotEqual(objA, objB);
        Assert.Equal(Path.Combine(outputDir, "SubA", "Same.obj"), objA);
        Assert.Equal(Path.Combine(outputDir, "SubB", "Same.obj"), objB);

        // The .deps.json sidecar lives alongside the .obj, not flat
        // under outputDir, so the same-basename collision cannot
        // reappear at the dependency-tracking layer.
        string DepOf(IExternalAction action) => action.ProducedItems
            .First(i => i.FullPath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            .FullPath;

        Assert.Equal(objA + ".deps.json", DepOf(compileA));
        Assert.Equal(objB + ".deps.json", DepOf(compileB));
    }

    /// <summary>
    /// When <c>moduleSourceDir</c> is omitted (or empty), the toolchain
    /// falls back to flat basename-only naming under <c>outputDir</c>.
    /// This preserves the contract used by every flag-emission test in
    /// this file and by callers that genuinely have no module-source
    /// tree to anchor against (e.g. ad-hoc one-shot compiles in the
    /// future). The fallback also limits the blast radius of the
    /// per-subdir-naming fix to call sites that explicitly opt in.
    /// </summary>
    [Fact]
    public void CompileSource_NoModuleSourceDir_KeepsFlatBasenameLayout()
    {
        ModuleRules module = NewModule();
        TargetRules target = NewTarget();

        IExternalAction compile = _toolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir)
            .Single();

        string objPath = compile.ProducedItems
            .First(i => i.FullPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            .FullPath;

        Assert.Equal(Path.Combine(_scratchDir, "Foo.obj"), objPath);
    }

    // ===== Response-file pattern (XCore.dll linker fix) =====

    /// <summary>
    /// Linker filename-too-long fix: <see cref="XMSVCToolChain.LinkModule"/>
    /// emits the full argument list (envelope flags + /LIBPATH +
    /// system libs + .obj paths + /OUT) into
    /// <see cref="IExternalAction.ResponseFileContents"/> and leaves
    /// <see cref="IExternalAction.CommandArguments"/> empty. The
    /// <see cref="ProcessActionRunner"/> appends the <c>@&lt;rsp&gt;</c>
    /// indirection unconditionally; link.exe parses the response file
    /// at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Modules with 250+ object files (e.g. XCore at 481 actions) blew
    /// past Windows' ~32 KB <c>CreateProcessW</c> command-line limit
    /// before this fix, with the textbook
    /// <c>Win32Exception: filename or extension is too long</c>. XBT
    /// applies the response-file pattern unconditionally per the Prime
    /// Directive -- the right shape for every link -- rather than gating
    /// on per-host limits.
    /// </para>
    /// </remarks>
    [Fact]
    public void LinkModule_UsesResponseFile_NotCommandArguments()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = _toolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        // CommandArguments is empty; the runner appends "@<rsp>" at
        // dispatch time. Keeping CommandArguments empty is the canonical
        // signal that the action delegates its argument transport to the
        // response file.
        Assert.Empty(link.CommandArguments);

        // ResponseFileContents holds the full link command body.
        Assert.NotNull(link.ResponseFileContents);
        Assert.NotEmpty(link.ResponseFileContents!);
    }

    /// <summary>
    /// The response file body is line-oriented (one arg per line,
    /// LF terminator). link.exe parses both whitespace-separated and
    /// newline-separated response files identically; XBT uses the
    /// newline form because it is the canonical MSVC convention and is
    /// readable when diagnosing a link failure.
    /// </summary>
    [Fact]
    public void LinkModule_ResponseFile_IsOneArgPerLine_LfTerminated()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = _toolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        string rsp = link.ResponseFileContents!;
        Assert.Contains('\n', rsp);
        // CRLF is the host's Environment.NewLine on Windows but the body
        // is LF-only so two builds on different hosts produce byte-
        // identical content. Spot-check that no CR sneaks in.
        Assert.DoesNotContain('\r', rsp);
        // Each non-empty arg occupies a full line; tally distinct line
        // count and cross-check against the spec-required floor (envelope
        // flags + /OUT + at least one default system lib + the single
        // .obj). Tight floor avoids brittling on future flag additions.
        int lineCount = rsp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(lineCount >= 8,
            $"Expected the response file body to contain at least 8 lines (envelope flags + /OUT + system libs + .obj); got {lineCount}.");
    }

    /// <summary>
    /// A link command with 100+ object files materializes a response file
    /// that contains every .obj path on its own line, with
    /// <see cref="IExternalAction.CommandArguments"/> still empty. This
    /// is the codified scenario the response-file pattern exists to
    /// solve: the equivalent flat command line would exceed Windows'
    /// 32 KB limit and fail at <c>CreateProcessW</c>.
    /// </summary>
    [Fact]
    public void LinkModule_LongObjList_AllPathsInResponseFile_CmdArgsEmpty()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        // 200 synthetic .obj paths, each one moderately long, mirror the
        // XCore.dll shape that exposed the original failure (250+ .obj
        // files yielding a >32 KB link command line). Path stems include
        // an index so the entries are unique and sort-stable.
        const int objCount = 200;
        FileItem[] objs = new FileItem[objCount];
        int totalArgBytes = 0;
        for (int i = 0; i < objCount; i++)
        {
            string objPath = Path.Combine(
                _scratchDir, "obj",
                $"Some_Module_With_A_Modest_Name_{i:D4}.cpp.obj");
            objs[i] = FileItem.GetItemByPath(objPath);
            totalArgBytes += objPath.Length + 1; // +1 for the line terminator
        }

        IExternalAction link = _toolchain.LinkModule(module, target, objs, _scratchDir);

        // The visible command line is empty -- the runner is responsible
        // for appending the "@<rsp>" indirection at dispatch time.
        Assert.Empty(link.CommandArguments);

        // Every synthetic .obj path appears on its own line in the
        // response file body.
        string rsp = link.ResponseFileContents!;
        for (int i = 0; i < objCount; i++)
        {
            string expected = objs[i].FullPath;
            Assert.Contains(expected, rsp);
        }

        // Cross-check that the response file would, in fact, have
        // exceeded the typical command-line limit if it had been passed
        // on argv. The threshold guarantees the test is exercising the
        // limit-busting regime rather than a fits-comfortably case.
        Assert.True(totalArgBytes > 10_000,
            $"Expected the .obj arg block alone to exceed 10 KB; got {totalArgBytes} bytes.");

        // The response file body itself is correspondingly large.
        Assert.True(rsp.Length > 10_000,
            $"Expected the response file body to reflect the long arg list; got {rsp.Length} chars.");
    }

    /// <summary>
    /// The response file body is sorted by .obj path (ordinal). Two
    /// link calls with the same .obj set must produce byte-identical
    /// response file content so the ActionHistory cache hits when
    /// nothing changed. The body participates in
    /// <see cref="ExternalAction.ComputeCommandVersion"/>'s hash per
    /// item 4, so any reordering rotates the action key and forces a
    /// re-link.
    /// </summary>
    [Fact]
    public void LinkModule_ResponseFile_IsDeterministicAcrossCalls()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem[] objsForward = new[]
        {
            FileItem.GetItemByPath(Path.Combine(_scratchDir, "A.obj")),
            FileItem.GetItemByPath(Path.Combine(_scratchDir, "B.obj")),
            FileItem.GetItemByPath(Path.Combine(_scratchDir, "C.obj")),
        };
        FileItem[] objsReversed = new[]
        {
            FileItem.GetItemByPath(Path.Combine(_scratchDir, "C.obj")),
            FileItem.GetItemByPath(Path.Combine(_scratchDir, "B.obj")),
            FileItem.GetItemByPath(Path.Combine(_scratchDir, "A.obj")),
        };

        IExternalAction linkForward = _toolchain.LinkModule(module, target, objsForward, _scratchDir);
        IExternalAction linkReversed = _toolchain.LinkModule(module, target, objsReversed, _scratchDir);

        // LinkModule defensively sorts its .obj list internally, so the
        // input order at the call site does not affect the response file
        // content. Two calls with the same .obj set produce byte-
        // identical bodies regardless of caller-supplied order.
        Assert.Equal(linkForward.ResponseFileContents, linkReversed.ResponseFileContents);
    }

    /// <summary>
    /// The response file body participates in
    /// <see cref="IExternalAction.CommandVersion"/> per
    /// <see cref="ExternalAction.ComputeCommandVersion"/> item 4. A
    /// change to any .obj path rotates the version hash, which
    /// invalidates the cached link. Without this property, two distinct
    /// link inputs would silently alias to the same cache entry.
    /// </summary>
    [Fact]
    public void LinkModule_ResponseFileBody_FlowsIntoCommandVersion()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem objA = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        FileItem objB = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Bar.obj"));

        IExternalAction linkA = _toolchain.LinkModule(module, target, new[] { objA }, _scratchDir);
        IExternalAction linkB = _toolchain.LinkModule(module, target, new[] { objB }, _scratchDir);

        Assert.NotEqual(linkA.ResponseFileContents, linkB.ResponseFileContents);
        Assert.NotEqual(linkA.CommandVersion, linkB.CommandVersion);
    }

    /// <summary>
    /// <see cref="XToolChain.FormatResponseFile"/> quotes args containing
    /// whitespace with double-quotes and escapes any embedded double
    /// quote with a backslash. Path-with-space inputs (rare on Windows
    /// CI but possible on dev workstations under <c>C:\Program Files</c>)
    /// round-trip cleanly through both link.exe's and clang's response-
    /// file parsers, both of which follow the CRT quoting rules captured
    /// here.
    /// </summary>
    [Fact]
    public void FormatResponseFile_QuotesArgsWithWhitespace_And_EscapesEmbeddedQuotes()
    {
        string body = XToolChain.FormatResponseFile(new[]
        {
            "/nologo",                                   // verbatim
            "/LIBPATH:C:\\Program Files\\Lib",           // path with space -> quoted
            "\"already-quoted\"",                        // embedded quotes -> escaped
            "",                                           // empty arg -> "" (round-trip)
        });

        // Args without whitespace are appended verbatim, terminator LF.
        Assert.Contains("/nologo\n", body);
        // Path with space wraps in double quotes.
        Assert.Contains("\"/LIBPATH:C:\\Program Files\\Lib\"\n", body);
        // Embedded double quote escaped with backslash.
        Assert.Contains("\"\\\"already-quoted\\\"\"\n", body);
        // Empty arg becomes "".
        Assert.Contains("\"\"\n", body);
    }

    // =================================================================
    // Phase 5 test-link wiring: LinkExecutable + LinkModule.additionalLibraries
    // =================================================================

    /// <summary>
    /// LinkExecutable emits the executable flag set: no <c>/DLL</c>;
    /// <c>/SUBSYSTEM:CONSOLE</c> + <c>/ENTRY:mainCRTStartup</c>;
    /// <c>.exe</c> extension on the produced item.
    /// </summary>
    [Fact]
    public void LinkExecutable_EmitsConsoleSubsystem_NotDll()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "MyTest.obj"));
        IExternalAction link = _toolchain.LinkExecutable(
            module, target, obj, exeName: "MyTest", outputDir: _scratchDir);

        string rsp = link.ResponseFileContents!;
        Assert.DoesNotContain("/DLL", rsp.Split('\n'));
        Assert.Contains("/SUBSYSTEM:CONSOLE", rsp);
        Assert.Contains("/ENTRY:mainCRTStartup", rsp);

        // Produced item carries the .exe extension.
        Assert.Single(link.ProducedItems);
        Assert.EndsWith("MyTest.exe", link.ProducedItems[0].FullPath);
    }

    /// <summary>
    /// LinkExecutable carries the same reproducibility envelope as
    /// LinkModule (the test exe must be cross-host bit-stable for the
    /// per-test-cpp executable pattern to be cache-coherent).
    /// </summary>
    [Fact]
    public void LinkExecutable_ReproducibilityEnvelope_PresentInResponseFile()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "T.obj"));
        IExternalAction link = _toolchain.LinkExecutable(
            module, target, obj, exeName: "T", outputDir: _scratchDir);

        string rsp = link.ResponseFileContents!;
        Assert.Contains("/BREPRO", rsp);
        Assert.Contains("/TIMESTAMP:0", rsp);
        Assert.Contains("/INCREMENTAL:NO", rsp);
        Assert.Contains("/cgthreads:8", rsp);
    }

    /// <summary>
    /// LinkExecutable appends additional libraries (transitive dep
    /// import libs + module-declared additional_libraries) AFTER the
    /// .obj entry on the link line. The libs appear in the response
    /// file body.
    /// </summary>
    [Fact]
    public void LinkExecutable_AppendsAdditionalLibraries()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "T.obj"));
        string libA = Path.Combine(_scratchDir, "lib", "XCore.lib");
        string libB = Path.Combine(_scratchDir, "lib", "Sleef.lib");
        IExternalAction link = _toolchain.LinkExecutable(
            module, target, obj,
            exeName: "T",
            outputDir: _scratchDir,
            additionalLibraries: new[] { libA, libB });

        string rsp = link.ResponseFileContents!;
        Assert.Contains(libA, rsp);
        Assert.Contains(libB, rsp);
    }

    /// <summary>
    /// LinkExecutable's PrerequisiteItems include the .obj plus any
    /// additionalPrerequisites passed in (typically the producer-
    /// visible .dll paths of transitive deps). additionalLibraries
    /// reaches the linker via the response file but is NOT added to
    /// prereqs because its .lib producer is conditional on exports.
    /// </summary>
    [Fact]
    public void LinkExecutable_PrerequisitesIncludeObjAndAdditionalPrerequisites()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "T.obj"));
        string dllA = Path.Combine(_scratchDir, "bin", "AAA.dll");
        string dllB = Path.Combine(_scratchDir, "bin", "ZZZ.dll");

        IExternalAction link = _toolchain.LinkExecutable(
            module, target, obj,
            exeName: "T",
            outputDir: _scratchDir,
            additionalLibraries: null,
            additionalPrerequisites: new[] { dllB, dllA }); // reversed input

        // PrerequisiteItems sorted ordinal: AAA.dll, T.obj, ZZZ.dll.
        Assert.Equal(3, link.PrerequisiteItems.Count);
        Assert.Contains(link.PrerequisiteItems, p => p.FullPath == dllA);
        Assert.Contains(link.PrerequisiteItems, p => p.FullPath == dllB);
        Assert.Contains(link.PrerequisiteItems, p => p.FullPath == obj.FullPath);
    }

    /// <summary>
    /// LinkModule (the existing surface) accepts an additionalLibraries
    /// list. The libs appear in the response file body AFTER the .obj
    /// paths so symbol resolution proceeds left-to-right correctly.
    /// </summary>
    [Fact]
    public void LinkModule_AppendsAdditionalLibraries_AfterObjs()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        string libA = Path.Combine(_scratchDir, "lib", "XCore.lib");
        IExternalAction link = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir,
            additionalLibraries: new[] { libA });

        string rsp = link.ResponseFileContents!;
        // The .obj path appears before the library on the link line
        // (left-to-right walk; .obj references pull in lib symbols).
        int objIdx = rsp.IndexOf(obj.FullPath, StringComparison.Ordinal);
        int libIdx = rsp.IndexOf(libA, StringComparison.Ordinal);
        Assert.True(objIdx >= 0, ".obj path absent from response file body.");
        Assert.True(libIdx >= 0, "library path absent from response file body.");
        Assert.True(objIdx < libIdx,
            $".obj (idx {objIdx}) must precede library (idx {libIdx}) on the link line.");
    }

    // ------------------------------------------------------------------
    // Phase 1g Sleef wiring: /DEF: emission + .def prerequisite invariants.
    // ------------------------------------------------------------------

    /// <summary>
    /// Phase 1g: <see cref="XMSVCToolChain.LinkModule"/> with a non-null
    /// <c>moduleDefFileAbsolute</c> emits <c>/DEF:&lt;abs&gt;</c> on the
    /// link command line. This is the structural fix for vendored
    /// ThirdParty libraries (Sleef in particular) whose upstream code
    /// carries no <c>__declspec(dllexport)</c> annotations: without
    /// <c>/DEF:</c>, link.exe produces a DLL with no exports and
    /// therefore no companion <c>.lib</c>, and every consumer's link
    /// fails with LNK1181.
    /// </summary>
    [Fact]
    public void LinkModule_WithModuleDefFile_EmitsDefFlag()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        string defPath = Path.Combine(_scratchDir, "Foo.def");
        IExternalAction link = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir,
            moduleDefFileAbsolute: defPath);

        string rsp = link.ResponseFileContents!;
        Assert.Contains($"/DEF:{defPath}", rsp);
    }

    /// <summary>
    /// Phase 1g: when <c>moduleDefFileAbsolute</c> is null (the common
    /// case for modules that don't declare an explicit export list),
    /// the link command line emits NO <c>/DEF:</c> flag.
    /// </summary>
    [Fact]
    public void LinkModule_WithoutModuleDefFile_OmitsDefFlag()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir);

        string rsp = link.ResponseFileContents!;
        Assert.DoesNotContain("/DEF:", rsp);
    }

    /// <summary>
    /// Phase 1g: the .def file lands in
    /// <see cref="IExternalAction.PrerequisiteItems"/> so the action
    /// graph's incremental-rebuild machinery invalidates the cached
    /// link when the .def is edited. Same discipline as the .obj
    /// inputs.
    /// </summary>
    [Fact]
    public void LinkModule_WithModuleDefFile_AddsDefToPrerequisites()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        string defPath = Path.Combine(_scratchDir, "Foo.def");
        IExternalAction link = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir,
            moduleDefFileAbsolute: defPath);

        Assert.Contains(link.PrerequisiteItems, fi =>
            string.Equals(fi.FullPath, defPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Phase 1g: an empty-string <c>moduleDefFileAbsolute</c> is
    /// treated as "no .def file" — no <c>/DEF:</c> flag and no
    /// prerequisite entry. This mirrors the null contract so callers
    /// that pass <c>string.Empty</c> by accident don't poison the
    /// link line with <c>/DEF:</c> (empty arg) which link.exe would
    /// reject.
    /// </summary>
    [Fact]
    public void LinkModule_WithEmptyModuleDefFile_TreatedAsNoDef()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir,
            moduleDefFileAbsolute: string.Empty);

        string rsp = link.ResponseFileContents!;
        Assert.DoesNotContain("/DEF:", rsp);
    }

    /// <summary>
    /// Phase 1g: presence of <c>moduleDefFileAbsolute</c> changes the
    /// response file body and therefore the
    /// <see cref="IExternalAction.CommandVersion"/>. A switch from
    /// no-def to with-def (or a .def path change) forces a re-link.
    /// </summary>
    [Fact]
    public void LinkModule_ModuleDefFile_FlowsIntoCommandVersion()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();
        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));

        IExternalAction linkNoDef = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir);
        IExternalAction linkWithDef = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir,
            moduleDefFileAbsolute: Path.Combine(_scratchDir, "Foo.def"));

        Assert.NotEqual(linkNoDef.ResponseFileContents, linkWithDef.ResponseFileContents);
        Assert.NotEqual(linkNoDef.CommandVersion, linkWithDef.CommandVersion);
    }

    /// <summary>
    /// LinkExecutable + LinkModule produce DIFFERENT response file
    /// bodies (and thus distinct CommandVersions) for the same .obj
    /// input. The executable flag set is part of the cache key so a
    /// switch from production-link to test-link forces a re-link.
    /// </summary>
    [Fact]
    public void LinkExecutable_DistinctFromLinkModule_OnSameObj()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget();

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Shared.obj"));
        IExternalAction asDll = _toolchain.LinkModule(
            module, target, new[] { obj }, _scratchDir);
        IExternalAction asExe = _toolchain.LinkExecutable(
            module, target, obj, exeName: "Shared", outputDir: _scratchDir);

        Assert.NotEqual(asDll.ResponseFileContents, asExe.ResponseFileContents);
        Assert.NotEqual(asDll.CommandVersion, asExe.CommandVersion);
        // The produced extension differs (.dll vs .exe).
        Assert.EndsWith(".dll", asDll.ProducedItems[0].FullPath);
        Assert.EndsWith(".exe", asExe.ProducedItems[0].FullPath);
    }

    // ----- Helpers -----

    private static ModuleRules NewModule(
        bool simPath = false,
        SimdLevel simdLevel = SimdLevel.Default,
        FPSemantics fp = FPSemantics.Default)
    {
        return new ModuleRules
        {
            Name = "XTest",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            SimPath = simPath,
            SimdLevel = simdLevel,
            FPSemantics = fp,
        };
    }

    private static TargetRules NewTarget()
    {
        return new TargetRules
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Editor,
            Platform = Platform.Win64,
            Configuration = BuildConfiguration.Development,
            StationRole = StationRole.Engineer,
            SimdLevelDefault = SimdLevel.SSE42,
        };
    }

    private FileItem MakeSource(string name)
    {
        string path = Path.Combine(_scratchDir, name);
        // We don't need the file to actually exist on disk for these
        // flag-emission tests, but FileItem expects an absolute path.
        return FileItem.GetItemByPath(path);
    }
}
