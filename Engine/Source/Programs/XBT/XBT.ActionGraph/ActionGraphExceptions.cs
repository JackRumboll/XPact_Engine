// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// Two distinct actions produced the same <see cref="FileItem"/> with
/// different commands. Mirrors UE's
/// <c>FActionGraph::CheckForConflicts</c> exit but with a typed exception
/// + structured diagnostic.
/// </summary>
/// <remarks>
/// Maps to Toolchain Contract Rev 13 Section 13 exit code <c>80</c>
/// (<c>ActionGraphCycle</c> family -- "internal: should never happen;
/// symptom of a graph builder bug"). The bug is in whichever subsystem
/// emitted the two conflicting actions; the diagnostic names the file
/// and the two action descriptions to point at the source.
/// </remarks>
public sealed class ActionGraphConflictException : XBTException
{
    /// <summary>The conflicting output file.</summary>
    public string ConflictPath { get; }

    /// <summary>Description of the first producer.</summary>
    public string FirstAction { get; }

    /// <summary>Description of the second (conflicting) producer.</summary>
    public string SecondAction { get; }

    /// <summary>Construct an action-graph conflict.</summary>
    public ActionGraphConflictException(string conflictPath, string firstAction, string secondAction)
        : base(
            $"ActionGraph conflict: file '{conflictPath}' is produced by two actions with "
            + $"different commands. First: {firstAction}. Second: {secondAction}.",
            exitCode: 80)
    {
        ConflictPath = conflictPath;
        FirstAction = firstAction;
        SecondAction = secondAction;
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
