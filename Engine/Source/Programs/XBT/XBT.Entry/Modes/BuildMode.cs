// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    ///   <item><c>-EngineRoot=&lt;path&gt;</c> (override the engine root discovery; spec-canonical)</item>
    ///   <item><c>-Engine=&lt;path&gt;</c> (legacy alias of <c>-EngineRoot=</c>)</item>
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

        // Audit fix R6-C7: acquire a per-(engineRoot, target, config,
        // platform) named mutex BEFORE touching the intermediate /
        // build tree. Two concurrent xbt builds against the same target
        // would otherwise race on ActionHistory.bin and likely corrupt
        // it. Mutex name is hashed so paths with spaces / case /
        // separator quirks all reduce to one canonical name.
        //
        // On Linux the System.Threading.Mutex maps to a CLR-internal
        // named primitive that is process-tree scoped, not file-system
        // scoped; the same canonical name therefore still works across
        // sibling xbt processes on POSIX.
        string mutexName = ComposeBuildMutexName(
            engineRoot,
            options.TargetName,
            options.Configuration,
            options.Platform);
        using Mutex buildMutex = new(initiallyOwned: false, name: mutexName, out _);

        bool mutexAcquired;
        try
        {
            // Block unless -NoMutexWait was passed; in that case fail
            // immediately with exit 1 (GenericFailure) so an IDE wrapper
            // can detect a concurrent build instead of blocking
            // indefinitely.
            mutexAcquired = options.NoMutexWait
                ? buildMutex.WaitOne(TimeSpan.Zero)
                : WaitMutexWithProgressLog(buildMutex, cancellationToken);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing. We took
            // ownership anyway. AbandonedMutexException is "warning,
            // not failure" -- the cache may be in a half-state but the
            // ActionHistory loader tolerates that (Audit fix M15: torn
            // reads leave the live archive untouched and the next save
            // rewrites cleanly).
            Logger.Warning(
                "Previous XBT build process exited without releasing the build mutex. " +
                "Proceeding -- ActionHistory.bin is self-healing on a torn read.",
                new DiagnosticContext { Action = "build-mutex" });
            mutexAcquired = true;
        }

        if (!mutexAcquired)
        {
            // -NoMutexWait was set AND another build is in progress.
            throw new XBTException(
                $"Another XBT build is already in progress for target='{options.TargetName}' " +
                $"config={options.Configuration} platform={options.Platform} (engine='{engineRoot}'). " +
                "Wait for it to finish, or omit -NoMutexWait to block until it releases.",
                exitCode: 1);
        }

        try
        {
            return RunInternalLocked(options, engineRoot, studioRoot, projectRoot, projectRoots, sw, cancellationToken);
        }
        finally
        {
            buildMutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Audit fix R6-C7: emit a periodic progress log while blocked on
    /// the build mutex. WaitOne(Timeout.Infinite) with a cancellation
    /// token expects a WaitHandle. Mutex inherits from WaitHandle so we
    /// can poll with a small timeout and re-emit a "still waiting"
    /// message every few seconds so the operator knows the build hasn't
    /// hung silently.
    /// </summary>
    private static bool WaitMutexWithProgressLog(Mutex mutex, CancellationToken cancellationToken)
    {
        // Poll every 2.5 s. The poll interval is short enough that the
        // operator sees the "waiting" message within a few seconds of
        // running; long enough that it doesn't spam the log.
        TimeSpan poll = TimeSpan.FromMilliseconds(2_500);
        bool emittedNotice = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutex.WaitOne(poll))
            {
                return true;
            }
            if (!emittedNotice)
            {
                Logger.Info(
                    "Another XBT build is already in progress for the same target+config+platform. Waiting...",
                    new DiagnosticContext { Action = "build-mutex" });
                emittedNotice = true;
            }
        }
    }

    /// <summary>
    /// Compose a global mutex name from the build's identity. The name
    /// is the literal prefix <c>XBT_Build_</c> + the first 32 hex
    /// characters of a BLAKE3 of <c>(engineRoot, target, config, platform)</c>.
    /// </summary>
    /// <remarks>
    /// Hashing the identity avoids the OS-name validity rules (Win32
    /// mutex names cannot contain backslashes outside of the
    /// <c>Global\</c> / <c>Local\</c> prefix; Linux ipc names have
    /// their own restrictions) and gives a deterministic name that two
    /// concurrent invocations from the same engine root collide on.
    /// </remarks>
    internal static string ComposeBuildMutexName(
        string engineRoot,
        string targetName,
        BuildConfiguration configuration,
        Platform platform)
    {
        // Canonicalize the engineRoot so two callers using different
        // casing on Windows agree. On Linux we preserve case.
        string canonicalRoot = OperatingSystem.IsWindows()
            ? Path.GetFullPath(engineRoot).ToUpperInvariant()
            : Path.GetFullPath(engineRoot);

        string identity = $"{canonicalRoot}|{targetName}|{configuration}|{platform}";
        IoHash hash = IoHash.Compute(System.Text.Encoding.UTF8.GetBytes(identity));
        // First 32 hex chars (128 bits) of the BLAKE3 digest -- a
        // mutex-name collision over the engine's lifetime is
        // astronomically unlikely. The "XBT_Build_" prefix is
        // descriptive so an operator inspecting tasklist / lsof can
        // identify the mutex's owner.
        return "XBT_Build_" + hash.ToString().Substring(0, 32);
    }

    private static BuildResult RunInternalLocked(
        BuildOptions options,
        string engineRoot,
        string? studioRoot,
        string? projectRoot,
        IReadOnlyList<string> projectRoots,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {

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
        // Audit fix M14: honour an explicit -Architecture= override
        // when provided; otherwise pick a platform-appropriate default
        // (x86_64 on Win64/Linux, aarch64 on Android).
        string architecture = options.Architecture
            ?? DefaultArchitectureForPlatform(options.Platform);

        TargetRules target = new()
        {
            Name = options.TargetName,
            TargetType = options.TargetType,
            Configuration = options.Configuration,
            Platform = options.Platform,
            Architecture = architecture,
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

        // ---- 8.5 Validate MinimumToolchainVersion per module -----------
        // Audit fix C7: every module's declared
        // MinimumToolchainVersion must be <= the live
        // toolchain.ToolchainVersion. Mismatch fails the build with exit
        // 23 (EngineOrToolchainVersionMismatch).
        ValidateMinimumToolchainVersion(modules, toolchain);

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

        // ---- 9 prelude: resolve the manifest output directory ----
        // The Phase 1f wiring of XHT actions (XBT.html Section 9.4 +
        // XHT.html Section 9) requires every ParseHeadersAction /
        // EmitReflectionAction to reference the eventual Manifest.json
        // path. The manifest itself is still written at step 9.5 below,
        // BEFORE the executor pump dispatches any action, so the on-disk
        // file is in place when XHT actually runs; we just need to know
        // the path during action emission to wire it into the action
        // graph as a prerequisite of each XHT action.
        string defaultIntermediateBuildDir = Path.Combine(
            engineRoot, "Intermediate", "Build", target.Name,
            target.Configuration.ToString());
        // Round-6 final-cleanup M2: -Out=<dir> on write-manifest plumbs
        // through BuildOptions.ManifestOutputDirectory. When set, the
        // manifest is emitted to the override directory; otherwise the
        // default intermediate-build directory is used.
        string manifestOutputDir = options.ManifestOutputDirectory ?? defaultIntermediateBuildDir;
        Directory.CreateDirectory(manifestOutputDir);

        Dictionary<string, ModuleFileSet> fileSetByModule = new(StringComparer.Ordinal);
        List<IExternalAction> actions = EmitActions(
            engineRoot,
            target,
            toolchain,
            targetModules,
            fileSetByModule,
            manifestOutputDir,
            cancellationToken,
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
        EmitManifest(
            engineRoot,
            target,
            engineVersion,
            targetModules,
            fileSetByModule,
            manifestOutputDir);

        // Audit fix C11 (write-manifest mode): when ManifestOnly is set,
        // skip the action graph + executor pump entirely. The manifest
        // is the only deliverable.
        if (options.ManifestOnly)
        {
            sw.Stop();
            return new BuildResult(
                Success: true,
                ActionsRan: 0,
                ActionsCached: 0,
                ActionsFailed: 0,
                FirstFailingExitCode: 0,
                Duration: sw.Elapsed,
                Actions: emittedForReport);
        }

        // ---- 10. Build the action graph --------------------------------
        Simgenics.XPact.XBT.ActionGraph.ActionGraph graph = new(actions);
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        // Audit fix R6-C6: proactively flag any produced / prerequisite
        // path that exceeds the Windows MAX_PATH limit (260 chars).
        // No-op on Linux / macOS. The check throws XBTException with
        // exit code 71 (LinkFailed) and a diagnostic naming the
        // offending path and its action, instead of letting cl.exe /
        // link.exe surface a cryptic "cannot open file" error deep
        // inside the compile.
        graph.CheckPathLengths();

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

    /// <summary>
    /// Audit fix M14: per-platform default architecture used when no
    /// explicit <c>-Architecture=</c> override is supplied. Windows
    /// and Linux default to <c>x86_64</c>; Android defaults to
    /// <c>aarch64</c> (the dominant Android ABI; Phase 2 may add
    /// <c>armeabi-v7a</c> / <c>x86_64</c> as additional architectures).
    /// </summary>
    private static string DefaultArchitectureForPlatform(Platform platform)
    {
        return platform switch
        {
            Platform.Android => "aarch64",
            _ => "x86_64",
        };
    }

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

    /// <summary>
    /// Audit fix C7: validate every module's
    /// <see cref="ModuleRules.MinimumToolchainVersion"/> against the
    /// live <see cref="XToolChain.ToolchainVersion"/>. Mismatches batch
    /// into a single diagnostic and fail with exit 23
    /// (<c>EngineOrToolchainVersionMismatch</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version comparison uses the existing
    /// <see cref="SemanticVersion"/> parser. A module that declares a
    /// malformed semver, or whose minimum exceeds the live toolchain,
    /// is recorded as an offender. Empty / null
    /// <c>MinimumToolchainVersion</c> means "no constraint" and is
    /// skipped.
    /// </para>
    /// </remarks>
    private static void ValidateMinimumToolchainVersion(
        ModuleCatalog modules,
        XToolChain toolchain)
    {
        string liveVersionString = toolchain.ToolchainVersion;
        if (!SemanticVersion.TryParse(liveVersionString, out SemanticVersion liveVersion))
        {
            Logger.Warning(
                $"Toolchain version '{liveVersionString}' is not in MAJOR.MINOR.PATCH form; " +
                "skipping per-module MinimumToolchainVersion checks.",
                new DiagnosticContext { Action = "validate-toolchain-version" });
            return;
        }

        List<string> offenders = new();
        foreach (ModuleRecord rec in modules.Modules)
        {
            string? min = rec.Rules.MinimumToolchainVersion;
            if (string.IsNullOrEmpty(min))
            {
                continue;
            }
            if (!SemanticVersion.TryParse(min, out SemanticVersion minVersion))
            {
                offenders.Add(
                    $"Module '{rec.Rules.Name}' declares malformed " +
                    $"MinimumToolchainVersion = '{min}'. Expected semver " +
                    "in MAJOR.MINOR.PATCH form.");
                continue;
            }
            if (liveVersion.CompareTo(minVersion) < 0)
            {
                offenders.Add(
                    $"Module '{rec.Rules.Name}' requires toolchain " +
                    $">= {minVersion} but the active toolchain is " +
                    $"{liveVersion}.");
            }
        }

        if (offenders.Count > 0)
        {
            StringBuilder sb = new();
            sb.AppendLine("MinimumToolchainVersion check failed for the following module(s):");
            foreach (string offender in offenders)
            {
                sb.AppendLine("  " + offender);
            }
            throw new XBTException(sb.ToString().TrimEnd(), exitCode: 23);
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
                // Audit fix M9: exit 10 was wrong here -- exit 10 is for
                // CLI argument errors, but the platform value reached
                // ConstructToolchain after Parse already accepted it.
                // Mapping to exit 23 (EngineOrToolchainVersionMismatch)
                // mirrors the "no compatible toolchain" failures on
                // platforms that DO parse but lack a backend.
                throw new XBTException(
                    $"Unsupported platform: {target.Platform}. " +
                    "No XToolChain backend is registered for this platform.",
                    exitCode: 23);
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
            // Audit fix C8: test modules are included ONLY when
            // Configuration == Test. Previous logic included them in
            // every Editor build regardless of configuration, including
            // Shipping Editor -- a leak that put test-only code into
            // shipped binaries. The stricter interpretation matches the
            // spec: bIsTestModule belongs in test builds, full stop.
            if (rec.Rules.bIsTestModule && target.Configuration != BuildConfiguration.Test)
            {
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
        string manifestOutputDir,
        CancellationToken cancellationToken,
        out IReadOnlyList<IExternalAction> emittedForReport)
    {
        // Audit fix C9: the cancellation token is now plumbed through
        // every per-module and per-source iteration so a long emit pass
        // honours Ctrl-C / IDE-cancellation requests promptly.
        cancellationToken.ThrowIfCancellationRequested();

        List<IExternalAction> actions = new();
        List<FileItem> allSourceFiles = new();
        List<IExternalAction> reportActions = new();

        // Phase 1f (XHT wiring): the manifest path the eventual XHT
        // subprocesses read. The manifest itself is written by
        // EmitManifest at step 9.5 (before the executor dispatches), so
        // the path is valid by the time XHT actually runs even though
        // it does not yet exist on disk at action-emission time. We
        // pre-resolve the XHT executable too: a missing exe means the
        // current build does NOT have an XHT binary handy and the
        // reflection-enabled modules will fail at run time -- that is
        // acceptable for Phase 1f because the build still proceeds with
        // every module that has no reflection markers. Modules that DO
        // have markers will fail loudly at executor-dispatch time.
        string manifestJsonPath = Path.Combine(manifestOutputDir, "Manifest.json");
        string? xhtExePath = TryResolveXhtExecutable(engineRoot, target.Platform);

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
            // Audit fix C9: cancellation point at every module iteration.
            cancellationToken.ThrowIfCancellationRequested();

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

            // Phase 1f: emit XHT ParseHeadersAction + EmitReflectionAction
            // for any module that uses reflection markers (XCLASS, XSTRUCT,
            // XENUM, etc. in .h files; [XClass], [XStruct], etc.
            // attributes in .cs files). Per XBT.html Section 9.4 + XHT.html
            // Section 9: ParseHeadersAction is one per module (consumes
            // headers + .cs sources; produces an opaque token cache);
            // EmitReflectionAction is one per module (consumes the same
            // inputs + the parse-stage token cache; produces .gen.h /
            // .gen.cpp / .init.gen.cpp / .gen.manifest pre-discovered
            // through the XBT-side XhtOutputNaming mirror).
            //
            // Modules without markers skip XHT actions entirely: the
            // detection heuristic scans the small token vocabulary listed
            // in Toolchain Contract Rev 13.6 Section 11.2.
            if (xhtExePath is not null && HasReflectionMarkers(headerFiles, csharpFiles))
            {
                IReadOnlyList<string> reflectionHeaderRelativePaths =
                    BuildReflectionHeaderPaths(moduleDir, headerFiles);
                IReadOnlyList<FileItem> reflectionInputs = MergeReflectionInputs(headerFiles, csharpFiles);

                // ParseHeadersAction first.
                ParseHeadersAction parseAction = new(
                    moduleName: module.Name,
                    xhtExecutablePath: xhtExePath,
                    manifestJsonPath: manifestJsonPath,
                    outputDirectory: moduleObjDir,
                    sourceFiles: reflectionInputs);
                actions.Add(parseAction);
                reportActions.Add(parseAction);

                // EmitReflectionAction depends on the parse action's
                // produced token cache plus the same source set. Merge
                // the tokens.bin into the prerequisite list so the
                // action graph orders the two actions correctly.
                List<FileItem> emitPrereqs = new(reflectionInputs.Count + parseAction.ProducedItems.Count);
                emitPrereqs.AddRange(reflectionInputs);
                emitPrereqs.AddRange(parseAction.ProducedItems);

                EmitReflectionAction emitAction = new(
                    moduleName: module.Name,
                    xhtExecutablePath: xhtExePath,
                    manifestJsonPath: manifestJsonPath,
                    outputDirectory: moduleObjDir,
                    reflectionInputs: emitPrereqs,
                    reflectionHeaderRelativePaths: reflectionHeaderRelativePaths,
                    generatedCppFilenameBase: module.Name);
                actions.Add(emitAction);
                reportActions.Add(emitAction);
            }

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
                // Audit fix C9: real cancellation token replaces the
                // previous no-op stub. Honored at every per-source
                // iteration so a long compile-action emit pass yields
                // promptly on Ctrl-C / IDE cancellation.
                cancellationToken.ThrowIfCancellationRequested();
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

            // Audit fix M7: lift module-declared PreBuildHooks /
            // PostBuildHooks into the action graph. Each hook's
            // CreateAction(context) returns an object the caller must
            // cast to IExternalAction. A non-conforming return (cast
            // failure) or a hook with no OutputFiles fails the build
            // with exit 81 (BuildHookOutputMismatch) per Contract
            // Section 13.
            string moduleIntermediate = Path.Combine(
                engineRoot, "Intermediate", "Build", target.Name,
                target.Configuration.ToString(), target.Platform.ToString(), module.Name);
            string moduleOutputDir = Path.Combine(engineRoot, "Binaries", target.Platform.ToString());
            BuildHookContext hookContext = new(
                Module: module,
                Target: new ReadOnlyTargetRules(target),
                IntermediateDir: moduleIntermediate,
                OutputDir: moduleOutputDir,
                Cancellation: cancellationToken);
            LiftHooks(module.PreBuildHooks, module.Name, "pre", hookContext, actions, reportActions);
            LiftHooks(module.PostBuildHooks, module.Name, "post", hookContext, actions, reportActions);
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
    /// Audit fix M7: lift one module's pre/post build hooks into the
    /// action graph. Per Contract Section 9.5, every hook's
    /// CreateAction(context) MUST return an
    /// <see cref="IExternalAction"/> with non-empty
    /// <see cref="IExternalAction.ProducedItems"/>. Failing either
    /// invariant throws <see cref="XBTException"/> with exit 81
    /// (<c>BuildHookOutputMismatch</c>).
    /// </summary>
    private static void LiftHooks(
        IReadOnlyList<IBuildHook> hooks,
        string moduleName,
        string hookKind,
        BuildHookContext context,
        List<IExternalAction> actions,
        List<IExternalAction> reportActions)
    {
        if (hooks is null || hooks.Count == 0)
        {
            return;
        }

        for (int i = 0; i < hooks.Count; i++)
        {
            IBuildHook hook = hooks[i];
            object created;
            try
            {
                created = hook.CreateAction(context);
            }
            catch (Exception ex)
            {
                throw new XBTException(
                    $"Module '{moduleName}': {hookKind}-build hook[{i}] " +
                    $"({hook.GetType().FullName}) CreateAction threw " +
                    $"{ex.GetType().Name}: {ex.Message}",
                    exitCode: 81);
            }

            if (created is not IExternalAction asAction)
            {
                throw new XBTException(
                    $"Module '{moduleName}': {hookKind}-build hook[{i}] " +
                    $"({hook.GetType().FullName}) CreateAction returned " +
                    $"{created?.GetType().FullName ?? "null"}, which is not an IExternalAction.",
                    exitCode: 81);
            }

            if (asAction.ProducedItems is null || asAction.ProducedItems.Count == 0)
            {
                throw new XBTException(
                    $"Module '{moduleName}': {hookKind}-build hook[{i}] " +
                    $"({hook.GetType().FullName}) returned an action with no " +
                    "ProducedItems. Hooks must declare every file they write " +
                    "so the action graph can track outputs.",
                    exitCode: 81);
            }

            actions.Add(asAction);
            reportActions.Add(asAction);
        }
    }

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
            //
            // Audit fix C2: declared order preserved (Public then Private,
            // in declaration order). The compiler emits flags in declared
            // order; sorting the manifest would create a two-way drift
            // where the manifest claimed one order while the compile
            // command used another, defeating the manifest's purpose as
            // an auditable record of what the compiler saw.
            List<string> includePaths = new();
            includePaths.AddRange(m.PublicIncludePaths);
            includePaths.AddRange(m.PrivateIncludePaths);

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
            // Audit fix M8: per-module EngineVersionCompat is sourced
            // from the ModuleRules field when set; otherwise defaults
            // to "*" (any-version-compatible). Phase 1 TOML/Roslyn
            // parsers do not author this field yet -- the default flows
            // through unchanged for now.
            string engineVersionCompat = string.IsNullOrEmpty(m.EngineVersionCompat)
                ? "*"
                : m.EngineVersionCompat;

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
                EngineVersionCompat: engineVersionCompat,
                SimdLevel: m.SimdLevel,
                PCHUsage: m.PCHUsage,
                ExcludeFromSharedPCH: m.bExcludeFromSharedPCH,
                AllowHotReload: m.bAllowHotReload,
                IsTestModule: m.bIsTestModule,
                DeprecationMessage: m.DeprecationMessage,
                MinimumToolchainVersion: m.MinimumToolchainVersion);
            manifestModules.Add(manifestModule);
        }

        // Audit fix C1/C10 + Rev 13 reconciliation: per-target fields
        // (architecture / ABI envelope / SimPath + station policy) live
        // under the nested Manifest.Target record so the JSON shape
        // mirrors the FBS TargetInfo grouping. The Phase 1 ABI defaults
        // (GCRootABI, ExceptionABI, ManglingScheme) feed Manifest.Target.
        Manifest.TargetInfo targetInfo = new(
            Name: target.Name,
            Type: target.TargetType,
            Platform: target.Platform,
            Configuration: target.Configuration,
            Architecture: target.Architecture,
            GCRootABI: ManifestFbs.DefaultGCRootABI,
            ExceptionABI: ManifestFbs.DefaultExceptionABI,
            ManglingScheme: ManifestFbs.DefaultManglingScheme,
            FipsMode: target.FipsMode,
            SimPathConservativeRootsAllowed: target.SimPathConservativeRootsAllowed,
            SimdLevelDefault: target.SimdLevelDefault,
            StationRole: target.StationRole);

        Manifest.Manifest manifest = new(
            ContractVersion: ContractVersion.Current,
            EngineVersion: engineVersion.ToString(),
            Target: targetInfo,
            RootLocalPath: NormalisePathForward(engineRoot),
            ExternalDependenciesFile: null,
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

    /// <summary>
    /// Phase 1f reflection-marker detection heuristic per Toolchain
    /// Contract Rev 13.6 Section 11.2 (marker macro vocabulary +
    /// attribute equivalents). A module that uses any reflection marker
    /// gets the XHT action pair injected into the build graph; a module
    /// without markers skips XHT entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The detection is a literal-substring scan: we look for either
    /// <c>XCLASS(</c>, <c>XSTRUCT(</c>, etc. in the header bytes or
    /// <c>[XClass]</c>, <c>[XStruct]</c>, etc. in the C# bytes. The
    /// scan is byte-substring not lexer-aware (a marker in a comment
    /// would still match). This is consistent with UHT's own
    /// pre-discovery heuristic and is intentionally permissive: a false
    /// positive emits an XHT action whose run is harmless (the manifest
    /// is empty if no real markers exist); a false negative would mean
    /// missing reflection metadata at run time, which is the worse
    /// failure mode. Phase 2 will replace the heuristic with a proper
    /// XHT preflight pass.
    /// </para>
    /// <para>
    /// Files that cannot be read (locked, deleted between enumeration
    /// and scan, etc.) are conservatively treated as containing a
    /// marker so XHT is invoked rather than risk skipping a module
    /// that needs it.
    /// </para>
    /// </remarks>
    internal static bool HasReflectionMarkers(
        IReadOnlyList<FileItem> headerFiles,
        IReadOnlyList<FileItem> csharpFiles)
    {
        foreach (FileItem h in headerFiles)
        {
            if (FileContainsAny(h.FullPath, s_cppReflectionMarkers))
            {
                return true;
            }
        }
        foreach (FileItem cs in csharpFiles)
        {
            if (FileContainsAny(cs.FullPath, s_csharpReflectionMarkers))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// C++ reflection-marker vocabulary per Toolchain Contract Rev 13.6
    /// Section 11.2 (the parenthesis is part of the marker so a textual
    /// identifier reference -- e.g. a docstring naming the marker -- does
    /// not match).
    /// </summary>
    private static readonly string[] s_cppReflectionMarkers = new[]
    {
        "XCLASS(",
        "XSTRUCT(",
        "XENUM(",
        "XINTERFACE(",
        "XFUNCTION(",
        "XPROPERTY(",
        "XDELEGATE(",
    };

    /// <summary>
    /// C# reflection-attribute vocabulary. Square brackets mirror the C#
    /// attribute syntax; a passing-mention of <c>XClass</c> in
    /// commentary text does not match.
    /// </summary>
    private static readonly string[] s_csharpReflectionMarkers = new[]
    {
        "[XClass",
        "[XStruct",
        "[XEnum",
        "[XInterface",
        "[XFunction",
        "[XProperty",
        "[XDelegate",
    };

    /// <summary>
    /// Read up to 1 MiB of a file's bytes and check whether any of the
    /// supplied literal substrings appears in the UTF-8 text. Files
    /// that cannot be read return true conservatively (so a reflection
    /// module is not silently skipped). The 1 MiB cap bounds the scan
    /// cost for pathological large generated files; reflection markers
    /// always appear within the first few hundred bytes of a real
    /// source file (next to the type declaration).
    /// </summary>
    private static bool FileContainsAny(string path, IReadOnlyList<string> needles)
    {
        const int MaxBytes = 1 * 1024 * 1024;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int toRead = (int)Math.Min(fs.Length, MaxBytes);
            byte[] buffer = new byte[toRead];
            int read = fs.Read(buffer, 0, toRead);
            string text = Encoding.UTF8.GetString(buffer, 0, read);
            foreach (string needle in needles)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Build the sorted list of reflection-header relative paths the
    /// <see cref="EmitReflectionAction"/> consumes for pre-discovery.
    /// </summary>
    /// <remarks>
    /// XHT's per-header output filenames depend only on the header's
    /// stem (filename without extension); the absolute path is passed
    /// here so the action's hash captures the full source identity. The
    /// caller's <paramref name="moduleDir"/> is the relative-path
    /// anchor; paths inside the module are normalised to forward
    /// slashes so Windows + Linux produce identical relative-paths.
    /// </remarks>
    internal static IReadOnlyList<string> BuildReflectionHeaderPaths(
        string moduleDir,
        IReadOnlyList<FileItem> headerFiles)
    {
        List<string> paths = new(headerFiles.Count);
        string moduleDirFull = Path.GetFullPath(moduleDir);
        foreach (FileItem h in headerFiles)
        {
            string rel = Path.GetRelativePath(moduleDirFull, h.FullPath).Replace('\\', '/');
            paths.Add(rel);
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// Merge the per-module header set and C# source set into the union
    /// the XHT actions consume as <c>PrerequisiteItems</c>. Sorted +
    /// deduped ordinal so the action's hash is stable.
    /// </summary>
    internal static IReadOnlyList<FileItem> MergeReflectionInputs(
        IReadOnlyList<FileItem> headerFiles,
        IReadOnlyList<FileItem> csharpFiles)
    {
        List<FileItem> merged = new(headerFiles.Count + csharpFiles.Count);
        merged.AddRange(headerFiles);
        merged.AddRange(csharpFiles);
        merged.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        if (merged.Count > 1)
        {
            List<FileItem> deduped = new(merged.Count);
            string? last = null;
            foreach (FileItem fi in merged)
            {
                if (!string.Equals(last, fi.FullPath, StringComparison.Ordinal))
                {
                    deduped.Add(fi);
                    last = fi.FullPath;
                }
            }
            merged = deduped;
        }
        return merged;
    }

    /// <summary>
    /// Resolve the XHT executable path for the given platform. Returns
    /// null when no XHT binary is present at the expected location; the
    /// caller decides whether absence is fatal (a reflection-enabled
    /// module without XHT) or harmless (no reflection-enabled modules
    /// in the build).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout per <c>/Documents/XBT.html</c> Section 18.1 + Toolchain
    /// Contract Section 10.1: XBT + XHT both live under
    /// <c>&lt;EngineRoot&gt;/Binaries/&lt;Platform&gt;/</c>. The Phase
    /// 1f resolver is filesystem-based; Phase 2 will resolve through a
    /// content-addressable lookup (XPactBuildAccelerator).
    /// </para>
    /// </remarks>
    internal static string? TryResolveXhtExecutable(string engineRoot, Platform platform)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string exeName = isWindows ? "xht.exe" : "xht";
        string platformDir = platform.ToString();
        string candidate = Path.Combine(engineRoot, "Binaries", platformDir, exeName);
        if (File.Exists(candidate))
        {
            return candidate;
        }
        // Fall-back: some test fixtures put the engine root one level up;
        // try the parent's Binaries directory too.
        string? parent = Directory.GetParent(engineRoot)?.FullName;
        if (parent is not null)
        {
            string alt = Path.Combine(parent, "Binaries", platformDir, exeName);
            if (File.Exists(alt))
            {
                return alt;
            }
        }
        return null;
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
    /// <summary>
    /// Architecture override, e.g. <c>"x86_64"</c> or <c>"aarch64"</c>.
    /// When null, the value from <see cref="TargetRules.Architecture"/>
    /// is used (defaults to <c>"x86_64"</c>). Per audit fix M14: needed
    /// for Android cross-compile (aarch64 / armeabi-v7a / x86_64 hosts).
    /// </summary>
    public string? Architecture { get; init; }

    /// <summary>
    /// Audit fix C11: when true, BuildMode emits the manifest and then
    /// stops -- no action graph build, no executor pump, no compiles.
    /// Used by the dedicated <c>write-manifest</c> mode for IDE
    /// integration ("give me a manifest I can read" without paying for
    /// a full build).
    /// </summary>
    public bool ManifestOnly { get; init; }

    /// <summary>
    /// Round-6 final-cleanup M2: optional output directory for the
    /// manifest emission. When null the manifest is written to the
    /// default
    /// <c>Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/Manifest.json</c>
    /// path. When set, <c>Manifest.json</c> and <c>Manifest.fbs.bin</c>
    /// are emitted into the specified directory instead (the directory
    /// is created if missing). Used by <c>write-manifest -Out=&lt;dir&gt;</c>
    /// for IDE wrappers that want the manifest written to a known
    /// location outside the build tree.
    /// </summary>
    public string? ManifestOutputDirectory { get; init; }

    /// <summary>
    /// Audit fix R6-C7: when true, BuildMode fails immediately (exit 1)
    /// if another XBT build is in progress for the same (engineRoot,
    /// target, config, platform) tuple, instead of blocking on the
    /// build mutex until the sibling releases. Wrapper / CI usage where
    /// the caller does not want an indefinite wait sets this.
    /// </summary>
    public bool NoMutexWait { get; init; }

    /// <summary>
    /// Audit fix M14: the set of CLI flags we accept-and-warn rather
    /// than reject. These are documented spec flags whose action-graph
    /// integration has not yet landed; ignoring them lets a forward-
    /// compatible IDE wrapper pass them in without breaking the build.
    /// </summary>
    private static readonly string[] s_acceptedButUnimplemented = new[]
    {
        "-NoCompile",
        "-DryRun",
        "-WarningsAsErrors",
        "-Modules",
        "-Workers",
        "-LiveCoding",
    };

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
        string? architecture = null;
        bool noMutexWait = false;

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
            else if (arg.StartsWith("-EngineRoot=", StringComparison.OrdinalIgnoreCase))
            {
                // Round-6 final-cleanup M2: spec-canonical name per
                // XBT.html Section 1.2.
                engineRoot = arg["-EngineRoot=".Length..];
            }
            else if (arg.StartsWith("-Engine=", StringComparison.OrdinalIgnoreCase))
            {
                // Legacy alias preserved for backwards compatibility.
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
            else if (arg.StartsWith("-Architecture=", StringComparison.OrdinalIgnoreCase))
            {
                // Audit fix M14: -Architecture= is wired through to
                // TargetRules.Architecture, surfacing into the manifest.
                architecture = arg["-Architecture=".Length..];
            }
            else if (arg.Equals("-NoMutexWait", StringComparison.OrdinalIgnoreCase))
            {
                // Audit fix R6-C7: opt out of blocking on the build
                // mutex. Concurrent xbt builds for the same target +
                // config + platform fail immediately instead of waiting.
                noMutexWait = true;
            }
            else if (IsAcceptedButUnimplemented(arg))
            {
                // Audit fix M14: accept-and-warn for forward-compat spec
                // flags not yet implemented. Lets a wrapper pass them
                // in without failing the parse.
                Logger.Warning(
                    $"CLI flag '{arg}' is accepted but not yet implemented in Phase 1; ignoring.",
                    new DiagnosticContext { Action = "build-options-parse" });
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
            Architecture = architecture,
            NoMutexWait = noMutexWait,
        };
    }

    /// <summary>
    /// Audit fix M14: helper for accept-and-warn behaviour on spec
    /// flags whose action-graph integration is deferred. Matches
    /// <c>-Foo=</c> as well as <c>-Foo</c> bare.
    /// </summary>
    private static bool IsAcceptedButUnimplemented(string arg)
    {
        foreach (string prefix in s_acceptedButUnimplemented)
        {
            if (arg.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (arg.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
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
