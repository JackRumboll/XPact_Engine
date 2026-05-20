// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// The per-action contract every node in the build DAG implements.
/// Mirrors UE's <c>IExternalAction</c> in intent (one action = one
/// command produces a set of files from a set of prerequisites) but with
/// the UE footguns preempted per <c>/Documents/XBT.html</c> Rev 4
/// Section 5.2 and Toolchain Contract Rev 13 Section 10.3.
/// </summary>
/// <remarks>
/// <para>
/// Every property is <c>get</c>-only; implementations are immutable
/// once constructed. Determinism: every collection-shaped field returns
/// a stably-ordered enumeration (sorted ordinal on prerequisites /
/// produces / deletes). The <see cref="ExternalAction"/> sealed record
/// is the canonical implementation and enforces these invariants at
/// construction.
/// </para>
/// <para>
/// <strong>Note on field shape vs XBT.html Section 5.2.</strong> XBT.html
/// Rev 4 declares the collection fields as <c>SortedSet&lt;FileItem&gt;</c>.
/// This C# interface surfaces them as <c>IReadOnlyList&lt;FileItem&gt;</c>
/// because (1) consumers iterate; they do not test membership, so the
/// sorted-set's lookup advantage is not paid for; (2) the action graph
/// hashes the collection's serialised form for
/// <see cref="CommandVersion"/>, and an indexed list with documented
/// ordinal sort is the cheapest stable hash input; (3) the spec request
/// in <c>plans/unreal-engine-source-code-lively-pillow.md</c> Section B.2
/// explicitly calls for <c>IReadOnlyList</c>. The sort invariant is
/// enforced by <see cref="ExternalAction"/>'s constructor.
/// </para>
/// </remarks>
public interface IExternalAction
{
    /// <summary>The slot in <see cref="XActionType"/> this action occupies.</summary>
    XActionType ActionType { get; }

    /// <summary>
    /// Files this action reads. Must be sorted by
    /// <see cref="FileItem.FullPath"/> with <c>StringComparer.Ordinal</c>
    /// so the action's <see cref="CommandVersion"/> is reproducible across
    /// runs and machines.
    /// </summary>
    IReadOnlyList<FileItem> PrerequisiteItems { get; }

    /// <summary>
    /// Files this action writes. Same sort discipline as
    /// <see cref="PrerequisiteItems"/>.
    /// </summary>
    IReadOnlyList<FileItem> ProducedItems { get; }

    /// <summary>
    /// Files this action deletes after a successful run (e.g. intermediate
    /// preprocessor temps). Sorted ordinal.
    /// </summary>
    IReadOnlyList<FileItem> DeleteItems { get; }

    /// <summary>
    /// Absolute path to the executable that runs the action (e.g.
    /// <c>cl.exe</c>, <c>clang.exe</c>, or an in-process command marker
    /// for an XBT-internal action).
    /// </summary>
    string CommandPath { get; }

    /// <summary>
    /// Command-line arguments passed to <see cref="CommandPath"/>. Stable
    /// across runs (no env-dependent ordering). For long command lines
    /// the bulk should live in <see cref="ResponseFileContents"/> with
    /// only the <c>@responsefile</c> indirection here.
    /// </summary>
    IReadOnlyList<string> CommandArguments { get; }

    /// <summary>
    /// Response-file body. Null if this action does not use a response
    /// file. When non-null, XBT writes this to a sibling
    /// <c>.rsp</c>/<c>.response</c> file before invoking
    /// <see cref="CommandPath"/> and adds <c>@&lt;path&gt;</c> to the
    /// command line.
    /// </summary>
    string? ResponseFileContents { get; }

    /// <summary>
    /// Working directory the action runs in. Repo root is the canonical
    /// value; subprocesses that require their own working directory
    /// (e.g. some clang-tidy invocations) specify a different value.
    /// </summary>
    string WorkingDirectory { get; }

    /// <summary>
    /// BLAKE3 hash of the action's command-line + arguments + response
    /// file. Auto-derived at <see cref="ActionGraph.Link"/> time by the
    /// implementation; the interface only exposes the value.
    /// </summary>
    IoHash CommandVersion { get; }

    /// <summary>Verb form: <c>"Compile"</c>, <c>"Link"</c>, etc. Human-readable.</summary>
    string CommandDescription { get; }

    /// <summary>
    /// Object form: <c>"XScoring.cpp"</c>, <c>"XScoring.dll"</c>, etc.
    /// Combined with <see cref="CommandDescription"/> on the progress
    /// line in the streaming JSON channel.
    /// </summary>
    string StatusDescription { get; }

