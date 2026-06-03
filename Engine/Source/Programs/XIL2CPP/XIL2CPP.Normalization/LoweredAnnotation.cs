// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Base type for every per-node lowering decision a Pass-2 normalizer
/// records, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.1 + 3.2
/// (the approved hybrid annotation-layer design).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass 2 annotates; it does NOT rewrite.</b> The Pass-1
/// <see cref="Microsoft.CodeAnalysis.CSharp.CSharpCompilation"/> and its
/// semantic models stay authoritative so <c>GetTypeInfo</c> /
/// <c>GetSymbolInfo</c> / <c>AnalyzeDataFlow</c> remain valid for Pass 3.
/// A normalizer therefore expresses its lowering decision (e.g.
/// <c>using</c>-block to try/finally, record positional-ctor expansion,
/// pattern-match cascade, <c>[Conditional]</c> call elision) as an
/// additive annotation keyed on the original Roslyn
/// <see cref="Microsoft.CodeAnalysis.SyntaxNode"/> identity, NOT as a new
/// syntax tree.
/// </para>
/// <para>
/// <b>Extensibility (the parallel-safe contract).</b> Each concrete
/// normalizer defines its OWN <see cref="LoweredAnnotation"/> subclass in
/// its OWN file; this foundation ships only the base. Subclasses are stored
/// and retrieved by their concrete runtime type through
/// <c>NormalizedUnit.GetAnnotation&lt;T&gt;</c> /
/// <c>NormalizedUnit.GetAnnotations&lt;T&gt;</c>, so a later agent can
/// introduce a brand-new annotation kind WITHOUT modifying
/// <c>NormalizedUnit</c> or <c>NormalizedUnitBuilder</c>.
/// </para>
/// </remarks>
public abstract class LoweredAnnotation
{
    /// <summary>
    /// A short, stable, human-readable discriminator for this annotation
    /// kind (e.g. <c>"using-block"</c>, <c>"record-positional-ctor"</c>).
    /// Used in diagnostics, logging, and deterministic ordering; it is NOT
    /// the dispatch key (retrieval is by concrete CLR type). Implementations
    /// MUST return a constant, allocation-free string so two runs over
    /// identical input produce identical diagnostic text (the
    /// X-IL2CPP-MANGLE-DET / X-IL2CPP-CSPATH-DET determinism gates).
    /// </summary>
    public abstract string Kind { get; }
}
