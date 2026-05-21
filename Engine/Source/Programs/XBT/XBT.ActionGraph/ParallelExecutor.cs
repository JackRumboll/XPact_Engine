// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// Local-only parallel executor for the build's action graph. Sized to
/// <see cref="Environment.ProcessorCount"/> by default; configurable via
/// <see cref="ParallelExecutorOptions.WorkerCount"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>/Documents/XBT.html</c> Rev 4 Sections 6.1-6.4:
/// </para>
/// <list type="bullet">
///   <item>Topological order respects the dependency graph; ties broken by <see cref="IoHash"/> ordinal.</item>
///   <item>Failure isolation: one action's non-zero exit fails that action only; independent actions continue.</item>
///   <item>Atomic-rename contract: every produced file is written to <c>&lt;output&gt;.tmp.&lt;pid&gt;.&lt;actionid&gt;</c> and renamed into place on success.</item>
///   <item>SIGINT: outstanding actions get a 5-second grace period; the executor sweeps its own temp files and returns exit 130.</item>
///   <item><see cref="IExternalAction.CanExecuteRemotely"/> is respected as a hint but ignored at Phase 1 (no remote executor yet).</item>
/// </list>
/// <para>
/// Per-action dispatch is delegated to an injected
/// <see cref="IActionRunner"/> so the executor can be unit-tested with a
/// fake runner that does not spawn subprocesses. The default
/// <see cref="ProcessActionRunner"/> implements the real subprocess dispatch.
/// </para>
/// </remarks>
public sealed class ParallelExecutor
{
    private readonly ParallelExecutorOptions _options;
    private readonly IActionRunner _runner;
    private readonly ActionHistory? _history;

    /// <summary>Construct an executor with the given options and runner.</summary>
    public ParallelExecutor(
        ParallelExecutorOptions options,
        IActionRunner runner,
        ActionHistory? history = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        _options = options;
        _runner = runner;
        _history = history;
    }

    /// <summary>
    /// Audit fix R6-C4: subprocess lifetime guard. A
    /// <see cref="WindowsJobObject"/> with
    /// <c>KILL_ON_JOB_CLOSE | BREAKAWAY_OK</c> is created once per
    /// <see cref="Execute"/> call; every spawned subprocess is assigned
    /// to it. When the executor returns -- normal exit or unhandled
    /// throw -- the using-disposal closes the handle and the OS
    /// terminates every process still in the job.
    /// </summary>
    /// <remarks>
    /// On Linux / macOS the wrapper is a no-op stub (POSIX uses process
    /// groups via <c>setpgid</c>; Phase 1's Linux executor is short-
    /// lived enough not to need this protection).
    /// </remarks>
    internal WindowsJobObject? CurrentJobObject { get; private set; }

    /// <summary>
    /// Run every action in the graph in topological order, respecting
    /// dependencies. Returns the per-action result for each linked
    /// action in <see cref="ActionGraph.SortedActions"/> order.
    /// </summary>
    /// <param name="graph">
    /// A linked action graph (call <see cref="ActionGraph.Link"/> +
    /// <see cref="ActionGraph.DetectCycles"/> before <see cref="Execute"/>).
    /// </param>
    /// <param name="cancellationToken">Honoured at every dispatch point.</param>
    /// <returns>Result for each linked action.</returns>
    public ExecutionReport Execute(ActionGraph graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        IReadOnlyList<LinkedAction> sorted = graph.SortedActions;

        // Audit fix R6-C4: create a per-execute job object that every
        // spawned subprocess will be assigned to. The using statement
        // around the entire Execute body ensures the handle closes on
        // any exit path (normal return, exception, cancellation) --
        // closing the last handle to a JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        // job triggers the OS to kill every process still in the job,
        // which is exactly the orphan-prevention guarantee we want.
        //
        // On Linux / macOS WindowsJobObject is a no-op shell;
        // IsAvailable returns false and AssignProcess is a no-op.
        using WindowsJobObject jobObject = new();
        jobObject.Create();
        CurrentJobObject = jobObject;
        try
        {
            return ExecuteInternal(graph, sorted, cancellationToken);
        }
        finally
        {
            CurrentJobObject = null;
        }
    }

