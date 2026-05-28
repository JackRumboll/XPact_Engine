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

        // Audit fix R7-M5: the mutex's scope is now narrowed to wrap
        // ONLY the brief ActionHistory.LoadFromDisk window at startup
        // and ActionHistory.Save window at shutdown. Action execution
        // runs without the mutex held; per-action atomic-rename +
        // per-action temp-file naming + the orphan-temp-file sweep
        // provide cross-process safety for the action output paths,
        // and ActionHistory.bin's torn-write tolerance (audit M15)
        // protects the load path. Two concurrent
        // `xbt build -Target=Editor` invocations now proceed in
        // parallel except for the load/save windows -- a typical
        // load + save together is under 50 ms on a populated tree.
        //
        // The CoordinatedActionHistory wrapper below acquires the
        // mutex at construction, calls LoadFromDisk, releases the
        // mutex, then acquires again at Save() time.
        using CoordinatedActionHistory historyCoordinator = new(
            mutexName,
            options.NoMutexWait,
            cancellationToken);

        return RunInternalLocked(
            options,
            engineRoot,
            studioRoot,
            projectRoot,
            projectRoots,
            sw,
            historyCoordinator,
            cancellationToken);
    }

    /// <summary>
    /// Audit fix R7-M5: narrow-scope mutex helper. Wraps
    /// <see cref="ActionHistory"/> so the build mutex is held only
    /// during <see cref="ActionHistory.LoadFromDisk"/> (via the
    /// constructor) and <see cref="ActionHistory.Save"/> (via
    /// <see cref="SaveAndRelease"/>). Action execution between the two
    /// windows runs without the mutex, so two concurrent builds
    /// against the same target can proceed in parallel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-action atomic-rename + per-action temp-file naming + the
    /// orphan-sweep provide cross-process safety for the action
    /// output paths -- two builds writing the same compile output go
    /// through distinct temp paths (<c>&lt;output&gt;.tmp.&lt;pid&gt;.&lt;actionid&gt;</c>)
    /// and rename atomically; the last writer wins (which is the
    /// correct semantics since both produce byte-identical output
    /// given the same command + inputs).
    /// </para>
    /// </remarks>
    private sealed class CoordinatedActionHistory : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly string _mutexName;
        public ActionHistory? History { get; private set; }
        public bool RecoveredFromAbandon { get; private set; }
        private bool _saved;

        public CoordinatedActionHistory(string mutexName, bool noMutexWait, CancellationToken cancellationToken)
        {
            _mutexName = mutexName;
            _mutex = new Mutex(initiallyOwned: false, name: mutexName, out _);
            _ = AcquireWithRecovery(noMutexWait, cancellationToken);
        }

        private bool AcquireWithRecovery(bool noMutexWait, CancellationToken cancellationToken)
        {
            try
            {
                if (noMutexWait ? _mutex.WaitOne(TimeSpan.Zero) : WaitMutexWithProgressLog(_mutex, cancellationToken))
                {
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                Logger.Warning(
                    "Previous XBT build process exited without releasing the build mutex " +
                    $"({_mutexName}). Proceeding -- ActionHistory.bin is self-healing on a " +
                    "torn read; widened orphan sweep will reap any temp files the crashed " +
                    "holder left in the Intermediate/ tree.",
                    new DiagnosticContext { Action = "build-mutex" });
                RecoveredFromAbandon = true;
                return true;
            }

            throw new XBTException(
                "Another XBT build is already in progress against the same engine + target + " +
                "config + platform + user identity. Wait for it to finish, or omit " +
                "-NoMutexWait to block until it releases.",
                exitCode: 1);
        }

        /// <summary>
        /// Open the action history (LoadFromDisk runs under the mutex
        /// already acquired in the constructor), then release the
        /// mutex so action execution can run unobstructed.
        /// </summary>
        public void OpenAndUnlock(string targetDir, BuildConfiguration configuration)
        {
            History = ActionHistory.Open(targetDir, configuration);
            // Release immediately -- action execution does not need
            // the mutex. The mutex is re-acquired in SaveAndRelease
            // to serialise the post-build write.
            _mutex.ReleaseMutex();
        }

        /// <summary>
        /// Re-acquire the mutex, save the history, release again.
        /// Safe to call once. Failure to save is logged but never
        /// throws -- best-effort persistence per audit M15.
        /// </summary>
        public void SaveAndRelease(string targetDir)
        {
            if (_saved || History is null)
            {
                return;
            }
            _saved = true;
            // Re-acquire briefly so a sibling builder doesn't race us.
            bool reacquired = false;
            try
            {
                try
                {
                    reacquired = _mutex.WaitOne(TimeSpan.FromSeconds(30));
                }
                catch (AbandonedMutexException)
                {
                    // A sibling crashed while we were running. Fine --
                    // we still own the lock; proceed.
                    reacquired = true;
                }
                if (!reacquired)
                {
                    Logger.Warning(
                        $"ActionHistory.Save: failed to re-acquire build mutex ({_mutexName}) " +
                        "within 30 s. Skipping save -- the next build will tolerate the staleness.",
                        new DiagnosticContext { Action = "save-history" });
                    return;
                }
                try
                {
                    History.Save();
                }
                catch (IOException ex)
                {
                    Logger.Warning(
                        $"Failed to save ActionHistory at {targetDir}: {ex.Message}",
                        new DiagnosticContext { Action = "save-history" });
                }
            }
            finally
            {
                if (reacquired)
                {
                    try { _mutex.ReleaseMutex(); }
                    catch (ApplicationException) { /* not owned -- best effort */ }
                }
            }
        }

        public void Dispose()
        {
            // The mutex is disposed via 'using' on the outer Mutex
            // instance; we don't dispose here because SaveAndRelease
            // may still need to re-acquire. The outer 'using' in
            // RunBuild's caller pattern handles disposal via the
            // 'using' on the field above.
            _mutex.Dispose();
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
    /// characters of a BLAKE3 of <c>(userIdentity, engineRoot, target,
    /// config, platform)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hashing the identity avoids the OS-name validity rules (Win32
    /// mutex names cannot contain backslashes outside of the
    /// <c>Global\</c> / <c>Local\</c> prefix; Linux ipc names have
    /// their own restrictions) and gives a deterministic name that two
    /// concurrent invocations from the same engine root collide on.
    /// </para>
    /// <para>
    /// Audit fix R7-M4: the user identity (Windows SID or POSIX euid)
    /// is folded into the hash so two distinct users on the same
    /// machine building the same target never collide on the mutex.
    /// Prior to R7-M4 a developer build on a shared Windows CI host
    /// could block another user's identical build for the wait
    /// timeout, since the mutex name only encoded (engineRoot, target,
    /// config, platform).
    /// </para>
    /// <para>
    /// Multi-checkout-on-same-volume: two checkouts at distinct paths
    /// (e.g. <c>C:\repo-main\XPact_Engine</c> and
    /// <c>C:\repo-feature\XPact_Engine</c>) produce distinct
    /// canonical roots and therefore distinct mutex names; concurrent
    /// builds across checkouts proceed independently. Same-checkout
    /// builds with the same target/config/platform serialize on the
    /// mutex as designed.
    /// </para>
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

        // Audit fix R7-M4: include user identity so multi-user hosts
        // don't collide. On Windows we use the user's SID via
        // WindowsIdentity; on Linux/Mac we use the effective UID via
        // Environment.UserName (the SID is Windows-only and we don't
        // want to P/Invoke geteuid here; UserName is "best-effort
        // identity" -- collisions across users sharing a username
        // would be a misconfiguration, not a security issue, since
        // the mutex governs cooperation between same-target builds,
        // not authorization). The hash bucketing means the exact
        // identity form doesn't matter -- only that two different
        // users produce two different identity strings.
        string userIdentity = ComputeUserIdentity();

        string identity = $"{userIdentity}|{canonicalRoot}|{targetName}|{configuration}|{platform}";
        IoHash hash = IoHash.Compute(System.Text.Encoding.UTF8.GetBytes(identity));
        // First 32 hex chars (128 bits) of the BLAKE3 digest -- a
        // mutex-name collision over the engine's lifetime is
        // astronomically unlikely. The "XBT_Build_" prefix is
        // descriptive so an operator inspecting tasklist / lsof can
        // identify the mutex's owner.
        return "XBT_Build_" + hash.ToString().Substring(0, 32);
    }

    /// <summary>
    /// Audit fix R7-M4: produce a stable per-user identity string used
    /// to disambiguate the build mutex name across users on the same
    /// machine. On Windows this is the user's SID; on POSIX this is
    /// the user name (Environment.UserName resolves to the effective
    /// uid's account name).
    /// </summary>
    private static string ComputeUserIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                System.Security.Principal.WindowsIdentity? id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                string? sid = id?.User?.Value;
                if (!string.IsNullOrEmpty(sid))
                {
                    return "sid:" + sid;
                }
            }
            catch (System.Security.SecurityException)
            {
                // Permission-denied at SID lookup is rare but can
                // happen on restricted hosts; fall through to the
                // username path so we still produce a stable identity.
            }
        }
        // POSIX or Windows SID lookup failed: use UserName.
        string name = Environment.UserName;
        return "user:" + name;
    }

    private static BuildResult RunInternalLocked(
        BuildOptions options,
        string engineRoot,
        string? studioRoot,
        string? projectRoot,
        IReadOnlyList<string> projectRoots,
        Stopwatch sw,
        CoordinatedActionHistory historyCoordinator,
        CancellationToken cancellationToken)
    {
        // Audit fix R7-M5: surface the abandon-recovery flag locally
        // so existing call sites that reference it (the widened
        // orphan sweep below) keep working under the new mutex
        // coordinator wrapper.
        bool mutexRecoveredFromAbandon = historyCoordinator.RecoveredFromAbandon;

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
            // Audit fix R8-C1: thread the Android NDK API level through
            // so XClangToolChain on Android can emit the correct
            // --target=<arch>-linux-android<API> flag. The default 21
            // covers Android 5.0+ (NDK r26's minimum 64-bit target);
            // higher floors are an opt-in via -AndroidApiLevel=.
            AndroidApiLevel = options.AndroidApiLevel ?? 21,
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

        // Audit fix R7-C5: open the reflection-marker cache before
        // action emit so HasReflectionMarkers can short-circuit on
        // unchanged files. The cache lives at
        // Intermediate/Build/<Target>/<Config>/ReflectionMarkerCache.bin
        // alongside ActionHistory.bin.
        string markerCacheRoot = Path.Combine(engineRoot, "Intermediate", "Build", target.Name);
        ReflectionMarkerCache markerCache = ReflectionMarkerCache.Open(markerCacheRoot, target.Configuration);

        Dictionary<string, ModuleFileSet> fileSetByModule = new(StringComparer.Ordinal);
        List<IExternalAction> actions = EmitActions(
            engineRoot,
            target,
            toolchain,
            targetModules,
            fileSetByModule,
            manifestOutputDir,
            markerCache,
            cancellationToken,
            out IReadOnlyList<IExternalAction> emittedForReport,
            out IReadOnlyList<SimPathArtefact> pendingSimPathArtefacts);

        // Audit fix R7-C5: persist the marker cache so the next build
        // benefits from the scan results. Best-effort; failure to save
        // is logged but does not abort the build.
        try
        {
            markerCache.Save();
        }
        catch (IOException ex)
        {
            Logger.Warning(
                $"Failed to save reflection-marker cache: {ex.Message}",
                new DiagnosticContext { Action = "marker-cache-save" });
        }

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
        // Audit fix R7-M5: ActionHistory.Open's LoadFromDisk runs
        // under the build mutex (acquired in the coordinator). The
        // mutex is released immediately after the load returns so
        // action execution proceeds without blocking sibling builds;
        // re-acquired briefly in SaveAndRelease at shutdown.
        string intermediateRoot = Path.Combine(engineRoot, "Intermediate", "Build", target.Name);
        Directory.CreateDirectory(intermediateRoot);
        historyCoordinator.OpenAndUnlock(intermediateRoot, target.Configuration);
        ActionHistory history = historyCoordinator.History!;

        // ---- 11.1 Open CppDependencyCache (audit fix R3-C1) -----------
        // The dependency cache lives alongside ActionHistory.bin and
        // shares the same lifecycle. It records the transitive-header
        // set discovered post-compile from Clang's .d file or MSVC's
        // /sourceDependencies JSON so the next build's IsActionOutdated
        // correctly invalidates when a transitively-included header
        // (not in PrerequisiteItems) is edited.
        //
        // The Open() call inherits the build mutex's just-released
        // window because the load happens under no concurrency
        // pressure (we are still inside BuildMode startup). The Save()
        // at shutdown does the same atomic-rename dance as
        // ActionHistory.Save and is similarly self-healing on a torn
        // write.
        CppDependencyCache cppDependencyCache = CppDependencyCache.Open(
            intermediateRoot,
            target.Configuration);

        // ---- 11.5 Orphan temp-file sweep (XBT.html Section 6.4) --------
        // Sweep the entire intermediate-build tree (not just this
        // target's subtree) so a sibling crashed XBT run targeting a
        // different config gets cleaned up too. Best-effort: failures
        // are logged but do not abort the build.
        //
        // Audit fix R7-C7: when we recovered from an abandoned mutex
        // (the prior XBT holder died mid-run), widen the sweep to the
        // entire Intermediate/ tree. The crashed holder may have been
        // writing to ProjectFiles/, ManifestEmit/, or any other
        // intermediate-tree subdirectory; the narrow Intermediate/Build
        // sweep would miss those temps and they would accumulate.
        try
        {
            string sweepRoot = mutexRecoveredFromAbandon
                ? Path.Combine(engineRoot, "Intermediate")
                : Path.Combine(engineRoot, "Intermediate", "Build");
            ParallelExecutor.SweepOrphanedTempFiles(sweepRoot);
        }
        catch (Exception ex)
        {
            Logger.Warning(
                $"Orphan temp-file sweep failed: {ex.GetType().Name}: {ex.Message}",
                new DiagnosticContext { Action = "orphan-sweep" });
        }

        // ---- 12. Execute -----------------------------------------------
        ExecutionReport report = ExecuteGraph(graph, history, cppDependencyCache, cancellationToken);

        // ---- 12.5 Surface per-action failure diagnostics ---------------
        // ParallelExecutor stores a per-action ErrorMessage on every
        // failed ActionResult but historically had no callback / log
        // path to dump them; a build with 800 silent failures was
        // unactionable. Walk the results once and emit the first N
        // root-cause failures (failed actions that were NOT skipped due
        // to a failed prerequisite) so the build log surfaces the actual
        // toolchain / runner error rather than just an aggregate count.
        // We cap the emission so a mass-failure (e.g. a missing system
        // header that breaks every TU) doesn't flood the log.
        const int MaxFailuresToReport = 20;
        int rootCauseEmitted = 0;
        foreach (var kvp in report.Results)
        {
            ActionResult r = kvp.Value;
            if (r.Success || r.Skipped)
            {
                continue;
            }
            if (rootCauseEmitted >= MaxFailuresToReport)
            {
                break;
            }
            string? msg = r.ErrorMessage;
            Logger.Error(
                $"Action failed: {kvp.Key.Description} (exit={r.ExitCode}): " +
                (string.IsNullOrEmpty(msg) ? "(no message)" : msg),
                exitCode: r.ExitCode,
                new DiagnosticContext
                {
                    Action = kvp.Key.Action.CommandDescription,
                    Module = kvp.Key.Action.Module,
                    Tier = kvp.Key.Action.Tier,
                    SimPath = kvp.Key.Action.SimPath,
                });
            rootCauseEmitted++;
        }

        // ---- 13. Save ActionHistory + CppDependencyCache --------------
        // Audit fix R7-M5: re-acquires the build mutex briefly, saves,
        // releases. Save is best-effort -- a failure is logged but
        // never aborts the build.
        historyCoordinator.SaveAndRelease(intermediateRoot);

        // CppDependencyCache.Save is also best-effort: it sits outside
        // the mutex but the atomic-rename + fsync pattern matches
        // ActionHistory.Save so a concurrent reader either sees the
        // pre-Save bytes or the post-Save bytes, never a torn state.
        try
        {
            cppDependencyCache.Save();
        }
        catch (Exception ex)
        {
            Logger.Warning(
                $"CppDependencyCache.Save failed: {ex.GetType().Name}: {ex.Message}. " +
                "The next build's transitive-header invalidation may miss headers " +
                "discovered in this run; the cache is self-healing on the next " +
                "successful save.",
                new DiagnosticContext { Action = "cppdeps-save" });
        }

        // ---- 13.5 Sim-path SleefFMACheck verification ------------------
        // Phase 1g Fix B-2 + XCore-4a Rev 3 Section 17.3 C-extra:
        // every sim-path linked artefact must be FMA-free. The check
        // is gated to runs where every action succeeded (no point
        // scanning a binary that did not link), and to platforms /
        // configurations where llvm-objdump is available. A failure
        // here surfaces the build with exit code 41 -- the
        // sim-path-determinism violation slot.
        if (report.Results.Count > 0)
        {
            bool everyActionSucceeded = true;
            foreach (var kvp in report.Results)
            {
                if (!kvp.Value.Success)
                {
                    everyActionSucceeded = false;
                    break;
                }
            }
            if (everyActionSucceeded)
            {
                // We collected sim-path artefacts at link-emission
                // time; the artefacts list is propagated into the
                // BuildResult-time scan loop below. The integration
                // helper handles llvm-objdump location + arch-string
                // derivation.
                foreach (SimPathArtefact artefact in pendingSimPathArtefacts)
                {
                    try
                    {
                        Simgenics.XPact.XBT.Toolchain.SleefFMACheckIntegration
                            .VerifyArtefact(artefact.ArtefactPath, artefact.Platform);
                    }
                    catch (FileNotFoundException ex) when (
                        ex.FileName?.Contains("llvm-objdump",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // llvm-objdump unavailable: emit warning, do
                        // not fail the build. CI shards set
                        // LLVM_OBJDUMP and pin the check; local dev
                        // builds without the binary skip the scan
                        // (the determinism contract is a CI gate, not
                        // a per-build-on-every-developer gate).
                        Logger.Warning(
                            $"SleefFMACheck: skipping FMA scan of "
                            + $"'{artefact.ArtefactPath}' (module "
                            + $"'{artefact.ModuleName}') -- llvm-objdump "
                            + "not located. Set LLVM_OBJDUMP to enable the "
                            + "post-link sim-path determinism check.",
                            new DiagnosticContext { Action = "sleef-fma-scan" });
                    }
                    catch (Simgenics.XPact.XBT.Toolchain.ToolchainBannedFlagException ex)
                    {
                        // Explicit FMA hit. Re-throw so the build's
                        // exit code carries the 41 / sim-path-
                        // determinism failure surface.
                        throw new XBTException(
                            $"SleefFMACheck failed for module "
                            + $"'{artefact.ModuleName}' at '{artefact.ArtefactPath}': "
                            + ex.Message,
                            exitCode: 41);
                    }
                }
            }
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
        // Audit fix R7-M8: collect path-vs-declared-tier mismatches in
        // parallel with link-tier violations so a single failed
        // validation pass reports every problem.
        List<string> pathMismatches = new();
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

            string? pathDiagnostic = TierValidator.ValidateDeclaredTierAgainstPath(
                rec.Rules.Name,
                rec.Rules.Tier,
                rec.DescriptorPath);
            if (pathDiagnostic is not null)
            {
                pathMismatches.Add(pathDiagnostic);
            }
        }
        if (allViolations.Count > 0 || pathMismatches.Count > 0)
        {
            List<string> all = new();
            all.AddRange(allViolations.Select(v => v.FormatMessage()));
            all.AddRange(pathMismatches);
            string combined = string.Join(Environment.NewLine, all);
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
        ReflectionMarkerCache markerCache,
        CancellationToken cancellationToken,
        out IReadOnlyList<IExternalAction> emittedForReport,
        out IReadOnlyList<SimPathArtefact> pendingSimPathArtefactsOut)
    {
        // Audit fix C9: the cancellation token is now plumbed through
        // every per-module and per-source iteration so a long emit pass
        // honours Ctrl-C / IDE-cancellation requests promptly.
        cancellationToken.ThrowIfCancellationRequested();

        List<IExternalAction> actions = new();
        List<FileItem> allSourceFiles = new();
        List<IExternalAction> reportActions = new();

        // Phase 1g Fix B-2: collect sim-path linked artefacts emitted
        // during the per-module loop. After the loop completes, the
        // runner verifies each artefact via SleefFMACheckIntegration
        // (post-link disassembly scan for forbidden FMA instructions).
        // Integration ships as a synchronous side-band check rather
        // than a new XActionType to avoid rotating
        // ActionHistory.CurrentVersion.
        List<SimPathArtefact> pendingSimPathArtefacts = new();

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
            if (xhtExePath is not null && HasReflectionMarkers(headerFiles, csharpFiles, markerCache))
            {
                IReadOnlyList<string> reflectionHeaderRelativePaths =
                    BuildReflectionHeaderPaths(moduleDir, headerFiles);
                IReadOnlyList<FileItem> reflectionInputs = MergeReflectionInputs(headerFiles, csharpFiles);

                // ParseHeadersAction first. Audit fix R7-C2: pin the
                // working directory to the canonical engine root (the
                // manifest's RootLocalPath = engineRoot) so two builds
                // from different shell CWDs produce identical action
                // commands, and propagate tier / configuration /
                // platform / sim-path so the action surfaces them on
                // the JSON channel and the CommandVersion hash
                // distinguishes configs / platforms / sim-path TUs.
                ParseHeadersAction parseAction = new(
                    moduleName: module.Name,
                    xhtExecutablePath: xhtExePath,
                    manifestJsonPath: manifestJsonPath,
                    outputDirectory: moduleObjDir,
                    sourceFiles: reflectionInputs,
                    workingDirectory: engineRoot,
                    tier: module.Tier.ToString(),
                    configuration: target.Configuration,
                    platform: target.Platform,
                    simPath: module.SimPath);
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
                    generatedCppFilenameBase: module.Name,
                    workingDirectory: engineRoot,
                    tier: module.Tier.ToString(),
                    configuration: target.Configuration,
                    platform: target.Platform,
                    simPath: module.SimPath);
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
                    toolchain.CompileSource(
                        module, target, source, moduleObjDir, pchBinding,
                        moduleSourceDir: moduleDir);
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

                // Phase 1g Fix B-2: post-link SleefFMACheck for sim-path
                // modules. Per XCore-4a Rev 3 Section 17.3 C-extra,
                // every sim-path linked artefact must be FMA-free.
                // The scan runs synchronously after the link succeeds
                // and aborts the build with exit code 41 on a hit.
                //
                // We register the verification as a follow-on closure
                // tied to the link action's outputs; the runner invokes
                // it after the link action completes. This integration
                // is intentionally NOT a new XActionType slot because
                // a slot addition rotates ActionHistory.CurrentVersion
                // and invalidates every cache entry across the engine.
                if (module.SimPath)
                {
                    foreach (FileItem produced in link.ProducedItems)
                    {
                        // Only scan the primary linked binary (.dll / .so /
                        // .lib / .a). Other artefacts (import libraries,
                        // PDBs) are not disassemble-able for FMA purposes.
                        string ext = Path.GetExtension(produced.FullPath);
                        bool isLinkBinary = ext is ".dll" or ".so" or ".lib"
                                                 or ".a"  or ".exe" or ".o";
                        if (!isLinkBinary)
                        {
                            continue;
                        }

                        pendingSimPathArtefacts.Add(
                            new SimPathArtefact(
                                ArtefactPath: produced.FullPath,
                                Platform: target.Platform,
                                ModuleName: module.Name));
                    }
                }
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
        pendingSimPathArtefactsOut = pendingSimPathArtefacts;
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
        return HasReflectionMarkers(headerFiles, csharpFiles, cache: null);
    }

    /// <summary>
    /// Audit fix R7-C5: cache-aware overload. The cache stores
    /// (content hash, marker-present) per file path; a hit short-
    /// circuits the file read entirely. The cache is updated with the
    /// scan result so the next build benefits regardless of whether
    /// this build is short-circuited or not.
    /// </summary>
    internal static bool HasReflectionMarkers(
        IReadOnlyList<FileItem> headerFiles,
        IReadOnlyList<FileItem> csharpFiles,
        ReflectionMarkerCache? cache)
    {
        bool foundAny = false;
        foreach (FileItem h in headerFiles)
        {
            if (FileContainsAnyCached(h, s_cppReflectionMarkers, cache))
            {
                foundAny = true;
                // We still walk the rest so the cache is populated
                // for every file, not just the prefix up to the first
                // hit. Walking the remainder costs at most one read
                // per uncached file -- amortised free on hit-heavy
                // module graphs.
            }
        }
        foreach (FileItem cs in csharpFiles)
        {
            if (FileContainsAnyCached(cs, s_csharpReflectionMarkers, cache))
            {
                foundAny = true;
            }
        }
        return foundAny;
    }

    /// <summary>
    /// Audit fix R7-C5: cache-aware variant of
    /// <see cref="FileContainsAny"/>. Looks up the file's content hash
    /// in the cache first; on hit, returns the stored marker-present
    /// flag without reading the file. On miss, runs the full scan
    /// (encoding detection + 64 KiB cap + comment strip + substring
    /// search) and stores the result in the cache.
    /// </summary>
    private static bool FileContainsAnyCached(FileItem file, IReadOnlyList<string> needles, ReflectionMarkerCache? cache)
    {
        if (cache is not null)
        {
            // Reading FileItem.ContentHash on a not-yet-hashed file
            // costs one file read; the cache lookup then short-
            // circuits the marker-scan if the hash matches. This is
            // strictly faster than the scan even on cache miss
            // (BLAKE3 + the substring scan together vs. just the
            // substring scan), but on cache HIT the second read is
            // skipped, which is the dominant benefit.
            IoHash contentHash;
            try
            {
                contentHash = file.ContentHash;
            }
            catch (IOException)
            {
                // Conservative: treat unreadable as needs-scan -> needs-XHT.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            if (cache.TryGet(file.FullPath, contentHash, out bool cachedResult))
            {
                return cachedResult;
            }

            bool fresh = FileContainsAny(file.FullPath, needles);
            cache.Set(file.FullPath, contentHash, fresh);
            return fresh;
        }
        return FileContainsAny(file.FullPath, needles);
    }

    /// <summary>
    /// C++ reflection-marker vocabulary per Toolchain Contract Rev 13.6
    /// Section 11.2 (the parenthesis is part of the marker so a textual
    /// identifier reference -- e.g. a docstring naming the marker -- does
    /// not match).
    /// </summary>
    /// <remarks>
    /// Audit fix R7-M1: derived from
    /// <see cref="Manifest.ContractSurface.MarkerMacros"/> so a new
    /// marker added to the canonical surface (the C# source of truth
    /// per ContractSurface.cs) is automatically recognized by the
    /// scan. The prior hand-coded list (7 markers) had drifted from the
    /// canonical list (10 markers) and silently missed XPARAM / XMETA /
    /// XGENERATED_BODY -- which meant a module containing only those
    /// markers would be (incorrectly) flagged as not needing XHT.
    /// XGENERATED_BODY in particular is the canonical body macro that
    /// every X-prefixed class with reflection emits; missing it is a
    /// real correctness gap.
    /// </remarks>
    internal static readonly string[] s_cppReflectionMarkers = BuildCppReflectionMarkers();

    /// <summary>
    /// C# reflection-attribute vocabulary. Square brackets mirror the C#
    /// attribute syntax; a passing-mention of <c>XClass</c> in
    /// commentary text does not match.
    /// </summary>
    /// <remarks>
    /// Audit fix R7-M1: derived from
    /// <see cref="Manifest.ContractSurface.MarkerMacros"/>. The C# form
    /// is the C++ macro name in TitleCase prefixed with <c>[</c>;
    /// <c>XGENERATED_BODY</c> has no C# attribute form (it is a C++
    /// body macro) and is excluded.
    /// </remarks>
    internal static readonly string[] s_csharpReflectionMarkers = BuildCsharpReflectionMarkers();

    /// <summary>
    /// Audit fix R7-M1: derive the C++ needle set from
    /// <see cref="Manifest.ContractSurface.MarkerMacros"/>. Each marker is
    /// the macro name followed by a literal <c>(</c> so a textual
    /// identifier reference (e.g. an XML doc naming the marker) does
    /// not produce a false positive.
    /// </summary>
    private static string[] BuildCppReflectionMarkers()
    {
        IReadOnlyList<string> source = Manifest.ContractSurface.MarkerMacros;
        string[] result = new string[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            result[i] = source[i] + "(";
        }
        return result;
    }

    /// <summary>
    /// Audit fix R7-M1: derive the C# attribute needle set from
    /// <see cref="Manifest.ContractSurface.MarkerMacros"/>. The C# form
    /// uses C# attribute syntax (<c>[</c> + TitleCase name); the body
    /// macro <c>XGENERATED_BODY</c> has no C# analogue and is excluded.
    /// </summary>
    private static string[] BuildCsharpReflectionMarkers()
    {
        IReadOnlyList<string> source = Manifest.ContractSurface.MarkerMacros;
        List<string> result = new(source.Count);
        foreach (string macro in source)
        {
            if (string.Equals(macro, "XGENERATED_BODY", StringComparison.Ordinal))
            {
                // Body-macro: C++-only.
                continue;
            }
            string titled = ToTitleCaseFromUpper(macro);
            result.Add("[" + titled);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Audit fix R7-M1: convert an upper-case macro name like
    /// <c>XCLASS</c> into the corresponding C# attribute name
    /// <c>XClass</c>. Splits on underscores: each segment is
    /// title-cased independently (so <c>FOO_BAR</c> becomes
    /// <c>FooBar</c>) so a multi-word macro contract addition lands
    /// with the right C# attribute spelling automatically.
    /// </summary>
    private static string ToTitleCaseFromUpper(string upper)
    {
        ArgumentNullException.ThrowIfNull(upper);
        if (upper.Length == 0)
        {
            return upper;
        }
        StringBuilder sb = new(upper.Length);
        bool capitalizeNext = true;
        foreach (char c in upper)
        {
            if (c == '_')
            {
                capitalizeNext = true;
                continue;
            }
            if (capitalizeNext)
            {
                sb.Append(char.ToUpperInvariant(c));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Audit fix R7-C5: scan a file for any of the supplied literal
    /// substrings, with encoding sniffing, 64 KiB cap, and C++ comment
    /// stripping so an XCLASS reference inside a <c>//</c> or
    /// <c>/* */</c> comment does NOT produce a false positive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encoding detection.</b> The leading 2-4 bytes are checked for
    /// a BOM:
    /// </para>
    /// <list type="bullet">
    ///   <item>UTF-8 BOM (<c>EF BB BF</c>) -&gt; UTF-8 decode, BOM skipped.</item>
    ///   <item>UTF-16 LE BOM (<c>FF FE</c>) -&gt; UTF-16 little-endian decode.</item>
    ///   <item>UTF-16 BE BOM (<c>FE FF</c>) -&gt; UTF-16 big-endian decode.</item>
    ///   <item>UTF-32 BOMs (<c>FF FE 00 00</c> / <c>00 00 FE FF</c>) -&gt; rejected; UTF-32 source is not supported per Contract Section 6.</item>
    ///   <item>No BOM -&gt; default UTF-8 (Contract Section 6 mandates UTF-8 source).</item>
    /// </list>
    /// <para>
    /// <b>Scan cap.</b> 64 KiB. Real-world reflection markers appear in
    /// the first hundred bytes of any source file next to the type
    /// declaration. The prior 1 MiB cap was 16x too generous and paid
    /// for I/O on no real-world file.
    /// </para>
    /// <para>
    /// <b>Comment stripping.</b> A simple C++ comment stripper rewrites
    /// <c>//</c> line comments and <c>/* */</c> block comments to
    /// spaces before the substring search. This eliminates the most
    /// common false-positive class (a marker name mentioned in a doc
    /// comment of an unrelated type). C# <c>///</c> doc comments are
    /// handled by the same <c>//</c> rule (anything from <c>//</c> to
    /// end-of-line is comment text).
    /// </para>
    /// <para>
    /// <b>Failure mode.</b> Files that cannot be read return true
    /// conservatively so a reflection module is not silently skipped.
    /// </para>
    /// </remarks>
    internal static bool FileContainsAny(string path, IReadOnlyList<string> needles)
    {
        const int MaxBytes = 64 * 1024;
        try
        {
            byte[] buffer;
            int read;
            long fileLength;
            using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fileLength = fs.Length;
                int toRead = (int)Math.Min(fileLength, MaxBytes);
                buffer = new byte[toRead];
                read = fs.Read(buffer, 0, toRead);
            }

            if (!TryDecodeWithBom(buffer, read, out string? text))
            {
                // Encoding we don't support -> conservatively claim it
                // has markers so XHT is invoked. XHT will surface a
                // clearer error if the source is genuinely unsupported.
                return true;
            }

            string scanText = StripCppComments(text!);
            foreach (string needle in needles)
            {
                if (scanText.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // Audit fix R3-M6: the 64 KiB cap is a determinism trade-off
            // (cheaper scan, but real-world markers always live in the
            // first ~100 bytes next to the type declaration). The rare
            // case of a >64 KiB license / comment header above a real
            // marker would silently miss the marker and skip XHT,
            // producing a runtime "missing reflection metadata" failure
            // that is hard to diagnose.
            //
            // When we hit the cap AND found no marker, conservatively
            // claim marker-positive (over-invoke XHT, which is harmless
            // if no marker exists) AND emit an info diagnostic so an
            // operator can investigate the file. Over-invocation costs
            // one no-op XHT run; an undetected marker costs a runtime
            // failure.
            if (fileLength > MaxBytes)
            {
                Logger.Info(
                    $"Reflection-marker scan: '{path}' is {fileLength} bytes; "
                    + $"only the first {MaxBytes} bytes were scanned. No marker was found "
                    + "in the scanned region; treating the file as marker-positive so "
                    + "XHT is invoked on it. If this file genuinely has no markers, "
                    + "consider trimming the leading comment / license header so the "
                    + "scan cap is not exhausted.",
                    new DiagnosticContext { Action = "reflection-marker-scan" });
                return true;
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
    /// Audit fix R7-C5: BOM-based encoding sniffer. Returns true with
    /// the decoded string on a supported encoding; returns false when
    /// the BOM identifies an unsupported encoding (UTF-32).
    /// </summary>
    internal static bool TryDecodeWithBom(byte[] buffer, int length, out string? text)
    {
        text = null;
        if (length == 0)
        {
            text = string.Empty;
            return true;
        }

        // UTF-32 LE: FF FE 00 00 -- check BEFORE UTF-16 LE (which is
        // FF FE) so the 4-byte prefix wins.
        if (length >= 4
            && buffer[0] == 0xFF && buffer[1] == 0xFE
            && buffer[2] == 0x00 && buffer[3] == 0x00)
        {
            return false;
        }
        // UTF-32 BE: 00 00 FE FF.
        if (length >= 4
            && buffer[0] == 0x00 && buffer[1] == 0x00
            && buffer[2] == 0xFE && buffer[3] == 0xFF)
        {
            return false;
        }
        // UTF-8 BOM: EF BB BF.
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(buffer, 3, length - 3);
            return true;
        }
        // UTF-16 LE: FF FE.
        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(buffer, 2, length - 2);
            return true;
        }
        // UTF-16 BE: FE FF.
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(buffer, 2, length - 2);
            return true;
        }
        // No BOM -> UTF-8 per Contract Section 6.
        text = Encoding.UTF8.GetString(buffer, 0, length);
        return true;
    }

    /// <summary>
    /// Audit fix R7-C5: rewrite C++ <c>//</c> line comments and
    /// <c>/* */</c> block comments in <paramref name="src"/> to spaces.
    /// Preserves string literals so a marker name inside a regular
    /// string doesn't get stripped (rare but possible).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state machine is intentionally simple: it does NOT handle
    /// raw string literals (<c>R"(...)"</c>), trigraphs, or line
    /// continuations. A marker name inside a raw string literal is an
    /// edge case that does not occur in practice; should a false
    /// positive arise from such a case, the conservative behaviour
    /// (claim markers present) errs on the side of running XHT, which
    /// is the correct failure mode.
    /// </para>
    /// </remarks>
    internal static string StripCppComments(string src)
    {
        if (string.IsNullOrEmpty(src))
        {
            return src;
        }

        StringBuilder sb = new(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            char c = src[i];

            // String literal -- copy through unchanged so comments
            // inside strings don't confuse us. (A literal " inside a
            // string is escaped as \"; we handle that below.)
            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    char d = src[i];
                    if (d == '\\' && i + 1 < src.Length)
                    {
                        // Escape: copy both chars (\ + escaped char).
                        sb.Append(d);
                        sb.Append(src[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(d);
                    i++;
                    if (d == '"')
                    {
                        break;
                    }
                }
                continue;
            }

            // Character literal -- same treatment.
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    char d = src[i];
                    if (d == '\\' && i + 1 < src.Length)
                    {
                        sb.Append(d);
                        sb.Append(src[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(d);
                    i++;
                    if (d == '\'')
                    {
                        break;
                    }
                }
                continue;
            }

            // Line comment.
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                sb.Append("  ");
                i += 2;
                while (i < src.Length && src[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }
                continue;
            }

            // Block comment.
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i + 1 < src.Length)
                {
                    sb.Append("  ");
                    i += 2;
                }
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
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
        CppDependencyCache cppDependencyCache,
        CancellationToken cancellationToken)
    {
        ParallelExecutor executor = new(
            new ParallelExecutorOptions(),
            new CopyrightAndProcessActionRunner(),
            history,
            cppDependencyCache);
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
    /// Audit fix R8-C1: Android NDK API level override. Only consulted
    /// when <see cref="Platform"/> == <see cref="Platform.Android"/>;
    /// flows through to <see cref="TargetRules.AndroidApiLevel"/> which
    /// composes the per-API-level Clang target triple
    /// (<c>--target=&lt;arch&gt;-linux-android&lt;API&gt;</c>). When null,
    /// the <see cref="TargetRules.AndroidApiLevel"/> default (21) applies.
    /// </summary>
    public int? AndroidApiLevel { get; init; }

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
        int? androidApiLevel = null;
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
            else if (arg.StartsWith("-AndroidApiLevel=", StringComparison.OrdinalIgnoreCase))
            {
                // Audit fix R8-C1: -AndroidApiLevel= overrides the
                // default Android NDK API level. Drives the
                // `--target=<arch>-linux-android<API>` flag emission in
                // XClangToolChain. Phase 1 default is 21 (NDK r26's
                // minimum 64-bit target).
                string raw = arg["-AndroidApiLevel=".Length..];
                if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                                   System.Globalization.CultureInfo.InvariantCulture,
                                   out int parsed) || parsed <= 0)
                {
                    throw new BuildOptionsParseException(
                        $"Invalid -AndroidApiLevel value '{raw}'. Expected a positive integer.");
                }
                androidApiLevel = parsed;
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
            AndroidApiLevel = androidApiLevel,
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

/// <summary>
/// One linked sim-path artefact pending the post-link SleefFMACheck
/// disassembly scan. Per XCore-4a Rev 3 Section 17.3 C-extra + Phase
/// 1g Fix B-2, sim-path-linked artefacts must be FMA-free for cross-
/// architecture bit-exactness; the scan runs after every action in
/// the build graph succeeds and surfaces exit code 41 on a hit.
/// </summary>
/// <param name="ArtefactPath">Absolute path to the linked binary.</param>
/// <param name="Platform">Target platform (drives the architecture
/// string for <see cref="Simgenics.XPact.XBT.Toolchain.SleefFMACheck"/>).</param>
/// <param name="ModuleName">Owning module name (surfaced in the
/// diagnostic on hit).</param>
public sealed record SimPathArtefact(
    string ArtefactPath,
    Platform Platform,
    string ModuleName);
