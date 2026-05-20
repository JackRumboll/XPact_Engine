// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.ActionGraph.Actions;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;
using Simgenics.XPact.XBT.Toolchain;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// XBT's <c>build</c> mode: end-to-end build orchestration per
/// <c>/Documents/XBT.html</c> Rev 4 Section 1.1 + Toolchain Contract
/// Rev 13 Section 10.1.
/// </summary>
/// <remarks>
/// <para>
/// The mode wires together every Phase 1.2 + 1.3 subsystem into a single
/// pipeline:
/// </para>
/// <list type="number">
///   <item>Parse CLI args; pick a default
///   <see cref="BuildConfiguration"/> / <see cref="Platform"/>.</item>
///   <item>Discover engine + studio + project roots.</item>
///   <item>Load <c>Engine.xengine</c>; derive engine semver.</item>
///   <item>Enumerate plugins.</item>
///   <item>Enumerate modules (TOML + Roslyn-fallback per Phase 1.2.1).</item>
///   <item>Validate engine-version compatibility.</item>
///   <item>Validate tier rules.</item>
///   <item>Construct the toolchain (MSVC on Win64, Clang on Linux/Android).</item>
///   <item>Emit per-module actions: <c>ValidateCopyrightAction</c>,
///   <c>PCHGenerationAction</c>, <c>CompileCppAction</c>,
///   <c>LinkModuleAction</c>.</item>
///   <item>Open <c>ActionHistory</c> for the target/config combo.</item>
///   <item>Execute outdated actions via <c>ParallelExecutor</c>.</item>
///   <item>Save <c>ActionHistory</c> + report status.</item>
/// </list>
/// <para>
/// Exit codes follow Toolchain Contract Rev 13 Section 13. Each subsystem's
/// typed exception carries its own <see cref="XBTException.ExitCode"/>
/// which surfaces verbatim through the build mode.
/// </para>
/// </remarks>
[XBTMode("build")]
public sealed class BuildMode : IToolMode<BuildMode>
{
    public static string Name => "build";

    public static string Description =>
        "Discover, validate, manifest, and compile the named target / config / platform.";

    /// <summary>
    /// Run the build asynchronously. The CLI flags accepted:
    /// <list type="bullet">
    ///   <item><c>-Target=&lt;name&gt;</c> (required).</item>
    ///   <item><c>-Configuration=Debug|DebugGame|Development|Test|Shipping</c></item>
    ///   <item><c>-Platform=Win64|Linux|Android</c></item>
    ///   <item><c>-StationRole=None|Engineer|Instructor|Trainee</c></item>
    ///   <item><c>-FipsMode</c></item>
    ///   <item><c>-Engine=&lt;path&gt;</c> (override the engine root discovery)</item>
    ///   <item><c>-Project=&lt;path&gt;</c> (override the project root discovery)</item>
    ///   <item><c>-Studio=&lt;path&gt;</c> (override the studio root discovery)</item>
    /// </list>
    /// </summary>
    public Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequestedWithDiagnostic("BuildMode.ExecuteAsync");

