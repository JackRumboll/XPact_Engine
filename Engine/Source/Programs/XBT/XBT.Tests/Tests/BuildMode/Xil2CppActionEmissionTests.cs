// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.ActionGraph.Actions;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode;

/// <summary>
/// Action-graph emission coverage for XIL2CPP Phase 6.a wiring into
/// <see cref="Simgenics.XPact.XBT.Entry.BuildMode"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8 +
/// <c>/Documents/XBT.html</c> Rev 11 Section 5.1.
/// </summary>
/// <remarks>
/// <para>
/// Builds a synthetic engine fixture with two C#-bearing modules where one
/// (<c>XScoring</c>) depends on the other (<c>XCoreCs</c>), plus a
/// C++-only module (<c>XCpp</c>) that emits NO XIL2CPP actions. A fake
/// <c>xil2cpp.exe</c> is dropped under <c>Binaries/Win64/</c> so the
/// BuildMode resolver finds it.
/// </para>
/// <para>
/// <b>Default-off invariant.</b> WITHOUT <c>-EnableXIL2CPP</c> the action
/// graph carries NO <see cref="ReferenceCompileCSharpAction"/> /
/// <see cref="XIL2CPPAction"/> nodes -- the same graph a pre-XIL2CPP build
/// emits. WITH the flag, each C#-bearing module emits both, and the
/// Section 9.8 ordering edges (dependency refonly DLLs in the prerequisite
/// set) are present.
/// </para>
/// <para>
/// Audit fix R5-M3: in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection because <c>BuildMode.Run</c> instantiates a toolchain
/// whose emit paths read <c>ToolchainSelfHash.XbtBinaryHash</c>.
/// </para>
/// </remarks>
[Trait("Category", "SmokeBuild")]
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class Xil2CppActionEmissionTests : IDisposable
{
    private readonly string _scratch;
    private readonly string _engineRoot;

    public Xil2CppActionEmissionTests()
    {
        _scratch = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.Xil2CppActionEmission",
            Guid.NewGuid().ToString("N"));
        _engineRoot = Path.Combine(_scratch, "Engine");
        BuildFixture();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// WITH -EnableXIL2CPP a C#-bearing module emits a
    /// ReferenceCompileCSharpAction AND an XIL2CPPAction, and the
    /// dependency-consumer's actions carry the dependency module's
    /// refonly DLL as a prerequisite (the Section 9.8 ordering edge).
    /// </summary>
    [Fact]
    public void WithFlag_CSharpModule_EmitsRefCompileAndTranspile_WithDependencyEdge()
    {
        if (!IsToolchainAvailable())
        {
            Simgenics.XPact.XBT.Core.Logger.Warning(
                "Xil2CppActionEmissionTests skipped: no compatible toolchain on this host.");
            return;
        }

        BuildResult result = RunBuild(enableXil2cpp: true);
        Assert.NotEmpty(result.Actions);

        // Two C#-bearing modules => two of each action.
        var refCompiles = result.Actions
            .OfType<ReferenceCompileCSharpAction>()
            .ToList();
        var transpiles = result.Actions
            .OfType<XIL2CPPAction>()
            .ToList();

        Assert.Equal(2, refCompiles.Count);
        Assert.Equal(2, transpiles.Count);
        Assert.Equal(2, result.Actions.Count(a => a.ActionType == XActionType.ReferenceCompileCSharpAction));
        Assert.Equal(2, result.Actions.Count(a => a.ActionType == XActionType.XIL2CPPAction));

        // The C++-only module emits NEITHER action.
        Assert.DoesNotContain(refCompiles, a => a.ModuleName == "XCpp");
        Assert.DoesNotContain(transpiles, a => a.ModuleName == "XCpp");

        ReferenceCompileCSharpAction scoringRef =
            Assert.Single(refCompiles, a => a.ModuleName == "XScoring");
        XIL2CPPAction scoringTranspile =
            Assert.Single(transpiles, a => a.ModuleName == "XScoring");

        // The dependency (XCoreCs) refonly DLL path the consumer expects.
        string expectedDepDll = ComposeRefonlyDllPath("XCoreCs");

        // ReferenceCompileCSharpAction(XScoring) prerequisites include the
        // dependency refonly DLL (the Section 9.8 ordering edge:
        // ReferenceCompileCSharpAction(XCoreCs) before
        // ReferenceCompileCSharpAction(XScoring)).
        Assert.Contains(scoringRef.PrerequisiteItems,
            f => PathsEqual(f.FullPath, expectedDepDll));
        Assert.Contains(scoringRef.DependencyReferences,
            d => d.ModuleName == "XCoreCs" && PathsEqual(d.RefOnlyDllPath, expectedDepDll));

        // XIL2CPPAction(XScoring) prerequisites include the dependency
        // refonly DLL (the Section 9.8 ordering edge:
        // ReferenceCompileCSharpAction(XCoreCs) before XIL2CPPAction(XScoring)).
        Assert.Contains(scoringTranspile.PrerequisiteItems,
            f => PathsEqual(f.FullPath, expectedDepDll));
        Assert.Contains(scoringTranspile.DependencyReferences,
            d => d.ModuleName == "XCoreCs" && PathsEqual(d.RefOnlyDllPath, expectedDepDll));

        // The dependency module (XCoreCs) has no C# deps of its own, so its
        // actions carry no dependency refonly edges.
        ReferenceCompileCSharpAction coreRef =
            Assert.Single(refCompiles, a => a.ModuleName == "XCoreCs");
        XIL2CPPAction coreTranspile =
            Assert.Single(transpiles, a => a.ModuleName == "XCoreCs");
        Assert.Empty(coreRef.DependencyReferences);
        Assert.Empty(coreTranspile.DependencyReferences);

        // The produced/consumed FileItem edge is shared: the
        // ReferenceCompileCSharpAction(XCoreCs) ProducedItem IS the DLL the
        // consumer lists as a prerequisite (same interned path).
        FileItem coreProduced = Assert.Single(coreRef.ProducedItems);
        Assert.True(PathsEqual(coreProduced.FullPath, expectedDepDll),
            $"Producer DLL {coreProduced.FullPath} should equal consumer prereq {expectedDepDll}.");
    }

    /// <summary>
    /// WITHOUT the flag (default) NO ReferenceCompileCSharpAction /
    /// XIL2CPPAction is emitted -- the default action graph is unchanged.
    /// </summary>
    [Fact]
    public void WithoutFlag_Default_EmitsNoXil2CppActions()
    {
        if (!IsToolchainAvailable())
        {
            Simgenics.XPact.XBT.Core.Logger.Warning(
                "Xil2CppActionEmissionTests skipped: no compatible toolchain on this host.");
            return;
        }

        BuildResult result = RunBuild(enableXil2cpp: false);
        Assert.NotEmpty(result.Actions);

        Assert.Empty(result.Actions.OfType<ReferenceCompileCSharpAction>());
        Assert.Empty(result.Actions.OfType<XIL2CPPAction>());
        Assert.Equal(0, result.Actions.Count(a => a.ActionType == XActionType.ReferenceCompileCSharpAction));
        Assert.Equal(0, result.Actions.Count(a => a.ActionType == XActionType.XIL2CPPAction));
    }

    /// <summary>
    /// The default-off graph is byte-for-byte unaffected by the new flag:
    /// the set of emitted action types + their count is identical whether
    /// the flag is parsed-but-false or simply absent. Verified by comparing
    /// the non-XIL2CPP action multiset across a flag-on and flag-off run.
    /// </summary>
    [Fact]
    public void FlagOn_AddsOnlyXil2CppActions_RestOfGraphIdentical()
    {
        if (!IsToolchainAvailable())
        {
            Simgenics.XPact.XBT.Core.Logger.Warning(
                "Xil2CppActionEmissionTests skipped: no compatible toolchain on this host.");
            return;
        }

        BuildResult off = RunBuild(enableXil2cpp: false);
        BuildResult on = RunBuild(enableXil2cpp: true);

        // Every action type EXCEPT the two XIL2CPP types must appear with
        // the same multiplicity in both graphs.
        var offCounts = off.Actions
            .Select(a => a.ActionType)
            .GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count());
        var onCounts = on.Actions
            .Where(a => a.ActionType != XActionType.ReferenceCompileCSharpAction
                     && a.ActionType != XActionType.XIL2CPPAction)
            .Select(a => a.ActionType)
            .GroupBy(t => t)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(offCounts.Count, onCounts.Count);
        foreach (var kvp in offCounts)
        {
            Assert.True(onCounts.TryGetValue(kvp.Key, out int onCount),
                $"Action type {kvp.Key} present off-flag but absent on-flag.");
            Assert.Equal(kvp.Value, onCount);
        }

        // The off graph carries none of the new types.
        Assert.DoesNotContain(off.Actions, a =>
            a.ActionType == XActionType.ReferenceCompileCSharpAction
            || a.ActionType == XActionType.XIL2CPPAction);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private BuildResult RunBuild(bool enableXil2cpp)
    {
        BuildOptions options = new()
        {
            TargetName = "XScoring",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = _engineRoot,
            EnableXIL2CPP = enableXil2cpp,
        };
        return Simgenics.XPact.XBT.Entry.BuildMode.Run(options, CancellationToken.None);
    }

    /// <summary>
    /// The XIL2CPP intermediate root mirrors BuildMode's resolution
    /// (repo root = engineRoot's parent; Intermediate/Build/XIL2CPP).
    /// </summary>
    private string ComposeRefonlyDllPath(string moduleName)
    {
        string repoRoot = Directory.GetParent(_engineRoot)!.FullName;
        return Path.Combine(
            repoRoot, "Intermediate", "Build", "XIL2CPP",
            moduleName, "Reference", moduleName + ".refonly.dll");
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsToolchainAvailable()
        => VCEnvironment.TryDiscover(out _) == VCEnvironment.DiscoveryResult.Found;

    private void BuildFixture()
    {
        Directory.CreateDirectory(_engineRoot);

        // Engine descriptor.
        File.WriteAllText(
            Path.Combine(_engineRoot, "Engine.xengine"),
            "{\n" +
            "  \"_comment\": \"Copyright Simgenics. All Rights Reserved.\",\n" +
            "  \"FileVersion\": 1,\n" +
            "  \"EngineName\": \"Xil2CppFixture\",\n" +
            "  \"EngineVersion\": \"0.1.0\",\n" +
            "  \"Copyright\": \"Copyright Simgenics. All Rights Reserved.\",\n" +
            "  \"MinSupportedPlatforms\": [\"Win64\", \"Linux\"],\n" +
            "  \"BuildTargets\": [\"Editor\", \"Game\", \"Server\"],\n" +
            "  \"BuildConfigurations\": [\"Debug\", \"DebugGame\", \"Development\", \"Test\", \"Shipping\"]\n" +
            "}\n");

        // Module XCoreCs: C#-only leaf module (no deps).
        WriteCsModule(
            "XCoreCs",
            languages: "[\"CSharp\"]",
            dependencyToml: "",
            csNamespace: "XCoreCs",
            csType: "CoreThing");

        // Module XScoring: C#-only module depending on XCoreCs.
        WriteCsModule(
            "XScoring",
            languages: "[\"CSharp\"]",
            dependencyToml: "public_dependency_modules = [\"XCoreCs\"]\n",
            csNamespace: "XScoring",
            csType: "Widget");

        // Module XCpp: C++-only module (emits no XIL2CPP actions).
        WriteCppModule("XCpp");

        // Fake xil2cpp executable so the BuildMode resolver finds it.
        string binDir = Path.Combine(_engineRoot, "Binaries", "Win64");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "xil2cpp.exe"), "fake xil2cpp");
    }

    private void WriteCsModule(
        string name,
        string languages,
        string dependencyToml,
        string csNamespace,
        string csType)
    {
        string moduleDir = Path.Combine(_engineRoot, "Source", "Runtime", name);
        Directory.CreateDirectory(moduleDir);

        File.WriteAllText(
            Path.Combine(moduleDir, name + ".Build.toml"),
            "# Copyright Simgenics. All Rights Reserved.\n" +
            $"name = \"{name}\"\n" +
            "tier = \"Engine\"\n" +
            "module_type = \"Runtime\"\n" +
            $"languages = {languages}\n" +
            dependencyToml);

        string privateDir = Path.Combine(moduleDir, "Private");
        Directory.CreateDirectory(privateDir);
        File.WriteAllText(
            Path.Combine(privateDir, csType + ".cs"),
            "// Copyright Simgenics. All Rights Reserved.\n" +
            $"namespace {csNamespace} {{ public class {csType} {{ }} }}\n");
    }

    private void WriteCppModule(string name)
    {
        string moduleDir = Path.Combine(_engineRoot, "Source", "Runtime", name);
        Directory.CreateDirectory(moduleDir);

        File.WriteAllText(
            Path.Combine(moduleDir, name + ".Build.toml"),
            "# Copyright Simgenics. All Rights Reserved.\n" +
            $"name = \"{name}\"\n" +
            "tier = \"Engine\"\n" +
            "module_type = \"Runtime\"\n" +
            "languages = [\"Cpp\"]\n" +
            "public_include_paths = [\"Public\"]\n");

        string publicDir = Path.Combine(moduleDir, "Public");
        string privateDir = Path.Combine(moduleDir, "Private");
        Directory.CreateDirectory(publicDir);
        Directory.CreateDirectory(privateDir);
        File.WriteAllText(
            Path.Combine(publicDir, name + ".h"),
            "// Copyright Simgenics. All Rights Reserved.\n#pragma once\nint XCppFn();\n");
        File.WriteAllText(
            Path.Combine(privateDir, name + ".cpp"),
            "// Copyright Simgenics. All Rights Reserved.\n" +
            $"#include \"{name}.h\"\nint XCppFn() {{ return 0; }}\n");
    }
}
