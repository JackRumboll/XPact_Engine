// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// The complete slot table for action-graph node types per
/// <c>/Documents/XBT.html</c> Rev 4 Section 5.1 + Toolchain Contract
/// Rev 13 Section 10.3.
/// </summary>
/// <remarks>
/// <para>
/// The slot ordinals are <strong>append-only</strong>. The reserved-slot
/// discipline matters because <c>ActionHistory.CurrentVersion</c> is
/// auto-derived from a BLAKE3 hash of the <c>IExternalAction</c> field
/// set <em>including this enum's declaration</em>; adding a new value to
/// the enum changes the hash, which would invalidate every
/// <c>ActionHistory</c> entry across the engine. Pre-allocating slots
/// 9&ndash;12 + 15 means Phase 2 work can introduce these action types
/// without forcing a full rebuild of every existing target. Slot 14
/// (<see cref="ReferenceCompileCSharpAction"/>) was promoted out of the
/// reserved range when the XIL2CPP cross-module reference-compile action
/// landed; its ordinal is preserved so the promotion is append-only.
/// </para>
/// <para>
/// Slot 17 (formerly <c>RunPostBuildAction</c>) is <strong>retired,
/// not reused</strong>. Reusing slot 17 would alias old
/// <c>ActionHistory</c> entries to a different action type. Post-build
/// work is now expressed via <c>IBuildHook</c>, whose
/// <c>CreateAction</c> returns an existing action type
/// (<see cref="CompileCppAction"/>, <see cref="WriteManifestAction"/>,
/// etc.) appropriate to the hook's work.
/// </para>
/// <para>
/// Phase 1 emits actions of types 0&ndash;8, 13, and 16 only; slot 14
/// (<see cref="ReferenceCompileCSharpAction"/>) is emitted once the
/// XIL2CPP cross-module reference-compile path is wired. The remaining
/// reserved slots are never instantiated until Phase 2 systems land.
/// </para>
/// </remarks>
public enum XActionType
{
    /// <summary>Validate every source file's copyright header. Cached by source hash.</summary>
    ValidateCopyrightAction = 0,

    /// <summary>Write the JSON manifest + FlatBuffers binary sidecar (one per build).</summary>
    WriteManifestAction = 1,

    /// <summary>XHT pass 1: per-module token parse from .h/.cpp sources.</summary>
    ParseHeadersAction = 2,

    /// <summary>XHT pass 2: per-header .gen.cpp + per-module .init.gen.cpp aggregator emit; parallel with ParseHeaders on next module.</summary>
    EmitReflectionAction = 3,

    /// <summary>XIL2CPP per-module transpile (.cs -> .cs.cpp + .cs.h).</summary>
    XIL2CPPAction = 4,

    /// <summary>PCH generation; separate from CompileCpp for a distinct diagnostic surface.</summary>
    PCHGenerationAction = 5,

    /// <summary>Single TU compile (.cpp -> .obj/.o), original-authored, XHT-generated, or XIL2CPP-generated.</summary>
    CompileCppAction = 6,

    /// <summary>clang-tidy / MSVC /analyze static analysis; composes with CompileCpp, doesn't replace.</summary>
    StaticAnalysisAction = 7,

    /// <summary>Final per-module link (.dll on Win64, .so on Linux/Android).</summary>
    LinkModuleAction = 8,

    /// <summary>Reserved for Phase 2 XPactBuildAccelerator: distributed-cache hit check.</summary>
    Reserved_DistributedCacheCheck = 9,

    /// <summary>Reserved for Phase 2 XPactBuildAccelerator: DerivedDataCache fetch.</summary>
    Reserved_DerivedDataFetch = 10,

    /// <summary>Reserved for Phase 2 XLiveCoding: hot-reload cascade orchestrator.</summary>
    Reserved_LiveCodingCascade = 11,

    /// <summary>Reserved for Phase 2 XLiveCoding: patch-DLL emit (per-(Target, Module) gen counter).</summary>
    Reserved_PatchDllEmit = 12,

    /// <summary>
    /// Post-MVP whole-program XIL2CPP Tier 2 promotion pass per Toolchain
    /// Contract Rev 12 Section 5.2 Pass 2. Promotes functions conservatively
    /// classified Tier 1 in Pass 1 to Tier 2 when their cross-module callees
    /// can be proven NoThrow. Phase 1: defined but never emitted; the slot
    /// is reserved by ordinal so future Phase 2 emit does not perturb the
    /// CurrentVersion hash.
    /// </summary>
    Tier2WholeProgramPass = 13,

    /// <summary>
    /// Per-module C# reference compile: emits <c>M.refonly.dll</c>
    /// (a metadata-only reference assembly, no method bodies) so a
    /// downstream module's XIL2CPP transpile can resolve cross-module
    /// C# types through Roslyn against its dependencies' public surface
    /// without recompiling their full IL. The action runs once per
    /// module ahead of that module's <see cref="XIL2CPPAction"/> for any
    /// consumer, mirroring the C++ side's "compile a header-only view of
    /// the dependency first" ordering. Slot 14 is now a named,
    /// emit-eligible action (formerly the Phase 2 reserved placeholder
    /// <c>Reserved_Phase2_F</c>); it is enumerated on the canonical
    /// <c>ContractSurface.ActionTypes</c> so a rename rotates
    /// ContractVersion.
    /// </summary>
    ReferenceCompileCSharpAction = 14,

    /// <summary>Reserved for Phase 2 system G (TBD).</summary>
    Reserved_Phase2_G = 15,

    /// <summary>Build a plugin's <c>.xplugin</c> -derived manifest fragment.</summary>
    BuildPluginManifestAction = 16,

    // Slot 17 (formerly RunPostBuildAction) RETIRED, not reused.
}