        try
        {
            BuildOptions options = BuildOptions.Parse(args);
            return Task.FromResult(RunBuild(options, cancellationToken));
        }
        catch (BuildOptionsParseException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return Task.FromResult(ex.ExitCode);
        }
        catch (OperationCanceledException)
        {
            Logger.Error("Build cancelled.", exitCode: 130);
            return Task.FromResult(130);
        }
    }

    /// <summary>
    /// Programmatic entry point for tests + the smoke harness. Tests
    /// invoke this directly instead of spawning <c>xbt.exe</c> so they
    /// can inspect the resulting action graph and execution report.
    /// </summary>
    internal static BuildResult Run(BuildOptions options, CancellationToken cancellationToken)
    {
        return RunInternal(options, cancellationToken);
    }

    private static int RunBuild(BuildOptions options, CancellationToken cancellationToken)
    {
        try
        {
            BuildResult result = RunInternal(options, cancellationToken);
            if (result.Success)
            {
                Logger.Info(
                    $"xbt build completed: {result.ActionsRan} action(s) ran, " +
                    $"{result.ActionsCached} cached.",
                    new DiagnosticContext { Action = "build" });
                return 0;
            }

            Logger.Error(
                $"xbt build failed: {result.ActionsFailed} action(s) failed " +
                $"(ran={result.ActionsRan}, cached={result.ActionsCached}).",
                exitCode: result.FirstFailingExitCode,
                new DiagnosticContext { Action = "build" });
            return result.FirstFailingExitCode;
        }
        catch (XBTException ex)
        {
            Logger.Error(ex.Message, exitCode: ex.ExitCode);
            return ex.ExitCode;
        }
    }

    private static BuildResult RunInternal(BuildOptions options, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // ---- 1. Discover engine / studio / project roots ----------------
        string engineRoot = options.EngineRoot ?? DiscoverEngineRoot();
        string? studioRoot = options.StudioRoot;
        string? projectRoot = options.ProjectRoot;
        IReadOnlyList<string> projectRoots = projectRoot is null
            ? Array.Empty<string>()
            : new[] { projectRoot };

        Logger.Info(
            $"xbt build: target='{options.TargetName}' " +
            $"config={options.Configuration} platform={options.Platform} " +
            $"engine='{engineRoot}'.",
            new DiagnosticContext { Action = "build" });

        // ---- 2. Engine semver discovery ---------------------------------
        SemanticVersion engineVersion = EngineVersionValidator.DiscoverEngineVersion(engineRoot);
        Logger.Info(
            $"Engine version {engineVersion} discovered from {engineRoot}/Engine.xengine.",
            new DiagnosticContext { Action = "build" });

        cancellationToken.ThrowIfCancellationRequested();

        // ---- 3. Plugin discovery ---------------------------------------
        PluginCatalog plugins = PluginEnumerator.Enumerate(
            engineRoot,
            studioRoot,
            projectRoots,
            DiscoveryDiagnostics.Default);
        Logger.Info(
            $"Plugin discovery: {plugins.Entries.Count} plugin(s) found.",
            new DiagnosticContext { Action = "build" });

        // ---- 4. Construct TargetRules ----------------------------------
        TargetRules target = new()
        {
            Name = options.TargetName,
            TargetType = options.TargetType,
            Configuration = options.Configuration,
            Platform = options.Platform,
            StationRole = options.StationRole,
            FipsMode = options.FipsMode,
        };

        // ---- 5. Engine-version checks (project + plugins) --------------
        ProjectDescriptor? projectDescriptor = LoadProjectDescriptor(projectRoot, options.TargetName);
        if (projectDescriptor is not null)
        {
            EngineVersionValidator.ValidateProject(projectDescriptor, engineVersion);
        }
        EngineVersionValidator.ValidatePlugins(
            ResolveEnabledPluginDescriptors(plugins, projectDescriptor),
            engineVersion);

        // ---- 6. Module enumeration -------------------------------------
        List<string> sourceRoots = CollectSourceRoots(engineRoot, studioRoot, projectRoot, plugins);
        ModuleCatalog modules = ModuleEnumerator.Enumerate(
            sourceRoots,
            DiscoveryDiagnostics.Default,
            target);
        Logger.Info(
            $"Module discovery: {modules.Count} module(s) found across " +
            $"{sourceRoots.Count} source root(s).",
            new DiagnosticContext { Action = "build" });

        cancellationToken.ThrowIfCancellationRequested();

        // ---- 7. Tier validation ----------------------------------------
        ValidateTierRules(modules);

        // ---- 8. Construct the toolchain --------------------------------
        XToolChain toolchain = ConstructToolchain(target, engineRoot);

        // ---- 9. Emit per-module actions --------------------------------
        IReadOnlyList<ModuleRecord> targetModules = SelectTargetModules(modules, target);
        if (targetModules.Count == 0)
        {
            Logger.Warning(
                "No modules selected for the target. Nothing to build.",
                new DiagnosticContext { Action = "build" });
            sw.Stop();
            return new BuildResult(
                Success: true,
                ActionsRan: 0,
                ActionsCached: 0,
                ActionsFailed: 0,
                FirstFailingExitCode: 0,
                Duration: sw.Elapsed,
                Actions: Array.Empty<IExternalAction>());
        }

        Dictionary<string, ModuleFileSet> fileSetByModule = new(StringComparer.Ordinal);
        List<IExternalAction> actions = EmitActions(
            engineRoot,
            target,
            toolchain,
            targetModules,
            fileSetByModule,
            out IReadOnlyList<IExternalAction> emittedForReport);

        // ---- 9.5 Emit the manifest (Step 0.5 addendum + Contract Section 8) ----
        // The manifest is a snapshot of discovery + configuration produced
        // ONCE per BuildMode invocation. XHT (System 2) and XIL2CPP
        // (System 6) consume this file; it is the wire surface between
        // XBT and downstream tooling.
        //
        // Phase 1 emits the manifest directly from BuildMode rather than
        // via a WriteManifestAction node in the action graph. The
        // XActionType.WriteManifestAction slot at ordinal 1 remains
        // reserved; future phases may construct a WriteManifestAction
        // IExternalAction to gain cache integration and let XHT/XIL2CPP
        // reference the manifest's ProducedItems as PrerequisiteItems.
        // For Phase 1 the simpler direct-emission path is sufficient
        // because the manifest's content hash is not yet a CacheKeyComponent
        // for downstream actions.
        string intermediateBuildDir = Path.Combine(
            engineRoot, "Intermediate", "Build", target.Name,
            target.Configuration.ToString());
        Directory.CreateDirectory(intermediateBuildDir);
        EmitManifest(
            engineRoot,
            target,
            engineVersion,
            targetModules,
            fileSetByModule,
            intermediateBuildDir);

        // ---- 10. Build the action graph --------------------------------
        Simgenics.XPact.XBT.ActionGraph.ActionGraph graph = new(actions);
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        // ---- 11. Open ActionHistory ------------------------------------
        string intermediateRoot = Path.Combine(engineRoot, "Intermediate", "Build", target.Name);
        Directory.CreateDirectory(intermediateRoot);
        ActionHistory history = ActionHistory.Open(intermediateRoot, target.Configuration);

        // ---- 11.5 Orphan temp-file sweep (XBT.html Section 6.4) --------
        // Sweep the entire intermediate-build tree (not just this
        // target's subtree) so a sibling crashed XBT run targeting a
        // different config gets cleaned up too. Best-effort: failures
        // are logged but do not abort the build.
        try
        {
            string intermediateBuildTree = Path.Combine(engineRoot, "Intermediate", "Build");
            ParallelExecutor.SweepOrphanedTempFiles(intermediateBuildTree);
        }
        catch (Exception ex)
        {
            Logger.Warning(
                $"Orphan temp-file sweep failed: {ex.GetType().Name}: {ex.Message}",
                new DiagnosticContext { Action = "orphan-sweep" });
        }

        // ---- 12. Execute -----------------------------------------------
        ExecutionReport report = ExecuteGraph(graph, history, cancellationToken);

        // ---- 13. Save ActionHistory ------------------------------------
        try
        {
            history.Save();
        }
        catch (IOException ex)
        {
            Logger.Warning(
                $"Failed to save ActionHistory at {intermediateRoot}: {ex.Message}",
                new DiagnosticContext { Action = "save-history" });
        }

        // ---- 14. Aggregate result --------------------------------------
        int ran = 0;
        int cached = 0;
        int failed = 0;
        int firstFailingExit = 0;
        foreach (var kvp in report.Results)
        {
            ActionResult r = kvp.Value;
            if (r.Success)
            {
                if (r.Skipped) cached++;
                else ran++;
            }
            else
            {
                failed++;
                if (firstFailingExit == 0)
                {
                    firstFailingExit = r.ExitCode != 0 ? r.ExitCode : 1;
                }
            }
        }

        sw.Stop();
        return new BuildResult(
            Success: failed == 0 && !report.Cancelled,
            ActionsRan: ran,
            ActionsCached: cached,
            ActionsFailed: failed,
            FirstFailingExitCode: failed == 0 ? 0 : (firstFailingExit == 0 ? 1 : firstFailingExit),
            Duration: sw.Elapsed,
            Actions: emittedForReport);
    }

    // ---------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------

    private static string DiscoverEngineRoot()
    {
        // Walk up from CWD looking for an Engine.xengine sibling. Mirrors
        // the RepoRoot walk; ergonomics for "run xbt from anywhere inside
        // the repo".
        string? cursor = Environment.CurrentDirectory;
        while (!string.IsNullOrEmpty(cursor))
        {
            string candidate = Path.Combine(cursor, "Engine", "Engine.xengine");
            if (File.Exists(candidate))
            {
                return Path.Combine(cursor, "Engine");
            }
            // Or maybe we're already inside Engine/.
            candidate = Path.Combine(cursor, "Engine.xengine");
            if (File.Exists(candidate))
            {
                return cursor;
            }
            DirectoryInfo? parent = Directory.GetParent(cursor);
            if (parent is null)
            {
                break;
            }
            cursor = parent.FullName;
        }
        throw new XBTException(
            "Could not discover the engine root. Pass -Engine=<path> or run xbt from inside the repo.",
            exitCode: 10);
    }

    private static ProjectDescriptor? LoadProjectDescriptor(string? projectRoot, string targetName)
    {
        if (projectRoot is null)
        {
            return null;
        }

        // Look for <projectRoot>/<targetName>.xproject first; fall back
        // to the first *.xproject we find in the directory.
        string explicitPath = Path.Combine(projectRoot, targetName + ".xproject");
        if (File.Exists(explicitPath))
        {
            return ProjectDescriptorParser.ParseFile(explicitPath);
        }
        if (Directory.Exists(projectRoot))
        {
            foreach (string path in Directory.EnumerateFiles(projectRoot, "*.xproject", SearchOption.TopDirectoryOnly))
            {
                return ProjectDescriptorParser.ParseFile(path);
            }
        }
        return null;
    }

    private static IReadOnlyList<PluginDescriptor> ResolveEnabledPluginDescriptors(
        PluginCatalog plugins,
        ProjectDescriptor? project)
    {
        // For Phase 1.3 we treat every plugin as enabled unless the
        // project's DisabledPlugins list explicitly opts it out. Engine
        // plugins always-enabled per XBT.html Section 12.2.
        HashSet<string> disabled = project is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(project.DisabledPlugins, StringComparer.Ordinal);

        List<PluginDescriptor> enabled = new();
        foreach (PluginEntry entry in plugins.Entries)
        {
            PluginDescriptor descriptor = entry.Resolved.Descriptor;
            if (disabled.Contains(descriptor.Name))
            {
                continue;
            }
            if (descriptor.EnabledByDefault)
            {
                enabled.Add(descriptor);
            }
        }
        return enabled;
    }

    private static List<string> CollectSourceRoots(
        string engineRoot,
        string? studioRoot,
        string? projectRoot,
        PluginCatalog plugins)
    {
        List<string> roots = new();
        string engineSource = Path.Combine(engineRoot, "Source");
        if (Directory.Exists(engineSource))
        {
            roots.Add(engineSource);
        }
        if (studioRoot is not null)
        {
            string studioSource = Path.Combine(studioRoot, "Source");
            if (Directory.Exists(studioSource))
            {
                roots.Add(studioSource);
            }
        }
        if (projectRoot is not null)
        {
            string projectSource = Path.Combine(projectRoot, "Source");
            if (Directory.Exists(projectSource))
            {
                roots.Add(projectSource);
            }
        }
        foreach (PluginEntry entry in plugins.Entries)
        {
            string pluginDir = Path.GetDirectoryName(entry.Resolved.DescriptorPath)!;
            string pluginSource = Path.Combine(pluginDir, "Source");
            if (Directory.Exists(pluginSource))
            {
                roots.Add(pluginSource);
            }
        }
        return roots;
    }

    private static void ValidateTierRules(ModuleCatalog modules)
    {
        List<TierViolation> allViolations = new();
        foreach (ModuleRecord rec in modules.Modules)
        {
            IReadOnlyList<TierViolation> v = TierValidator.ValidateModuleDeps(
                rec.Rules,
                rec.Rules.Tier,
                resolveTier: modules.ResolveTier);
            if (v.Count > 0)
            {
                allViolations.AddRange(v);
            }
        }
        if (allViolations.Count > 0)
        {
            string combined = string.Join(
                Environment.NewLine,
                allViolations.Select(v => v.FormatMessage()));
            throw new XBTException(
                "Tier-validation failures detected:" + Environment.NewLine + combined,
                exitCode: 21);
        }
    }

    private static XToolChain ConstructToolchain(TargetRules target, string engineRoot)
    {
        switch (target.Platform)
        {
            case Platform.Win64:
                if (VCEnvironment.TryDiscover(out VCEnvironment? env) != VCEnvironment.DiscoveryResult.Found
                    || env is null)
                {
                    throw new XBTException(
                        "No compatible MSVC toolchain found on this host. " +
                        "Install Visual Studio 2026 BuildTools (or set VS_INSTALLDIR).",
                        exitCode: 23);
                }
                return new XMSVCToolChain(env, engineRoot);

            case Platform.Linux:
            case Platform.Android:
                if (!XClangToolChain.TryDiscover(target.Platform, engineRoot, out XClangToolChain? clang)
                    || clang is null)
                {
                    throw new XBTException(
                        $"No compatible Clang toolchain found for {target.Platform}. " +
                        "Install Clang >= 18 (Linux) or the Android NDK r26+ (Android).",
                        exitCode: 23);
                }
                return clang;

            default:
                throw new XBTException(
                    $"Unsupported platform: {target.Platform}.",
                    exitCode: 10);
        }
    }

    private static IReadOnlyList<ModuleRecord> SelectTargetModules(ModuleCatalog modules, TargetRules target)
    {
        // Phase 1.3: every module in the catalog is in scope. Phase 2
        // will add target-specific module filtering (additional / disable
        // module lists, test-module gating, etc.). The
        // <see cref="TargetRules.DisableModules"/> list is honoured here
        // as a minimal opt-out path.
        HashSet<string> disabled = new(target.DisableModules, StringComparer.Ordinal);
        List<ModuleRecord> selected = new();
        foreach (ModuleRecord rec in modules.Modules)
        {
            if (disabled.Contains(rec.Rules.Name))
            {
                continue;
            }
            if (rec.Rules.bIsTestModule && target.TargetType != BuildTargetType.Editor)
            {
                // Test modules ship only into Editor / dev builds, never
                // into Game / Server.
                continue;
            }
            selected.Add(rec);
        }
        return selected;
    }

    private static List<IExternalAction> EmitActions(
        string engineRoot,
        TargetRules target,
        XToolChain toolchain,
        IReadOnlyList<ModuleRecord> targetModules,
        Dictionary<string, ModuleFileSet> fileSetByModule,
        out IReadOnlyList<IExternalAction> emittedForReport)
    {
        List<IExternalAction> actions = new();
        List<FileItem> allSourceFiles = new();
        List<IExternalAction> reportActions = new();

        // Shared-PCH grouping pre-pass (Phase 1.4b per Contract Rev 13
        // Section 1.5). Walk the selected modules, group by the resolved
        // absolute path of each module's SharedPCHHeaderFile, emit ONE
        // PCHGenerationAction per group of >= 2 participants, and record
        // the resulting PCHBinding so the per-module emit loop below can
        // pass the same shared binding to every participant's
        // CompileSource call. A group of size 1 falls back to private-PCH
        // semantics (per the spec: single-participant "shared" PCHs are
        // wasteful; emit a Logger.Info and treat as private).
        Dictionary<string, PCHBinding> sharedPchBindingByModule =
            BuildSharedPchGroups(toolchain, target, engineRoot, targetModules, actions, reportActions);

        foreach (ModuleRecord rec in targetModules)
        {
            ModuleRules module = rec.Rules;
            string moduleDir = Path.GetDirectoryName(rec.DescriptorPath)!;

            // Enumerate the module's source files.
            (IReadOnlyList<FileItem> sourceFiles, IReadOnlyList<FileItem> headerFiles, IReadOnlyList<FileItem> csharpFiles) =
                EnumerateModuleFiles(moduleDir);
            fileSetByModule[module.Name] = new ModuleFileSet(sourceFiles, headerFiles, csharpFiles);
            allSourceFiles.AddRange(sourceFiles);
            allSourceFiles.AddRange(headerFiles);
            allSourceFiles.AddRange(csharpFiles);

            string moduleObjDir = Path.Combine(
                engineRoot, "Intermediate", "Build", target.Name,
                target.Configuration.ToString(), target.Platform.ToString(), module.Name);
            Directory.CreateDirectory(moduleObjDir);

            // PCH binding resolution: shared PCH wins if the module is in
            // a shared group; otherwise fall back to the private-PCH
            // path (existing Phase 1.3 semantics).
            PCHBinding? pchBinding;
            if (sharedPchBindingByModule.TryGetValue(module.Name, out PCHBinding? sharedBinding))
            {
                pchBinding = sharedBinding;
            }
            else
            {
                pchBinding = TryGeneratePCH(toolchain, module, target, moduleDir, moduleObjDir);
                if (pchBinding is not null)
                {
                    actions.Add(pchBinding.Action);
                    reportActions.Add(pchBinding.Action);
                }
            }

            // Per-source compile actions.
            List<FileItem> objectFiles = new();
            foreach (FileItem source in sourceFiles)
            {
                cancellationToken_ThrowIfNoOpFastPath();
                IReadOnlyList<IExternalAction> compileActions =
                    toolchain.CompileSource(module, target, source, moduleObjDir, pchBinding);
                foreach (IExternalAction compile in compileActions)
                {
                    actions.Add(compile);
                    reportActions.Add(compile);
                    // The first ProducedItem of a CompileCppAction is the
                    // .obj/.o; the dep file (if any) sorts after it.
                    foreach (FileItem produced in compile.ProducedItems)
                    {
                        if (produced.FullPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)
                            || produced.FullPath.EndsWith(".o", StringComparison.OrdinalIgnoreCase))
                        {
                            objectFiles.Add(produced);
                            break;
                        }
                    }
                }
            }

            // Link action.
            if (objectFiles.Count > 0)
            {
                string moduleBinDir = Path.Combine(engineRoot, "Binaries", target.Platform.ToString());
                Directory.CreateDirectory(moduleBinDir);
                IExternalAction link = toolchain.LinkModule(module, target, objectFiles, moduleBinDir);
                actions.Add(link);
                reportActions.Add(link);
            }
        }

        // One ValidateCopyrightAction for the whole build, fed the union
        // of every authored file across modules. The marker file under
        // Intermediate/ is the cache anchor (ActionHistory uses it to
        // decide whether to re-run; see ValidateCopyrightAction's class
        // remarks).
        if (allSourceFiles.Count > 0)
        {
            string markerDir = Path.Combine(
                engineRoot, "Intermediate", "Build", target.Name,
                target.Configuration.ToString(), target.Platform.ToString());
            Directory.CreateDirectory(markerDir);
            string markerPath = Path.Combine(markerDir, "ValidateCopyright.marker");

            ValidateCopyrightAction copyright = new(
                allSourceFiles,
                engineRoot,
                target.Configuration,
                target.Platform,
                markerFilePath: markerPath);
            // Insert at the front so the action graph orders it earliest.
            actions.Insert(0, copyright);
            reportActions.Insert(0, copyright);
        }

        emittedForReport = reportActions;
        return actions;
    }

    /// <summary>
    /// Marker so future maintainers see the cancellation-check pattern
    /// even though we currently do not pass the token through this leaf
    /// per the existing API.
    /// </summary>
    private static void cancellationToken_ThrowIfNoOpFastPath() { }

    /// <summary>
    /// Per-module file enumeration produced by
    /// <see cref="EnumerateModuleFiles"/>. Carries the C++ sources, C++
    /// headers, and C# sources so the manifest emitter can author the
    /// <see cref="Simgenics.XPact.XBT.Manifest.Module.SourceFiles"/> and
    /// <see cref="Simgenics.XPact.XBT.Manifest.Module.CSharpSources"/>
    /// lists without rewalking the filesystem.
    /// </summary>
    private sealed record ModuleFileSet(
        IReadOnlyList<FileItem> Sources,
        IReadOnlyList<FileItem> Headers,
        IReadOnlyList<FileItem> CSharpSources);

    /// <summary>
    /// Build a <see cref="Simgenics.XPact.XBT.Manifest.Manifest"/> POCO
    /// from the post-discovery state and write both forms (JSON +
    /// FlatBuffers binary sidecar) to
    /// <c>&lt;intermediateBuildDir&gt;/Manifest.json</c> and
    /// <c>Manifest.fbs.bin</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per the Step 0.5 addendum + Toolchain Contract Rev 13 Section 10.2,
    /// the manifest is the wire surface XHT (System 2) and XIL2CPP
    /// (System 6) consume. It is emitted once per BuildMode invocation,
    /// before the action graph is built, so a downstream tool that runs
    /// against an interrupted build still gets a consistent snapshot.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> The serialisation paths in
    /// <see cref="Manifest.ManifestJson"/> and
    /// <see cref="Manifest.ManifestFbs"/> are deterministic by construction
    /// (no timestamps, sorted enumerations). This method preserves the
    /// alphabetical module ordering from
    /// <see cref="ModuleCatalog.Modules"/> -- the modules list is the
    /// same byte sequence on two builds of the same source tree.
    /// </para>
    /// </remarks>
    private static void EmitManifest(
        string engineRoot,
        TargetRules target,
        SemanticVersion engineVersion,
        IReadOnlyList<ModuleRecord> targetModules,
        IReadOnlyDictionary<string, ModuleFileSet> fileSetByModule,
        string intermediateBuildDir)
    {
        // Sort participants alphabetically before serialising. The
        // ModuleCatalog already does this, but defending here keeps the
        // emitter independent of the catalog's invariant.
        List<ModuleRecord> orderedModules = targetModules
            .OrderBy(r => r.Rules.Name, StringComparer.Ordinal)
            .ToList();

        List<Simgenics.XPact.XBT.Manifest.Module> manifestModules =
            new(orderedModules.Count);
        HashSet<string> dynamicModuleNames =
            new(StringComparer.Ordinal);

        foreach (ModuleRecord rec in orderedModules)
        {
            ModuleRules m = rec.Rules;
            string moduleDir = Path.GetDirectoryName(rec.DescriptorPath)!;
            string baseDirectory = ToRelativeForward(engineRoot, moduleDir);

            fileSetByModule.TryGetValue(m.Name, out ModuleFileSet? files);
            files ??= new ModuleFileSet(
                Array.Empty<FileItem>(),
                Array.Empty<FileItem>(),
                Array.Empty<FileItem>());

            // Build the SourceFile list from the filesystem walk. Paths
            // are recorded module-base-directory-relative with forward
            // slashes so the manifest stays portable across hosts.
            List<SourceFile> sourceFiles =
                new(files.Sources.Count + files.Headers.Count + files.CSharpSources.Count);
            foreach (FileItem cpp in files.Sources)
            {
                sourceFiles.Add(new SourceFile(
                    RelativePath: ToRelativeForward(moduleDir, cpp.FullPath),
                    IsCSharp: false,
                    IsHeader: false,
                    IsTestOnly: m.bIsTestModule));
            }
            foreach (FileItem h in files.Headers)
            {
                sourceFiles.Add(new SourceFile(
                    RelativePath: ToRelativeForward(moduleDir, h.FullPath),
                    IsCSharp: false,
                    IsHeader: true,
                    IsTestOnly: m.bIsTestModule));
            }
            foreach (FileItem cs in files.CSharpSources)
            {
                sourceFiles.Add(new SourceFile(
                    RelativePath: ToRelativeForward(moduleDir, cs.FullPath),
                    IsCSharp: true,
                    IsHeader: false,
                    IsTestOnly: m.bIsTestModule));
            }
            // Sort by relative path ordinal so the manifest is byte-stable
            // independent of the filesystem walk order.
            sourceFiles.Sort(static (a, b) =>
                string.CompareOrdinal(a.RelativePath, b.RelativePath));

            // C# source list: module-base-directory-relative paths, sorted
            // ordinal. Fed by the filesystem walk in Phase 1; future phases
            // may merge a parser-authored CSharpSources list on
            // ModuleRules (currently absent).
            List<string> csharpSourcePaths = files.CSharpSources
                .Select(cs => ToRelativeForward(moduleDir, cs.FullPath))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            // Aggregate module-dependency edges. The contract's three
            // lists (PublicDeps, PrivateDeps, DynamicallyLoadedModules)
            // collapse to a single ModuleDep[] in the manifest; the
            // dynamic edges carry through a side-channel set consumed by
            // ManifestFbs.Serialize for the FBS-only is_dynamic flag.
            List<ModuleDep> deps = new();
            deps.AddRange(m.PublicDependencyModuleNames);
            deps.AddRange(m.PrivateDependencyModuleNames);
            foreach (ModuleDep dyn in m.DynamicallyLoadedModuleNames)
            {
                deps.Add(new ModuleDep(dyn.Name, InterfaceModule: false));
                dynamicModuleNames.Add(dyn.Name);
            }
            // Sort by Name ordinal for byte-stable output.
            deps.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

            // Merge public + private include paths for the manifest;
            // consumers see one flat list per Section 9.1.
            List<string> includePaths = new();
            includePaths.AddRange(m.PublicIncludePaths);
            includePaths.AddRange(m.PrivateIncludePaths);
            includePaths.Sort(StringComparer.Ordinal);

            List<string> publicDefines = m.PublicDefinitions
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            // Phase 1 leaves header-category lists as the descriptor-
            // declared values. The filesystem walk is not split by
            // Public/Private/Internal in Phase 1 (Step 0.5 addendum
            // Section 2.2), so we cannot author those lists from the
            // walk; pass through what the descriptor parser populated.
            // For modules without a parser-authored list, an empty
            // string[] is the honest answer.
            Simgenics.XPact.XBT.Manifest.Module manifestModule = new(
                Name: m.Name,
                Tier: m.Tier,
                ModuleType: m.ModuleType,
                Languages: m.Languages,
                BaseDirectory: baseDirectory,
                SourceFiles: sourceFiles,
                PublicHeaders: Array.Empty<string>(),
                PrivateHeaders: Array.Empty<string>(),
                InternalHeaders: Array.Empty<string>(),
                CSharpSources: csharpSourcePaths,
                IncludePaths: includePaths,
                PublicDefines: publicDefines,
                ModuleDependencies: deps,
                GeneratedCPPFilenameBase: m.Name + ".gen",
                SimPath: m.SimPath,
                EngineVersionCompat: "*",
                SimdLevel: m.SimdLevel,
                PCHUsage: m.PCHUsage,
                ExcludeFromSharedPCH: m.bExcludeFromSharedPCH,
                AllowHotReload: m.bAllowHotReload,
                IsTestModule: m.bIsTestModule,
                DeprecationMessage: m.DeprecationMessage,
                MinimumToolchainVersion: m.MinimumToolchainVersion);
            manifestModules.Add(manifestModule);
        }

        Manifest.Manifest manifest = new(
            ContractVersion: ContractVersion.Current,
            EngineVersion: engineVersion.ToString(),
            TargetName: target.Name,
            TargetType: target.TargetType,
            Configuration: target.Configuration,
            Platform: target.Platform,
            RootLocalPath: NormalisePathForward(engineRoot),
            ExternalDependenciesFile: null,
            FipsMode: target.FipsMode,
            SimPathConservativeRootsAllowed: target.SimPathConservativeRootsAllowed,
            StationRole: target.StationRole,
            SimdLevelDefault: target.SimdLevelDefault,
            Modules: manifestModules);

        string manifestJsonPath = Path.Combine(intermediateBuildDir, "Manifest.json");
        string manifestFbsPath = Path.Combine(intermediateBuildDir, "Manifest.fbs.bin");

        long jsonSize = ManifestJson.Serialize(manifest, manifestJsonPath);
        long fbsSize = ManifestFbs.Serialize(manifest, manifestFbsPath, dynamicModuleNames: dynamicModuleNames);

        Logger.Info(
            $"manifest written: {manifestJsonPath} ({jsonSize} bytes), " +
            $"{manifestFbsPath} ({fbsSize} bytes); modules={manifestModules.Count}.",
            new DiagnosticContext { Action = "write-manifest" });
    }

    /// <summary>
    /// Normalise a path to forward slashes. Phase 1 manifests are
    /// portable across Win64 + Linux; the wire form uses <c>/</c>
    /// uniformly so a manifest emitted on Windows reads identically on
    /// Linux.
    /// </summary>
    private static string NormalisePathForward(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Compute <paramref name="absolutePath"/>'s position relative to
    /// <paramref name="baseDir"/> and return it with forward slashes.
    /// Returns the absolute path (forward-slashed) if no relative form
    /// is possible (e.g. different drives on Windows).
    /// </summary>
    private static string ToRelativeForward(string baseDir, string absolutePath)
    {
        try
        {
            string rel = Path.GetRelativePath(baseDir, absolutePath);
            return NormalisePathForward(rel);
        }
        catch (ArgumentException)
        {
            return NormalisePathForward(absolutePath);
        }
    }

    /// <summary>
    /// Group selected modules by the absolute canonical path of each
    /// module's <see cref="ModuleRules.SharedPCHHeaderFile"/>. For each
    /// group of size &gt;= 2 participants, emit ONE shared PCH generation
    /// action via <see cref="XToolChain.GenerateSharedPCH"/> and record
    /// the resulting <see cref="PCHBinding"/> against every participant's
    /// name. A group of size 1 emits a <see cref="Core.Logger.Info"/>
    /// diagnostic and falls through to private-PCH semantics; the
    /// per-module emit loop downstream picks up the private path via
    /// <see cref="TryGeneratePCH"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Header path resolution.</b> The grouping key is the canonical
    /// absolute path of each module's <c>shared_pch_header_file</c>.
    /// Two resolution forms per Toolchain Contract Rev 13.1 Section 1.5:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Bare name</b> (no <c>/</c> or <c>\</c>) -- UE-style
    ///   logical include-path resolution. The
    ///   <see cref="SharedPchResolver.FindIncludeFile"/> helper
    ///   searches every module's
    ///   <see cref="ModuleRules.PublicIncludePaths"/> for the file and
    ///   returns the first on-disk match. Iteration order matches the
    ///   alphabetical module ordering established by
    ///   <see cref="ModuleCatalog"/>, so the choice is deterministic.
    ///   If no module exposes the file, the resolver throws
    ///   <see cref="DescriptorParseException"/> with exit 30.</item>
    ///   <item><b>Relative path</b> (contains <c>/</c> or <c>\</c>) --
    ///   legacy resolution relative to the module's <c>BaseDirectory</c>.
    ///   The parser rejects any <c>..</c> traversal so this branch
    ///   covers intra-module headers exclusively.</item>
    /// </list>
    /// <para>
    /// Per Contract Rev 13 Section 1.5 / <c>/Documents/XBT.html</c>
    /// Section 15.4: SimPath modules cannot participate in any shared
    /// PCH group. The parser-side validator rejects SimPath +
    /// <c>shared_pch_header_file</c> at parse time (exit 30); this
    /// method re-checks at emit time as defence-in-depth and propagates
    /// the toolchain's exit-41 failure if any participant is SimPath.
    /// </para>
    /// <para>
    /// The returned map keys on participant module name (every name has a
    /// corresponding entry in <paramref name="targetModules"/>). The
    /// downstream emit loop uses
    /// <see cref="Dictionary{TKey,TValue}.TryGetValue"/> to decide
    /// whether a given module uses the shared binding or falls back to
    /// the private-PCH path.
    /// </para>
    /// </remarks>
    private static Dictionary<string, PCHBinding> BuildSharedPchGroups(
        XToolChain toolchain,
        TargetRules target,
        string engineRoot,
        IReadOnlyList<ModuleRecord> targetModules,
        List<IExternalAction> actions,
        List<IExternalAction> reportActions)
    {
        Dictionary<string, PCHBinding> bindingByModule =
            new(StringComparer.Ordinal);

        // ---- 1. Walk modules and bucket by resolved absolute header path ----
        //
        // Keys are canonical absolute paths produced by
        // <see cref="Path.GetFullPath(string)"/>. Path.GetFullPath
        // preserves case on Windows and is case-sensitive on Linux, so
        // the dictionary uses StringComparer.Ordinal -- matching the
        // host filesystem's case sensitivity. (OrdinalIgnoreCase would
        // collapse case-different-but-physically-distinct paths on
        // Linux into a single bucket, which is wrong.)
        Dictionary<string, List<ModuleRecord>> groupRecords =
            new(StringComparer.Ordinal);
        Dictionary<string, string> groupRelativeHeader =
            new(StringComparer.Ordinal);

        foreach (ModuleRecord rec in targetModules)
        {
            ModuleRules module = rec.Rules;
            if (string.IsNullOrEmpty(module.SharedPCHHeaderFile))
            {
                continue;
            }

            string moduleDir = Path.GetDirectoryName(rec.DescriptorPath)!;
            string sharedHeader = module.SharedPCHHeaderFile;

            // Two resolution forms per Toolchain Contract Rev 13.1
            // Section 1.5 (PCH rules) + XBT Section 7:
            //   (a) BARE NAME (no separator) -- UE-style logical
            //       include-path resolution. Search every module's
            //       PublicIncludePaths for the file; the first
            //       on-disk match wins (deterministic because the
            //       module list is sorted alphabetically by
            //       ModuleCatalog).
            //   (b) RELATIVE PATH -- legacy relative-to-module-dir
            //       resolution. Path.GetFullPath collapses the
            //       (rejected-by-parser) `..` references; only paths
            //       inside the module's tree pass the parser, so this
            //       branch handles intra-module headers.
            string absoluteCanonicalPath;
            if (!sharedHeader.Contains('/') && !sharedHeader.Contains('\\'))
            {
                string? resolved = SharedPchResolver.FindIncludeFile(
                    sharedHeader, targetModules);
                if (resolved is null)
                {
                    throw new DescriptorParseException(
                        $"Module '{module.Name}' declared shared_pch_header_file = " +
                        $"'{sharedHeader}' but the file could not be found in any " +
                        "module's PublicIncludePaths. Either declare " +
                        $"'{sharedHeader}' as a file under one of the participants' " +
                        "PublicIncludePaths directories, or use a relative path " +
                        "with path separators if the file lives outside any " +
                        "include path (per Toolchain Contract Rev 13.1 Section 1.5). " +
                        "On Linux the path lookup is case-sensitive; ensure " +
                        "PublicIncludePaths entries match the exact on-disk directory case.",
                        exitCode: 30,
                        filePath: rec.DescriptorPath);
                }
                absoluteCanonicalPath = resolved;
            }
            else
            {
                string headerPath = Path.Combine(moduleDir, sharedHeader);
                absoluteCanonicalPath = Path.GetFullPath(headerPath);
            }

            if (!groupRecords.TryGetValue(absoluteCanonicalPath, out List<ModuleRecord>? list))
            {
                list = new List<ModuleRecord>();
                groupRecords[absoluteCanonicalPath] = list;
                groupRelativeHeader[absoluteCanonicalPath] = sharedHeader;
            }
            list.Add(rec);
        }

        // ---- 2. For each group, emit (size>=2) or log + fall through (size==1) ----
        // Iterate keys sorted ordinal so action emission order is
        // deterministic across runs.
        List<string> sortedGroupKeys = new(groupRecords.Keys);
        sortedGroupKeys.Sort(StringComparer.Ordinal);

        foreach (string headerKey in sortedGroupKeys)
        {
            List<ModuleRecord> participantRecords = groupRecords[headerKey];

            // Sort participants alphabetically for the alphabetical-
            // include-order policy (Contract Section 1.5) and to feed the
            // toolchain a deterministic ordering.
            participantRecords.Sort(static (a, b) =>
                string.CompareOrdinal(a.Rules.Name, b.Rules.Name));

            if (participantRecords.Count == 1)
            {
                // Single-participant "shared" PCH: fall back to private-
                // PCH semantics per the spec. Log informationally so the
                // developer knows the declaration is wasteful.
                ModuleRules onlyModule = participantRecords[0].Rules;
                Logger.Info(
                    $"module '{onlyModule.Name}' declared a shared PCH but is the only participant " +
                    "-- using private PCH semantics for it.",
                    new DiagnosticContext
                    {
                        Action = "shared-pch-grouping",
                        Module = onlyModule.Name,
                        Tier = onlyModule.Tier.ToString(),
                    });
                continue;
            }

            // 2+ participants: enforce no-SimPath defence-in-depth,
            // resolve the header file existence, run the include-order
            // rewriter, and emit ONE shared PCH action.
            foreach (ModuleRecord pr in participantRecords)
            {
                if (pr.Rules.SimPath)
                {
                    throw new ToolchainBannedFlagException(
                        $"Module '{pr.Rules.Name}' is SimPath but appears in a shared-PCH " +
                        "group (header: " + headerKey + "). Contract Rev 13 Section 1.5 " +
                        "forbids SimPath modules from participating in any shared PCH. " +
                        "Drop the shared_pch_header_file declaration from the SimPath " +
                        "module (use pch_header_file = ... for a private PCH instead).");
                }
            }

            if (!File.Exists(headerKey))
            {
                Logger.Warning(
                    "Shared PCH header file was not found at '" + headerKey + "'. " +
                    "Skipping shared PCH generation for this group (participants: " +
                    string.Join(", ", participantRecords.Select(p => p.Rules.Name)) +
                    "). Each participant will compile without a PCH.",
                    new DiagnosticContext { Action = "shared-pch-grouping" });
                continue;
            }

            // Per Contract Section 1.5: rewrite include order
            // alphabetically. Idempotent.
            PCHIncludeOrderRewriter.RewriteFile(headerKey, moduleName: null);

            FileItem headerFileItem = FileItem.GetItemByPath(headerKey);

            // Place the shared PCH artefacts under a target-scoped
            // intermediate directory. The directory is shared across
            // modules (the artefact itself is shared).
            string sharedIntermediateDir = Path.Combine(
                engineRoot, "Intermediate", "Build", target.Name,
                target.Configuration.ToString(), target.Platform.ToString(),
                "_Shared");
            Directory.CreateDirectory(sharedIntermediateDir);

            // Build the participant ModuleRules list (sorted ordinal by
            // name) for the toolchain call.
            List<ModuleRules> participantRules =
                participantRecords.Select(r => r.Rules).ToList();

            PCHBinding binding = toolchain.GenerateSharedPCH(
                headerFile: groupRelativeHeader[headerKey],
                participants: participantRules,
                headerFileItem: headerFileItem,
                target: target,
                outputDir: sharedIntermediateDir);

            actions.Add(binding.Action);
            reportActions.Add(binding.Action);

            // Index the binding by every participant's name so the
            // downstream emit loop finds it.
            foreach (ModuleRules m in participantRules)
            {
                bindingByModule[m.Name] = binding;
            }

            Logger.Info(
                $"Shared PCH group ({participantRules.Count} participants) emitted for " +
                $"header '{headerKey}'.",
                new DiagnosticContext { Action = "shared-pch-grouping" });
        }

        return bindingByModule;
    }

    /// <summary>
    /// Try to generate a per-module PCH. Returns null when the module
    /// doesn't use a PCH (NoPCHs or no PrivatePCHHeaderFile) -- the
    /// caller threads null through to <see cref="XToolChain.CompileSource"/>.
    /// </summary>
    private static PCHBinding? TryGeneratePCH(
        XToolChain toolchain,
        ModuleRules module,
        TargetRules target,
        string moduleDir,
        string moduleObjDir)
    {
        PCHUsageMode effectiveUsage = XToolChain.ResolvePCHUsage(module);
        if (effectiveUsage == PCHUsageMode.NoPCHs
            || string.IsNullOrEmpty(module.PrivatePCHHeaderFile))
        {
            return null;
        }

        string headerPath = Path.Combine(moduleDir, module.PrivatePCHHeaderFile);
        if (!File.Exists(headerPath))
        {
            Logger.Warning(
                $"Module '{module.Name}' declares PrivatePCHHeaderFile='{module.PrivatePCHHeaderFile}' " +
                $"but the file was not found at {headerPath}. Skipping PCH generation.",
                new DiagnosticContext { Module = module.Name });
            return null;
        }

        // Per Contract Section 1.5: rewrite the PCH header's include
        // order alphabetically before generation. The rewrite is
        // idempotent and emits a Logger.Info if it changes anything.
        PCHIncludeOrderRewriter.RewriteFile(headerPath, module.Name);

        FileItem header = FileItem.GetItemByPath(headerPath);
        return toolchain.GeneratePCH(module, target, module.PrivatePCHHeaderFile!, header, moduleObjDir);
    }

    /// <summary>
    /// Per-module source-file enumeration helper. Walks the module directory
    /// recursively and returns three lists: C++ TU sources (<c>.cpp</c>), C++
    /// headers (<c>.h</c>), and C# sources (<c>.cs</c>). Descriptor files
    /// (<c>.Build.cs</c>, <c>.Build.toml</c>, <c>.Build.expr</c>) are excluded
    /// from the C# list -- they are build metadata, not module source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three lists are sorted ordinal so iteration order is reproducible
    /// across runs and machines (matches the determinism contract documented
    /// in Step 0.5 addendum Section 2.3).
    /// </para>
    /// <para>
    /// Phase 1 does not distinguish <c>Public/</c>, <c>Private/</c>,
    /// <c>Internal/</c>, or <c>Classes/</c> subtrees -- every file under
    /// <paramref name="moduleDir"/> is treated equally. The header-category
    /// split that the manifest schema exposes is not derived from filesystem
    /// layout in Phase 1; it carries whatever a parser populated on
    /// <c>ModuleRules</c> through unchanged.
    /// </para>
    /// </remarks>
    private static (IReadOnlyList<FileItem> Sources, IReadOnlyList<FileItem> Headers, IReadOnlyList<FileItem> CSharpSources)
        EnumerateModuleFiles(string moduleDir)
    {
        List<FileItem> sources = new();
        List<FileItem> headers = new();
        List<FileItem> csharpSources = new();

        if (!Directory.Exists(moduleDir))
        {
            return (sources, headers, csharpSources);
        }

        // Walk Public/Private/Internal subtrees + the module root.
        EnumerationOptions opts = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };
        foreach (string path in Directory.EnumerateFiles(moduleDir, "*.cpp", opts))
        {
            sources.Add(FileItem.GetItemByPath(path));
        }
        foreach (string path in Directory.EnumerateFiles(moduleDir, "*.h", opts))
        {
            headers.Add(FileItem.GetItemByPath(path));
        }
        foreach (string path in Directory.EnumerateFiles(moduleDir, "*.cs", opts))
        {
            // Exclude descriptor files: <ModuleName>.Build.cs and the
            // legacy/sibling <ModuleName>.Build.toml/.expr forms. Build
            // descriptors are metadata XBT consumes for discovery; they are
            // not part of the module's source set.
            string fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            csharpSources.Add(FileItem.GetItemByPath(path));
        }
        // Sort ordinal for deterministic order.
        sources.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        headers.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        csharpSources.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        return (sources, headers, csharpSources);
    }

    private static ExecutionReport ExecuteGraph(
        Simgenics.XPact.XBT.ActionGraph.ActionGraph graph,
        ActionHistory history,
        CancellationToken cancellationToken)
    {
        ParallelExecutor executor = new(
            new ParallelExecutorOptions(),
            new CopyrightAndProcessActionRunner(),
            history);
        return executor.Execute(graph, cancellationToken);
    }

    /// <summary>
    /// Composite runner that handles the in-process actions XBT itself
    /// authors (currently <see cref="ValidateCopyrightAction"/>) and
    /// delegates everything else to the production
    /// <see cref="ProcessActionRunner"/>.
    /// </summary>
    private sealed class CopyrightAndProcessActionRunner : IActionRunner
    {
        private readonly ProcessActionRunner _fallback = new();

        public ActionRunResult RunAction(ActionRunContext context)
        {
            if (context.Action is ValidateCopyrightAction copyrightAction)
            {
                ValidationReport report = copyrightAction.Run(context.CancellationToken);
                if (report.Success)
                {
                    // Write the marker temp file the ParallelExecutor
                    // expects per the atomic-rename contract (XBT.html
                    // Section 6.4). When the validator ran successfully,
                    // we touch the marker so ActionHistory can record
                    // the successful key against it on the next pass.
                    foreach ((FileItem produced, string tempPath) in context.TempOutputPaths)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                        File.WriteAllText(
                            tempPath,
                            $"ValidateCopyright OK: {report.FilesChecked} file(s) checked.\n");
                    }
                    return new ActionRunResult(Success: true, ExitCode: 0, ErrorMessage: null);
                }
                string message =
                    $"Copyright validation failed for {report.Failures.Count} file(s):" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, report.Failures);
                Logger.Error(
                    message,
                    exitCode: ValidateCopyrightAction.MissingHeaderExitCode,
                    new DiagnosticContext { Action = "validate-copyright" });
                return new ActionRunResult(
                    Success: false,
                    ExitCode: ValidateCopyrightAction.MissingHeaderExitCode,
                    ErrorMessage: message);
            }

            return _fallback.RunAction(context);
        }
    }
}