    private ExecutionReport ExecuteInternal(
        ActionGraph graph,
        IReadOnlyList<LinkedAction> sorted,
        CancellationToken cancellationToken)
    {
        // Audit fix R6-C1: snapshot the producer map keyed on
        // FileItem.FullPath. The post-action hook (RecordContentHashes)
        // consults this set to classify each prerequisite as either
        // raw-source (record content hash) or producer-output (skip --
        // the upstream action's own RecordHash covers that path). Stored
        // as a HashSet so the look-up is O(1) per prereq.
        HashSet<string> producedPaths = new(StringComparer.Ordinal);
        foreach (string path in graph.ProducerByPath.Keys)
        {
            producedPaths.Add(path);
        }

        Dictionary<LinkedAction, ActionResult> results = new(sorted.Count);
        Dictionary<LinkedAction, int> remainingDeps = new(sorted.Count);
        foreach (LinkedAction node in sorted)
        {
            remainingDeps[node] = node.PrerequisiteActions.Count;
        }

        // Channel of ready actions, drained by worker threads. We use a
        // BlockingCollection<>; the ordering within "ready" matches the
        // deterministic topological order (every node we add was already
        // sorted upstream).
        using BlockingCollection<LinkedAction> ready = new(new ConcurrentQueue<LinkedAction>());

        // Seed: every node whose prerequisites are zero. We walk in
        // sorted order so the queue is initially in the deterministic
        // sort sequence.
        object readyGate = new();
        foreach (LinkedAction node in sorted)
        {
            if (remainingDeps[node] == 0)
            {
                ready.Add(node);
            }
        }

        int workerCount = _options.WorkerCount;
        if (workerCount <= 0)
        {
            workerCount = Environment.ProcessorCount;
        }

        int totalSubmitted = 0;
        int totalCompleted = 0;
        int totalFailed = 0;
        object completionGate = new();
        ManualResetEventSlim allDone = new(initialState: sorted.Count == 0);

        // Pre-mark "submitted" for the initial ready set.
        lock (completionGate)
        {
            foreach (LinkedAction node in sorted)
            {
                if (remainingDeps[node] == 0)
                {
                    totalSubmitted++;
                }
            }
        }

        // Per-action context to feed the runner. We allocate one
        // counter per executor instance for the temp-file actionid
        // suffix.
        long actionId = 0;

        // Spin up worker threads. We use Task.Run (thread-pool) for
        // simplicity rather than dedicated threads; the work is bounded
        // by the action graph's parallel width.
        Task[] workers = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            workers[w] = Task.Run(() =>
            {
                while (true)
                {
                    LinkedAction node;
                    try
                    {
                        if (!ready.TryTake(out node!, Timeout.Infinite, cancellationToken))
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (InvalidOperationException)
                    {
                        // BlockingCollection completed.
                        return;
                    }

                    long thisActionId = Interlocked.Increment(ref actionId);
                    ActionResult result = RunAction(node, thisActionId, producedPaths, cancellationToken);

                    lock (completionGate)
                    {
                        results[node] = result;
                        totalCompleted++;
                        if (!result.Success)
                        {
                            totalFailed++;
                        }

                        if (result.Success)
                        {
                            // Release dependents.
                            foreach (LinkedAction dep in node.DependentActions)
                            {
                                int newDeg = remainingDeps[dep] - 1;
                                remainingDeps[dep] = newDeg;
                                if (newDeg == 0 && !results.ContainsKey(dep))
                                {
                                    totalSubmitted++;
                                    ready.Add(dep);
                                }
                            }
                        }
                        else
                        {
                            // Mark dependents as skipped (cascading failure)
                            // and account for them in the completion count
                            // so the executor terminates when only failed
                            // / skipped actions remain.
                            int skipped = SkipDependents(node, results, remainingDeps);
                            totalCompleted += skipped;
                            totalFailed += skipped;
                        }

                        if (totalCompleted == sorted.Count)
                        {
                            allDone.Set();
                            ready.CompleteAdding();
                        }
                    }
                }
            }, CancellationToken.None);
        }

        // Wait until either every action has resolved or cancellation
        // fires. On cancellation we tear down the queue, the workers
        // return promptly, and we sweep our temp files.
        try
        {
            allDone.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ready.CompleteAdding();
        }

        try
        {
            Task.WaitAll(workers, _options.CancellationGracePeriod);
        }
        catch (AggregateException)
        {
            // Worker exceptions are surfaced via ActionResult, not by
            // throwing out of Execute.
        }

        // Sweep any temp files we created that did not get atomically
        // renamed (e.g. cancelled actions).
        SweepTempFiles();

        bool cancelled = cancellationToken.IsCancellationRequested;
        return new ExecutionReport(
            results,
            totalCompleted,
            totalFailed,
            cancelled);
    }

