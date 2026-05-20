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
        // Audit fix C3: producer-map keys are FileItem.FullPath. Every
        // FileItem in the build goes through FileItem.GetItemByPath which
        // normalizes the drive letter to uppercase on Windows
        // (NormalizePathForCache; audit fix M4). So a producer at
        // "C:\Build\Foo.obj" and a prerequisite at "c:\build\Foo.obj"
        // (entered through different code paths) reach this map under
        // the same canonical key. StringComparer.Ordinal is therefore
        // correct on both Win64 (case-insensitive FS, normalised to
        // uppercase) and Linux/Android (genuinely case-sensitive).
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
    /// <see cref="IExternalAction.CommandVersion"/>,
    /// <see cref="IExternalAction.PrerequisiteItems"/>, or
    /// <see cref="IExternalAction.WorkingDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two actions producing the same file with the <em>same</em>
    /// <see cref="IExternalAction.CommandVersion"/>, prerequisite set, and
    /// working directory are tolerated -- they are byte-for-byte identical
    /// and one is a redundant declaration. In practice this case is rare;
    /// XBT subsystems should not duplicate emission. The first occurrence
    /// wins.
    /// </para>
    /// <para>
    /// Audit fix R6-C3: <see cref="IExternalAction.CommandVersion"/> alone
    /// is not sufficient to declare two producers byte-identical. Two
    /// actions can share a command-line + working-directory + cache-key
    /// payload (so <see cref="IExternalAction.CommandVersion"/> matches)
    /// yet differ in their prerequisite set -- e.g. one declares a header
    /// dependency the other does not. The differing prerequisites mean
    /// the two actions WOULD invalidate under different conditions, so
    /// silently dropping one would suppress staleness signals. Each
    /// extra check below names the divergent field in its exception
    /// message so a future graph builder bug is easier to diagnose.
    /// </para>
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
                            secondAction: linked.Description,
                            divergentField: "CommandVersion");
                    }
                    // Audit fix R6-C3: same CommandVersion but
                    // divergent prerequisite-item sets means the two
                    // actions would invalidate under different
                    // conditions. Surface this as a conflict, not a
                    // warning -- silently dropping one suppresses real
                    // staleness signals.
                    if (!PrerequisiteSetsEqual(existing.Action, linked.Action))
                    {
                        throw new ActionGraphConflictException(
                            produced.FullPath,
                            firstAction: existing.Description,
                            secondAction: linked.Description,
                            divergentField: "PrerequisiteItems");
                    }
                    if (!string.Equals(
                            existing.Action.WorkingDirectory,
                            linked.Action.WorkingDirectory,
                            StringComparison.Ordinal))
                    {
                        throw new ActionGraphConflictException(
                            produced.FullPath,
                            firstAction: existing.Description,
                            secondAction: linked.Description,
                            divergentField: "WorkingDirectory");
                    }
                    // Audit fix M16: byte-identical duplicates are
                    // tolerated (keeping the first), but no longer
                    // silently. Emit a warning so the duplicate
                    // emission shows up in the build log -- a subsystem
                    // double-registering an action is almost always a
                    // bug worth investigating.
                    Logger.Warning(
                        $"Action graph: byte-identical duplicate producer for " +
                        $"'{produced.FullPath}'. First: '{existing.Description}'; " +
                        $"duplicate: '{linked.Description}'. Keeping the first; " +
                        "subsystem may be double-emitting.",
                        new DiagnosticContext { Action = "action-graph-link" });
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
    /// Audit fix R6-C6: proactively validate that every produced /
    /// prerequisite path on Windows fits in MAX_PATH. cl.exe and
    /// link.exe surface MAX_PATH overflow as cryptic "cannot open file"
    /// or "path not found" errors deep inside the compile / link;
    /// checking up front emits an actionable diagnostic naming the
    /// offending path and the action it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Linux / macOS this is a no-op -- paths can run to 4096 bytes
    /// (PATH_MAX) and the toolchains correctly fail with ENAMETOOLONG
    /// at the actual offender.
    /// </para>
    /// <para>
    /// Exit code 71 (<c>LinkFailed</c>) is the closest existing code in
    /// the Toolchain Contract Section 13 surface: a path-length-induced
    /// failure manifests as a link-stage failure on Windows because
    /// link.exe is most often the offender (intermediate object paths
    /// + library search paths combine to exceed MAX_PATH long before
    /// any individual source file does). The contract does not have a
    /// dedicated "path too long" code; 71 is the appropriate proxy
    /// rather than introducing a new code that would bump the
    /// contract surface.
    /// </para>
    /// </remarks>
    public void CheckPathLengths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int WindowsMaxPath = 260;
        List<string> offenders = new();
        foreach (LinkedAction linked in _all)
        {
            foreach (FileItem produced in linked.Action.ProducedItems)
            {
                if (produced.FullPath.Length > WindowsMaxPath)
                {
                    offenders.Add(
                        $"  Produced item ({produced.FullPath.Length} chars) " +
                        $"of action '{linked.Description}':\n" +
                        $"    {produced.FullPath}");
                }
            }
            foreach (FileItem prereq in linked.Action.PrerequisiteItems)
            {
                if (prereq.FullPath.Length > WindowsMaxPath)
                {
                    offenders.Add(
                        $"  Prerequisite ({prereq.FullPath.Length} chars) " +
                        $"of action '{linked.Description}':\n" +
                        $"    {prereq.FullPath}");
                }
            }
        }

        if (offenders.Count == 0)
        {
            return;
        }

        string message =
            $"ActionGraph: {offenders.Count} path(s) exceed the Windows MAX_PATH limit ({WindowsMaxPath} characters). " +
            "MSVC and link.exe surface this as cryptic 'cannot open file' / 'path not found' errors deep inside " +
            "compile / link. Shorten the path (move the engine / project closer to the drive root) or enable " +
            "long-path support engine-wide (Toolchain Contract Section 13 exit code 71 -- LinkFailed -- is the " +
            "closest existing code; a path-length failure manifests as a link-stage failure in practice).\n" +
            string.Join("\n", offenders);
        throw new XBTException(message, exitCode: 71);
    }

    /// <summary>
    /// Audit fix R6-C3 helper: compare two actions' prerequisite-item
    /// lists by path. Returns true iff both lists are the same length
    /// and contain the same paths in the same order. Both lists are
    /// guaranteed sorted by <see cref="ExternalAction.Create"/>'s
    /// invariant check, so a sequential ordinal compare is sufficient.
    /// </summary>
    private static bool PrerequisiteSetsEqual(IExternalAction a, IExternalAction b)
    {
        IReadOnlyList<FileItem> ap = a.PrerequisiteItems;
        IReadOnlyList<FileItem> bp = b.PrerequisiteItems;
        if (ap.Count != bp.Count)
        {
            return false;
        }
        for (int i = 0; i < ap.Count; i++)
        {
            if (!string.Equals(ap[i].FullPath, bp[i].FullPath, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
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
