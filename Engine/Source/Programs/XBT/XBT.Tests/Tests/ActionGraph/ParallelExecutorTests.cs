// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph;

/// <summary>
/// Verifies <see cref="ParallelExecutor"/> against the executor invariants
/// from <c>/Documents/XBT.html</c> Rev 4 Section 6: every action runs once,
/// failure isolation works, cancellation is honoured, and the executor
/// observes the <see cref="IExternalAction.CanExecuteRemotely"/> hint
/// without changing Phase 1 dispatch.
/// </summary>
public sealed class ParallelExecutorTests : IDisposable
{
    private readonly string _scratchDir;
    private int _seq;

    public ParallelExecutorTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ParallelExecutor",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
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

    /// <summary>
    /// Execute a 3-action chain. Every action runs once, in topological
    /// order, and every produced file is created on disk after the
    /// atomic rename.
    /// </summary>
    [Fact]
    public void Execute_SimpleChain_AllActionsRun()
    {
        FileItem aOut = MakeFileItem("a.out");
        FileItem bOut = MakeFileItem("b.out");
        FileItem cOut = MakeFileItem("c.out");

        IExternalAction a = MakeAction("A", Array.Empty<FileItem>(), new[] { aOut });
        IExternalAction b = MakeAction("B", new[] { aOut }, new[] { bOut });
        IExternalAction c = MakeAction("C", new[] { bOut }, new[] { cOut });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a, b, c });
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        var runner = new RecordingActionRunner();
        var executor = new ParallelExecutor(new ParallelExecutorOptions { WorkerCount = 2 }, runner);
        ExecutionReport report = executor.Execute(graph, CancellationToken.None);

        Assert.True(report.AllSucceeded);
        Assert.Equal(3, report.TotalCompleted);
        Assert.Equal(0, report.TotalFailed);
        Assert.True(File.Exists(aOut.FullPath));
        Assert.True(File.Exists(bOut.FullPath));
        Assert.True(File.Exists(cOut.FullPath));
    }

    /// <summary>
    /// Failure isolation: A succeeds; B (depends on A) fails; C (independent
    /// of B) still completes. D (depends on B) is skipped.
    /// </summary>
    [Fact]
    public void Execute_BFails_IndependentCStillRuns()
    {
        FileItem aOut = MakeFileItem("a.out");
        FileItem bOut = MakeFileItem("b.out");
        FileItem cOut = MakeFileItem("c.out");
        FileItem dOut = MakeFileItem("d.out");

        IExternalAction a = MakeAction("A", Array.Empty<FileItem>(), new[] { aOut });
        IExternalAction b = MakeAction("B", new[] { aOut }, new[] { bOut });
        IExternalAction c = MakeAction("C", Array.Empty<FileItem>(), new[] { cOut });
        IExternalAction d = MakeAction("D", new[] { bOut }, new[] { dOut });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a, b, c, d });
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        var runner = new RecordingActionRunner();
        runner.FailDescriptionContains = "B";
        var executor = new ParallelExecutor(new ParallelExecutorOptions { WorkerCount = 2 }, runner);
        ExecutionReport report = executor.Execute(graph, CancellationToken.None);

        Assert.False(report.AllSucceeded);
        Assert.Equal(4, report.TotalCompleted);
        Assert.True(report.TotalFailed >= 1);

        // C ran successfully (independent of B's failure).
        var cResult = report.Results.Single(kvp => ReferenceEquals(kvp.Key.Action, c)).Value;
        Assert.True(cResult.Success);
        Assert.True(File.Exists(cOut.FullPath));

        // D was skipped because its prerequisite B failed.
        var dResult = report.Results.Single(kvp => ReferenceEquals(kvp.Key.Action, d)).Value;
        Assert.False(dResult.Success);
        Assert.True(dResult.Skipped);
    }

    /// <summary>
    /// Cancellation: pre-cancel the token; Execute returns promptly with
    /// the Cancelled flag set; no orphan temp files remain.
    /// </summary>
    [Fact]
    public void Execute_PreCancelledToken_NoOrphanTempFiles()
    {
        FileItem aOut = MakeFileItem("cancelled.out");
        IExternalAction a = MakeAction("A", Array.Empty<FileItem>(), new[] { aOut });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a });
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var runner = new RecordingActionRunner();
        var executor = new ParallelExecutor(new ParallelExecutorOptions { WorkerCount = 1 }, runner);
        ExecutionReport report = executor.Execute(graph, cts.Token);

        Assert.True(report.Cancelled);

        // No temp files should remain in the scratch dir.
        string[] tempFiles = Directory.GetFiles(_scratchDir, "*.tmp.*");
        Assert.Empty(tempFiles);
    }

    /// <summary>
    /// Deterministic ordering: the recording runner observes the same
    /// dispatch sequence on two consecutive runs of the same graph
    /// (single-worker so worker-pool scheduling does not interleave).
    /// </summary>
    [Fact]
    public void Execute_DeterministicDispatchOrder_SingleWorker()
    {
        FileItem aOut = MakeFileItem("a");
        FileItem bOut = MakeFileItem("b");
        FileItem cOut = MakeFileItem("c");
        FileItem dOut = MakeFileItem("d");

        IExternalAction a = MakeAction("A", Array.Empty<FileItem>(), new[] { aOut });
        IExternalAction b = MakeAction("B", Array.Empty<FileItem>(), new[] { bOut });
        IExternalAction c = MakeAction("C", Array.Empty<FileItem>(), new[] { cOut });
        IExternalAction d = MakeAction("D", Array.Empty<FileItem>(), new[] { dOut });

        IReadOnlyList<string> RunOnce()
        {
            // Re-create the graph each iteration so the sort state is fresh.
            var g = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a, b, c, d });
            g.Link();
            g.DetectCycles();
            g.Sort();
            var rec = new RecordingActionRunner();
            var exe = new ParallelExecutor(new ParallelExecutorOptions { WorkerCount = 1 }, rec);
            exe.Execute(g, CancellationToken.None);
            // Reset produced files so the next run isn't short-circuited
            // by ActionHistory (we don't pass a history here, but the
            // files exist on disk from the first run).
            foreach (FileItem f in new[] { aOut, bOut, cOut, dOut })
            {
                if (File.Exists(f.FullPath))
                {
                    File.Delete(f.FullPath);
                }
            }
            return rec.OrderedDescriptions;
        }

        IReadOnlyList<string> firstRun = RunOnce();
        IReadOnlyList<string> secondRun = RunOnce();

        Assert.Equal(firstRun, secondRun);
        Assert.Equal(4, firstRun.Count);
    }

    /// <summary>
    /// CanExecuteRemotely is observed but does not change Phase 1 dispatch:
    /// an action with CanExecuteRemotely = false still runs locally
    /// without error.
    /// </summary>
    [Fact]
    public void Execute_CanExecuteRemotelyFalse_StillDispatchesLocally()
    {
        FileItem aOut = MakeFileItem("local-only");
        IExternalAction a = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            ProducedItems = new[] { aOut },
            CommandPath = "/fake/cl.exe",
            CommandArguments = Array.Empty<string>(),
            WorkingDirectory = _scratchDir,
            CommandDescription = "Compile",
            StatusDescription = "local-only",
            CanExecuteRemotely = false,
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
        });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a });
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        var runner = new RecordingActionRunner();
        var executor = new ParallelExecutor(new ParallelExecutorOptions { WorkerCount = 1 }, runner);
        ExecutionReport report = executor.Execute(graph, CancellationToken.None);

        Assert.True(report.AllSucceeded);
        Assert.True(File.Exists(aOut.FullPath));
    }

    // ----- Helpers -----

    private IExternalAction MakeAction(
        string name,
        IReadOnlyList<FileItem> prereqs,
        IReadOnlyList<FileItem> produces)
    {
        List<FileItem> sortedPrereqs = new(prereqs);
        sortedPrereqs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        List<FileItem> sortedProduces = new(produces);
        sortedProduces.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        return ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.CompileCppAction,
            PrerequisiteItems = sortedPrereqs,
            ProducedItems = sortedProduces,
            CommandPath = $"/fake/{name}.exe",
            CommandArguments = new[] { "-D" + name + "=1" },
            WorkingDirectory = _scratchDir,
            CommandDescription = "Test",
            StatusDescription = name,
            bUseActionHistory = false,  // tests pass no history; do not require it
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
        });
    }

    private FileItem MakeFileItem(string label)
    {
        int n = Interlocked.Increment(ref _seq);
        string path = Path.Combine(_scratchDir, $"{label}-{n}.bin");
        return FileItem.GetItemByPath(path);
    }

    /// <summary>
    /// Test runner that records the dispatch sequence and writes each
    /// action's temp outputs with a deterministic payload. Optionally
    /// fails actions whose <see cref="ActionRunContext.Action"/>'s
    /// description contains a configured substring.
    /// </summary>
    private sealed class RecordingActionRunner : IActionRunner
    {
        private readonly object _gate = new();
        private readonly List<string> _orderedDescriptions = new();

        /// <summary>Configurable: cause any action whose StatusDescription contains this substring to fail.</summary>
        public string? FailDescriptionContains { get; set; }

        public IReadOnlyList<string> OrderedDescriptions
        {
            get { lock (_gate) { return _orderedDescriptions.ToArray(); } }
        }

        public ActionRunResult RunAction(ActionRunContext context)
        {
            lock (_gate)
            {
                _orderedDescriptions.Add(context.Action.StatusDescription);
            }

            if (FailDescriptionContains is not null
             && context.Action.StatusDescription.Contains(FailDescriptionContains, StringComparison.Ordinal))
            {
                return new ActionRunResult(false, ExitCode: 70, ErrorMessage: "synthetic failure");
            }

            // Write each temp output with a small payload.
            foreach ((FileItem produced, string tempPath) in context.TempOutputPaths)
            {
                File.WriteAllText(tempPath, "synthetic-" + produced.FullPath);
            }
            return new ActionRunResult(true, ExitCode: 0, ErrorMessage: null);
        }
    }
}