    private ActionResult RunAction(
        LinkedAction linked,
        long actionId,
        IReadOnlySet<string> producedPaths,
        CancellationToken cancellationToken)
    {
        IExternalAction action = linked.Action;
        if (cancellationToken.IsCancellationRequested)
        {
            return new ActionResult(linked, Success: false, Skipped: true, ExitCode: 130, ErrorMessage: "cancelled");
        }

        // Skip if up-to-date (ActionHistory says so).
        if (_history is not null && action.bUseActionHistory && !_history.IsActionOutdated(linked))
        {
            return new ActionResult(linked, Success: true, Skipped: true, ExitCode: 0, ErrorMessage: null);
        }

        // Compose the temp-file plan: every ProducedItem is written to
        // <output>.tmp.<pid>.<actionid> in the output's own directory;
        // on success the runner renames each into place.
        int pid = Environment.ProcessId;
        Dictionary<FileItem, string> tempPaths = new(action.ProducedItems.Count);
        foreach (FileItem produced in action.ProducedItems)
        {
            string dir = Path.GetDirectoryName(produced.FullPath)!;
            Directory.CreateDirectory(dir);
            string baseName = Path.GetFileName(produced.FullPath);
            string tempName = $"{baseName}.tmp.{pid}.{actionId}";
            tempPaths[produced] = Path.Combine(dir, tempName);
        }

        ActionRunContext context = new(
            Action: action,
            TempOutputPaths: tempPaths,
            ProcessId: pid,
            ActionId: actionId,
            CancellationToken: cancellationToken)
        {
            // Audit fix R6-C4: hand the runner the executor's job
            // object so any spawned subprocess can be assigned to it
            // for kill-on-exit protection.
            JobObject = CurrentJobObject,
        };

        try
        {
            ActionRunResult runResult = _runner.RunAction(context);
            if (cancellationToken.IsCancellationRequested)
            {
                DeleteTempFiles(tempPaths.Values);
                return new ActionResult(linked, Success: false, Skipped: false, ExitCode: 130, ErrorMessage: "cancelled mid-run");
            }

            if (!runResult.Success)
            {
                DeleteTempFiles(tempPaths.Values);
                return new ActionResult(linked, Success: false, Skipped: false, ExitCode: runResult.ExitCode, ErrorMessage: runResult.ErrorMessage);
            }

            // Atomic rename + update ActionHistory.
            foreach ((FileItem produced, string tempPath) in tempPaths)
            {
                if (!File.Exists(tempPath))
                {
                    // The runner declared success but did not write
                    // the temp. Treat as failure.
                    DeleteTempFiles(tempPaths.Values);
                    return new ActionResult(
                        linked,
                        Success: false,
                        Skipped: false,
                        ExitCode: 70,
                        ErrorMessage: $"action {linked.Description} did not produce temp output {tempPath}");
                }
                // Audit fix R6-C5: wrap the rename in the AV-retry helper.
                // Compiler output is the textbook AV-scan target on Win64;
                // a transient lock here is the most common failure mode.
                string capturedTemp = tempPath;
                string capturedDest = produced.FullPath;
                FileSystemOps.RetryOnTransientIOException(
                    () => File.Move(capturedTemp, capturedDest, overwrite: true));
                // FileItem caches metadata; drop it so the next access
                // picks up the new content.
                produced.Invalidate();
            }

            // Record the new content hashes.
            if (_history is not null && action.bUseActionHistory)
            {
                IoHash actionKey = ActionHistory.ComputeActionKey(action);
                foreach (FileItem produced in action.ProducedItems)
                {
                    _history.RecordHash(produced, actionKey);
                }

                // Audit fix R6-C1: record the content hash of every raw-
                // source prerequisite (a prereq whose path is NOT a
                // producer's output). Producer-output prereqs are
                // intentionally skipped -- their upstream action's
                // RecordHash above covers staleness through the producer-
                // key map; recording a content hash for them would be a
                // redundant lookup that adds no diagnostic value and
                // costs an extra read of the producer-emitted bytes.
                //
                // This wires up the otherwise-unused content-hash map
                // (audit fix M3) so the IsActionOutdated rule 4 in
                // ActionHistory has data to compare against the live
                // file. Without this, raw-source change detection falls
                // back to FileItem mtime, which the contract bans.
                foreach (FileItem prereq in action.PrerequisiteItems)
                {
                    if (producedPaths.Contains(prereq.FullPath))
                    {
                        continue;
                    }
                    if (!File.Exists(prereq.FullPath))
                    {
                        // Raw-source prereq that vanished mid-build. The
                        // command somehow succeeded without it (or the
                        // toolchain reads the file via a path our
                        // FileItem does not normalise to). Skip rather
                        // than crash -- the next staleness check will
                        // surface the divergence via IsActionOutdated.
                        continue;
                    }
                    try
                    {
                        _history.RecordContentHash(prereq, prereq.ContentHash);
                    }
                    catch (IOException)
                    {
                        // Best-effort: a transient read failure on a
                        // raw-source prereq should not fail the action,
                        // since the action itself already succeeded.
                        // The next IsActionOutdated call will see no
                        // recorded content hash and conservatively
                        // re-run, which is the safe direction.
                    }
                }
            }

            return new ActionResult(linked, Success: true, Skipped: false, ExitCode: 0, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            DeleteTempFiles(tempPaths.Values);
            return new ActionResult(linked, Success: false, Skipped: false, ExitCode: 130, ErrorMessage: "cancelled");
        }
        catch (Exception ex)
        {
            DeleteTempFiles(tempPaths.Values);
            Logger.Error(
                $"Action {linked.Description} threw {ex.GetType().Name}: {ex.Message}",
                exitCode: 70,
                new DiagnosticContext
                {
                    Action = action.CommandDescription,
                    Module = action.Module,
                    Tier = action.Tier,
                    SimPath = action.SimPath,
                });
            return new ActionResult(linked, Success: false, Skipped: false, ExitCode: 70, ErrorMessage: ex.Message);
        }
    }

    private static int SkipDependents(
        LinkedAction failed,
        Dictionary<LinkedAction, ActionResult> results,
        Dictionary<LinkedAction, int> remainingDeps)
    {
        int skippedCount = 0;
        Stack<LinkedAction> work = new();
        foreach (LinkedAction dep in failed.DependentActions)
        {
            work.Push(dep);
        }
        while (work.Count > 0)
        {
            LinkedAction d = work.Pop();
            if (results.ContainsKey(d))
            {
                continue;
            }
            results[d] = new ActionResult(d, Success: false, Skipped: true, ExitCode: 70, ErrorMessage: $"Skipped: prerequisite '{failed.Description}' failed");
            remainingDeps[d] = -1;
            skippedCount++;
            foreach (LinkedAction next in d.DependentActions)
            {
                if (!results.ContainsKey(next))
                {
                    work.Push(next);
                }
            }
        }
        return skippedCount;
    }

    private void SweepTempFiles()
    {
        // Conservative sweep: only the executor's *known* temp paths are
        // tracked via RunAction's DeleteTempFiles. The orphan-sweep at
        // next startup (XBT.html Section 6.4) covers temp files from
        // crashed previous runs; that is implemented by
        // <see cref="SweepOrphanedTempFiles"/> and called from BuildMode
        // before the executor dispatches anything.
    }

    /// <summary>
    /// Orphan-temp-file sweep per <c>/Documents/XBT.html</c> Rev 4
    /// Section 6.4: walk the intermediate-build root looking for
    /// <c>*.tmp.&lt;pid&gt;.&lt;actionid&gt;</c> files and delete any
    /// whose &lt;pid&gt; does not match a currently-running process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from <c>BuildMode</c> startup before the
    /// <see cref="Execute"/> pump begins. Safely re-entrant: multiple
    /// concurrent XBT invocations all sweeping the same tree at once
    /// is fine -- each invocation skips temps owned by any live pid
    /// (which includes the other sibling XBT processes), and deletes
    /// are best-effort. A delete that loses a race with another sweeper
    /// silently no-ops.
    /// </para>
    /// <para>
    /// The temp-file naming convention is fixed by
    /// <see cref="RunAction"/>: <c>&lt;output&gt;.tmp.&lt;pid&gt;.&lt;actionid&gt;</c>
    /// where &lt;pid&gt; is decimal. We match
    /// <c>*.tmp.&lt;number&gt;.&lt;number&gt;</c> via a per-file regex
    /// rather than a shell glob so the parse is portable across hosts.
    /// </para>
    /// </remarks>
    public static void SweepOrphanedTempFiles(string intermediateBuildRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(intermediateBuildRoot);
        if (!Directory.Exists(intermediateBuildRoot))
        {
            return;
        }

        int deletedFiles = 0;
        HashSet<string> deletedDirs = new(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> tempCandidates;
        try
        {
            tempCandidates = Directory.EnumerateFiles(
                intermediateBuildRoot,
                "*.tmp.*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchType = MatchType.Simple,
                });
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (string path in tempCandidates)
        {
            string fileName = Path.GetFileName(path);
            if (!TryParseTempFilePid(fileName, out int pid))
            {
                continue;
            }

            if (IsProcessAlive(pid))
            {
                // Belongs to a sibling XBT run (or our own pid on
                // restart-during-build). Leave it alone.
                continue;
            }

            try
            {
                File.Delete(path);
                deletedFiles++;
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null)
                {
                    deletedDirs.Add(dir);
                }
            }
            catch (IOException)
            {
                // Best-effort. A concurrent sweeper may have raced us.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort.
            }
        }

        if (deletedFiles > 0)
        {
            Logger.Debug(
                $"orphan sweep: deleted {deletedFiles} temp file(s) from " +
                $"{deletedDirs.Count} director(ies) under {intermediateBuildRoot}.");
        }
    }

    /// <summary>
    /// Parse the pid component of an
    /// <c>&lt;output&gt;.tmp.&lt;pid&gt;.&lt;actionid&gt;</c> file
    /// name. Returns false for any name that does not match the exact
    /// convention.
    /// </summary>
    /// <remarks>
    /// Audit fix R4-C1: the actionid suffix segment must be hex (digits +
    /// a-f/A-F) with no further dots. Two emit styles co-exist in
    /// production:
    /// <list type="bullet">
    ///   <item>Decimal counter -- the <see cref="ParallelExecutor"/>'s
    ///   per-instance counter via <c>RunAction</c>. Yields an all-digit
    ///   suffix (e.g. <c>5</c>).</item>
    ///   <item>GUID nonce ("N" format = 32 hex chars) -- the manifest
    ///   writer's <see cref="ManifestJson.AtomicWriteAllBytes"/> and
    ///   <see cref="ManifestFbs.AtomicWriteAllBytes"/> helpers, plus
    ///   <see cref="ActionHistory.Save"/>. Yields a hex-with-letters
    ///   suffix (e.g. <c>5e8a3b...</c>).</item>
    /// </list>
    /// Accepting either style guarantees both producer paths get swept
    /// when their pid is no longer alive. The pid segment must remain
    /// decimal because <see cref="Environment.ProcessId"/> is a 32-bit
    /// integer; the OS process identifier surface itself is decimal.
    /// </remarks>
    private static bool TryParseTempFilePid(string fileName, out int pid)
    {
        pid = 0;
        // Find the LAST ".tmp." occurrence in the filename; output file
        // names may contain dots (e.g. "Foo.cpp.obj.tmp.1234.5"), and
        // the convention is "<anything>.tmp.<pid>.<actionid>".
        int tmpIdx = fileName.LastIndexOf(".tmp.", StringComparison.Ordinal);
        if (tmpIdx < 0)
        {
            return false;
        }
        int pidStart = tmpIdx + ".tmp.".Length;
        if (pidStart >= fileName.Length)
        {
            return false;
        }
        int pidEnd = fileName.IndexOf('.', pidStart);
        if (pidEnd < 0)
        {
            return false;
        }
        string pidSegment = fileName[pidStart..pidEnd];
        if (pidSegment.Length == 0)
        {
            return false;
        }
        // The actionid suffix after the pid must be hex (digits + a-f /
        // A-F) with no further dots. This accepts both producer styles
        // (decimal counter from RunAction; 32-hex-char GUID nonce from
        // the manifest writers + ActionHistory).
        string actionIdSegment = fileName[(pidEnd + 1)..];
        if (actionIdSegment.Length == 0
            || !IsHexString(actionIdSegment))
        {
            return false;
        }
        return int.TryParse(pidSegment, out pid) && pid > 0;
    }

    private static bool IsHexString(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9')
                      || (c >= 'a' && c <= 'f')
                      || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Test whether a pid corresponds to a live process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix M11: cross-platform behaviour. Both Windows and POSIX
    /// use <see cref="Process.GetProcessById"/> as the probe -- the
    /// .NET CLR maps to <c>OpenProcess</c> on Windows and reads
    /// <c>/proc/&lt;pid&gt;</c> on Linux -- but the error categories
    /// each surface differ:
    /// </para>
    /// <list type="bullet">
    ///   <item>Windows: <see cref="ArgumentException"/> when the pid is
    ///   gone; <see cref="System.ComponentModel.Win32Exception"/> with
    ///   <c>ERROR_ACCESS_DENIED (5)</c> when the process exists but is
    ///   owned by another user. The access-denied case maps to "alive
    ///   but not ours" -- we conservatively treat it as alive so we do
    ///   not delete another user's sibling-XBT temp files.</item>
    ///   <item>Linux: <see cref="ArgumentException"/> when the pid is
    ///   gone or unreadable; <see cref="UnauthorizedAccessException"/>
    ///   when the proc entry exists but is owned by another user.
    ///   Treated the same as the Windows access-denied path: conservative
    ///   "alive but not ours".</item>
    /// </list>
    /// </remarks>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            // GetProcessById succeeded -> a process with that pid exists.
            // We do not require any particular ownership; the temp file
            // convention is "tmp.<pid>" and a sibling XBT instance is
            // permitted to own it.
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // Per docs: thrown when no process with that id is running.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows access-denied / other OS error -- treat as "alive
            // but not ours" so we do not delete another user's sibling
            // temp files.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Linux equivalent of Windows access-denied: the process
            // exists in /proc but we cannot read its metadata. Same
            // conservative treatment.
            return true;
        }
    }

