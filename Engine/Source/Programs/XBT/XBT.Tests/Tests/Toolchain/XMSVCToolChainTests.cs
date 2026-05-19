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
/// Tests construct a synthetic <see cref="VCEnvironment"/> so the
/// toolchain code path runs deterministically without depending on a
/// real MSVC install. The flag-emission assertions inspect the
/// generated <see cref="IExternalAction.CommandArguments"/> directly.
/// </remarks>
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
    /// Non-SimPath module with FPSemantics.Default resolves to /fp:fast
    /// (non-SimPath production default).
    /// </summary>
    [Fact]
    public void NonSimPathModule_FpDefault_EmitsFpFast()
    {
        ModuleRules module = NewModule(simPath: false);
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
    /// Reproducibility envelope (XBT.html Section 19.1):
    /// every MSVC compile emits /Brepro + /pathmap:&lt;root&gt;=X:/R;
    /// every MSVC link emits /TIMESTAMP:0 + /BREPRO + /INCREMENTAL:NO.
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
