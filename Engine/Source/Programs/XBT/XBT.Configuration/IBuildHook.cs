// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Threading;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Typed pre/post-build hook per Toolchain Contract Rev 13 Section 9.5
/// and <c>/Documents/XBT.html</c> Rev 4 Section 5.7. Hooks return a
/// first-class <c>IExternalAction</c> (defined by
/// <c>XBT.ActionGraph</c>) that XBT's executor schedules as a normal
/// graph node -- cacheable by input content hash + the action's
/// <c>CacheKeyComponents</c>, incrementalizable, parallelizable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why typed, not freeform shell?</b> Per Contract Section 9.5: UE's
/// <c>CustomBuildSteps.cs</c> pattern (freeform shell command strings
/// with environment variable interpolation) is the source of most
/// "I cannot reproduce this on my machine" build bugs. XPact rejects
/// that approach entirely; hooks declare inputs and outputs ahead of
/// time so the action graph can prove what they depend on and what
/// they produce.
/// </para>
/// <para>
/// <b>Validation rules</b> XBT enforces at action-graph link time:
/// </para>
/// <list type="bullet">
///   <item>A hook with no declared inputs is flagged as a warning --
///   the hook will run on every build and never cache.</item>
///   <item>A hook whose action writes a file not declared in
///   <see cref="OutputFiles"/> fails the build with exit code <strong>81</strong>
///   (<c>BuildHookOutputMismatch</c> per Contract Section 13).</item>
///   <item>A hook that fails to produce a declared output also fails
///   with exit code 81.</item>
/// </list>
/// </remarks>
public interface IBuildHook
{
    /// <summary>
    /// Declared input files (absolute paths). XBT tracks these as the
    /// returned action's <c>PrerequisiteItems</c> so input content
    /// hashes drive incrementalization.
    /// </summary>
    IReadOnlyList<string> InputFiles { get; }

    /// <summary>
    /// Declared output files (absolute paths). XBT tracks these as the
    /// returned action's <c>ProducedItems</c>. Writing outside this
    /// declared set fails the build with exit 81.
    /// </summary>
    IReadOnlyList<string> OutputFiles { get; }

    /// <summary>
    /// Construct the <c>IExternalAction</c> the hook runs as. The
    /// returned action participates in the action graph fully:
    /// scheduled by the executor, cached by ActionHistory +
    /// CacheKeyComponents, skipped on incremental rebuild when
    /// inputs are unchanged and outputs are current.
    /// </summary>
    /// <param name="context">
    /// Context populated by XBT immediately before the call:
    /// the owning module, the target, the intermediate / output
    /// directories, and a cancellation token. The returned action
    /// reads <see cref="BuildHookContext"/> at construction time only
    /// -- the action graph mutates no fields on the context after
    /// construction.
    /// </param>
    /// <returns>
    /// An <c>IExternalAction</c> instance. The return type is
    /// <c>object</c> because <c>IExternalAction</c> lives in the
    /// downstream <c>XBT.ActionGraph</c> assembly which this assembly
    /// must not reference circularly. The action graph casts at
    /// link time; a non-conforming return fails the build with exit
    /// 81 (<c>BuildHookOutputMismatch</c>).
    /// </returns>
    object CreateAction(BuildHookContext context);
}

/// <summary>
/// Context passed to <see cref="IBuildHook.CreateAction"/> at action-graph
/// link time. All fields are populated by XBT before the call; the hook
/// reads them at construction.
/// </summary>
/// <param name="Module">
/// The <see cref="ModuleRules"/> this hook is attached to (via
/// <see cref="ModuleRules.PreBuildHooks"/> or
/// <see cref="ModuleRules.PostBuildHooks"/>). Read-only at the call
/// site.
/// </param>
/// <param name="Target">
/// Read-only view of the driving target.
/// </param>
/// <param name="IntermediateDir">
/// Absolute path to the intermediate directory the hook should write
/// into. Already created.
/// </param>
/// <param name="OutputDir">
/// Absolute path to the final-output directory for this module
/// (e.g. <c>/Engine/Binaries/Win64/</c>). Hooks rarely write here
/// directly; the action's atomic-rename contract (XBT.html Section
/// 6.4) is preserved by writing to <paramref name="IntermediateDir"/>
/// first.
/// </param>
/// <param name="Cancellation">
/// Cancellation token. The action XBT schedules from this context
/// participates in the standard cancellation contract per XBT.html
/// Section 6.4; the hook implementation itself rarely needs to consult
/// this token because the action body is scheduled separately.
/// </param>
public sealed record BuildHookContext(
    ModuleRules Module,
    ReadOnlyTargetRules Target,
    string IntermediateDir,
    string OutputDir,
    CancellationToken Cancellation);
