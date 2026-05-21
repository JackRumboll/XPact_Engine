// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
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
/// Exercises the shared-PCH generation path on
/// <see cref="XMSVCToolChain.GenerateSharedPCH"/> and
/// <see cref="XClangToolChain.GenerateSharedPCH"/> per Toolchain Contract
/// Rev 13 Section 1.5 + <c>/Documents/XBT.html</c> Rev 4 Section 15.4.
/// Phase 1.4b scope: emit a single PCHGenerationAction shared by 2+
/// participants, key the cache by participants + their PublicDefinitions,
/// reject SimPath participation, and produce deterministic command lines.
/// </summary>
/// <remarks>
/// This class is in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection because
/// <see cref="SameGroup_DeterministicCommandVersion"/> asserts two
/// back-to-back <c>GenerateSharedPCH</c> calls produce the same
/// <c>CommandVersion</c>; both calls read
/// <see cref="Simgenics.XPact.XBT.Core.ToolchainSelfHash.XbtBinaryHash"/>
/// through the toolchain's cache-key build, so a parallel test mutating
/// the override slot between them would race the assertion. See that
/// collection's remarks for the full rationale.
/// </remarks>
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class SharedPchTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly XMSVCToolChain _msvc;
    private readonly XClangToolChain _clang;

    public SharedPchTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.SharedPch",
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

    // ---------------------------------------------------------------------
    // 1. Two modules share -> ONE shared PCHGenerationAction.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Two modules declare the same SharedPCHHeaderFile; the toolchain
    /// emits a SINGLE PCHGenerationAction; each module's consumer compile
    /// references the shared PCH via /Yu (MSVC) or -include-pch (Clang).
    /// </summary>
    [Fact]
    public void TwoModulesShare_OneSharedActionEmitted()
    {
        FileItem header = MakeFile(
            "Common.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");

        ModuleRules a = NewModule(name: "MA", sharedPchHeader: "Common.h");
        ModuleRules b = NewModule(name: "MB", sharedPchHeader: "Common.h");
        TargetRules target = NewTarget();

        PCHBinding binding = _msvc.GenerateSharedPCH(
            headerFile: "Common.h",
            participants: new[] { a, b },
            headerFileItem: header,
            target: target,
            outputDir: _scratchDir);

        // ONE action emitted, of type PCHGenerationAction.
        Assert.Equal(XActionType.PCHGenerationAction, binding.Action.ActionType);

        // The action command includes /Yc<header> (MSVC create-PCH form).
        Assert.Contains("/YcCommon.h", binding.Action.CommandArguments);

        // Consumer compiles reference the shared PCH via /Yu.
        FileItem srcA = MakeFile("A.cpp", "// Copyright Simgenics. All Rights Reserved.\nvoid A(){}\n");
        FileItem srcB = MakeFile("B.cpp", "// Copyright Simgenics. All Rights Reserved.\nvoid B(){}\n");

        IExternalAction compileA = _msvc.CompileSource(a, target, srcA, _scratchDir, binding).Single();
        IExternalAction compileB = _msvc.CompileSource(b, target, srcB, _scratchDir, binding).Single();

        Assert.Contains("/YuCommon.h", compileA.CommandArguments);
        Assert.Contains("/YuCommon.h", compileB.CommandArguments);

        // Both consumer compiles depend on the SAME PCH output file.
        Assert.Contains(compileA.PrerequisiteItems, p => p.FullPath == binding.PchOutputFile.FullPath);
        Assert.Contains(compileB.PrerequisiteItems, p => p.FullPath == binding.PchOutputFile.FullPath);
    }

    // ---------------------------------------------------------------------
    // 2. Three modules share -> ONE shared action; all three participate.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Three modules share a header; the toolchain emits ONE action whose
    /// CacheKeyComponents list every participant's name (so changing any
    /// participant's flags invalidates the shared PCH).
    /// </summary>
    [Fact]
    public void ThreeModulesShare_OneActionWithAllParticipantsListed()
    {
        FileItem header = MakeFile(
            "Three.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <string>\n");

        ModuleRules a = NewModule(name: "A1", sharedPchHeader: "Three.h");
        ModuleRules b = NewModule(name: "B2", sharedPchHeader: "Three.h");
        ModuleRules c = NewModule(name: "C3", sharedPchHeader: "Three.h");
        TargetRules target = NewTarget();

        PCHBinding binding = _msvc.GenerateSharedPCH(
            headerFile: "Three.h",
            participants: new[] { a, b, c },
            headerFileItem: header,
            target: target,
            outputDir: _scratchDir);

        Assert.Equal(XActionType.PCHGenerationAction, binding.Action.ActionType);

        // CacheKeyComponents include a Participant=<name> entry for each.
        Assert.Contains("Participant=A1", binding.Action.CacheKeyComponents);
        Assert.Contains("Participant=B2", binding.Action.CacheKeyComponents);
        Assert.Contains("Participant=C3", binding.Action.CacheKeyComponents);
    }

    // ---------------------------------------------------------------------
    // 3. Different SharedPCHHeaderFile values -> two distinct shared actions.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Two groups with different shared headers produce two distinct
    /// PCHGenerationAction instances with different CommandVersion hashes.
    /// </summary>
    [Fact]
    public void DifferentSharedHeaders_TwoDistinctActions()
    {
        FileItem headerA = MakeFile(
            "GroupA.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");
        FileItem headerB = MakeFile(
            "GroupB.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <string>\n");

        ModuleRules m1 = NewModule(name: "M1", sharedPchHeader: "GroupA.h");
        ModuleRules m2 = NewModule(name: "M2", sharedPchHeader: "GroupA.h");
        ModuleRules m3 = NewModule(name: "M3", sharedPchHeader: "GroupB.h");
        ModuleRules m4 = NewModule(name: "M4", sharedPchHeader: "GroupB.h");

        TargetRules target = NewTarget();
        PCHBinding bindingA = _msvc.GenerateSharedPCH(
            headerFile: "GroupA.h",
            participants: new[] { m1, m2 },
            headerFileItem: headerA,
            target: target,
            outputDir: Path.Combine(_scratchDir, "ga"));
        PCHBinding bindingB = _msvc.GenerateSharedPCH(
            headerFile: "GroupB.h",
            participants: new[] { m3, m4 },
            headerFileItem: headerB,
            target: target,
            outputDir: Path.Combine(_scratchDir, "gb"));

        // Distinct PCH output paths.
        Assert.NotEqual(bindingA.PchOutputFile.FullPath, bindingB.PchOutputFile.FullPath);

        // Distinct CommandVersion hashes.
        Assert.NotEqual(bindingA.Action.CommandVersion, bindingB.Action.CommandVersion);
    }

    // ---------------------------------------------------------------------
    // 4. Single participant -> NOT a shared-PCH emission (caller handles).
    //    Note: the toolchain method itself accepts a 1-participant input
    //    (it's a valid edge case the helper supports); the BuildMode
    //    grouping layer is what enforces the "fall back to private-PCH
    //    semantics" rule. This test exercises that the toolchain method
    //    does NOT crash on a 1-participant input and produces a valid
    //    PCHBinding for the lone participant -- the spec's
    //    Logger.Info("single participant") branch lives in BuildMode and
    //    is covered by SharedPchGroupingTests below.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The toolchain method itself accepts a 1-participant input as a
    /// valid edge case; the BuildMode grouping pass is what diverts a
    /// 1-participant declaration into private-PCH semantics. Verified
    /// here: GenerateSharedPCH does not crash on a 1-participant input.
    /// </summary>
    [Fact]
    public void SingleParticipantToToolchain_DoesNotCrash()
    {
        FileItem header = MakeFile(
            "Solo.h",
            "// Copyright Simgenics. All Rights Reserved.\n");
        ModuleRules a = NewModule(name: "SoloMod", sharedPchHeader: "Solo.h");
        TargetRules target = NewTarget();

        PCHBinding binding = _msvc.GenerateSharedPCH(
            headerFile: "Solo.h",
            participants: new[] { a },
            headerFileItem: header,
            target: target,
            outputDir: _scratchDir);

        Assert.Equal(XActionType.PCHGenerationAction, binding.Action.ActionType);
        Assert.EndsWith(".pch", binding.PchOutputFile.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // 5. SimPath in shared group -> exit 41.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Any SimPath participant in a shared-PCH group causes the toolchain
    /// to throw <see cref="ToolchainBannedFlagException"/> with exit 41,
    /// per Contract Rev 13 Section 1.5.
    /// </summary>
    [Fact]
    public void SimPathParticipant_RejectedWithExit41()
    {
        FileItem header = MakeFile(
            "Bad.h",
            "// Copyright Simgenics. All Rights Reserved.\n");

        ModuleRules safe = NewModule(name: "Safe", sharedPchHeader: "Bad.h");
        ModuleRules unsafeSim = new()
        {
            Name = "Unsafe",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            SimPath = true,
            SimdLevel = SimdLevel.SSE42,
            PCHUsage = PCHUsageMode.NoSharedPCHs,
            SharedPCHHeaderFile = "Bad.h",
        };

        ToolchainBannedFlagException ex = Assert.Throws<ToolchainBannedFlagException>(
            () => _msvc.GenerateSharedPCH(
                headerFile: "Bad.h",
                participants: new[] { safe, unsafeSim },
                headerFileItem: header,
                target: NewTarget(),
                outputDir: _scratchDir));
        Assert.Equal(41, ex.ExitCode);
        Assert.Contains("Unsafe", ex.Message);
        Assert.Contains("SimPath", ex.Message);
    }

    // ---------------------------------------------------------------------
    // 6. Cache key: changing participant PublicDefinitions invalidates PCH.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The shared PCH's CommandVersion incorporates every participant's
    /// PublicDefinitions content hash; mutating one participant's defines
    /// produces a different CommandVersion and a different on-disk hash.
    /// </summary>
    [Fact]
    public void PublicDefinitionsChange_InvalidatesSharedPCH()
    {
        FileItem header = MakeFile(
            "Cache.h",
            "// Copyright Simgenics. All Rights Reserved.\n");

        ModuleRules a = NewModule(name: "AA", sharedPchHeader: "Cache.h");
        ModuleRules b1 = NewModule(name: "BB", sharedPchHeader: "Cache.h");
        b1.PublicDefinitions.Add("DEFINE_V1=1");

        ModuleRules b2 = NewModule(name: "BB", sharedPchHeader: "Cache.h");
        b2.PublicDefinitions.Add("DEFINE_V2=1");

        TargetRules target = NewTarget();
        PCHBinding bindingV1 = _msvc.GenerateSharedPCH(
            headerFile: "Cache.h",
            participants: new[] { a, b1 },
            headerFileItem: header,
            target: target,
            outputDir: Path.Combine(_scratchDir, "v1"));
        PCHBinding bindingV2 = _msvc.GenerateSharedPCH(
            headerFile: "Cache.h",
            participants: new[] { a, b2 },
            headerFileItem: header,
            target: target,
            outputDir: Path.Combine(_scratchDir, "v2"));

        // Different group hashes => different on-disk paths => different
        // CommandVersion.
        Assert.NotEqual(bindingV1.PchOutputFile.FullPath, bindingV2.PchOutputFile.FullPath);
        Assert.NotEqual(bindingV1.Action.CommandVersion, bindingV2.Action.CommandVersion);
    }

    // ---------------------------------------------------------------------
    // 7. MSVC and Clang produce DIFFERENT concrete command lines for the
    //    same grouping.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The same grouping on different toolchains emits distinct command
    /// lines (MSVC: /Yc + .pch; Clang: -x c++-header + .pchi).
    /// </summary>
    [Fact]
    public void MsvcVsClang_DifferentCommandLinesForSameGrouping()
    {
        FileItem header = MakeFile(
            "Cross.h",
            "// Copyright Simgenics. All Rights Reserved.\n#include <string>\n");

        ModuleRules m1 = NewModule(name: "X1", sharedPchHeader: "Cross.h");
        ModuleRules m2 = NewModule(name: "X2", sharedPchHeader: "Cross.h");

        PCHBinding msvcBinding = _msvc.GenerateSharedPCH(
            headerFile: "Cross.h",
            participants: new[] { m1, m2 },
            headerFileItem: header,
            target: NewTarget(Platform.Win64),
            outputDir: Path.Combine(_scratchDir, "msvc"));
        PCHBinding clangBinding = _clang.GenerateSharedPCH(
            headerFile: "Cross.h",
            participants: new[] { m1, m2 },
            headerFileItem: header,
            target: NewTarget(Platform.Linux),
            outputDir: Path.Combine(_scratchDir, "clang"));

        // MSVC uses /Yc<header> and produces a .pch artefact.
        Assert.Contains(msvcBinding.Action.CommandArguments, a => a.StartsWith("/Yc", StringComparison.Ordinal));
        Assert.EndsWith(".pch", msvcBinding.PchOutputFile.FullPath, StringComparison.OrdinalIgnoreCase);

        // Clang uses -x c++-header and produces a .pchi artefact.
        Assert.Contains("-x", clangBinding.Action.CommandArguments);
        Assert.Contains("c++-header", clangBinding.Action.CommandArguments);
        Assert.EndsWith(".pchi", clangBinding.PchOutputFile.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // 8. Determinism: same input groups -> byte-identical CommandVersion.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Two GenerateSharedPCH invocations with the same inputs (same
    /// participants, same header, same outputDir) produce byte-identical
    /// <see cref="IExternalAction.CommandVersion"/> hashes. Argument-order
    /// of the participants array does NOT change the hash because the
    /// toolchain sorts participants ordinal before emitting.
    /// </summary>
    [Fact]
    public void SameGroup_DeterministicCommandVersion()
    {
        FileItem header = MakeFile(
            "Deterministic.h",
            "// Copyright Simgenics. All Rights Reserved.\n");

        // Distinct ModuleRules instances with the same field values. The
        // hash must depend on values, not on object identity.
        ModuleRules a1 = NewModule(name: "AA", sharedPchHeader: "Deterministic.h");
        ModuleRules b1 = NewModule(name: "BB", sharedPchHeader: "Deterministic.h");
        ModuleRules a2 = NewModule(name: "AA", sharedPchHeader: "Deterministic.h");
        ModuleRules b2 = NewModule(name: "BB", sharedPchHeader: "Deterministic.h");

        TargetRules target = NewTarget();

        // Same outputDir so the on-disk path is identical between runs;
        // the determinism guarantee is on (inputs -> CommandVersion).
        string sharedOutputDir = Path.Combine(_scratchDir, "shared");

        PCHBinding binding1 = _msvc.GenerateSharedPCH(
            headerFile: "Deterministic.h",
            participants: new[] { a1, b1 },
            headerFileItem: header,
            target: target,
            outputDir: sharedOutputDir);
        PCHBinding binding2 = _msvc.GenerateSharedPCH(
            headerFile: "Deterministic.h",
            participants: new[] { a2, b2 },
            headerFileItem: header,
            target: target,
            outputDir: sharedOutputDir);

        Assert.Equal(binding1.Action.CommandVersion, binding2.Action.CommandVersion);

        // Also: argument-order shouldn't matter -- the toolchain sorts
        // participants ordinal internally before hashing.
        PCHBinding binding3 = _msvc.GenerateSharedPCH(
            headerFile: "Deterministic.h",
            participants: new[] { b1, a1 },     // reverse order
            headerFileItem: header,
            target: target,
            outputDir: sharedOutputDir);

        Assert.Equal(binding1.Action.CommandVersion, binding3.Action.CommandVersion);
    }

    // ---------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------

    private static ModuleRules NewModule(string name, string sharedPchHeader)
    {
        return new ModuleRules
        {
            Name = name,
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
            Languages = Languages.Cpp,
            PCHUsage = PCHUsageMode.NoSharedPCHs,
            SharedPCHHeaderFile = sharedPchHeader,
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
