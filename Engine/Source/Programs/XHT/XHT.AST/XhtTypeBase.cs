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
/// Section 3.3 + Section 5.3. It applies the single-letter UE-prefix
/// strip (<see cref="StringUtils.StripCppPrefix"/>) and the
/// lowercase-invariant fold (<see cref="StringUtils.ToCaselessKey"/>) so
/// the C++ <c>AXValve</c> and the C# <c>Valve</c> hash to the same entry
/// (<c>valve</c>). C# identifiers carry no UE prefix; the strip is a
/// no-op on them.
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
    /// The key is computed by stripping a single leading UE-convention
    /// prefix letter (<c>A</c> / <c>U</c> / <c>I</c> / <c>F</c>) when the
    /// second character is also uppercase, then lowercasing. The XPact
    /// <c>X</c> prefix is not strippable -- it is the permanent project-
    /// wide prefix per the master plan's naming row.
    /// </remarks>
    public string CaselessKey => StringUtils.ToCaselessKey(StringUtils.StripCppPrefix(Name));
}
