// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// One parsed specifier with its (possibly empty) value list per
/// <c>/Documents/XHT.html</c> Rev 8 Section 7.2.
/// </summary>
/// <remarks>
/// <para>
/// Specifiers capture the raw <c>(Key=Value)</c> / <c>(Key)</c> /
/// <c>(Key=V1,V2)</c> forms inside <c>XCLASS(...)</c>, <c>XPROPERTY(...)</c>,
/// etc. Semantic validation (specifier coherence, value-type matching, the
/// <c>EditAnywhere + Transient</c> contradiction etc.) happens during the
/// resolver's validator pass (Section 6); parser output preserves the raw
/// tokens.
/// </para>
/// <para>
/// <see cref="Values"/> is empty for flag-only specifiers
/// (<c>EditAnywhere</c>, <c>BlueprintReadOnly</c>); contains one entry for
/// single-value forms (<c>Category="Setup"</c>); contains multiple entries
/// for list forms (<c>meta=(Tooltip="..", DisplayName="..")</c>).
/// </para>
/// </remarks>
/// <param name="Key">Specifier name as authored (case preserved for diagnostics).</param>
/// <param name="Values">Specifier value list; empty for flag-only specifiers.</param>
/// <param name="Span">Source location of the specifier (the key token's range).</param>
public sealed record Specifier(
    string Key,
    IReadOnlyList<string> Values,
    SourceSpan Span)
{
    /// <summary>
    /// Construct a flag-only specifier (no value list). Convenience helper
    /// used by the parser for the <c>EditAnywhere</c>-style specifier forms.
    /// </summary>
    /// <param name="key">Specifier name. Must not be null.</param>
    /// <param name="span">Source location of the specifier.</param>
    /// <returns>A <see cref="Specifier"/> with empty <see cref="Values"/>.</returns>
    public static Specifier Flag(string key, SourceSpan span)
    {
        return new Specifier(key, System.Array.Empty<string>(), span);
    }
}
