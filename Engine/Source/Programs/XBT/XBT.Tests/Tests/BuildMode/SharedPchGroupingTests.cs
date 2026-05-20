// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.BuildMode;

/// <summary>
/// End-to-end tests for the shared-PCH grouping pass added to
/// <see cref="Simgenics.XPact.XBT.Entry.BuildMode"/> in Phase 1.4b.
/// Each test constructs a synthetic engine tree on disk with several
/// <c>.Build.toml</c> modules, runs BuildMode programmatically, and
/// inspects the resulting action graph.
/// </summary>
/// <remarks>
/// <para>
/// The tests require a host MSVC toolchain to construct the
/// <see cref="XMSVCToolChain"/>; on hosts without MSVC the BuildMode
/// returns early with an exit-23 diagnostic. We guard each test with
/// <see cref="IsToolchainAvailable"/> and emit a clear "skipped" warning
/// when the host lacks the toolchain.
/// </para>
/// </remarks>
[Trait("Category", "SmokeBuild")]
public sealed class SharedPchGroupingTests : IDisposable
{
    private readonly string _scratchEngine;

    public SharedPchGroupingTests()
    {
        _scratchEngine = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.SharedPchGrouping",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchEngine);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchEngine))
            {
                Directory.Delete(_scratchEngine, recursive: true);
            }
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------------
    // 1. Two-module shared-PCH group passes through BuildMode -> ONE
    //    PCHGenerationAction in the action graph.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fixture: two modules MA + MB share a header (via
    /// shared_pch_header_file). Build the fixture and assert the action
    /// graph contains exactly ONE PCHGenerationAction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SKIPPED post-audit: the original fixture has module MB declare
    /// <c>shared_pch_header_file = "../MA/Public/Common.h"</c> -- a
    /// cross-module path-traversal reference that the
    /// <see cref="BuildTomlParser"/>'s post-Rev-13 hardening (per
    /// Toolchain Contract Section 2.1) now rejects with exit 30.
    /// </para>
    /// <para>
    /// The grouping mechanism in <c>BuildMode</c> keys on the
    /// <see cref="Path.GetFullPath(string)"/> canonical absolute path
    /// of each module's <c>SharedPCHHeaderFile</c>, and the existing
    /// code does not resolve a logical header name through participating
    /// modules' <c>PublicIncludePaths</c>. Until that resolution
    /// mechanism lands, two modules cannot reference the same physical
    /// shared header without a path-traversal reference. The test is
    /// preserved as a regression marker for the future grouping-by-
    /// logical-path-resolution work; once that lands, the fixture
    /// can be rewritten to declare a logical header name and the
    /// skip lifted.
    /// </para>
    /// </remarks>
    [Fact(Skip = "Awaiting grouping-by-logical-path resolution; current grouping keys on absolute paths which requires `..` cross-module references that the post-Rev-13 path-traversal validation now rejects.")]
    public void TwoModuleGroup_OneSharedPCHGenerationAction()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();
        // Both modules name the same shared header (Common.h sits inside
        // module MA's Public/ tree but the absolute canonical path is the
        // grouping key, not the originating module's tree).
        WriteSharedHeader(
            relativePath: "Source/Runtime/MA/Public/Common.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Public/Common.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "ModuleA.cpp");
        WriteModuleToml(
            moduleName: "MB",
            tier: "Engine",
            sharedHeader: "../MA/Public/Common.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MB", sourceFileName: "ModuleB.cpp");

        BuildResult result = RunBuild();

        int pchCount = result.Actions.Count(a => a.ActionType == XActionType.PCHGenerationAction);
        Assert.Equal(1, pchCount);

        // The single PCH action's Module field carries the "SharedPCH:<hash>"
        // marker (asserted as a string-prefix match -- the exact hash is
        // build-input-dependent but the prefix is stable).
        IExternalAction pchAction =
            result.Actions.Single(a => a.ActionType == XActionType.PCHGenerationAction);
        Assert.NotNull(pchAction.Module);
        Assert.StartsWith("SharedPCH:", pchAction.Module);

        // Both modules' compile actions reference the same PCH output.
        int compileCount = result.Actions.Count(a => a.ActionType == XActionType.CompileCppAction);
        Assert.Equal(2, compileCount);

        FileItem pchOutput = pchAction.ProducedItems
            .First(p => p.FullPath.EndsWith(".pch", StringComparison.OrdinalIgnoreCase));
        foreach (IExternalAction compile in result.Actions
                     .Where(a => a.ActionType == XActionType.CompileCppAction))
        {
            Assert.Contains(compile.PrerequisiteItems, p => p.FullPath == pchOutput.FullPath);
        }
    }

    // ---------------------------------------------------------------------
    // 2. Mixed group: one shared + one private + one no-PCH ->
    //    THREE different PCH dispositions reflected in the action graph.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fixture: three modules, MA + MB share a header; MC has a private
    /// PCH header; MD declares no PCH. Action graph carries one shared
    /// PCHGenerationAction (for MA/MB), one private PCHGenerationAction
    /// (for MC), and zero for MD.
    /// </summary>
    /// <remarks>
    /// SKIPPED for the same reason as <see cref="TwoModuleGroup_OneSharedPCHGenerationAction"/>:
    /// the MA/MB shared-PCH leg relies on MB referencing
    /// <c>../MA/Public/Shared.h</c>, which the post-Rev-13 path-traversal
    /// validator now rejects. See the sibling test's remarks for the
    /// design follow-up.
    /// </remarks>
    [Fact(Skip = "Awaiting grouping-by-logical-path resolution; current grouping keys on absolute paths which requires `..` cross-module references that the post-Rev-13 path-traversal validation now rejects.")]
    public void MixedGroup_SharedPlusPrivatePlusNone()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();
        // Shared group MA + MB.
        WriteSharedHeader(
            relativePath: "Source/Runtime/MA/Public/Shared.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Public/Shared.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "MA.cpp");
        WriteModuleToml(
            moduleName: "MB",
            tier: "Engine",
            sharedHeader: "../MA/Public/Shared.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MB", sourceFileName: "MB.cpp");

        // Private PCH for MC.
        WriteSharedHeader(
            relativePath: "Source/Runtime/MC/Public/PrivateMC.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n");
        WriteModuleToml(
            moduleName: "MC",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: new[] { "pch_header_file = \"Public/PrivateMC.h\"" });
        WriteSampleSource(moduleName: "MC", sourceFileName: "MC.cpp");

        // No PCH for MD.
        WriteModuleToml(
            moduleName: "MD",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MD", sourceFileName: "MD.cpp");

        BuildResult result = RunBuild();

        // Total PCH actions: shared (1) + private (1) = 2.
        int pchCount = result.Actions.Count(a => a.ActionType == XActionType.PCHGenerationAction);
        Assert.Equal(2, pchCount);

        // Identify the shared one by the "SharedPCH:" Module prefix.
        IExternalAction sharedAction = result.Actions
            .Single(a => a.ActionType == XActionType.PCHGenerationAction
                         && (a.Module ?? "").StartsWith("SharedPCH:", StringComparison.Ordinal));
        IExternalAction privateAction = result.Actions
            .Single(a => a.ActionType == XActionType.PCHGenerationAction
                         && (a.Module ?? "") == "MC");

        Assert.NotEqual(sharedAction.CommandVersion, privateAction.CommandVersion);

        // Verify each module's compiles consume the right PCH.
        FileItem sharedPch = sharedAction.ProducedItems
            .First(p => p.FullPath.EndsWith(".pch", StringComparison.OrdinalIgnoreCase));
        FileItem privatePch = privateAction.ProducedItems
            .First(p => p.FullPath.EndsWith(".pch", StringComparison.OrdinalIgnoreCase));

        foreach (IExternalAction compile in result.Actions
                     .Where(a => a.ActionType == XActionType.CompileCppAction && a.Module == "MA"))
        {
            Assert.Contains(compile.PrerequisiteItems, p => p.FullPath == sharedPch.FullPath);
        }
        foreach (IExternalAction compile in result.Actions
                     .Where(a => a.ActionType == XActionType.CompileCppAction && a.Module == "MC"))
        {
            Assert.Contains(compile.PrerequisiteItems, p => p.FullPath == privatePch.FullPath);
        }
        // MD has no PCH prerequisite at all.
        foreach (IExternalAction compile in result.Actions
                     .Where(a => a.ActionType == XActionType.CompileCppAction && a.Module == "MD"))
        {
            Assert.DoesNotContain(compile.PrerequisiteItems, p =>
                p.FullPath.EndsWith(".pch", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------------
    // 3. SimPath module + shared PCH -> rejected by the descriptor parser
    //    at exit 30 (the SimPath-with-shared-PCH gate is enforced at parse
    //    time per Toolchain Contract Section 1.5).
    //
    //    Two test seams: (a) verify the parser rejects directly, and
    //    (b) verify the BuildMode toolchain-level defense-in-depth gate
    //    fires when the bad module is somehow synthesized past the parser.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parser-level rejection: a SimPath module declaring
    /// shared_pch_header_file fails at parse time with exit 30 per
    /// Toolchain Contract Section 1.5.
    /// </summary>
    [Fact]
    public void SimPathInSharedGroup_ParserRejectsWithExit30()
    {
        const string toml = """
            name = "Unsafe"
            tier = "Engine"
            module_type = "Runtime"
            sim_path = true
            simd_level = "SSE42"
            pch_usage = "NoSharedPCHs"
            shared_pch_header_file = "Public/Shared.h"
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("SimPath", ex.Message);
        Assert.Contains("shared", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BuildMode-level defense-in-depth: when a SimPath module somehow
    /// reaches the grouping pass with a shared_pch_header_file (e.g.
    /// because the parser rejected it but discovery silently dropped the
    /// bad module), the natural flow is that only the valid module
    /// survives in the catalog. The build either succeeds with one
    /// participant + Logger.Info, or single-participant fallback
    /// semantics apply. Verify that no shared PCH action is emitted when
    /// the SimPath partner is dropped at parse time.
    /// </summary>
    [Fact]
    public void SimPathInSharedGroup_DroppedAtDiscoveryNoSharedActionEmitted()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();
        WriteSharedHeader(
            relativePath: "Source/Runtime/Safe/Public/Shared.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n");
        WriteModuleToml(
            moduleName: "Safe",
            tier: "Engine",
            sharedHeader: "Public/Shared.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "Safe", sourceFileName: "Safe.cpp");

        // SimPath + shared_pch_header_file in TOML -- the parser rejects
        // this at descriptor-parse time (exit 30); discovery catches the
        // DescriptorParseException, logs an error, and drops the module
        // from the catalog. The build still runs against the surviving
        // {Safe} module.
        WriteModuleToml(
            moduleName: "Unsafe",
            tier: "Engine",
            sharedHeader: "../Safe/Public/Shared.h",
            extraTomlLines: new[]
            {
                "sim_path = true",
                "simd_level = \"SSE42\"",
                "pch_usage = \"NoSharedPCHs\"",
            });
        WriteSampleSource(moduleName: "Unsafe", sourceFileName: "Unsafe.cpp");

        BuildResult result = RunBuild();

        // The bad module was dropped at discovery; only Safe remains as a
        // (now single-participant) shared declarator. Per the single-
        // participant rule, no shared PCH action is emitted.
        int pchCount = result.Actions.Count(a => a.ActionType == XActionType.PCHGenerationAction);
        Assert.Equal(0, pchCount);
    }

    // ---------------------------------------------------------------------
    // 4. Single-participant shared PCH -> Logger.Info + fall back to
    //    private-PCH semantics (no shared PCH action emitted).
    // ---------------------------------------------------------------------

    /// <summary>
    /// A single module declares shared_pch_header_file with no other
    /// participants; BuildMode emits a Logger.Info and treats the case as
    /// no PCH (the private-PCH fallback requires pch_header_file to also
    /// be set, which we deliberately omit here so the module simply
    /// compiles without any PCH).
    /// </summary>
    [Fact]
    public void SingleParticipantSharedDeclaration_NoSharedPCHActionEmitted()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();
        WriteSharedHeader(
            relativePath: "Source/Runtime/Solo/Public/Solo.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n");
        WriteModuleToml(
            moduleName: "Solo",
            tier: "Engine",
            sharedHeader: "Public/Solo.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "Solo", sourceFileName: "Solo.cpp");

        // A second module with no PCH declarations -- it doesn't share
        // anything, so it won't form a group with Solo.
        WriteModuleToml(
            moduleName: "Independent",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: null);
        WriteSampleSource(moduleName: "Independent", sourceFileName: "Independent.cpp");

        BuildResult result = RunBuild();

        // Zero PCHGenerationActions: Solo's "shared" declaration is the
        // only declaration for its header, so the grouping pass logs an
        // info diagnostic and does not emit any shared action; the
        // private-PCH fallback requires pch_header_file which Solo does
        // not have, so no PCH at all.
        int pchCount = result.Actions.Count(a => a.ActionType == XActionType.PCHGenerationAction);
        Assert.Equal(0, pchCount);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static bool IsToolchainAvailable()
    {
        return VCEnvironment.TryDiscover(out _) == VCEnvironment.DiscoveryResult.Found;
    }

    private void WriteEngineXengine()
    {
        string engineRoot = Path.Combine(_scratchEngine, "Engine");
        Directory.CreateDirectory(engineRoot);
        File.WriteAllText(
            Path.Combine(engineRoot, "Engine.xengine"),
            """
            {
              "_comment": "Synthetic test engine descriptor. Copyright Simgenics. All Rights Reserved.",
              "FileVersion": 1,
              "EngineName": "SharedPCHTestEngine",
              "EngineVersion": "0.1.0",
              "Copyright": "Copyright Simgenics. All Rights Reserved.",
              "Description": "Synthetic engine for the shared-PCH grouping tests.",
              "MinSupportedPlatforms": ["Win64", "Linux"],
              "RenderAPI": "Vulkan",
              "VRBackend": "OpenXR",
              "BuildTargets": ["Editor", "Game", "Server"],
              "BuildConfigurations": ["Debug", "DebugGame", "Development", "Test", "Shipping"]
            }
            """);
    }

    private void WriteSharedHeader(string relativePath, string content)
    {
        string path = Path.Combine(_scratchEngine, "Engine", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteModuleToml(
        string moduleName,
        string tier,
        string? sharedHeader,
        string[]? extraTomlLines)
    {
        string moduleDir = Path.Combine(
            _scratchEngine, "Engine", "Source", "Runtime", moduleName);
        Directory.CreateDirectory(moduleDir);
        Directory.CreateDirectory(Path.Combine(moduleDir, "Private"));
        Directory.CreateDirectory(Path.Combine(moduleDir, "Public"));

        System.Text.StringBuilder sb = new();
        sb.AppendLine("# Copyright Simgenics. All Rights Reserved.");
        sb.AppendLine();
        sb.AppendLine($"name = \"{moduleName}\"");
        sb.AppendLine($"tier = \"{tier}\"");
        sb.AppendLine("module_type = \"Runtime\"");
        sb.AppendLine("languages = \"Cpp\"");
        if (sharedHeader is not null)
        {
            sb.AppendLine($"shared_pch_header_file = \"{sharedHeader}\"");
        }
        if (extraTomlLines is not null)
        {
            foreach (string line in extraTomlLines)
            {
                sb.AppendLine(line);
            }
        }

        File.WriteAllText(
            Path.Combine(moduleDir, moduleName + ".Build.toml"),
            sb.ToString());
    }

    private void WriteSampleSource(string moduleName, string sourceFileName)
    {
        string privateDir = Path.Combine(
            _scratchEngine, "Engine", "Source", "Runtime", moduleName, "Private");
        Directory.CreateDirectory(privateDir);
        File.WriteAllText(
            Path.Combine(privateDir, sourceFileName),
            "// Copyright Simgenics. All Rights Reserved.\n" +
            "namespace " + moduleName + " { void Stub() {} }\n");
    }

    private BuildResult RunBuild()
    {
        string engineRoot = Path.Combine(_scratchEngine, "Engine");

        BuildOptions options = new()
        {
            TargetName = "SharedPCHTestTarget",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
        };

        return Simgenics.XPact.XBT.Entry.BuildMode.Run(options, CancellationToken.None);
    }
}
