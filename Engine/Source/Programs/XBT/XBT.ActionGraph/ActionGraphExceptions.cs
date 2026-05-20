// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// Two distinct actions produced the same <see cref="FileItem"/> with
/// divergent payloads. Mirrors UE's
/// <c>FActionGraph::CheckForConflicts</c> exit but with a typed exception
/// + structured diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// Maps to Toolchain Contract Rev 13 Section 13 exit code <c>80</c>
/// (<c>ActionGraphCycle</c> family -- "internal: should never happen;
/// symptom of a graph builder bug"). The bug is in whichever subsystem
/// emitted the two conflicting actions; the diagnostic names the file,
/// the two action descriptions, and the specific divergent field to
/// point at the source.
/// </para>
/// <para>
/// Audit fix R6-C3: <see cref="DivergentField"/> records which payload
/// element differed -- <c>CommandVersion</c>, <c>PrerequisiteItems</c>,
/// or <c>WorkingDirectory</c>. This is what the operator needs to
/// diagnose the source: a divergent CommandVersion points at the
/// emitter constructing different arguments; a divergent prereq set
/// points at the emitter declaring different dependencies; a divergent
/// working dir points at the emitter rooting two actions in different
/// project subtrees.
/// </para>
/// </remarks>
public sealed class ActionGraphConflictException : XBTException
{
    /// <summary>The conflicting output file.</summary>
    public string ConflictPath { get; }

    /// <summary>Description of the first producer.</summary>
    public string FirstAction { get; }

    /// <summary>Description of the second (conflicting) producer.</summary>
    public string SecondAction { get; }

    /// <summary>
    /// Audit fix R6-C3: the <see cref="IExternalAction"/> field that
    /// differed between the two producers. One of
    /// <c>"CommandVersion"</c>, <c>"PrerequisiteItems"</c>,
    /// <c>"WorkingDirectory"</c>.
    /// </summary>
    public string DivergentField { get; }

    /// <summary>
    /// Construct an action-graph conflict naming the divergent field.
    /// </summary>
    public ActionGraphConflictException(
        string conflictPath,
        string firstAction,
        string secondAction,
        string divergentField)
        : base(
            $"ActionGraph conflict: file '{conflictPath}' is produced by two actions with "
            + $"different {divergentField}. First: {firstAction}. Second: {secondAction}.",
            exitCode: 80)
    {
        ConflictPath = conflictPath;
        FirstAction = firstAction;
        SecondAction = secondAction;
        DivergentField = divergentField;
    }

    /// <summary>
    /// Legacy two-action constructor retained for compatibility with
    /// pre-R6 callers and tests that did not name a divergent field.
    /// Defaults the divergent-field label to <c>"CommandVersion"</c>
    /// (the only divergence kind the pre-R6 code detected).
    /// </summary>
    public ActionGraphConflictException(string conflictPath, string firstAction, string secondAction)
        : this(conflictPath, firstAction, secondAction, divergentField: "CommandVersion")
    {
    }
}

/// <summary>
/// A cycle was found in the action graph. The exception carries the
/// cycle path as a list of <see cref="IExternalAction.CommandDescription"/>
/// strings.
/// </summary>
/// <remarks>
/// Maps to Toolchain Contract Rev 13 Section 13 exit code <c>80</c>
/// (<c>ActionGraphCycle</c> -- "internal: should never happen; symptom of
/// a graph builder bug"). Cycles in the build graph indicate that two
/// modules' prerequisite declarations form a loop; the diagnostic
/// enumerates the cycle for the operator.
/// </remarks>
public sealed class ActionGraphCycleException : XBTException
{
    /// <summary>The cycle as a list of action descriptions, traversal order.</summary>
    public IReadOnlyList<string> CyclePath { get; }

    /// <summary>Construct an action-graph cycle exception.</summary>
    public ActionGraphCycleException(IReadOnlyList<string> cyclePath)
        : base($"ActionGraph cycle detected: {string.Join(" -> ", cyclePath)}", exitCode: 80)
    {
        CyclePath = cyclePath;
    }
}
