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

        // Link.
        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.obj"));
        IExternalAction link = _toolchain.LinkModule(module, target, new[] { obj }, _scratchDir);
        Assert.Contains("/BREPRO", link.CommandArguments);
        Assert.Contains("/TIMESTAMP:0", link.CommandArguments);
        Assert.Contains("/INCREMENTAL:NO", link.CommandArguments);
        Assert.Contains("/cgthreads:8", link.CommandArguments);
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

        // Every LibraryPaths entry must appear as a /LIBPATH:<path> arg.
        foreach (string libPath in env.LibraryPaths)
        {
            Assert.Contains($"/LIBPATH:{libPath}", link.CommandArguments);
        }

        // Default system libs must all be present.
        foreach (string sysLib in new[]
        {
            "kernel32.lib", "user32.lib", "gdi32.lib", "winspool.lib",
            "comdlg32.lib", "advapi32.lib", "shell32.lib", "ole32.lib",
            "oleaut32.lib", "uuid.lib", "odbc32.lib", "odbccp32.lib",
        })
        {
            Assert.Contains(sysLib, link.CommandArguments);
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
