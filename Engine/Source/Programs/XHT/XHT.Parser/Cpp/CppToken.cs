// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XHT.AST;

namespace Simgenics.XPact.XHT.Parser.Cpp;

/// <summary>
/// One token produced by <see cref="CppTokenizer"/> per
/// <c>/Documents/XHT.html</c> Rev 7 Section 3.1. Carries a column-precise
/// <see cref="SourceSpan"/> (XHT's deliberate divergence from UHT footgun
/// #1: UHT carries only the start line; XHT carries
/// <c>(line, column, length)</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Equality semantics.</b> Tokens are value records; equality compares
/// <see cref="Kind"/> + <see cref="Span"/> + <see cref="Text"/>. The
/// <see cref="Text"/> slice is the exact source substring for the token --
/// for string literals it includes the surrounding quotes + any prefix; for
/// raw string literals it includes the <c>R"DELIM(...)DELIM"</c> framing.
/// Empty for <see cref="CppTokenKind.EndOfFile"/>.
/// </para>
/// <para>
/// <b>Determinism.</b> The struct is immutable. Two tokenizer runs over
/// the same source produce structurally identical token sequences -- the
/// reproducibility contract per XHT.html Section 14.
/// </para>
/// </remarks>
/// <param name="Kind">Token kind discriminator.</param>
/// <param name="Span">Source location -- 1-based line + column + UTF-16 length.</param>
/// <param name="Text">Exact source slice for this token; <see cref="string.Empty"/> for <see cref="CppTokenKind.EndOfFile"/>.</param>
public readonly record struct CppToken(
    CppTokenKind Kind,
    SourceSpan Span,
    string Text);
