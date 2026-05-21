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
/// Verifies the per-module PCH generation path on
/// <see cref="XMSVCToolChain"/> and <see cref="XClangToolChain"/> per
/// Toolchain Contract Rev 13 Section 1.5 + <c>/Documents/XBT.html</c>
/// Rev 4 Section 7.4 / 4. Phase 1.3 scope: per-module PCH only;
/// shared-PCH modes downgrade with a warning.
/// </summary>
/// <remarks>
/// This class is in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection because <c>GenerateModulePCH</c> folds
/// <see cref="Simgenics.XPact.XBT.Core.ToolchainSelfHash.XbtBinaryHash"/>
/// into its emitted action's <c>CacheKeyComponents</c>; any
/// determinism-style assertion is sensitive to the override slot
/// flipping mid-test. Collection membership eliminates the race. See
/// that collection's remarks for the full rationale.
/// </remarks>
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class PCHGenerationTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly XMSVCToolChain _msvc;
    private readonly XClangToolChain _clang;

    public PCHGenerationTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.PCHGeneration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: @"C:\FakeVS",
            compilerPath: @"C:\FakeVS\VC\Tools\MSVC\14.40\bin\HostX64\x64\cl.exe",
            linkerPath: @"C:\FakeVS\VC\Tools\MSVC\14.40\bin\HostX64\x64\link.exe");
        _msvc = new XMSVCToolChain(env, repoRoot: @"C:\repo");
        _clang = new XClangToolChain(
            clangPath: "/usr/bin/clang",
            clangVersion: "18.0.0",
            platform: Platform.Linux,
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
    /// Module with NoSharedPCHs + PrivatePCHHeaderFile produces a
    /// PCHGenerationAction; downstream CompileSource calls thread the
    /// binding through and reference the PCH via /Yu, /Fp, and /FI.
    /// </summary>
    [Fact]
    public void Msvc_PerModulePCH_EmitsGenerationActionAndPlumbsToCompile()
    {
        ModuleRules module = NewModule(
            pchUsage: PCHUsageMode.NoSharedPCHs,
            privatePchHeader: "TestModulePCH.h");
        TargetRules target = NewTarget();

        // Create a real on-disk header so FileItem.ContentHash works.
        FileItem header = MakeFile(
            "TestModulePCH.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");

        PCHBinding binding = _msvc.GeneratePCH(module, target, "TestModulePCH.h", header, _scratchDir);

        Assert.Equal(XActionType.PCHGenerationAction, binding.Action.ActionType);
        Assert.Contains("/YcTestModulePCH.h", binding.Action.CommandArguments);
        Assert.Contains(binding.Action.CommandArguments, a => a.StartsWith("/Fp", StringComparison.Ordinal));

        // Downstream consumer compile picks up /Yu + /Fp + /FI.
        FileItem source = MakeFile("Hello.cpp", "// Copyright Simgenics. All Rights Reserved.\nvoid Foo(){}\n");
        var compileActions = _msvc.CompileSource(module, target, source, _scratchDir, binding);
        IExternalAction compile = compileActions.Single();
        Assert.Contains("/YuTestModulePCH.h", compile.CommandArguments);
        Assert.Contains(compile.CommandArguments, a => a.StartsWith("/Fp", StringComparison.Ordinal));

        // The compile depends on the PCH output (and the header).
        Assert.Contains(compile.PrerequisiteItems, p => p.FullPath == binding.PchOutputFile.FullPath);
        Assert.Contains(compile.PrerequisiteItems, p => p.FullPath == header.FullPath);
    }

    /// <summary>
    /// Module with PCHUsage=NoPCHs: no PCH binding is constructed (the
    /// build orchestrator skips GeneratePCH entirely). When CompileSource
    /// runs without a binding, the compile carries no /Yu.
    /// </summary>
    [Fact]
    public void Msvc_NoPCHs_NoPCHFlagsInCompile()
    {
        ModuleRules module = NewModule(
            pchUsage: PCHUsageMode.NoPCHs,
            privatePchHeader: null);
        TargetRules target = NewTarget();
        FileItem source = MakeFile("Hello.cpp", "// Copyright Simgenics. All Rights Reserved.\nvoid Foo(){}\n");

        var compileActions = _msvc.CompileSource(module, target, source, _scratchDir, pch: null);
        IExternalAction compile = compileActions.Single();
        Assert.DoesNotContain(compile.CommandArguments, a => a.StartsWith("/Yu", StringComparison.Ordinal));
        Assert.DoesNotContain(compile.CommandArguments, a => a.StartsWith("/Yc", StringComparison.Ordinal));
        Assert.Contains("PCH=none", compile.CacheKeyComponents);
    }

    /// <summary>
    /// Module with PCHUsage=UseSharedPCHs is downgraded to NoSharedPCHs
    /// with a Logger.Warning per Phase 1.3 spec. The downgrade is via
    /// <see cref="XToolChain.ResolvePCHUsage"/>.
    /// </summary>
    [Fact]
    public void Msvc_UseSharedPCHs_DowngradedToNoSharedPCHs()
    {
        ModuleRules module = NewModule(
            pchUsage: PCHUsageMode.UseSharedPCHs,
            privatePchHeader: "Shared.h");

        PCHUsageMode resolved = XToolChain.ResolvePCHUsage(module);
        Assert.Equal(PCHUsageMode.NoSharedPCHs, resolved);
    }

    /// <summary>
    /// SimPath module declaring PCHUsage=UseSharedPCHs fails at
    /// toolchain emit time with exit 41 (defence-in-depth -- the
    /// parser-side validator catches this earlier).
    /// </summary>
    [Fact]
    public void Msvc_SimPathWithUseSharedPCHs_RejectedWithExit41()
    {
        ModuleRules module = new ModuleRules
        {
            Name = "SimPathMod",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            SimPath = true,
            // Declare an explicit sim-path-banned PCH usage.
            PCHUsage = PCHUsageMode.UseSharedPCHs,
            PrivatePCHHeaderFile = "Foo.h",
            // SimPath modules require SimdLevel <= SSE42; explicitly opt
            // into the production baseline.
            SimdLevel = SimdLevel.SSE42,
        };
        TargetRules target = NewTarget();
        FileItem header = MakeFile("Foo.h", "// Copyright Simgenics. All Rights Reserved.\n");

        ToolchainBannedFlagException ex = Assert.Throws<ToolchainBannedFlagException>(
            () => _msvc.GeneratePCH(module, target, "Foo.h", header, _scratchDir));
        Assert.Equal(41, ex.ExitCode);
    }

    /// <summary>
    /// MSVC and Clang produce different concrete command lines for the
    /// same module/binding pair.
    /// </summary>
    [Fact]
    public void MsvcVsClang_DifferentCommandLines()
    {
        ModuleRules module = NewModule(
            pchUsage: PCHUsageMode.NoSharedPCHs,
            privatePchHeader: "CrossPCH.h");

        TargetRules msvcTarget = NewTarget(Platform.Win64);
        TargetRules clangTarget = NewTarget(Platform.Linux);

        FileItem header = MakeFile(
            "CrossPCH.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <string>\n");

        PCHBinding msvcBinding = _msvc.GeneratePCH(
            module, msvcTarget, "CrossPCH.h", header, Path.Combine(_scratchDir, "msvc"));
        PCHBinding clangBinding = _clang.GeneratePCH(
            module, clangTarget, "CrossPCH.h", header, Path.Combine(_scratchDir, "clang"));

        // MSVC uses /Yc<header> and a .pch artefact.
        Assert.Contains(msvcBinding.Action.CommandArguments, a => a.StartsWith("/Yc", StringComparison.Ordinal));
        Assert.EndsWith(".pch", msvcBinding.PchOutputFile.FullPath, StringComparison.OrdinalIgnoreCase);

        // Clang uses -x c++-header and a .pchi artefact.
        Assert.Contains("-x", clangBinding.Action.CommandArguments);
        Assert.Contains("c++-header", clangBinding.Action.CommandArguments);
        Assert.EndsWith(".pchi", clangBinding.PchOutputFile.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Include-order policy: a PCH header with non-alphabetical
    /// <c>#include</c> directives is rewritten alphabetically on disk;
    /// the rewriter reports the rewrite happened.
    /// </summary>
    [Fact]
    public void IncludeOrder_NonAlphabetical_RewrittenInAlphabeticalOrder()
    {
        // Hand-ordered includes: Z first, then A, then M.
        string disorderedSource =
            "// Copyright Simgenics. All Rights Reserved.\n" +
            "#include \"Z.h\"\n" +
            "#include \"A.h\"\n" +
            "#include \"M.h\"\n";
        FileItem header = MakeFile("Disordered.h", disorderedSource);

        PCHIncludeOrderRewriter.RewriteResult result = PCHIncludeOrderRewriter.Rewrite(disorderedSource);
        Assert.True(result.Changed);
        Assert.Equal(1, result.BlocksRewritten);

        // The rewritten text places A.h before M.h before Z.h.
        int idxA = result.RewrittenText.IndexOf("\"A.h\"", StringComparison.Ordinal);
        int idxM = result.RewrittenText.IndexOf("\"M.h\"", StringComparison.Ordinal);
        int idxZ = result.RewrittenText.IndexOf("\"Z.h\"", StringComparison.Ordinal);
        Assert.True(idxA > 0);
        Assert.True(idxA < idxM);
        Assert.True(idxM < idxZ);

        // File-level helper round-trip on disk.
        bool rewritten = PCHIncludeOrderRewriter.RewriteFile(header.FullPath);
        Assert.True(rewritten);
        string onDisk = File.ReadAllText(header.FullPath);
        int diskA = onDisk.IndexOf("\"A.h\"", StringComparison.Ordinal);
        int diskZ = onDisk.IndexOf("\"Z.h\"", StringComparison.Ordinal);
        Assert.True(diskA < diskZ);

        // Idempotent: second call is a no-op.
        bool second = PCHIncludeOrderRewriter.RewriteFile(header.FullPath);
        Assert.False(second);
    }

    // ----- Helpers -----

    private static ModuleRules NewModule(
        PCHUsageMode pchUsage = PCHUsageMode.Default,
        string? privatePchHeader = null)
    {
        return new ModuleRules
        {
            Name = "TestModule",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            PCHUsage = pchUsage,
            PrivatePCHHeaderFile = privatePchHeader,
        };
    }

    private static TargetRules NewTarget(Platform platform = Platform.Win64)
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

    private FileItem MakeFile(string name, string content)
    {
        string path = Path.Combine(_scratchDir, name);
        File.WriteAllText(path, content);
        return FileItem.GetItemByPath(path);
    }
}