/// <summary>
/// Parsed CLI options for <see cref="BuildMode"/>. Construction is via
/// <see cref="Parse"/>; the type is internal so test harnesses can
/// build it directly without re-formatting CLI args.
/// </summary>
internal sealed record BuildOptions
{
    public required string TargetName { get; init; }
    public required BuildConfiguration Configuration { get; init; }
    public required Platform Platform { get; init; }
    public BuildTargetType TargetType { get; init; } = BuildTargetType.Editor;
    public StationRole StationRole { get; init; } = StationRole.None;
    public bool FipsMode { get; init; }
    public string? EngineRoot { get; init; }
    public string? StudioRoot { get; init; }
    public string? ProjectRoot { get; init; }

    public static BuildOptions Parse(string[] args)
    {
        string? targetName = null;
        BuildConfiguration config = BuildConfiguration.Development;
        Platform platform = DefaultHostPlatform();
        BuildTargetType targetType = BuildTargetType.Editor;
        StationRole role = StationRole.None;
        bool fips = false;
        string? engineRoot = null;
        string? studioRoot = null;
        string? projectRoot = null;

        foreach (string arg in args)
        {
            if (arg.StartsWith("-Target=", StringComparison.OrdinalIgnoreCase))
            {
                targetName = arg["-Target=".Length..];
            }
            else if (arg.StartsWith("-Configuration=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-Configuration=".Length..];
                if (!Enum.TryParse(raw, ignoreCase: true, out config))
                {
                    throw new BuildOptionsParseException(
                        $"Invalid -Configuration value '{raw}'. Expected one of " +
                        string.Join(", ", Enum.GetNames<BuildConfiguration>()));
                }
            }
            else if (arg.StartsWith("-Platform=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-Platform=".Length..];
                if (!Enum.TryParse(raw, ignoreCase: true, out platform))
                {
                    throw new BuildOptionsParseException(
                        $"Invalid -Platform value '{raw}'. Expected one of " +
                        string.Join(", ", Enum.GetNames<Platform>()));
                }
            }
            else if (arg.StartsWith("-TargetType=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-TargetType=".Length..];
                if (!Enum.TryParse(raw, ignoreCase: true, out targetType))
                {
                    throw new BuildOptionsParseException(
                        $"Invalid -TargetType value '{raw}'. Expected one of " +
                        string.Join(", ", Enum.GetNames<BuildTargetType>()));
                }
            }
            else if (arg.StartsWith("-StationRole=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = arg["-StationRole=".Length..];
                if (!Enum.TryParse(raw, ignoreCase: true, out role))
                {
                    throw new BuildOptionsParseException(
                        $"Invalid -StationRole value '{raw}'.");
                }
            }
            else if (arg.Equals("-FipsMode", StringComparison.OrdinalIgnoreCase))
            {
                fips = true;
            }
            else if (arg.StartsWith("-Engine=", StringComparison.OrdinalIgnoreCase))
            {
                engineRoot = arg["-Engine=".Length..];
            }
            else if (arg.StartsWith("-Studio=", StringComparison.OrdinalIgnoreCase))
            {
                studioRoot = arg["-Studio=".Length..];
            }
            else if (arg.StartsWith("-Project=", StringComparison.OrdinalIgnoreCase))
            {
                projectRoot = arg["-Project=".Length..];
            }
            else
            {
                throw new BuildOptionsParseException($"Unknown argument '{arg}'.");
            }
        }

        if (string.IsNullOrEmpty(targetName))
        {
            throw new BuildOptionsParseException(
                "-Target=<TargetName> is required.");
        }

        return new BuildOptions
        {
            TargetName = targetName,
            Configuration = config,
            Platform = platform,
            TargetType = targetType,
            StationRole = role,
            FipsMode = fips,
            EngineRoot = engineRoot,
            StudioRoot = studioRoot,
            ProjectRoot = projectRoot,
        };
    }

    private static Platform DefaultHostPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Platform.Win64;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Platform.Linux;
        }
        // Fall back to Win64 -- tests on macOS / unsupported hosts will
        // typically pass -Platform= explicitly.
        return Platform.Win64;
    }
}

/// <summary>
/// Build-mode invocation outcome surfaced to programmatic callers (tests).
/// </summary>
internal sealed record BuildResult(
    bool Success,
    int ActionsRan,
    int ActionsCached,
    int ActionsFailed,
    int FirstFailingExitCode,
    TimeSpan Duration,
    IReadOnlyList<IExternalAction> Actions);

/// <summary>
/// Thrown by <see cref="BuildOptions.Parse"/> on malformed input. Maps
/// to exit code 10 (CLI argument error).
/// </summary>
internal sealed class BuildOptionsParseException : XBTException
{
    public BuildOptionsParseException(string message) : base(message, exitCode: 10) { }
}
