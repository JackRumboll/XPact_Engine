// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// Abstract base record for every reflected type XHT produces:
/// <see cref="XhtClass"/>, <see cref="XhtStruct"/>, <see cref="XhtEnum"/>,
/// <see cref="XhtInterface"/>, <see cref="XhtDelegate"/>. Per
/// <c>/Documents/XHT.html</c> Rev 5 Section 4.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutability.</b> AST records are immutable after parse. The
/// resolver's seven-active-phase pipeline (Section 5.1) mutates pointer
/// fields (<see cref="XhtClass.Super"/>, etc.) only via record
/// <c>with</c>-pattern reassignment; the type itself does not expose
/// in-place setters.
/// </para>
/// <para>
/// <b>Caseless symbol key.</b> <see cref="CaselessKey"/> is the engine-
/// name key the resolver's symbol table looks types up by per
/// Section 3.3 + Section 5.3. It is the lowercase-invariant form of
/// the source identifier (<see cref="StringUtils.ToCaselessKey"/>) --
/// XPact's permanent <c>X</c> prefix is preserved in the engine name
/// (per the master plan naming row), and no UE-convention single-letter
/// strip is applied (Round-2 user directive: A / U / I / F prefix
/// stripping is removed).
/// </para>
/// </remarks>
/// <param name="Name">The identifier as authored in source (case preserved).</param>
/// <param name="FullyQualifiedName">Identifier prefixed with the namespace path, module-relative.</param>
/// <param name="OuterName">Containing type's <see cref="Name"/>, or null for top-level types.</param>
/// <param name="ModuleName">The XBT-manifest module this type belongs to.</param>
/// <param name="Language">Source language (Cpp / CSharp); set at parse time by the originating parser.</param>
/// <param name="Span">Source location of the type's declaration.</param>
/// <param name="Specifiers">Raw parsed specifier list; validation happens in the resolver.</param>
public abstract record XhtTypeBase(
    string Name,
    string FullyQualifiedName,
    string? OuterName,
    string ModuleName,
    Language Language,
    SourceSpan Span,
    IReadOnlyList<Specifier> Specifiers)
{
    /// <summary>
    /// Engine-name key used by the resolver's symbol table per
    /// <c>/Documents/XHT.html</c> Rev 5 Section 3.3 + Section 5.3.
    /// </summary>
    /// <remarks>
    /// The key is the lowercased source identifier. XPact's permanent
    /// <c>X</c> prefix is preserved; no UE-convention prefix-strip is
    /// applied (Round-2 user directive removes the A / U / I / F strip
    /// rule).
    /// </remarks>
    public string CaselessKey => StringUtils.ToCaselessKey(Name);
}