    /// <summary>
    /// True iff the action's outputs are reproducible enough to be
    /// transported from a remote executor. XBT's local executor uses
    /// this as a hint; the Phase 2 <c>XPactBuildAccelerator</c>
    /// integration reads it as a hard gate per Toolchain Contract Rev 13
    /// Section 10.3.
    /// </summary>
    bool CanExecuteRemotely { get; }

    /// <summary>
    /// Hidden cache-key inputs beyond what
    /// <see cref="CommandVersion"/> captures. Hashed into the
    /// <see cref="ActionHistory"/> key alongside
    /// <see cref="CommandVersion"/> and
    /// <see cref="ResponseFileContents"/> per
    /// <c>/Documents/XBT.html</c> Section 15.1.
    /// </summary>
    /// <remarks>
    /// Examples: <c>SimdLevel</c>, <c>FpSemantics</c>-derived flag set,
    /// environment FIPS mode, station role. The field exists so
    /// subsystems can declare "this property influences the output even
    /// though it doesn't appear in <see cref="CommandArguments"/>."
    /// Components are hashed in declared order; each component string
    /// is hashed under <c>StringComparer.Ordinal</c>.
    /// </remarks>
    IReadOnlyList<string> CacheKeyComponents { get; }

    /// <summary>
    /// Cost hint for the scheduler. The local
    /// <c>ParallelExecutor</c> only dispatches an action when at least
    /// <see cref="Weight"/> worker slots are free. Typical compile
    /// actions weight 1.0; link actions and PCH generation weight
    /// higher; the in-process WriteManifest weight is near zero.
    /// </summary>
    double Weight { get; }

    /// <summary>
    /// Bucket label for <see cref="ActionHistory"/> partitioning per
    /// Section 15.2 (one cache file per bucket so two unrelated buckets
    /// can be read/written in parallel). Null means "default bucket".
    /// </summary>
    string? CacheBucket { get; }

    /// <summary>
    /// True iff the action participates in <see cref="ActionHistory"/>.
    /// Defaults to true; rare exceptions: actions whose outputs are
    /// inherently fresh on every run (e.g. emit-only diagnostic dumps).
    /// </summary>
    /// <remarks>
    /// Prefix preserved for Hungarian-style "boolean" naming consistency
    /// with UE's <c>bUseActionHistory</c>.
    /// </remarks>
    bool bUseActionHistory { get; }

    /// <summary>
    /// Owning module's name (e.g. <c>"XScoring"</c>). Surfaces in the
    /// streaming JSON channel + build log per
    /// <c>/Documents/XBT.html</c> Section 21.5. Null for non-module
    /// actions (e.g. WriteManifest).
    /// </summary>
    string? Module { get; }

    /// <summary>
    /// Owning tier name: <c>"Engine"</c>, <c>"Studio"</c>, or
    /// <c>"Project"</c>. Surfaces in the JSON channel. Null for
    /// non-module actions.
    /// </summary>
    string? Tier { get; }

    /// <summary>
    /// True iff the action originates from a sim-path translation unit.
    /// Surfaces in the JSON channel and to the toolchain's banned-flag
    /// emission per Toolchain Contract Rev 13 Section 4.
    /// </summary>
    bool SimPath { get; }

    /// <summary>Build configuration the action runs under.</summary>
    BuildConfiguration Configuration { get; }

    /// <summary>Target platform the action runs against.</summary>
    Platform Platform { get; }

    /// <summary>
    /// Optional path the toolchain emits header-dependency information to
    /// (e.g. Clang's <c>-MD</c>/<c>-MF</c> <c>.d</c> output, MSVC's
    /// <c>/sourceDependencies</c> JSON). The cache layer parses this file
    /// to detect when a header that was <c>#include</c>d by the action's
    /// translation unit -- but which never appeared in
    /// <see cref="PrerequisiteItems"/> -- has been edited, so the action
    /// can be invalidated without re-running a full scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase 1:</b> always null. The action graph + executor do not yet
    /// parse depfiles; the property exists so its addition does not bump
    /// <see cref="ActionHistory.CurrentVersion"/> a second time when the
    /// Phase 2 depfile pipeline lands (the auto-derivation hashes
    /// <c>IExternalAction</c>'s property set).
    /// </para>
    /// <para>
    /// <b>Phase 2:</b> toolchains emit a <c>.d</c> / <c>.json</c> file
    /// alongside the produced object; the cache layer reads the file at
    /// staleness-check time, walks the included header set, and adds
    /// each header's content hash to the prerequisite set used by
    /// <see cref="ActionHistory.IsActionOutdated"/>.
    /// </para>
    /// </remarks>
    FileItem? DependencyListFile { get; }
}
