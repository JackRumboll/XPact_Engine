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
    /// Reproducibility envelope (XBT.html Section 19.1): every Clang
    /// compile emits -fdebug-prefix-map + -fno-ident; every Clang link
    /// emits -Wl,--build-id=none.
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
