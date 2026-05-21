// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Resolver;

/// <summary>
/// The seven-active-phase resolve pipeline state per
/// <c>/Documents/XHT.html</c> Rev 8 Section 5.1 (Model A; Round-2 audit
/// C3 phase-reorder). The enum identifies the lifecycle phase
/// a <see cref="ResolverPipeline"/> instance is currently in (or has
/// completed) as it walks the populated symbol table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequence (Rev 6, Round-2 audit C3 phase-reorder).</b> Phases run
/// in this strict order:
/// </para>
/// <list type="number">
///   <item><description><see cref="Pairings"/> -- XCLASS + XINTERFACE companion pairing; partial-class merge.</description></item>
///   <item><description><see cref="InvalidCheck"/> -- orphan-interface + top-level-function sanity checks.</description></item>
///   <item><description><see cref="BindSuperAndBases"/> -- symbol-table-driven super-name resolution; populates <c>Super</c> pointers.</description></item>
///   <item><description><see cref="ResolveBases"/> -- cross-type type-pointer resolution; interface lists; <c>ClassWithin</c>.</description></item>
///   <item><description><see cref="Properties"/> -- property type resolution; <c>ReplicatedUsing</c> callback signature check.</description></item>
///   <item><description><see cref="RecursiveStructCheck"/> -- detects cycles through BOTH inheritance chains AND value-typed field references (XHT105). Reordered to AFTER Properties per Round-2 audit C3 so the field-type graph is fully resolved when the cycle walker runs.</description></item>
///   <item><description><see cref="Final"/> -- serial whole-program checks; cross-language consistency; specifier-conflict validators; cross-tier reference validation.</description></item>
/// </list>
/// <para>
/// <b>Why the Rev 6 reorder.</b> Rev 5 placed <see cref="RecursiveStructCheck"/>
/// between <see cref="BindSuperAndBases"/> and <see cref="ResolveBases"/>, so
/// the check could walk the <c>Super</c> chain but had no access to the
/// resolved field-type graph. A pattern like
/// <c>struct A { B b; }; struct B { A a; };</c> -- value-typed mutually-
/// recursive structs -- went undetected, causing infinite-recursion
/// hazards downstream. The Rev 6 reorder runs the check AFTER
/// <see cref="Properties"/> populates
/// <see cref="ResolverContext.ResolvedPropertyTypes"/>, so the walker
/// can traverse both the <c>Super</c> chain and every value-typed
/// field reference, naming the offending path.
/// </para>
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

    /// <summary>Cross-type type-pointer resolution: interface bases, <c>ClassWithin</c> outer-type pointer per Section 7.4.</summary>
    ResolveBases,

    /// <summary>Property type resolution + <c>ReplicatedUsing</c> callback resolution; container inner-type lookup.</summary>
    Properties,

    /// <summary>Detects cycles through BOTH <c>Super</c> chains AND value-typed field references (XHT105). Reordered after <see cref="Properties"/> per Round-2 audit C3 so the field-type graph is resolved.</summary>
    RecursiveStructCheck,

    /// <summary>Serial; cross-language consistency walk, specifier-conflict validators, cross-tier dep validation.</summary>
    Final,
}