    private static void DeleteTempFiles(IEnumerable<string> tempPaths)
    {
        foreach (string path in tempPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    // Audit fix R6-C5: wrap in the AV-retry helper.
                    // Windows Defender may have the just-failed write
                    // locked while it scans the partial bytes; the
                    // Delete then raises a sharing-violation IOException
                    // that we want to retry through rather than leave
                    // the temp lying around for the startup sweep.
                    string captured = path;
                    FileSystemOps.RetryOnTransientIOException(
                        () => File.Delete(captured));
                }
            }
            catch (IOException)
            {
                // Best-effort even after the retry schedule -- a
                // missing temp is fine; a stuck temp gets cleaned by
                // the startup sweep next run.
            }
            catch (UnauthorizedAccessException)
            {
                // AV quarantine that did not clear within the retry
                // window. Same best-effort treatment as IOException.
            }
        }
    }
}

/// <summary>
/// Options controlling <see cref="ParallelExecutor"/>'s behaviour.
/// </summary>
public sealed record ParallelExecutorOptions
{
    /// <summary>
    /// Number of worker threads. Zero or negative falls back to
    /// <see cref="Environment.ProcessorCount"/>. Override via
    /// <c>-Workers=N</c> at the CLI per XBT.html Section 6.
    /// </summary>
    public int WorkerCount { get; init; } = 0;

