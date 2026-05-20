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
                    ActionResult result = RunAction(node, thisActionId, cancellationToken);

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

    private ActionResult RunAction(LinkedAction linked, long actionId, CancellationToken cancellationToken)
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
            CancellationToken: cancellationToken);

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
                File.Move(tempPath, produced.FullPath, overwrite: true);
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
        // The actionid suffix after the pid must be all digits with no
        // further dots (a strict per-spec match).
        string actionIdSegment = fileName[(pidEnd + 1)..];
        if (actionIdSegment.Length == 0
            || !IsAllDigits(actionIdSegment))
        {
            return false;
        }
        return int.TryParse(pidSegment, out pid) && pid > 0;
    }

    private static bool IsAllDigits(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Test whether a pid corresponds to a live process. On Win64 and
    /// POSIX, <see cref="Process.GetProcessById"/> throws when the pid
    /// is not running; we map both the not-running and access-denied
    /// outcomes to "not alive" so the orphan sweep deletes the file.
    /// </summary>
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
            // Access denied or other OS error -- treat as "not ours,
            // leave alone" to be conservative (deleting another user's
            // sibling temp could break their build).
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
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort -- a missing temp is fine; a stuck temp
                // gets cleaned by the startup sweep next run.
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
    CancellationToken CancellationToken);

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

    /// <inheritdoc/>
    public ActionRunResult RunAction(ActionRunContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IExternalAction action = context.Action;

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

        using Process process = new() { StartInfo = psi };
        StringBuilder stdout = new();
        StringBuilder stderr = new();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
            {
                return new ActionRunResult(false, 1, "Process.Start returned false");
            }
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

            int exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                return new ActionRunResult(true, 0, null);
            }
            return new ActionRunResult(
                false,
                exitCode,
                $"{action.CommandDescription} exit {exitCode}: {stderr}");
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
    }
}
