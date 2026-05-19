// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// An <see cref="IExternalAction"/> wrapped with its resolved prerequisite
/// and dependent actions. Built by <see cref="ActionGraph.Link"/>.
/// </summary>
/// <remarks>
/// <para>
/// The action graph keeps a one-to-one mapping
/// (<see cref="IExternalAction"/> -> <see cref="LinkedAction"/>); the
/// linked wrapper is the form the executor and the
/// <see cref="ActionGraph.SortedActions"/> consumer iterate.
/// </para>
/// <para>
/// Equality is reference-based. Two linked actions are distinct nodes
/// even when their <see cref="IExternalAction.CommandVersion"/>s collide
/// (which only happens for two genuinely-identical commands; that case
/// is caught earlier by <see cref="ActionGraph.Link"/>'s conflict
/// detection).
/// </para>
/// </remarks>
public sealed class LinkedAction
{
    private readonly List<LinkedAction> _prerequisiteActions = new();
    private readonly List<LinkedAction> _dependentActions = new();

    /// <summary>The underlying action this wrapper carries.</summary>
    public IExternalAction Action { get; }

    /// <summary>
    /// Actions that must complete before this action can run -- the
    /// producers of <see cref="IExternalAction.PrerequisiteItems"/>.
    /// </summary>
    public IReadOnlyList<LinkedAction> PrerequisiteActions => _prerequisiteActions;

    /// <summary>
    /// Actions that consume this action's outputs. Populated by
    /// <see cref="ActionGraph.Link"/> as the reverse of
    /// <see cref="PrerequisiteActions"/> across the graph.
    /// </summary>
    public IReadOnlyList<LinkedAction> DependentActions => _dependentActions;

    /// <summary>Construct a linked wrapper for the underlying action.</summary>
    public LinkedAction(IExternalAction action)
    {
        Action = action;
    }

    /// <summary>
    /// Append a prerequisite link (this action depends on
    /// <paramref name="prerequisite"/>) and the matching dependent link
    /// on the other side. Internal so only
    /// <see cref="ActionGraph"/> populates the topology.
    /// </summary>
    internal void AddPrerequisite(LinkedAction prerequisite)
    {
        _prerequisiteActions.Add(prerequisite);
        prerequisite._dependentActions.Add(this);
    }

    /// <summary>
    /// Convenience: the action's command version, surfaced for
    /// deterministic sorting at the executor's dispatch site.
    /// </summary>
    public IoHash CommandVersion => Action.CommandVersion;

    /// <summary>
    /// Human-readable label: <c>"Compile XScoring.cpp"</c> etc.
    /// </summary>
    public string Description =>
        string.IsNullOrEmpty(Action.CommandDescription)
            ? Action.StatusDescription
            : $"{Action.CommandDescription} {Action.StatusDescription}";

    /// <inheritdoc/>
    public override string ToString() => Description;
}
