// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Resolver;

/// <summary>
/// The seven-active-phase resolve pipeline state per
/// <c>/Documents/XHT.html</c> Rev 5 Section 5.1 (Model A; Rev 4
/// phase-model convergence). The enum identifies the lifecycle phase
/// a <see cref="ResolverPipeline"/> instance is currently in (or has
/// completed) as it walks the populated symbol table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequence (Rev 4 corrected, X-CR2-Round2).</b> Phases run in this
/// strict order per Section 5.2:
/// </para>
/// <list type="number">
///   <item><description><see cref="Pairings"/> -- XCLASS + XINTERFACE companion pairing; partial-class merge.</description></item>
///   <item><description><see cref="InvalidCheck"/> -- orphan-interface + top-level-function sanity checks.</description></item>
///   <item><description><see cref="BindSuperAndBases"/> -- symbol-table-driven super-name resolution; populates <c>Super</c> pointers.</description></item>
///   <item><description><see cref="RecursiveStructCheck"/> -- <c>TopologicalStructVisit</c> cycle detection through inheritance chains.</description></item>
///   <item><description><see cref="ResolveBases"/> -- cross-type type-pointer resolution; interface lists; <c>ClassWithin</c>.</description></item>
///   <item><description><see cref="Properties"/> -- property type resolution; <c>ReplicatedUsing</c> callback signature check.</description></item>
///   <item><description><see cref="Final"/> -- serial whole-program checks; cross-language consistency; specifier-conflict validators; cross-tier reference validation.</description></item>
/// </list>
/// <para>
/// <b>Parallelism per Section 11.2 + Section 22.1.</b> Phases 2-6 are
/// per-header parallel-friendly; <see cref="Final"/> is serial by
/// design. Phase 1d ships sequential; per-phase parallelism is a later
/// optimization.
/// </para>
/// </remarks>
public enum ResolvePhase
{
    /// <summary>The default for unresolved nodes; no resolver work has been attempted yet.</summary>
    None,

    /// <summary>XCLASS + XINTERFACE companion pairing; partial-class merge for C# duplicates.</summary>
    Pairings,

    /// <summary>Orphan-interface + top-level-function sanity checks per UHT precedent.</summary>
    InvalidCheck,

    /// <summary>Symbol-table-driven super-name lookup; populates <c>Super</c> pointers via the caseless table.</summary>
    BindSuperAndBases,

    /// <summary><c>TopologicalStructVisit</c> cycle detection walking <c>Super</c> pointers; mirrors UHT <c>UhtSession.cs:2880-2932</c>.</summary>
    RecursiveStructCheck,

    /// <summary>Cross-type type-pointer resolution: interface bases, <c>ClassWithin</c> outer-type pointer per Section 7.4.</summary>
    ResolveBases,

    /// <summary>Property type resolution + <c>ReplicatedUsing</c> callback resolution; container inner-type lookup.</summary>
    Properties,

    /// <summary>Serial; cross-language consistency walk, specifier-conflict validators, cross-tier dep validation.</summary>
    Final,
}