    /// <summary>
    /// How long to wait for in-flight actions to finish cleanly after a
    /// cancellation fires (XBT.html Section 6.4 SIGINT path). Defaults
    /// to 5 seconds.
    /// </summary>
    public TimeSpan CancellationGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Per-action result the executor surfaces back to the caller.
/// </summary>
/// <param name="Action">The action this result applies to.</param>
/// <param name="Success">True iff the action ran (or was up-to-date) without failure.</param>
/// <param name="Skipped">True iff the action was up-to-date or skipped due to a prerequisite failure.</param>
/// <param name="ExitCode">Underlying subprocess exit code (0 on success; per Toolchain Contract Section 13 on failure).</param>
/// <param name="ErrorMessage">Human-readable error message; null on success.</param>
public sealed record ActionResult(
    LinkedAction Action,
    bool Success,
    bool Skipped,
    int ExitCode,
    string? ErrorMessage);

/// <summary>
/// Per-build summary the executor returns.
/// </summary>
/// <param name="Results">Per-linked-action result map.</param>
/// <param name="TotalCompleted">Number of actions that resolved (succeeded or failed).</param>
/// <param name="TotalFailed">Number of actions that ran and failed.</param>
/// <param name="Cancelled">True iff cancellation fired during execution.</param>
public sealed record ExecutionReport(
    IReadOnlyDictionary<LinkedAction, ActionResult> Results,
    int TotalCompleted,
    int TotalFailed,
    bool Cancelled)
{
    /// <summary>True iff every action succeeded and no cancellation fired.</summary>
    public bool AllSucceeded => !Cancelled && TotalFailed == 0;
}

/// <summary>
/// Per-call context the executor passes to the
/// <see cref="IActionRunner"/>. Carries the action plus the temp-file
/// names the runner must write to.
/// </summary>
public sealed record ActionRunContext(
    IExternalAction Action,
    IReadOnlyDictionary<FileItem, string> TempOutputPaths,
    int ProcessId,
    long ActionId,
    CancellationToken CancellationToken)
{
    /// <summary>
    /// Audit fix R6-C4: the executor's per-build job object, or null
    /// when running on a non-Windows host (where the wrapper is a
    /// no-op) or when the job-object creation failed. The runner
    /// assigns its spawned subprocess to this job after
    /// <c>Process.Start</c> so the OS kills the subprocess when XBT
    /// exits.
    /// </summary>
    public WindowsJobObject? JobObject { get; init; }
}

/// <summary>
/// The runner's per-action outcome. <c>Success = true</c> means the
/// runner wrote every temp output and is ready for atomic rename.
/// </summary>
public sealed record ActionRunResult(
    bool Success,
    int ExitCode,
    string? ErrorMessage);

/// <summary>
/// Strategy interface for running a single action. Production
/// implementations spawn a subprocess via <see cref="ProcessActionRunner"/>;
/// the test harness substitutes a fake.
/// </summary>
public interface IActionRunner
{
    /// <summary>Run a single action and return its outcome.</summary>
    ActionRunResult RunAction(ActionRunContext context);
}

/// <summary>
/// The production action runner. Spawns <see cref="Process"/> with the
/// action's command, captures stdout/stderr to the build log, and
/// returns the exit code.
/// </summary>
/// <remarks>
/// <para>
/// This runner does <strong>not</strong> implement temp-file redirection
/// for compile actions -- the toolchain abstraction is responsible for
/// constructing command lines that write to the temp paths the executor
/// hands it via <see cref="ActionRunContext.TempOutputPaths"/>. The
/// toolchain consumes the temp names and embeds them as
/// <c>/Fo&lt;temp&gt;</c> (MSVC) or <c>-o &lt;temp&gt;</c> (Clang) at
/// command construction time.
/// </para>
/// </remarks>
public sealed class ProcessActionRunner : IActionRunner
{
    /// <summary>
    /// Poll interval between <c>WaitForExit</c> checks while waiting for
    /// a subprocess to finish. 250 ms is fast enough that a SIGINT
    /// honour-deadline of one second still leaves time for the process
    /// tree kill + drain, and slow enough that the per-action busy-wait
    /// overhead is negligible.
    /// </summary>
    internal const int WaitForExitPollMs = 250;

