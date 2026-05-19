// Copyright Simgenics. All Rights Reserved.

using System;
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
/// Verifies <see cref="Simgenics.XPact.XBT.ActionGraph.ActionGraph"/>
/// against the invariants the executor and the toolchain depend on:
/// topological sort with deterministic tiebreak, conflict detection,
/// cycle detection, and the subset-walk used by Phase 2 XLiveCoding.
/// </summary>
/// <remarks>
/// Test fixtures construct synthetic actions with deterministic paths
/// rooted at the per-test scratch directory. <see cref="FileItem"/> is
/// the canonical identity; we never use <see cref="ExternalAction.Create"/>
/// directly with mismatched sort order in the happy path (the
/// constructor would throw); the conflict / cycle tests build degenerate
/// cases by hand.
/// </remarks>
public sealed class ActionGraphTests : IDisposable
{
    private readonly string _scratchDir;
    private int _seq;

    public ActionGraphTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ActionGraph",
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
    /// A -> B -> C: linear chain. Each action consumes the previous one's
    /// produced file and produces a new one. After Link/DetectCycles/Sort
    /// the order must be exactly [A, B, C].
    /// </summary>
    [Fact]
    public void LinearChain_TopologicalOrderIsDeterministic()
    {
        FileItem a_in = MakeFileItem("a-in");
        FileItem a_out = MakeFileItem("a-out");
        FileItem b_out = MakeFileItem("b-out");
        FileItem c_out = MakeFileItem("c-out");

        IExternalAction a = MakeAction("A", new[] { a_in }, new[] { a_out });
        IExternalAction b = MakeAction("B", new[] { a_out }, new[] { b_out });
        IExternalAction c = MakeAction("C", new[] { b_out }, new[] { c_out });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { c, a, b });
        graph.Link();
        graph.DetectCycles();
        IReadOnlyList<LinkedAction> sorted = graph.Sort();

