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
/// <remarks>
/// This class is in the <see cref="ToolchainSelfHashCollection"/> serial
/// collection because tests in it mutate
/// <see cref="Simgenics.XPact.XBT.Core.ToolchainSelfHash"/>'s process-wide
/// override slot (via <c>__SetForTesting</c>). See that collection's
/// remarks for the full rationale.
/// </remarks>
[Collection(nameof(ToolchainSelfHashCollection))]
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
        // Link args live in ResponseFileContents (unconditional response-
        // file pattern); CommandArguments is empty and ProcessActionRunner
        // appends "@<rsp>" to the visible command line.
        Assert.NotNull(link.ResponseFileContents);
        Assert.Contains("-Wl,--build-id=none", link.ResponseFileContents!);
        Assert.Contains("-fno-ident", link.ResponseFileContents!);
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

        // Link args live in ResponseFileContents (unconditional response-
        // file pattern); CommandArguments is empty.
        Assert.NotNull(link.ResponseFileContents);
        Assert.DoesNotContain("--remap-file=", link.ResponseFileContents!);
        Assert.DoesNotContain(
            "-fdebug-prefix-map=/home/user/repo=X:/R",
            link.ResponseFileContents!);
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

    // ===== Audit fix R8-C1: Android target triple =====

    /// <summary>
    /// Audit fix R8-C1: every Clang compile on Android emits
    /// <c>--target=&lt;arch&gt;-linux-android&lt;API&gt;</c>. Without this flag
    /// the NDK's generic <c>bin/clang</c> driver defaults to the host
    /// triple (x86-64 on Win64 / Linux build hosts) and produces .o
    /// files that won't link into an Android .so.
    /// </summary>
    [Fact]
    public void AndroidCompile_EmitsExplicitTargetTriple()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);

        var actions = _androidToolchain.CompileSource(module, target, MakeSource("XCore.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("--target=aarch64-linux-android24", compile.CommandArguments);
    }

    /// <summary>
    /// Audit fix R8-C1: every Clang link on Android emits the same
    /// <c>--target=</c> triple as the compile. A triple mismatch
    /// between compile and link would surface as a runtime ABI failure
    /// (the linker pulls in arch-specific runtime libs from
    /// <c>sysroot/usr/lib/&lt;triple&gt;/</c>).
    /// </summary>
    [Fact]
    public void AndroidLink_EmitsExplicitTargetTriple()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "XCore.o"));
        IExternalAction link = _androidToolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        // Link args (including the Android target triple) live in
        // ResponseFileContents per the unconditional response-file
        // pattern; CommandArguments is empty.
        Assert.NotNull(link.ResponseFileContents);
        Assert.Contains("--target=aarch64-linux-android24", link.ResponseFileContents!);
    }

    /// <summary>
    /// Audit fix R8-C1: GeneratePCH on Android emits the same
    /// <c>--target=</c> triple as the consumer compiles. A PCH
    /// compiled for a different triple than its consumers fails with
    /// "PCH was compiled for a different target".
    /// </summary>
    [Fact]
    public void AndroidGeneratePCH_EmitsExplicitTargetTriple()
    {
        ModuleRules module = NewModuleWithPch();
        TargetRules target = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);

        // Touch the PCH header file so the toolchain can resolve it.
        string pchHeaderPath = Path.Combine(_scratchDir, "XCorePCH.h");
        File.WriteAllText(pchHeaderPath, "// PCH header\n");
        FileItem pchHeader = FileItem.GetItemByPath(pchHeaderPath);

        PCHBinding binding = _androidToolchain.GeneratePCH(
            module, target, "XCorePCH.h", pchHeader, _scratchDir);

        Assert.Contains("--target=aarch64-linux-android24", binding.Action.CommandArguments);
    }

    /// <summary>
    /// Audit fix R8-C1: the same triple appears on every emit path so
    /// a compile, PCH, and link for the same target produce object
    /// files that link together. Triple drift across the three paths
    /// is the exact failure mode the audit fix prevents.
    /// </summary>
    [Fact]
    public void AndroidCompile_PCH_Link_AllShareSameTargetTriple()
    {
        ModuleRules module = NewModuleWithPch();
        TargetRules target = NewAndroidTarget(architecture: "aarch64", apiLevel: 21);

        string pchHeaderPath = Path.Combine(_scratchDir, "XCorePCH.h");
        File.WriteAllText(pchHeaderPath, "// PCH header\n");
        FileItem pchHeader = FileItem.GetItemByPath(pchHeaderPath);

        PCHBinding binding = _androidToolchain.GeneratePCH(
            module, target, "XCorePCH.h", pchHeader, _scratchDir);
        IExternalAction compile = _androidToolchain
            .CompileSource(module, target, MakeSource("XCore.cpp"), _scratchDir, binding)
            .Single();
        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "XCore.o"));
        IExternalAction link = _androidToolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        string expected = "--target=aarch64-linux-android21";
        Assert.Contains(expected, binding.Action.CommandArguments);
        Assert.Contains(expected, compile.CommandArguments);
        // Link args live in ResponseFileContents (unconditional response-
        // file pattern); CommandArguments is empty.
        Assert.NotNull(link.ResponseFileContents);
        Assert.Contains(expected, link.ResponseFileContents!);
    }

    /// <summary>
    /// Audit fix R8-C1: Linux Clang does NOT emit <c>--target=</c>.
    /// The host triple is correct on Linux; emitting an explicit
    /// triple would force a host-arch override on a non-cross
    /// toolchain.
    /// </summary>
    [Fact]
    public void LinuxCompile_DoesNotEmitTargetTriple()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("XCore.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.DoesNotContain(
            compile.CommandArguments,
            a => a.StartsWith("--target=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R8-C1: Architecture changes rotate the Android target
    /// triple. <c>aarch64</c> vs <c>armv7a</c> produce different
    /// triples and hence different cache keys.
    /// </summary>
    [Fact]
    public void AndroidArchitecture_DrivesTargetTripleAndCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules aarch64 = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);
        TargetRules armv7a = NewAndroidTarget(architecture: "armv7a", apiLevel: 24);

        IExternalAction a64 = _androidToolchain.CompileSource(module, aarch64, MakeSource("XCore.cpp"), _scratchDir).Single();
        IExternalAction a32 = _androidToolchain.CompileSource(module, armv7a, MakeSource("XCore.cpp"), _scratchDir).Single();

        Assert.Contains("--target=aarch64-linux-android24", a64.CommandArguments);
        Assert.Contains("--target=armv7a-linux-androideabi24", a32.CommandArguments);
        Assert.Contains("AndroidTargetTriple=aarch64-linux-android24", a64.CacheKeyComponents);
        Assert.Contains("AndroidTargetTriple=armv7a-linux-androideabi24", a32.CacheKeyComponents);
        Assert.NotEqual(a64.CommandVersion, a32.CommandVersion);
    }

    /// <summary>
    /// Audit fix R8-C1: ApiLevel changes rotate the Android target
    /// triple. <c>aarch64-linux-android21</c> vs
    /// <c>aarch64-linux-android24</c> produce different cache keys.
    /// </summary>
    [Fact]
    public void AndroidApiLevel_DrivesTargetTripleAndCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules api21 = NewAndroidTarget(architecture: "aarch64", apiLevel: 21);
        TargetRules api24 = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);

        IExternalAction at21 = _androidToolchain.CompileSource(module, api21, MakeSource("XCore.cpp"), _scratchDir).Single();
        IExternalAction at24 = _androidToolchain.CompileSource(module, api24, MakeSource("XCore.cpp"), _scratchDir).Single();

        Assert.Contains("--target=aarch64-linux-android21", at21.CommandArguments);
        Assert.Contains("--target=aarch64-linux-android24", at24.CommandArguments);
        Assert.NotEqual(at21.CommandVersion, at24.CommandVersion);
    }

    /// <summary>
    /// Audit fix R8-C1: an unrecognised Android architecture surfaces
    /// as exit 23 (EngineOrToolchainVersionMismatch) with a clear
    /// diagnostic, NOT a silent host-default codegen path. Catching
    /// the unknown triple up front is the entire point of the audit
    /// fix.
    /// </summary>
    [Fact]
    public void AndroidUnknownArchitecture_FailsWithExit23()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules badTarget = NewAndroidTarget(architecture: "mips64", apiLevel: 24);

        XBT.Core.XBTException ex = Assert.Throws<XBT.Core.XBTException>(
            () => _androidToolchain.CompileSource(module, badTarget, MakeSource("XCore.cpp"), _scratchDir));
        Assert.Equal(23, ex.ExitCode);
    }

    /// <summary>
    /// Audit fix R8-C1: determinism check -- identical inputs produce
    /// identical command lines including the <c>--target=</c> flag.
    /// </summary>
    [Fact]
    public void AndroidCompile_IsDeterministicAcrossInvocations()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);

        IExternalAction first = _androidToolchain.CompileSource(module, target, MakeSource("XCore.cpp"), _scratchDir).Single();
        IExternalAction second = _androidToolchain.CompileSource(module, target, MakeSource("XCore.cpp"), _scratchDir).Single();

        Assert.Equal(first.CommandArguments.Count, second.CommandArguments.Count);
        for (int i = 0; i < first.CommandArguments.Count; i++)
        {
            Assert.Equal(first.CommandArguments[i], second.CommandArguments[i]);
        }
        Assert.Equal(first.CommandVersion, second.CommandVersion);
    }

    // ===== Audit fix R8-M2: envelope flags hash =====

    /// <summary>
    /// Audit fix R8-M2: the envelope-flags hash appears in every
    /// compile action's CacheKeyComponents. A toolchain upgrade that
    /// silently changes any envelope flag's default would otherwise
    /// produce silent staleness; the hash forces invalidation.
    /// </summary>
    [Fact]
    public void EnvelopeFlagsHash_AppearsInCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        IExternalAction compile = _linuxToolchain
            .CompileSource(module, target, MakeSource("XCore.cpp"), _scratchDir)
            .Single();

        Assert.Contains(
            compile.CacheKeyComponents,
            c => c.StartsWith("EnvelopeFlagsHash=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R8-M2: the envelope-flags hash on Android differs
    /// from the envelope-flags hash on Linux because the Android
    /// emit includes the <c>--target=</c> flag. Two toolchains
    /// targeting different platforms must produce distinct envelope
    /// hashes.
    /// </summary>
    [Fact]
    public void EnvelopeFlagsHash_DiffersByPlatform()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules linuxTarget = NewTarget(Platform.Linux);
        TargetRules androidTarget = NewAndroidTarget(architecture: "aarch64", apiLevel: 24);

        IExternalAction onLinux = _linuxToolchain
            .CompileSource(module, linuxTarget, MakeSource("Foo.cpp"), _scratchDir).Single();
        IExternalAction onAndroid = _androidToolchain
            .CompileSource(module, androidTarget, MakeSource("Foo.cpp"), _scratchDir).Single();

        string linuxHash = onLinux.CacheKeyComponents
            .Single(c => c.StartsWith("EnvelopeFlagsHash=", StringComparison.Ordinal));
        string androidHash = onAndroid.CacheKeyComponents
            .Single(c => c.StartsWith("EnvelopeFlagsHash=", StringComparison.Ordinal));
        Assert.NotEqual(linuxHash, androidHash);
    }

    /// <summary>
    /// Audit fix R8-M2: the envelope-flags hash flows through the
    /// link cache key too, so an envelope flag change rotates link
    /// keys as well as compile keys.
    /// </summary>
    [Fact]
    public void EnvelopeFlagsHash_AppearsInLinkCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);
        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.o"));

        IExternalAction link = _linuxToolchain.LinkModule(module, target, new[] { obj }, _scratchDir);
        Assert.Contains(
            link.CacheKeyComponents,
            c => c.StartsWith("EnvelopeFlagsHash=", StringComparison.Ordinal));
    }

    // ===== Audit fix R8-M3: descriptor content hash =====

    /// <summary>
    /// Audit fix R8-M3: the descriptor content hash flows through
    /// every compile action's CacheKeyComponents. A descriptor edit
    /// (even one that doesn't change a single parsed field) rotates
    /// the hash and invalidates the cache.
    /// </summary>
    [Fact]
    public void DescriptorHash_AppearsInCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        // Set a synthetic descriptor hash that mirrors the discovery
        // layer's injection.
        module.ApplyDescriptorContentHash("0123456789abcdef");
        TargetRules target = NewTarget(Platform.Linux);

        IExternalAction compile = _linuxToolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains("DescriptorHash=0123456789abcdef", compile.CacheKeyComponents);
    }

    /// <summary>
    /// Audit fix R8-M3: two modules with different descriptor hashes
    /// produce different compile cache keys.
    /// </summary>
    [Fact]
    public void DescriptorHash_DifferentValues_ProduceDifferentCacheKeys()
    {
        ModuleRules first = NewModule(simPath: false);
        first.ApplyDescriptorContentHash("aaaaaaaaaaaaaaaa");
        ModuleRules second = NewModule(simPath: false);
        second.ApplyDescriptorContentHash("bbbbbbbbbbbbbbbb");

        TargetRules target = NewTarget(Platform.Linux);

        IExternalAction firstCompile = _linuxToolchain
            .CompileSource(first, target, MakeSource("F.cpp"), _scratchDir).Single();
        IExternalAction secondCompile = _linuxToolchain
            .CompileSource(second, target, MakeSource("F.cpp"), _scratchDir).Single();

        Assert.NotEqual(firstCompile.CommandVersion, secondCompile.CommandVersion);
    }

    /// <summary>
    /// Audit fix R8-M3: when DescriptorContentHash is null (test path
    /// without a descriptor on disk), the cache key uses a stable
    /// sentinel so two reads in the same process produce equal keys.
    /// </summary>
    [Fact]
    public void DescriptorHash_NullValue_UsesStableSentinel()
    {
        ModuleRules module = NewModule(simPath: false);
        // No ApplyDescriptorContentHash call -- DescriptorContentHash
        // remains null.
        Assert.Null(module.DescriptorContentHash);
        TargetRules target = NewTarget(Platform.Linux);

        IExternalAction compile = _linuxToolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains("DescriptorHash=(no-descriptor)", compile.CacheKeyComponents);
    }

    // ===== Audit fix R8-M4: XBT binary hash =====

    /// <summary>
    /// Audit fix R8-M4: the XBT binary's content hash flows through
    /// every compile action's CacheKeyComponents. A rebuild of XBT
    /// itself rotates the hash and invalidates every cached compile.
    /// </summary>
    [Fact]
    public void XbtBinaryHash_AppearsInCompileCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        IExternalAction compile = _linuxToolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        Assert.Contains(
            compile.CacheKeyComponents,
            c => c.StartsWith("XbtBinaryHash=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit fix R8-M4: a synthetic switch of the XBT binary hash via
    /// the test hook rotates every emitted cache key. Restores the
    /// override after the assertion so other tests in the suite are
    /// unaffected.
    /// </summary>
    [Fact]
    public void XbtBinaryHash_TestOverride_RotatesCacheKey()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        string realHash = ToolchainSelfHash.XbtBinaryHash;
        IExternalAction realKey = _linuxToolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();

        try
        {
            ToolchainSelfHash.__SetForTesting("0000000000000000");
            IExternalAction overridden = _linuxToolchain
                .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir).Single();
            Assert.Contains("XbtBinaryHash=0000000000000000", overridden.CacheKeyComponents);
            Assert.NotEqual(realKey.CommandVersion, overridden.CommandVersion);
        }
        finally
        {
            ToolchainSelfHash.__SetForTesting(null);
        }
        Assert.Equal(realHash, ToolchainSelfHash.XbtBinaryHash);
    }

    /// <summary>
    /// Clang mirror of XMSVCToolChainTests's duplicate-basename test:
    /// two source files with the same basename in different
    /// subdirectories under the module source root must produce
    /// distinct .o paths. Without this, the link step would receive
    /// duplicate prerequisites and the build would fail before any TU
    /// reached the linker.
    /// </summary>
    [Fact]
    public void CompileSource_DuplicateBasenameInDistinctSubdirs_ProducesDistinctObjPaths()
    {
        ModuleRules module = NewModule();
        TargetRules target = NewTarget(Platform.Linux);

        string moduleSourceDir = Path.Combine(_scratchDir, "ModSrc");
        string outputDir = Path.Combine(_scratchDir, "Obj");
        Directory.CreateDirectory(outputDir);

        FileItem sourceA = FileItem.GetItemByPath(
            Path.Combine(moduleSourceDir, "SubA", "Same.cpp"));
        FileItem sourceB = FileItem.GetItemByPath(
            Path.Combine(moduleSourceDir, "SubB", "Same.cpp"));

        IExternalAction compileA = _linuxToolchain.CompileSource(
            module, target, sourceA, outputDir,
            pch: null, moduleSourceDir: moduleSourceDir).Single();
        IExternalAction compileB = _linuxToolchain.CompileSource(
            module, target, sourceB, outputDir,
            pch: null, moduleSourceDir: moduleSourceDir).Single();

        string ObjOf(IExternalAction action) => action.ProducedItems
            .First(i => i.FullPath.EndsWith(".o", StringComparison.OrdinalIgnoreCase))
            .FullPath;

        string objA = ObjOf(compileA);
        string objB = ObjOf(compileB);

        Assert.NotEqual(objA, objB);
        Assert.Equal(Path.Combine(outputDir, "SubA", "Same.o"), objA);
        Assert.Equal(Path.Combine(outputDir, "SubB", "Same.o"), objB);

        // The .d sidecar lives alongside the .o, so the same-basename
        // collision cannot reappear at the dependency-tracking layer.
        string DepOf(IExternalAction action) => action.ProducedItems
            .First(i => i.FullPath.EndsWith(".d", StringComparison.OrdinalIgnoreCase))
            .FullPath;

        Assert.Equal(objA + ".d", DepOf(compileA));
        Assert.Equal(objB + ".d", DepOf(compileB));
    }

    /// <summary>
    /// When <c>moduleSourceDir</c> is omitted (or empty), the Clang
    /// toolchain falls back to flat basename-only naming under
    /// <c>outputDir</c>. Same rationale as the MSVC mirror test.
    /// </summary>
    [Fact]
    public void CompileSource_NoModuleSourceDir_KeepsFlatBasenameLayout()
    {
        ModuleRules module = NewModule();
        TargetRules target = NewTarget(Platform.Linux);

        IExternalAction compile = _linuxToolchain
            .CompileSource(module, target, MakeSource("Foo.cpp"), _scratchDir)
            .Single();

        string objPath = compile.ProducedItems
            .First(i => i.FullPath.EndsWith(".o", StringComparison.OrdinalIgnoreCase))
            .FullPath;

        Assert.Equal(Path.Combine(_scratchDir, "Foo.o"), objPath);
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

    /// <summary>
    /// Audit fix R8-C1: build an Android <see cref="TargetRules"/>
    /// with the architecture + API level set so the toolchain emits
    /// <c>--target=&lt;arch&gt;-linux-android&lt;API&gt;</c>.
    /// </summary>
    private static TargetRules NewAndroidTarget(string architecture, int apiLevel)
    {
        return new TargetRules
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Game,
            Platform = Platform.Android,
            Architecture = architecture,
            AndroidApiLevel = apiLevel,
            Configuration = BuildConfiguration.Development,
            StationRole = StationRole.Engineer,
            SimdLevelDefault = SimdLevel.SSE42,
        };
    }

    /// <summary>
    /// Audit fix R8-C1: build a <see cref="ModuleRules"/> that opts
    /// into a private PCH so GeneratePCH can be exercised.
    /// </summary>
    private static ModuleRules NewModuleWithPch()
    {
        return new ModuleRules
        {
            Name = "XTest",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            SimPath = false,
            PCHUsage = PCHUsageMode.NoSharedPCHs,
            PrivatePCHHeaderFile = "XCorePCH.h",
        };
    }

    private FileItem MakeSource(string name)
    {
        string path = Path.Combine(_scratchDir, name);
        return FileItem.GetItemByPath(path);
    }
}