    /// <summary>
    /// Audit fix M10: cap stdout/stderr capture per subprocess at this
    /// many UTF-8 bytes (16 MiB). Output beyond the cap is replaced by a
    /// truncation marker. Sized so that two concurrent compile
    /// subprocesses cannot push process memory past 32 MiB of captured
    /// diagnostics, while still preserving enough context for a typical
    /// compile-error report.
    /// </summary>
    internal const int MaxCaptureBytes = 16 * 1024 * 1024;

    /// <inheritdoc/>
    public ActionRunResult RunAction(ActionRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IExternalAction action = context.Action;

        // Audit fix R7-C1: materialize the response file BEFORE spawning
        // the subprocess. ResponseFileContents is in the action's
        // CommandVersion + ActionHistory key (per
        // ExternalAction.ComputeCommandVersion item 4 +
        // ActionHistory.ComputeActionKey item 4), so an action with a
        // non-null body MUST surface that body to the subprocess --
        // otherwise the cache key embeds a payload the toolchain never
        // sees, and a change to the body would invalidate the cache but
        // produce identical compiler input.
        //
        // Naming convention matches the orphan-temp-file sweep's
        // expectations (".tmp.<pid>.<actionid>" suffix; actionId is hex
        // here so SweepOrphanedTempFiles's hex-suffix gate passes). Lives
        // in the first ProducedItem's directory when one exists; falls
        // back to WorkingDirectory otherwise (rare; in-process actions
        // with empty produced sets don't carry response files).
        string? responseFilePath = null;
        if (!string.IsNullOrEmpty(action.ResponseFileContents))
        {
            string rspDir = ChooseResponseFileDirectory(action);
            string rspBase = ChooseResponseFileBaseName(action);
            // actionId is decoded as hex for the orphan-sweep gate; pid
            // segment stays decimal (Environment.ProcessId is a 32-bit
            // integer the OS reports in decimal).
            string rspName = $"{rspBase}.rsp.tmp.{context.ProcessId}.{context.ActionId:x}";
            responseFilePath = Path.Combine(rspDir, rspName);
            // AtomicWriteAllText handles fsync + AV-retry on rename;
            // the response file is on disk and durable before the
            // subprocess launches.
            FileSystemOps.AtomicWriteAllText(responseFilePath, action.ResponseFileContents!);
        }

        ProcessStartInfo psi = new()
        {
            FileName = action.CommandPath,
            WorkingDirectory = action.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string arg in action.CommandArguments)
        {
            psi.ArgumentList.Add(arg);
        }

        // Audit fix R7-C1: append the @<rsp-path> indirection AFTER the
        // toolchain's own arguments so toolchain options that come last
        // (overrides, output paths) still win. Both cl.exe and clang.exe
        // treat the trailing @file as an inclusion at that point in the
        // command stream.
        if (responseFilePath is not null)
        {
            psi.ArgumentList.Add("@" + responseFilePath);
        }

        using Process process = new() { StartInfo = psi };
        // Audit fix M10: cap stdout/stderr captures at 16 MB each. A
        // misbehaving compiler producing 100+ MB of diagnostics would
        // otherwise OOM XBT.
        BoundedStringBuilder stdout = new(MaxCaptureBytes);
        BoundedStringBuilder stderr = new(MaxCaptureBytes);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
            {
                return new ActionRunResult(false, 1, "Process.Start returned false");
            }
            // Audit fix R7-C1: the response file is now durable on disk
            // and the subprocess has the @<path> indirection. The file
            // is deleted in the finally block below regardless of how
            // we exit. (Note: the toolchain reads the response file
            // synchronously at startup; deleting it after process exit
            // is always safe -- the read window closed at the moment
            // process.Start returned.)
            // Audit fix R6-C4: assign the freshly-started subprocess to
            // the executor's job object before BeginOutputReadLine so
            // the kill-on-job-close guarantee applies as early as
            // possible. Failure (process already exited, nested-job
            // restrictions, ...) is logged inside AssignProcess and
            // proceeds best-effort -- the orphan-temp-file sweep at
            // next startup compensates for missed kills.
            context.JobObject?.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Cancellation-aware wait. WaitForExit() with no timeout
            // would block the worker thread indefinitely on a hung
            // subprocess and prevent SIGINT honour. Polling every
            // WaitForExitPollMs lets us inject a process-tree kill
            // when the cancellation token fires.
            while (!process.WaitForExit(WaitForExitPollMs))
            {
                if (context.CancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best-effort -- process may have just exited
                        // on its own between the WaitForExit poll and
                        // the Kill call, or we may lack the right to
                        // signal it; either way we propagate cancel.
                    }
                    context.CancellationToken.ThrowIfCancellationRequested();
                }
            }

