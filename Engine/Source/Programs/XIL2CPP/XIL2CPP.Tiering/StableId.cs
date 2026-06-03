// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;

namespace Simgenics.XPact.XIL2CPP.Tiering;

/// <summary>
/// A deterministic, per-function identifier used as the stable key for a
/// function's tier classification, per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2 / 3.3 (the <c>stableId</c> field of the per-module
/// <c>TierTable.partial.&lt;Module&gt;.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.c key.</b> The value is the function symbol's fully-qualified
/// signature display (namespace + containing types + member name + parameter
/// types), produced by <see cref="FromSymbol"/> with
/// <see cref="StableIdFormat"/>. It is a stable, deterministic string key:
/// the same symbol always renders the same value, two distinct overloads
/// render distinct values (the parameter types disambiguate), and no ambient
/// state (clock / culture / machine layout) participates.
/// </para>
/// <para>
/// <b>Superseded later.</b> Pass 5 (mangling assignment, Section 3.2) computes
/// the Itanium-ABI-style length-prefixed mangled name and becomes the
/// authoritative linker-level key; Pass 4 only needs a stable, deterministic
/// key to anchor the per-function tier verdict and to sort the table. This
/// type is therefore the Pass-4 placeholder for that identity, NOT the final
/// mangled name.
/// </para>
/// <para>
/// The format matches the Pass-3 <c>CrossModuleNoThrowAnalyzer</c>'s own
/// callee / caller display surrogate (same <see cref="SymbolDisplayFormat"/>
/// settings), so a Pass-3 caller-flag display string and a Pass-4
/// <see cref="StableId"/> for the same symbol are byte-identical -- which is
/// how Pass 4 joins the cross-module conservative-tier flags back to the
/// functions it classifies.
/// </para>
/// </remarks>
/// <param name="Value">The stable string key (never null / empty).</param>
public readonly record struct StableId(string Value)
{
    /// <summary>
    /// The fully-qualified display format that renders the
    /// <see cref="Value"/>: namespace + containing types + member name +
    /// parameter types, with special type names (<c>int</c>, <c>string</c>)
    /// and expanded nullable annotations. Identical to the Pass-3
    /// <c>CrossModuleNoThrowAnalyzer</c>'s stable-id surrogate format so the
    /// two tools' display strings agree exactly.
    /// </summary>
    /// <remarks>
    /// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> alone renders
    /// only a member's simple name (it qualifies type names, not members), so
    /// a custom format is needed to disambiguate overloads and to anchor the
    /// caller/callee identity across passes.
    /// </remarks>
    public static readonly SymbolDisplayFormat StableIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.ExpandNullable);

    /// <summary>
    /// Build a <see cref="StableId"/> from a function symbol by rendering it
    /// with <see cref="StableIdFormat"/>.
    /// </summary>
    /// <param name="symbol">The function symbol (method / accessor / operator / local function / lambda). Must not be null.</param>
    /// <returns>The stable id for the symbol.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="symbol"/> is null.</exception>
    public static StableId FromSymbol(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return new StableId(symbol.ToDisplayString(StableIdFormat));
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
