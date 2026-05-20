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

        List<IExternalAction> actions = EmitActions(
            engineRoot,
            target,
            toolchain,
            targetModules,
            out IReadOnlyList<IExternalAction> emittedForReport);

        // ---- 10. Build the action graph --------------------------------
        Simgenics.XPact.XBT.ActionGraph.ActionGraph graph = new(actions);
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        // ---- 11. Open ActionHistory ------------------------------------
        string intermediateRoot = Path.Combine(engineRoot, "Intermediate", "Build", target.Name);
        Directory.CreateDirectory(intermediateRoot);
        ActionHistory history = ActionHistory.Open(intermediateRoot, target.Configuration);

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
        out IReadOnlyList<IExternalAction> emittedForReport)
    {
        List<IExternalAction> actions = new();
        List<FileItem> allSourceFiles = new();
        List<IExternalAction> reportActions = new();

        foreach (ModuleRecord rec in targetModules)
        {
            ModuleRules module = rec.Rules;
            string moduleDir = Path.GetDirectoryName(rec.DescriptorPath)!;

            // Enumerate the module's source files.
            (IReadOnlyList<FileItem> sourceFiles, IReadOnlyList<FileItem> headerFiles) =
                EnumerateModuleFiles(moduleDir);
            allSourceFiles.AddRange(sourceFiles);
            allSourceFiles.AddRange(headerFiles);

            string moduleObjDir = Path.Combine(
                engineRoot, "Intermediate", "Build", target.Name,
                target.Configuration.ToString(), target.Platform.ToString(), module.Name);
            Directory.CreateDirectory(moduleObjDir);

            // PCH generation (if applicable).
            PCHBinding? pchBinding = TryGeneratePCH(toolchain, module, target, moduleDir, moduleObjDir);
            if (pchBinding is not null)
            {
                actions.Add(pchBinding.Action);
                reportActions.Add(pchBinding.Action);
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

    private static (IReadOnlyList<FileItem> Sources, IReadOnlyList<FileItem> Headers)
        EnumerateModuleFiles(string moduleDir)
    {
        List<FileItem> sources = new();
        List<FileItem> headers = new();

        if (!Directory.Exists(moduleDir))
        {
            return (sources, headers);
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
        // Sort ordinal for deterministic order.
        sources.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        headers.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        return (sources, headers);
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