            // Audit fix R4-C2: WaitForExit(int) returns when the process
            // has exited but does NOT wait for the async output / error
            // handlers to drain their queues. The parameterless
            // WaitForExit() overload is documented to additionally wait
            // for the OutputDataReceived / ErrorDataReceived
            // EventHandlers to flush their pending events. Without this
            // call, the subsequent stderr.ToString() at "exit code !=0"
            // can race a still-arriving error line and observe a
            // partially-populated capture buffer.
            process.WaitForExit();

            int exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                return new ActionRunResult(true, 0, null);
            }
            return new ActionRunResult(
                false,
                exitCode,
                $"{action.CommandDescription} exit {exitCode}: {stderr.ToString()}");
        }
        catch (OperationCanceledException)
        {
            // Surface cancellation up to ParallelExecutor.RunAction
            // which is responsible for cleaning temp files and emitting
            // the exit-130 ActionResult.
            throw;
        }
        catch (Exception ex)
        {
            return new ActionRunResult(false, 1, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Audit fix R7-C1: clean up the response file regardless of
            // success / failure / cancellation. The orphan-temp-file
            // sweep would also reap this on next startup (the
            // ".tmp.<pid>.<hex>" naming is recognised), but cleaning up
            // eagerly keeps the intermediate tree tidy.
            if (responseFilePath is not null)
            {
                try
                {
                    File.Delete(responseFilePath);
                }
                catch (IOException)
                {
                    // Best-effort -- the orphan sweep picks up stragglers.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort.
                }
            }
        }
    }

    /// <summary>
    /// Audit fix R7-C1: choose the directory the response file lives in.
    /// Prefers the first produced item's directory (the natural sibling
    /// location for an action's artefacts); falls back to the action's
    /// working directory when the action emits nothing on disk (rare;
    /// in-process write-manifest etc.).
    /// </summary>
    private static string ChooseResponseFileDirectory(IExternalAction action)
    {
        foreach (FileItem produced in action.ProducedItems)
        {
            string dir = Path.GetDirectoryName(produced.FullPath) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
        }
        if (!string.IsNullOrEmpty(action.WorkingDirectory))
        {
            Directory.CreateDirectory(action.WorkingDirectory);
            return action.WorkingDirectory;
        }
        // Final fallback: process temp. Unusual; an action with no
        // produced items and no working directory is a programming bug,
        // but we don't want the response-file write to surface that as
        // the user-visible failure.
        return Path.GetTempPath();
    }

    /// <summary>
    /// Audit fix R7-C1: choose the base filename for the response file.
    /// Uses the first produced item's stem when one exists so a "Foo.obj"
    /// action gets a "Foo.obj.rsp.tmp.&lt;pid&gt;.&lt;hex&gt;" sibling;
    /// falls back to the action description otherwise.
    /// </summary>
    private static string ChooseResponseFileBaseName(IExternalAction action)
    {
        foreach (FileItem produced in action.ProducedItems)
        {
            string name = Path.GetFileName(produced.FullPath);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        // Descriptions are human-prose so they may contain whitespace
        // or punctuation; collapse anything non-identifier to '_' so
        // the resulting filename is portable.
        string desc = action.CommandDescription ?? "action";
        Span<char> buf = stackalloc char[desc.Length];
        for (int i = 0; i < desc.Length; i++)
        {
            char c = desc[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }
        return new string(buf);
    }
}

/// <summary>
/// Audit fix M10: bounded <see cref="StringBuilder"/> wrapper that caps
/// total appended bytes at a configurable ceiling. Once the cap is
/// reached, every subsequent <see cref="AppendLine"/> is dropped and a
/// one-time truncation marker is appended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-safety contract (audit fix R4-C2).</b> Instances are
/// shared between the worker thread that owns the
/// <see cref="ProcessActionRunner"/> and the ThreadPool threads
/// <see cref="Process.OutputDataReceived"/> and
/// <see cref="Process.ErrorDataReceived"/> dispatch on. All access to
/// the mutable state (<see cref="_sb"/>, <see cref="_approxBytes"/>,
/// <see cref="_droppedBytes"/>, <see cref="_truncationMarkerEmitted"/>)
/// is serialized through <see cref="_gate"/>. The reading thread
/// (<see cref="ToString"/>) MUST only read after the subprocess has
/// fully exited AND its handler queue has been drained via
/// <see cref="Process.WaitForExit()"/> (the parameterless overload,
/// which waits for handler completion in addition to process exit).
/// Without that drain, a read may race a pending handler invocation.
/// </para>
/// </remarks>
internal sealed class BoundedStringBuilder
{
    private readonly StringBuilder _sb;
    private readonly int _maxBytes;
    private readonly object _gate = new();
    private int _approxBytes;
    private long _droppedBytes;
    private bool _truncationMarkerEmitted;

    public BoundedStringBuilder(int maxBytes)
    {
        _maxBytes = maxBytes;
        _sb = new StringBuilder();
    }

    public void AppendLine(string line)
    {
        if (line is null) return;
        // UTF-8 bytes for ASCII lines == char count + 1 for the line
        // terminator. We approximate to char count to avoid a full
        // utf-8 measurement on the hot path; the cap is intentionally
        // loose (the real ceiling is ~maxBytes, not exactly maxBytes).
        int lineSize = line.Length + 1;
        lock (_gate)
        {
            if (_approxBytes + lineSize <= _maxBytes)
            {
                _sb.AppendLine(line);
                _approxBytes += lineSize;
                return;
            }
            // Capped out. Emit a truncation marker once, then count
            // dropped bytes.
            if (!_truncationMarkerEmitted)
            {
                _sb.AppendLine();
                _sb.AppendLine($"...[output truncated at {_maxBytes} bytes]...");
                _truncationMarkerEmitted = true;
            }
            _droppedBytes += lineSize;
        }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            if (_truncationMarkerEmitted && _droppedBytes > 0)
            {
                return _sb.ToString() + $"[{_droppedBytes} bytes elided total]" + Environment.NewLine;
            }
            return _sb.ToString();
        }
    }
}
