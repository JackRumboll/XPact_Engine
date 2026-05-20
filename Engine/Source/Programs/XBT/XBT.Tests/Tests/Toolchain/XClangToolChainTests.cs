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
/// Verifies <see cref="XClangToolChain"/> emits the documented flag set
/// per Toolchain Contract Rev 13 Section 4.2 (Clang Linux + Android rows)
/// and <c>/Documents/XBT.html</c> Rev 4 Section 19.1 (reproducibility
/// envelope).
/// </summary>
public sealed class XClangToolChainTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly XClangToolChain _linuxToolchain;
    private readonly XClangToolChain _androidToolchain;

    public XClangToolChainTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.Clang",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);

        _linuxToolchain = new XClangToolChain(
            clangPath: "/usr/bin/clang",
            clangVersion: "18.0.0",
            platform: Platform.Linux,
            repoRoot: "/home/user/repo");

        _androidToolchain = new XClangToolChain(
            clangPath: "/opt/android-ndk/clang",
            clangVersion: "18.0.0-android",
            platform: Platform.Android,
            repoRoot: "/home/user/repo");
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
    /// SimPath module on Clang emits -ffp-contract=off + -mno-fma; banned
    /// -ffast-math is absent.
    /// </summary>
    [Fact]
    public void SimPathModule_EmitsContractOffAndMnoFma()
    {
        ModuleRules module = NewModule(simPath: true);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("XPhysics.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-ffp-contract=off", compile.CommandArguments);
        Assert.Contains("-mno-fma", compile.CommandArguments);
        Assert.DoesNotContain("-ffast-math", compile.CommandArguments);
    }

    /// <summary>
    /// Audit fix C6/M2: Non-SimPath module with FPSemantics.Default
    /// emits NO <c>-ffp-*</c>/<c>-ffast-math</c> flag. Compiler default
    /// applies (Clang defaults to IEEE-precise). Imprecise still maps
    /// to <c>-ffp-contract=fast</c>; Precise to <c>-ffp-contract=off</c>.
    /// </summary>
    [Fact]
    public void NonSimPathModule_FpDefault_EmitsNoFpFlag()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("XRenderer.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.DoesNotContain("-ffast-math", compile.CommandArguments);
        Assert.DoesNotContain("-ffp-contract=fast", compile.CommandArguments);
        Assert.DoesNotContain("-ffp-contract=off", compile.CommandArguments);
    }

    /// <summary>
    /// Non-SimPath module with SimdLevel.AVX2 emits -mavx2.
    /// </summary>
    [Fact]
    public void NonSimPathModule_Avx2_EmitsMavx2()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.AVX2);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("XAvxStuff.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-mavx2", compile.CommandArguments);
    }

    /// <summary>
    /// Android ARM64 SimPath module includes the NEON FMA suppress flags:
    /// -mllvm + -enable-fp-contract=false (per Contract Section 4.2 ARM64
    /// row).
    /// </summary>
    [Fact]
    public void AndroidArm64SimPath_NeonFmaSuppress()
    {
        ModuleRules module = NewModule(simPath: true);
        TargetRules target = NewTarget(Platform.Android);

        var actions = _androidToolchain.CompileSource(module, target, MakeSource("XPhysics.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-mllvm", compile.CommandArguments);
        Assert.Contains("-enable-fp-contract=false", compile.CommandArguments);
        Assert.Contains("-mno-fma", compile.CommandArguments);
    }

    /// <summary>
    /// Reproducibility envelope (XBT.html Section 19.1 + Contract
    /// Section 2.1): every Clang compile emits
    /// -fdebug-prefix-map + -fno-ident +
    /// -frandomize-layout-seed-file=&lt;path&gt;; every Clang link emits
    /// -Wl,--build-id=none + -fno-ident.
    /// </summary>
    /// <remarks>
    /// Audit fix M1: the previous Rev 13.1 emit included an invalid
    /// <c>--remap-file=</c> flag on the link line. The flag does not
    /// exist in clang or lld; -fdebug-prefix-map= on the compile side
    /// already normalizes DWARF source paths, and the linker copies
    /// DWARF sections through verbatim. No link-side path remap is
    /// required for the reproducibility envelope.
    /// </remarks>
    [Fact]
    public void ReproducibilityFlags_PresentOnEveryCompileAndLink()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        var compileActions = _linuxToolchain.CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir);
        IExternalAction compile = compileActions.Single();
        Assert.Contains("-fno-ident", compile.CommandArguments);
        Assert.Contains("-fdebug-prefix-map=/home/user/repo=X:/R", compile.CommandArguments);
        Assert.Contains("-fdeterministic-cgu-order", compile.CommandArguments);

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.o"));
        IExternalAction link = _linuxToolchain.LinkModule(module, target, new[] { obj }, _scratchDir);
        Assert.Contains("-Wl,--build-id=none", link.CommandArguments);
        Assert.Contains("-fno-ident", link.CommandArguments);
    }

    /// <summary>
    /// Contract Rev 13.1 Section 2.1: every Clang compile emits
    /// <c>-frandomize-layout-seed-file=&lt;abs-path&gt;</c> with the
    /// path resolving under the repo root. The seed file MUST be at the
    /// pinned location <c>Engine/Source/Programs/XBT/randomize-layout.seed</c>
    /// so it is identical across machines and reproducible across runs.
    /// </summary>
    [Fact]
    public void Compile_EmitsRandomizeLayoutSeedFileFlag()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        var compileActions = _linuxToolchain.CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir);
        IExternalAction compile = compileActions.Single();

        // Find the seed-file flag; assert its value contains the pinned
        // relative path (forward-slash form on Linux).
        string? seedFlag = compile.CommandArguments
            .FirstOrDefault(a => a.StartsWith("-frandomize-layout-seed-file=", StringComparison.Ordinal));
        Assert.NotNull(seedFlag);
        Assert.Contains(
            "randomize-layout.seed",
            seedFlag,
            StringComparison.Ordinal);
        Assert.Contains(
            "/home/user/repo",
            seedFlag,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Audit fix M1: the Clang link command MUST NOT emit
    /// <c>--remap-file=</c> (an invalid clang/lld flag that the
    /// previous Rev 13.1 emit incorrectly included), nor the
    /// compile-side <c>-fdebug-prefix-map=</c> (the linker copies
    /// DWARF sections through unmodified, so the compile-side remap
    /// is sufficient for the reproducibility envelope).
    /// </summary>
    [Fact]
    public void Link_DoesNotEmitInvalidRemapFlag_NorCompileSidePrefixMap()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.o"));
        IExternalAction link = _linuxToolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        Assert.DoesNotContain(link.CommandArguments, a => a.StartsWith("--remap-file=", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "-fdebug-prefix-map=/home/user/repo=X:/R",
            link.CommandArguments);
    }

    /// <summary>
    /// The mock clang path passed to the constructor flows through to the
    /// constructed action's CommandPath; the action's StatusDescription
    /// includes the source file name.
    /// </summary>
    [Fact]
    public void CompileAction_CommandPathMatchesConstructor()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("Trivial.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Equal("/usr/bin/clang", compile.CommandPath);
        Assert.Equal("Trivial.cpp", compile.StatusDescription);
        Assert.Contains(compile.CommandArguments, a => a.EndsWith("Trivial.cpp", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R4-M3: per-SIMD-level upper-bound suppression flag set.
    /// SSE2 must emit <c>-msse2</c> AND <c>-mno-sse3 -mno-ssse3
    /// -mno-sse4.1 -mno-sse4.2 -mno-avx -mno-avx2 -mno-avx512f</c>.
    /// Without the suppressions, Clang may auto-vectorize using higher
    /// SIMD intrinsics on hosts where they're enabled by default.
    /// </summary>
    [Fact]
    public void SimdLevel_SSE2_EmitsFullSuppressionSet()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.SSE2);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("Sse2.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-msse2", compile.CommandArguments);
        Assert.Contains("-mno-sse3", compile.CommandArguments);
        Assert.Contains("-mno-ssse3", compile.CommandArguments);
        Assert.Contains("-mno-sse4.1", compile.CommandArguments);
        Assert.Contains("-mno-sse4.2", compile.CommandArguments);
        Assert.Contains("-mno-avx", compile.CommandArguments);
        Assert.Contains("-mno-avx2", compile.CommandArguments);
        Assert.Contains("-mno-avx512f", compile.CommandArguments);
    }

    /// <summary>Audit fix R4-M3: SSE42 emits the SSE4.2 floor + AVX/AVX2/AVX512F suppressions.</summary>
    [Fact]
    public void SimdLevel_SSE42_EmitsAvxSuppressionSet()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.SSE42);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("Sse42.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-msse4.2", compile.CommandArguments);
        Assert.Contains("-mno-avx", compile.CommandArguments);
        Assert.Contains("-mno-avx2", compile.CommandArguments);
        Assert.Contains("-mno-avx512f", compile.CommandArguments);
    }

    /// <summary>Audit fix R4-M3: AVX emits the AVX floor + AVX2/AVX512F suppressions.</summary>
    [Fact]
    public void SimdLevel_AVX_EmitsAvx2AndAvx512Suppressions()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.AVX);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("Avx.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-mavx", compile.CommandArguments);
        Assert.Contains("-mno-avx2", compile.CommandArguments);
        Assert.Contains("-mno-avx512f", compile.CommandArguments);
    }

    /// <summary>Audit fix R4-M3: AVX2 emits the AVX2 floor + AVX512F suppression.</summary>
    [Fact]
    public void SimdLevel_AVX2_EmitsAvx512Suppression()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.AVX2);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("Avx2.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-mavx2", compile.CommandArguments);
        Assert.Contains("-mno-avx512f", compile.CommandArguments);
    }

    /// <summary>Audit fix R4-M3: AVX512 emits the AVX512 floor + width-extension flags (bw/dq/vl).</summary>
    [Fact]
    public void SimdLevel_AVX512_EmitsWidthExtensionFlags()
    {
        ModuleRules module = NewModule(simPath: false, simdLevel: SimdLevel.AVX512);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("Avx512.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-mavx512f", compile.CommandArguments);
        Assert.Contains("-mavx512bw", compile.CommandArguments);
        Assert.Contains("-mavx512dq", compile.CommandArguments);
        Assert.Contains("-mavx512vl", compile.CommandArguments);
    }

    /// <summary>
    /// Audit fix R4-M2: Clang version contributes to the
    /// CacheKeyComponents string list, mirroring MSVC's
    /// <c>MsvcVersion=</c>. Two toolchains differing only in
    /// version emit different cache key strings, which forces
    /// recompile on toolchain upgrade.
    /// </summary>
    [Fact]
    public void ClangVersion_ContributesToCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        XClangToolChain tcOld = new(
            clangPath: "/usr/bin/clang",
            clangVersion: "17.0.0",
            platform: Platform.Linux,
            repoRoot: "/home/user/repo");
        XClangToolChain tcNew = new(
            clangPath: "/usr/bin/clang",
            clangVersion: "18.0.0",
            platform: Platform.Linux,
            repoRoot: "/home/user/repo");

        IExternalAction oldAction =
            tcOld.CompileSource(module, target, MakeSource("V.cpp"), _scratchDir).Single();
        IExternalAction newAction =
            tcNew.CompileSource(module, target, MakeSource("V.cpp"), _scratchDir).Single();

        Assert.Contains("ClangVersion=17.0.0", oldAction.CacheKeyComponents);
        Assert.Contains("ClangVersion=18.0.0", newAction.CacheKeyComponents);
        // The pair of CacheKeyComponents arrays MUST NOT be identical
        // -- a Clang upgrade must change the cache key.
        Assert.NotEqual(
            string.Join("|", oldAction.CacheKeyComponents),
            string.Join("|", newAction.CacheKeyComponents));
    }

    /// <summary>
    /// Audit fix R4-M2: <see cref="XClangToolChain.ExtractSemver"/>
    /// returns the first plausible semver from arbitrary text. Covers
    /// the typical clang -dumpversion form ("18.0.0\n") and the
    /// --version banner form ("clang version 18.0.0 (https://...)").
    /// </summary>
    [Fact]
    public void ExtractSemver_ParsesDumpversionAndBannerForms()
    {
        Assert.Equal("18.0.0", XClangToolChain.ExtractSemver("18.0.0\n"));
        Assert.Equal("18.0.0", XClangToolChain.ExtractSemver(
            "clang version 18.0.0 (https://github.com/llvm/llvm-project.git ...)"));
        // No version: empty string.
        Assert.Equal(string.Empty, XClangToolChain.ExtractSemver("clang version unknown"));
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

    private static TargetRules NewTarget(Platform platform)
    {
        return new TargetRules
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Editor,
            Platform = platform,
            Configuration = BuildConfiguration.Development,
            StationRole = StationRole.Engineer,
            SimdLevelDefault = SimdLevel.SSE42,
        };
    }

    private FileItem MakeSource(string name)
    {
        string path = Path.Combine(_scratchDir, name);
        return FileItem.GetItemByPath(path);
    }
}
