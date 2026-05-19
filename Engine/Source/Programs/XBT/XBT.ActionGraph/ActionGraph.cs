// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// The build-time DAG of <see cref="IExternalAction"/> nodes. Owns the
/// producer map, conflict detection, cycle detection, and the
/// deterministic topological sort.
/// </summary>
/// <remarks>
/// <para>
/// Construction is two-phase: pass the raw actions into the constructor,
/// then call <see cref="Link"/> to resolve each action's prerequisites
/// (via the producer map) and detect cycles. Most callers want
/// <see cref="SortedActions"/> after <see cref="Link"/> completes.
/// </para>
/// <para>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 5.3:
/// </para>
/// <list type="bullet">
///   <item><see cref="Link"/> builds <c>FileItem -&gt; producer</c> and rejects multi-producer outputs.</item>
///   <item><see cref="DetectCycles"/> runs Kahn's algorithm; on cycle, throws <see cref="ActionGraphCycleException"/>.</item>
///   <item><see cref="SortedActions"/> returns the topological order with deterministic <see cref="IoHash"/> tiebreak.</item>
///   <item><see cref="GetActionsForSubset"/> returns the action sub-graph rooted at named modules (Section 16.6).</item>
/// </list>
/// </remarks>
public sealed class ActionGraph
{
    /// <summary>
    /// The deterministic ordering rule for <see cref="LinkedAction"/>
    /// instances at the same depth: ordinal comparison on the 32-byte
    /// <see cref="IoHash"/> digest. Stable across runs and machines
    /// because both operands are BLAKE3 of the same input.
    /// </summary>
    private static readonly Comparer<LinkedAction> s_commandVersionComparer = Comparer<LinkedAction>.Create(
        static (a, b) =>
        {
            byte[] aBytes = a.CommandVersion.ToByteArray();
            byte[] bBytes = b.CommandVersion.ToByteArray();
            for (int i = 0; i < IoHash.Length; i++)
            {
                int cmp = aBytes[i].CompareTo(bBytes[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            return 0;
        });

    private readonly List<LinkedAction> _all;
    private readonly Dictionary<string, LinkedAction> _producerByPath;
    private List<LinkedAction>? _sortedActions;

    /// <summary>All actions in the graph, in original input order.</summary>
    public IReadOnlyList<LinkedAction> AllActions => _all;

    /// <summary>
    /// Map of produced-file absolute path to the <see cref="LinkedAction"/>
    /// that produces it. Populated by <see cref="Link"/>. Two actions
    /// producing the same file with different commands fail
    /// <see cref="Link"/> with <see cref="ActionGraphConflictException"/>.
    /// </summary>
    public IReadOnlyDictionary<string, LinkedAction> ProducerByPath => _producerByPath;

    /// <summary>
    /// Topological order suitable for sequential or parallel dispatch.
    /// Available after <see cref="Link"/> + <see cref="DetectCycles"/>;
    /// throws if accessed before. Within a single "ready set" (all
    /// prerequisites satisfied), ties are broken by ordinal comparison
    /// on <see cref="IoHash"/> so two runs of the same graph yield the
    /// same execution order across machines.
    /// </summary>
    public IReadOnlyList<LinkedAction> SortedActions
    {
        get
        {
            if (_sortedActions is null)
            {
                throw new InvalidOperationException(
                    "ActionGraph.SortedActions is not available before Link() + DetectCycles() complete.");
            }
            return _sortedActions;
        }
    }

    /// <summary>
    /// Construct the graph from a flat set of actions. Construction does
    /// not link or sort -- callers run those phases explicitly so they
    /// can inspect intermediate state on failure.
    /// </summary>
    public ActionGraph(IEnumerable<IExternalAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _all = new List<LinkedAction>();
        _producerByPath = new Dictionary<string, LinkedAction>(StringComparer.Ordinal);
        foreach (IExternalAction action in actions)
        {
            _all.Add(new LinkedAction(action));
        }
    }

    /// <summary>
    /// Build the <c>FileItem -&gt; producer</c> map, validate uniqueness,
    /// and resolve each action's <see cref="LinkedAction.PrerequisiteActions"/>
    /// from the map. Throws <see cref="ActionGraphConflictException"/>
    /// (exit 80) on a duplicate-producer with different
    /// <see cref="IExternalAction.CommandVersion"/>.
    /// </summary>
    /// <remarks>
    /// Two actions producing the same file with the <em>same</em>
    /// <see cref="IExternalAction.CommandVersion"/> are tolerated -- they
    /// are byte-for-byte identical and one is a redundant declaration.
    /// In practice this case is rare; XBT subsystems should not duplicate
    /// emission. The first occurrence wins.
    /// </remarks>
    public void Link()
    {
        // 1. Producer map. Iterate in input order so the "first wins"
        //    rule applies deterministically.
        foreach (LinkedAction linked in _all)
        {
            foreach (FileItem produced in linked.Action.ProducedItems)
            {
                if (_producerByPath.TryGetValue(produced.FullPath, out LinkedAction? existing))
                {
                    if (existing.CommandVersion != linked.CommandVersion)
                    {
                        throw new ActionGraphConflictException(
                            produced.FullPath,
                            firstAction: existing.Description,
                            secondAction: linked.Description);
                    }
                    // Same command version -- byte-identical duplicate.
                    // Keep the first; do not double-register.
                    continue;
                }
                _producerByPath.Add(produced.FullPath, linked);
            }
        }

        // 2. Resolve each action's prerequisites. A prerequisite that
        //    has no producer is a leaf (e.g. an on-disk source file)
        //    and contributes no edge.
        foreach (LinkedAction linked in _all)
        {
            foreach (FileItem prereq in linked.Action.PrerequisiteItems)
            {
                if (_producerByPath.TryGetValue(prereq.FullPath, out LinkedAction? producer)
                    && !ReferenceEquals(producer, linked))
                {
                    linked.AddPrerequisite(producer);
                }
            }
        }
    }

    /// <summary>
    /// Run Kahn's algorithm + a stable secondary DFS to detect any
    /// remaining cycles. On cycle, throws
    /// <see cref="ActionGraphCycleException"/> (exit 80) carrying the
    /// cycle path as a list of action descriptions in traversal order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// We do not rely on <see cref="Link"/> raising on cycles -- the
    /// linker only writes the edge list. Kahn detects the cycle here so
    /// the diagnostic can name a specific cycle path.
    /// </para>
    /// <para>
    /// The cycle path is built by a DFS from a not-yet-emitted node when
    /// Kahn's queue empties prematurely. The first cycle found is
    /// reported; further cycles in the same graph are not enumerated to
    /// keep the diagnostic readable.
    /// </para>
    /// </remarks>
    public void DetectCycles()
    {
        Dictionary<LinkedAction, int> indegree = new(_all.Count);
        foreach (LinkedAction linked in _all)
        {
            indegree[linked] = linked.PrerequisiteActions.Count;
        }

        // Kahn's: enqueue every node with indegree 0, drain by decrementing
        // dependents' indegree until the queue is empty. Order within the
        // ready-set does not matter for cycle detection; we use a plain
        // Queue<> here -- the deterministic ordering is applied later in
        // ComputeSortedOrder().
        Queue<LinkedAction> ready = new();
        foreach ((LinkedAction node, int deg) in indegree)
        {
            if (deg == 0)
            {
                ready.Enqueue(node);
            }
        }

        int processed = 0;
        while (ready.Count > 0)
        {
            LinkedAction node = ready.Dequeue();
            processed++;
            foreach (LinkedAction dep in node.DependentActions)
            {
                int newDeg = indegree[dep] - 1;
                indegree[dep] = newDeg;
                if (newDeg == 0)
                {
                    ready.Enqueue(dep);
                }
            }
        }

        if (processed == _all.Count)
        {
            return;
        }

        // Cycle detected. Find one specific cycle by DFS from any node
        // that still has indegree > 0 in the residual graph. Tarjan
        // would be more general (it finds every SCC) but the build's
        // diagnostic only needs to name one cycle.
        IReadOnlyList<string> cyclePath = FindOneCycle(indegree);
        throw new ActionGraphCycleException(cyclePath);
    }

    private static IReadOnlyList<string> FindOneCycle(IReadOnlyDictionary<LinkedAction, int> indegree)
    {
        // Start from a node with positive indegree -- one such must
        // exist since Kahn could not drain it. Walk through its
        // prerequisites until we revisit a node; the back-edge closes
        // the cycle.
        LinkedAction? start = null;
        foreach ((LinkedAction node, int deg) in indegree)
        {
            if (deg > 0)
            {
                start = node;
                break;
            }
        }
        if (start is null)
        {
            // Should be unreachable -- Kahn's emit count exactly
            // matches drain only when there are no cycles.
            return Array.Empty<string>();
        }

        // DFS through prerequisites looking for any back edge.
        Dictionary<LinkedAction, int> stackIndex = new();
        List<LinkedAction> stack = new();
        return DfsForCycle(start, stack, stackIndex);
    }

    private static IReadOnlyList<string> DfsForCycle(
        LinkedAction node,
        List<LinkedAction> stack,
        Dictionary<LinkedAction, int> stackIndex)
    {
        if (stackIndex.TryGetValue(node, out int existingIndex))
        {
            // Cycle closes between stack[existingIndex] and the top.
            List<string> path = new();
            for (int i = existingIndex; i < stack.Count; i++)
            {
                path.Add(stack[i].Description);
            }
            path.Add(node.Description);
            return path;
        }
        stackIndex[node] = stack.Count;
        stack.Add(node);

        foreach (LinkedAction prereq in node.PrerequisiteActions)
        {
            IReadOnlyList<string> result = DfsForCycle(prereq, stack, stackIndex);
            if (result.Count > 0)
            {
                return result;
            }
        }

        stack.RemoveAt(stack.Count - 1);
        stackIndex.Remove(node);
        return Array.Empty<string>();
    }

    /// <summary>
    /// Compute the deterministic topological order. Must be called
    /// after <see cref="Link"/> and <see cref="DetectCycles"/>. The
    /// result is cached on <see cref="SortedActions"/>.
    /// </summary>
    public IReadOnlyList<LinkedAction> Sort()
    {
        if (_sortedActions is not null)
        {
            return _sortedActions;
        }

        Dictionary<LinkedAction, int> indegree = new(_all.Count);
        foreach (LinkedAction linked in _all)
        {
            indegree[linked] = linked.PrerequisiteActions.Count;
        }

        // SortedSet keyed on (CommandVersion ordinal compare) gives a
        // deterministic dequeue order within each "ready set" without
        // costing an O(N log N) sort per drain step. SortedSet is a
        // red-black tree under the hood; Min is O(log N).
        SortedSet<LinkedAction> ready = new(s_commandVersionComparer);
        foreach ((LinkedAction node, int deg) in indegree)
        {
            if (deg == 0)
            {
                ready.Add(node);
            }
        }

        List<LinkedAction> sorted = new(_all.Count);
        while (ready.Count > 0)
        {
            LinkedAction node = ready.Min!;
            ready.Remove(node);
            sorted.Add(node);
            foreach (LinkedAction dep in node.DependentActions)
            {
                int newDeg = indegree[dep] - 1;
                indegree[dep] = newDeg;
                if (newDeg == 0)
                {
                    ready.Add(dep);
                }
            }
        }

        // If Sort runs before DetectCycles, the order may be incomplete;
        // we surface that as a precondition rather than a silent partial
        // result.
        if (sorted.Count != _all.Count)
        {
            throw new InvalidOperationException(
                "ActionGraph.Sort() ran without DetectCycles() and the graph contains a cycle; "
                + "call DetectCycles() first for a precise diagnostic.");
        }

        _sortedActions = sorted;
        return sorted;
    }

    /// <summary>
    /// Return the deterministic action sub-graph rooted at the given
    /// module names per <c>/Documents/XBT.html</c> Section 16.6. The
    /// closure includes every action that contributes (transitively) to
    /// the named modules' link step; actions unrelated to the named
    /// modules are excluded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Module membership is matched against the action's
    /// <see cref="IExternalAction.Module"/> field. Cross-module
    /// prerequisites (e.g. an XHT-generated header consumed by another
    /// module) are pulled in transitively through the prerequisite walk.
    /// </para>
    /// <para>
    /// The result is topologically sorted with the same deterministic
    /// tiebreak rule as <see cref="SortedActions"/>. Used by the Phase 2
    /// XLiveCoding cascade for compile-only passes over a named module set.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LinkedAction> GetActionsForSubset(IEnumerable<string> moduleNames)
    {
        ArgumentNullException.ThrowIfNull(moduleNames);
        HashSet<string> wanted = new(moduleNames, StringComparer.Ordinal);

        // 1. Seed: every action whose Module is in `wanted`.
        HashSet<LinkedAction> visited = new();
        Stack<LinkedAction> work = new();
        foreach (LinkedAction linked in _all)
        {
            if (linked.Action.Module is { } moduleName && wanted.Contains(moduleName))
            {
                if (visited.Add(linked))
                {
                    work.Push(linked);
                }
            }
        }

        // 2. Walk prerequisites transitively.
        while (work.Count > 0)
        {
            LinkedAction node = work.Pop();
            foreach (LinkedAction prereq in node.PrerequisiteActions)
            {
                if (visited.Add(prereq))
                {
                    work.Push(prereq);
                }
            }
        }

        // 3. Topo-sort the subset. We reuse the precomputed
        //    `SortedActions` order when it exists -- preserves the same
        //    deterministic tiebreak as the full graph. Otherwise we
        //    compute it inline.
        IReadOnlyList<LinkedAction> sortedFull = _sortedActions ?? Sort();
        return sortedFull.Where(visited.Contains).ToList();
    }
}