        Assert.Equal(3, sorted.Count);
        Assert.Same(a, sorted[0].Action);
        Assert.Same(b, sorted[1].Action);
        Assert.Same(c, sorted[2].Action);
    }

    /// <summary>
    /// Diamond: A -> {B, C} -> D. B and C are independent (no prerequisite
    /// link between them); they must both appear before D and after A.
    /// </summary>
    [Fact]
    public void DiamondGraph_IndependentBranches_BothPrecedeJoin()
    {
        FileItem a_out = MakeFileItem("a-out");
        FileItem b_out = MakeFileItem("b-out");
        FileItem c_out = MakeFileItem("c-out");
        FileItem d_out = MakeFileItem("d-out");

        IExternalAction a = MakeAction("A", Array.Empty<FileItem>(), new[] { a_out });
        IExternalAction b = MakeAction("B", new[] { a_out }, new[] { b_out });
        IExternalAction c = MakeAction("C", new[] { a_out }, new[] { c_out });
        IExternalAction d = MakeAction("D", new[] { b_out, c_out }, new[] { d_out });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a, b, c, d });
        graph.Link();
        graph.DetectCycles();
        IReadOnlyList<LinkedAction> sorted = graph.Sort();

        int idx_a = IndexOf(sorted, a);
        int idx_b = IndexOf(sorted, b);
        int idx_c = IndexOf(sorted, c);
        int idx_d = IndexOf(sorted, d);

        Assert.True(idx_a < idx_b);
        Assert.True(idx_a < idx_c);
        Assert.True(idx_b < idx_d);
        Assert.True(idx_c < idx_d);
    }

    /// <summary>
    /// A produces X, B consumes X and produces Y, A also consumes Y --
    /// classic cycle. DetectCycles must throw with a path that names both
    /// actions.
    /// </summary>
    [Fact]
    public void Cycle_ThrowsActionGraphCycleException()
    {
        FileItem x = MakeFileItem("x");
        FileItem y = MakeFileItem("y");

        // A: produces X, prerequisite Y.
        IExternalAction a = MakeAction("A", new[] { y }, new[] { x });
        // B: produces Y, prerequisite X.
        IExternalAction b = MakeAction("B", new[] { x }, new[] { y });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a, b });
        graph.Link();

        ActionGraphCycleException ex = Assert.Throws<ActionGraphCycleException>(() => graph.DetectCycles());
        Assert.Equal(80, ex.ExitCode);
        Assert.NotEmpty(ex.CyclePath);
    }

    /// <summary>
    /// Two actions produce the same FileItem with different
    /// CommandVersions. Link must throw ActionGraphConflictException
    /// (exit 80).
    /// </summary>
    [Fact]
    public void Conflict_TwoActionsProducingSameFileWithDifferentCommands_Throws()
    {
        FileItem shared = MakeFileItem("shared-out");
        IExternalAction a = MakeAction("A", Array.Empty<FileItem>(), new[] { shared }, args: new[] { "-DA=1" });
        IExternalAction b = MakeAction("B", Array.Empty<FileItem>(), new[] { shared }, args: new[] { "-DB=1" });

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { a, b });
        ActionGraphConflictException ex = Assert.Throws<ActionGraphConflictException>(() => graph.Link());
        Assert.Equal(80, ex.ExitCode);
        Assert.Equal(shared.FullPath, ex.ConflictPath);
    }

    /// <summary>
    /// Deterministic sort: the same input list, with the same actions,
    /// produces the same sorted order across two ActionGraph instances.
    /// We test by constructing the graph twice and comparing the resulting
    /// CommandVersion sequence.
    /// </summary>
    [Fact]
    public void DeterministicSort_TwoIdenticalGraphs_SameOrder()
    {
        FileItem aOut = MakeFileItem("a");
        FileItem bOut = MakeFileItem("b");
        FileItem cOut = MakeFileItem("c");
        FileItem dOut = MakeFileItem("d");

        IExternalAction[] actions = new[]
        {
            MakeAction("A", Array.Empty<FileItem>(), new[] { aOut }),
            MakeAction("B", Array.Empty<FileItem>(), new[] { bOut }),
            MakeAction("C", Array.Empty<FileItem>(), new[] { cOut }),
            MakeAction("D", Array.Empty<FileItem>(), new[] { dOut }),
        };

        var graph1 = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(actions);
        graph1.Link();
        graph1.DetectCycles();
        IReadOnlyList<LinkedAction> sorted1 = graph1.Sort();

        // Same actions, but presented in reverse order:
        IExternalAction[] reversed = actions.Reverse().ToArray();
        var graph2 = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(reversed);
        graph2.Link();
        graph2.DetectCycles();
        IReadOnlyList<LinkedAction> sorted2 = graph2.Sort();

        // The sort must produce the same CommandVersion sequence in both
        // graphs because the CommandVersion-keyed tiebreak rule does not
        // depend on the input order.
        Assert.Equal(sorted1.Count, sorted2.Count);
        for (int i = 0; i < sorted1.Count; i++)
        {
            Assert.Equal(sorted1[i].CommandVersion, sorted2[i].CommandVersion);
        }
    }

    /// <summary>
    /// GetActionsForSubset returns only the closure of the named modules.
    /// We build a graph of three modules; subset on Module A returns only
    /// A's actions plus any transitively required deps.
    /// </summary>
    [Fact]
    public void Subset_ReturnsOnlyNamedModuleClosure()
    {
        // Module A produces only via its own actions (no shared inputs).
        FileItem a_out = MakeFileItem("a-out");
        IExternalAction aAction = MakeAction("A.compile", Array.Empty<FileItem>(), new[] { a_out }, module: "A");

        // Module B uses A's output -- this is the transitive link.
        FileItem b_out = MakeFileItem("b-out");
        IExternalAction bAction = MakeAction("B.compile", new[] { a_out }, new[] { b_out }, module: "B");

        // Module C is independent of A and B.
        FileItem c_out = MakeFileItem("c-out");
        IExternalAction cAction = MakeAction("C.compile", Array.Empty<FileItem>(), new[] { c_out }, module: "C");

        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(new[] { aAction, bAction, cAction });
        graph.Link();
        graph.DetectCycles();
        graph.Sort();

        // Subset by module name "A": only A's action.
        IReadOnlyList<LinkedAction> subsetA = graph.GetActionsForSubset(new[] { "A" });
        Assert.Single(subsetA);
        Assert.Same(aAction, subsetA[0].Action);

        // Subset by module name "B": A + B (B depends transitively on A).
        IReadOnlyList<LinkedAction> subsetB = graph.GetActionsForSubset(new[] { "B" });
        Assert.Equal(2, subsetB.Count);
        Assert.Contains(subsetB, la => ReferenceEquals(la.Action, aAction));
        Assert.Contains(subsetB, la => ReferenceEquals(la.Action, bAction));
        Assert.DoesNotContain(subsetB, la => ReferenceEquals(la.Action, cAction));
    }

    /// <summary>
    /// SortedActions throws when accessed before Link/DetectCycles.
    /// </summary>
    [Fact]
    public void SortedActions_BeforeLink_Throws()
    {
        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(Array.Empty<IExternalAction>());
        Assert.Throws<InvalidOperationException>(() => graph.SortedActions);
    }

    /// <summary>
    /// Empty graph: Link + DetectCycles + Sort all return empty results,
    /// no exceptions.
    /// </summary>
    [Fact]
    public void EmptyGraph_AllOperationsAreNoOps()
    {
        var graph = new Simgenics.XPact.XBT.ActionGraph.ActionGraph(Array.Empty<IExternalAction>());
        graph.Link();
        graph.DetectCycles();
        Assert.Empty(graph.Sort());
        Assert.Empty(graph.SortedActions);
    }

    // ----- Helpers -----

    private IExternalAction MakeAction(
        string name,
        IReadOnlyList<FileItem> prereqs,
        IReadOnlyList<FileItem> produces,
        IReadOnlyList<string>? args = null,
        string? module = null)
    {
        // Sort prereqs and produces by FullPath so the constructor's
        // sort invariant holds.
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
            CommandArguments = args ?? Array.Empty<string>(),
            WorkingDirectory = _scratchDir,
            CommandDescription = "Test",
            StatusDescription = name,
            Module = module,
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

    private static int IndexOf(IReadOnlyList<LinkedAction> sorted, IExternalAction action)
    {
        for (int i = 0; i < sorted.Count; i++)
        {
            if (ReferenceEquals(sorted[i].Action, action))
            {
                return i;
            }
        }
        return -1;
    }
}
