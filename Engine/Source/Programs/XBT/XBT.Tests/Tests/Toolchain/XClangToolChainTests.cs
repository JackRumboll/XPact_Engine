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
    /// Non-SimPath module with FPSemantics.Default emits the
    /// -ffast-math-family flags.
    /// </summary>
    [Fact]
    public void NonSimPathModule_FpDefault_EmitsFastMath()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        var actions = _linuxToolchain.CompileSource(module, target, MakeSource("XRenderer.cpp"), _scratchDir);
        IExternalAction compile = actions.Single();

        Assert.Contains("-ffast-math", compile.CommandArguments);
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
    /// Section 2.1, Rev 13.1): every Clang compile emits
    /// -fdebug-prefix-map + -fno-ident +
    /// -frandomize-layout-seed-file=&lt;path&gt;; every Clang link emits
    /// --remap-file=&lt;repoRoot&gt;=X:/R + -Wl,--build-id=none +
    /// -fno-ident.
    /// </summary>
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
    /// Contract Rev 13.1 Section 2.1: the Clang link command uses
    /// <c>--remap-file=&lt;RepoRoot&gt;=X:/R</c> (Clang's pathmap
    /// equivalent) instead of <c>-fdebug-prefix-map=</c>. The link side
    /// MUST NOT carry the compile-side debug-info flag because the
    /// linker's source-path remap surface is a different machinery.
    /// </summary>
    [Fact]
    public void Link_UsesRemapFileInsteadOfFdebugPrefixMap()
    {
        ModuleRules module = NewModule(simPath: false);
        TargetRules target = NewTarget(Platform.Linux);

        FileItem obj = FileItem.GetItemByPath(Path.Combine(_scratchDir, "Foo.o"));
        IExternalAction link = _linuxToolchain.LinkModule(module, target, new[] { obj }, _scratchDir);

        Assert.Contains("--remap-file=/home/user/repo=X:/R", link.CommandArguments);
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
