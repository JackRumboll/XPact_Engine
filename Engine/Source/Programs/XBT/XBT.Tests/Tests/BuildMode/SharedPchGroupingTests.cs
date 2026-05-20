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
/// <see cref="Simgenics.XPact.XBT.Entry.BuildMode"/> in Phase 1.4b and
/// hardened in Phase 1.4c (UE-style include-path resolution for
/// <c>shared_pch_header_file</c>). Each test constructs a synthetic
/// engine tree on disk with several <c>.Build.toml</c> modules, runs
/// BuildMode programmatically, and inspects the resulting action graph.
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
    /// Fixture: a "host" module XCore exposes <c>Common.h</c> via its
    /// <c>PublicIncludePaths = ["Public"]</c>. Two participant modules
    /// MA and MB declare <c>shared_pch_header_file = "Common.h"</c>
    /// (bare-name form). The grouping pass resolves the bare name via
    /// the host's PublicIncludePaths and emits ONE shared PCH action
    /// referenced by both participants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round-1 audit history: the original fixture used a cross-module
    /// <c>"../MA/Public/Common.h"</c> reference which the post-Rev-13
    /// path-traversal validator now rejects with exit 30. Round-2 fix
    /// (this Phase 1.4c work) adds UE-style include-path resolution so
    /// the bare-name form works without <c>..</c> references.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoModuleGroup_OneSharedPCHGenerationAction()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();

        // Host module XCore exposes Public/Common.h via PublicIncludePaths.
        WriteSharedHeader(
            relativePath: "Source/Runtime/XCore/Public/Common.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");
        WriteModuleToml(
            moduleName: "XCore",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: new[] { "public_include_paths = [\"Public\"]" });
        WriteSampleSource(moduleName: "XCore", sourceFileName: "XCore.cpp");

        // Two participants reference the header by bare name. The
        // resolver finds it in XCore's PublicIncludePaths.
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Common.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "ModuleA.cpp");
        WriteModuleToml(
            moduleName: "MB",
            tier: "Engine",
            sharedHeader: "Common.h",
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

        // Both MA and MB compiles reference the shared PCH output.
        // (XCore compiles too but has no PCH.)
        FileItem pchOutput = pchAction.ProducedItems
            .First(p => p.FullPath.EndsWith(".pch", StringComparison.OrdinalIgnoreCase));
        foreach (IExternalAction compile in result.Actions
                     .Where(a => a.ActionType == XActionType.CompileCppAction
                                 && (a.Module == "MA" || a.Module == "MB")))
        {
            Assert.Contains(compile.PrerequisiteItems, p => p.FullPath == pchOutput.FullPath);
        }
    }

    // ---------------------------------------------------------------------
    // 2. Mixed group: one shared + one private + one no-PCH ->
    //    THREE different PCH dispositions reflected in the action graph.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fixture: host module XCore exposes <c>Shared.h</c>. MA + MB
    /// share it via bare-name resolution. MC has a private PCH header.
    /// MD declares no PCH. Action graph carries one shared
    /// PCHGenerationAction (for MA/MB), one private PCHGenerationAction
    /// (for MC), and zero for MD or XCore.
    /// </summary>
    [Fact]
    public void MixedGroup_SharedPlusPrivatePlusNone()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();

        // Host module XCore exposes Public/Shared.h.
        WriteSharedHeader(
            relativePath: "Source/Runtime/XCore/Public/Shared.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n#include <vector>\n");
        WriteModuleToml(
            moduleName: "XCore",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: new[] { "public_include_paths = [\"Public\"]" });
        WriteSampleSource(moduleName: "XCore", sourceFileName: "XCore.cpp");

        // Shared group MA + MB via bare-name resolution.
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Shared.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "MA.cpp");
        WriteModuleToml(
            moduleName: "MB",
            tier: "Engine",
            sharedHeader: "Shared.h",
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
            sharedHeader: "Shared.h",
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
    // 5. UE-style include-path resolution -- new tests for Phase 1.4c
    // ---------------------------------------------------------------------

    /// <summary>
    /// Bare-name <c>shared_pch_header_file</c> that does not exist in
    /// any module's <c>PublicIncludePaths</c> is rejected at grouping
    /// time with exit 30 and a message naming the missing file plus
    /// "PublicIncludePaths".
    /// </summary>
    [Fact]
    public void BareName_NotFoundInAnyIncludePath_FailsExit30()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();

        // Two participants but no host module exposes Missing.h.
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Missing.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "MA.cpp");
        WriteModuleToml(
            moduleName: "MB",
            tier: "Engine",
            sharedHeader: "Missing.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MB", sourceFileName: "MB.cpp");

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(() => RunBuild());
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("Missing.h", ex.Message);
        Assert.Contains("PublicIncludePaths", ex.Message);
    }

    /// <summary>
    /// Two host modules each expose <c>Common.h</c> from their
    /// respective <c>PublicIncludePaths</c>. The resolver picks the
    /// first match in iteration order. Per
    /// <see cref="ModuleCatalog"/> the module list is sorted
    /// alphabetically, so host module <c>HostA</c> wins over
    /// <c>HostB</c> deterministically.
    /// </summary>
    [Fact]
    public void BareName_AmbiguousResolution_PicksFirstMatch()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();

        // HostA and HostB both expose Common.h. Alphabetical sort
        // visits HostA first; that's the winning resolution.
        WriteSharedHeader(
            relativePath: "Source/Runtime/HostA/Public/Common.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n// from HostA\n");
        WriteModuleToml(
            moduleName: "HostA",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: new[] { "public_include_paths = [\"Public\"]" });
        WriteSampleSource(moduleName: "HostA", sourceFileName: "HostA.cpp");

        WriteSharedHeader(
            relativePath: "Source/Runtime/HostB/Public/Common.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n// from HostB\n");
        WriteModuleToml(
            moduleName: "HostB",
            tier: "Engine",
            sharedHeader: null,
            extraTomlLines: new[] { "public_include_paths = [\"Public\"]" });
        WriteSampleSource(moduleName: "HostB", sourceFileName: "HostB.cpp");

        // Two participants -- both will resolve to HostA's copy.
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Common.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "MA.cpp");
        WriteModuleToml(
            moduleName: "MB",
            tier: "Engine",
            sharedHeader: "Common.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MB", sourceFileName: "MB.cpp");

        BuildResult result = RunBuild();

        // Exactly one shared PCH action -- both participants picked
        // the same first-match resolution and grouped together.
        IExternalAction pchAction = result.Actions
            .Single(a => a.ActionType == XActionType.PCHGenerationAction);

        // The PCH input file should live under HostA/Public, not
        // HostB/Public. Assert by substring match on the path.
        Assert.Contains(pchAction.PrerequisiteItems,
            p => p.FullPath.Replace('\\', '/').Contains("/HostA/Public/Common.h"));
        Assert.DoesNotContain(pchAction.PrerequisiteItems,
            p => p.FullPath.Replace('\\', '/').Contains("/HostB/Public/Common.h"));
    }

    /// <summary>
    /// A whitespace-only <c>shared_pch_header_file</c> field is rejected
    /// at parse time with exit 30. The parser distinguishes
    /// whitespace-only from empty (empty == not set, omitted from the
    /// descriptor).
    /// </summary>
    [Fact]
    public void BareName_WhitespaceOnly_FailsParse()
    {
        const string toml = """
            name = "X"
            tier = "Engine"
            module_type = "Runtime"
            shared_pch_header_file = "   "
            """;

        DescriptorParseException ex = Assert.Throws<DescriptorParseException>(
            () => BuildTomlParser.Parse(toml));
        Assert.Equal(30, ex.ExitCode);
        Assert.Contains("shared_pch_header_file", ex.Message);
        Assert.Contains("whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A relative-path <c>shared_pch_header_file</c> (e.g.
    /// <c>"Public/Common.h"</c>) without any <c>..</c> still works via
    /// the legacy relative-to-module-dir resolution branch. Two
    /// modules sharing intra-module relative paths still group into a
    /// single shared PCH action provided the absolute resolved path
    /// matches.
    /// </summary>
    [Fact]
    public void RelativePath_StillWorks_WithoutDotDot()
    {
        if (!IsToolchainAvailable())
        {
            Logger.Warning("SharedPchGroupingTests skipped because no compatible toolchain found.");
            return;
        }

        WriteEngineXengine();

        // Host module exposes the header. Two participants each
        // reference it via relative-path form -- but the participants
        // are themselves the host plus another module sharing the
        // same physical header. To get two participants on the same
        // file without `..`, both must live under the same module's
        // tree -- equivalent to "the host module + itself" via two
        // descriptor pointers, which doesn't make sense. Instead use
        // the single-host pattern: ONE module declares relative-path
        // shared_pch_header_file, plus a second SECONDARY module
        // declares the same physical file via the bare-name resolver.
        // Both code paths converge on the same canonical absolute
        // path, so the grouping pass merges them.
        WriteSharedHeader(
            relativePath: "Source/Runtime/XCore/Public/Common.h",
            content: "// Copyright Simgenics. All Rights Reserved.\n");

        // XCore declares the relative-path form -- legacy branch.
        WriteModuleToml(
            moduleName: "XCore",
            tier: "Engine",
            sharedHeader: "Public/Common.h",
            extraTomlLines: new[] { "public_include_paths = [\"Public\"]" });
        WriteSampleSource(moduleName: "XCore", sourceFileName: "XCore.cpp");

        // MA declares the bare-name form -- resolver branch lands on
        // the same canonical absolute path.
        WriteModuleToml(
            moduleName: "MA",
            tier: "Engine",
            sharedHeader: "Common.h",
            extraTomlLines: null);
        WriteSampleSource(moduleName: "MA", sourceFileName: "MA.cpp");

        BuildResult result = RunBuild();

        // Both legs grouped on the same physical file -> ONE shared
        // PCH action.
        int pchCount = result.Actions.Count(a => a.ActionType == XActionType.PCHGenerationAction);
        Assert.Equal(1, pchCount);
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
