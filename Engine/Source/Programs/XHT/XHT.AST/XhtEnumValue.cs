// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One reflected enum value with its <c>XMETA</c> specifiers. Per the
/// <c>XMETA</c> marker entry in Contract Section 1.1 +
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.1.
/// </summary>
/// <param name="Name">Enum value identifier.</param>
/// <param name="Value">Numeric value (resolved from explicit assignment or auto-numbering).</param>
/// <param name="Specifiers">Raw parsed <c>XMETA</c> specifiers on the value.</param>
/// <param name="Span">Source location of the value's declaration.</param>
public sealed record XhtEnumValue(
    string Name,
    long Value,
    IReadOnlyList<Specifier> Specifiers,
    SourceSpan Span);
