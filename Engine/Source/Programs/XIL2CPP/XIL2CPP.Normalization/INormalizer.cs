// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// A single Pass-2 AST normalizer per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2. Each concrete construct lowering (e.g. <c>using</c>-block to
/// try/finally, record positional-ctor expansion, pattern-match cascade,
/// local-function capture analysis, <c>[Conditional]</c> / unbodied-partial
/// call elision) is implemented as one normalizer in its own file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auto-discovery.</b> <see cref="Pass2Driver"/> reflection-discovers
/// every non-abstract <see cref="INormalizer"/> implementation in the
/// <c>XIL2CPP.Normalization</c> assembly that has a public parameterless
/// constructor, sorts them deterministically by <see cref="Name"/>, and
/// runs each against the shared <see cref="NormalizedUnitBuilder"/>. To add
/// a normalizer a later agent simply declares a class implementing this
/// interface in the production assembly -- no registration edit, no driver
/// edit.
/// </para>
/// <para>
/// <b>Annotate, do not rewrite.</b> A normalizer reads the Pass-1 result
/// (its trees, semantic models, and symbols) and records its lowering
/// decisions as additive metadata on the builder
/// (<c>AnnotateNode</c> / <c>Attach</c> / <c>AddSynthesized</c>) plus any
/// diagnostics (<c>AddDiagnostic</c>). It MUST NOT mutate the Pass-1
/// compilation or syntax trees; the Pass-1 binding info stays authoritative
/// for Pass 3.
/// </para>
/// <para>
/// <b>Determinism.</b> A normalizer MUST visit nodes in a stable order
/// (source-declaration / span order) and MUST NOT depend on ambient hash
/// ordering, <c>DateTime</c>, or <c>Random</c> (gates X-IL2CPP-MANGLE-DET /
/// X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// </remarks>
public interface INormalizer
{
    /// <summary>
    /// A stable, unique, human-readable name for this normalizer. It is the
    /// deterministic ordering key <see cref="Pass2Driver"/> sorts by
    /// (ordinal), so two builds run the normalizers in an identical order.
    /// Must be non-null / non-empty and unique across the assembly.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Record this normalizer's lowering decisions for the supplied Pass-1
    /// result onto the shared <paramref name="builder"/>.
    /// </summary>
    /// <param name="pass1">The Pass-1 result (compilation + semantic models + trees). Must not be null.</param>
    /// <param name="builder">The shared, mutable normalized-unit builder all normalizers write into. Must not be null.</param>
    void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder);
}
