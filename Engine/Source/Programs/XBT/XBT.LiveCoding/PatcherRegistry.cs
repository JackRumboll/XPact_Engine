// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XBT.ActionGraph;

namespace Simgenics.XPact.XBT.LiveCoding;

/// <summary>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 16.5: the seam Phase 2
/// XLiveCoding plugs into. Phase 1 ships with an empty registry; the
/// <c>BuildMode</c> call to <see cref="ApplyAll"/> is a no-op. Phase 2
/// XLiveCoding registers itself before BuildMode runs the executor;
/// its <see cref="IActionGraphPatcher.Patch"/> rewrites Compile action
/// arguments (.obj &rarr; .lc.obj, response file ext &rarr; .lc.response,
/// dependency file path).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering.</b> Patchers are applied in registration order. With
/// a single Phase 2 patcher (XLiveCoding) the order does not matter,
/// but the registry preserves it so future multi-patcher scenarios
/// have a deterministic shape.
/// </para>
/// <para>
/// <b>Thread safety.</b> The registry is mutated only at startup
/// (before <c>BuildMode</c> dispatches actions). The
/// <see cref="ApplyAll"/> read path takes a snapshot under a lock so
/// a concurrent <c>Register</c> on another thread cannot tear the
/// iteration.
/// </para>
/// </remarks>
public sealed class PatcherRegistry
{
    private readonly object _gate = new();
    private readonly List<IActionGraphPatcher> _patchers = new();

    /// <summary>The registered patchers, in registration order.</summary>
    public IReadOnlyList<IActionGraphPatcher> Patchers
    {
        get
        {
            lock (_gate)
            {
                // Defensive copy -- the caller iterating must not see a
                // concurrent Register tear its enumeration.
                return _patchers.ToArray();
            }
        }
    }

    /// <summary>
    /// Register a patcher. Phase 2 XLiveCoding calls this before
    /// BuildMode runs the executor. Calling twice with the same
    /// instance is allowed (no de-dup is performed); the spec
    /// rules each Phase 2 patcher registers exactly once.
    /// </summary>
    public void Register(IActionGraphPatcher patcher)
    {
        ArgumentNullException.ThrowIfNull(patcher);
        lock (_gate)
        {
            _patchers.Add(patcher);
        }
    }

    /// <summary>
    /// Phase 1: empty registry. <c>XBT.Entry/Modes/BuildMode.cs</c>
    /// calls <c>patcherRegistry.ApplyAll(actions, context)</c> -- a
    /// no-op in Phase 1 because no patchers are registered. Phase 2
    /// XLiveCoding registers itself before BuildMode runs the
    /// executor.
    /// </summary>
    /// <param name="actions">The linked action list, in topological
    /// order. Patchers receive this directly and may mutate the list
    /// (insert / remove / rewrite) under the patcher idempotence
    /// contract.</param>
    /// <param name="context">Per-call patcher context.</param>
    public void ApplyAll(IList<LinkedAction> actions, PatcherContext context)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(context);

        // Snapshot under the lock; release before invoking patcher
        // bodies so a long-running patcher does not block a concurrent
        // Register on a different thread.
        IActionGraphPatcher[] snapshot;
        lock (_gate)
        {
            if (_patchers.Count == 0)
            {
                return;
            }
            snapshot = _patchers.ToArray();
        }

        foreach (IActionGraphPatcher patcher in snapshot)
        {
            patcher.Patch(actions, context);
        }
    }
}
