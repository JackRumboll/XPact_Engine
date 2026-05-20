// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;

namespace Simgenics.XPact.XBT.LiveCoding;

/// <summary>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 16.5: a per-toolchain
/// hook that mutates the action graph after <c>Link()</c>+<c>Sort()</c>
/// but before executor dispatch. Phase 1 has an empty patcher list;
/// Phase 2 XLiveCoding adds <c>XLiveCodingPatcher</c>. Routes through
/// the originating toolchain class, NOT through executable-name string
/// matching (preempts UE footgun #5).
/// </summary>
/// <remarks>
/// <para>
/// Phase 1: <see cref="PatcherRegistry"/> is empty; the BuildMode call
/// to <c>ApplyAll</c> is a no-op. Phase 2 XLiveCoding registers itself
/// before BuildMode runs the executor; its <see cref="Patch"/> rewrites
/// the Compile action arguments (<c>.obj &rarr; .lc.obj</c>, response
/// file extension &rarr; <c>.lc.response</c>, dependency file path).
/// </para>
/// <para>
/// Patchers must be <em>idempotent</em>: a second invocation against
/// the same action list must produce the same result as the first.
/// </para>
/// </remarks>
public interface IActionGraphPatcher
{
    /// <summary>
    /// Name of the patcher for diagnostic output
    /// (e.g., <c>"LiveCodingPatcher"</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Patch the action graph in place. Phase 2 XLiveCoding rewrites
    /// Compile action arguments (.obj &rarr; .lc.obj, response file
    /// ext &rarr; .lc.response, dependency file path).
    /// </summary>
    /// <param name="actions">The linked action list, in topological order.</param>
    /// <param name="context">Patcher context -- target, configuration, station role, log channel.</param>
    void Patch(IList<LinkedAction> actions, PatcherContext context);
}

/// <summary>
/// Per-call context passed to every <see cref="IActionGraphPatcher"/>.
/// Holds the active <see cref="TargetRules"/>, the absolute engine root
/// path, and the diagnostic log channel name the patcher should write
/// diagnostics to.
/// </summary>
/// <param name="Target">The active target. The patcher consults
/// <see cref="TargetRules.bAllowHotReload"/> and
/// <see cref="TargetRules.StationRole"/> per the Section 16.4
/// enforcement matrix.</param>
/// <param name="EngineRootPath">Absolute path to the engine root.
/// Patchers compose intermediate paths under
/// <c>&lt;EngineRootPath&gt;/Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/</c>.</param>
/// <param name="LogChannel">Free-form channel tag the patcher uses
/// when emitting diagnostics so JSON-channel consumers can filter on
/// the originating subsystem (e.g. <c>"LiveCoding"</c>).</param>
public sealed record PatcherContext(
    TargetRules Target,
    string EngineRootPath,
    string LogChannel);
