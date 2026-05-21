// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Parser.Cpp;

/// <summary>
/// Kind discriminator for one <see cref="CppToken"/> per
/// <c>/Documents/XHT.html</c> Rev 7 Section 3.1 (handwritten C++ tokenizer).
/// Modelled as a single flat enum -- all punctuator forms appear as
/// dedicated kinds rather than as a sub-payload on a generic
/// <c>Punctuator</c> kind, so consumers can switch on punctuator
/// shape without an extra indirection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Marker recognition.</b> The 10 XHT reflection markers
/// (<c>XCLASS</c>, <c>XSTRUCT</c>, <c>XENUM</c>, <c>XINTERFACE</c>,
/// <c>XFUNCTION</c>, <c>XPROPERTY</c>, <c>XDELEGATE</c>, <c>XPARAM</c>,
/// <c>XMETA</c>, <c>XGENERATED_BODY</c>) are tokenised as
/// <see cref="XhtMarker"/> rather than the generic <see cref="Identifier"/>
/// kind; the marker-scan layer dispatches on this distinction
/// (Section 3.4). Regular C++ keywords (<c>class</c>, <c>static</c>,
/// <c>const</c>, ...) are tokenised as <see cref="Keyword"/>.
/// </para>
/// <para>
/// <b>Template-token disambiguation.</b> Per Section 3.1 the tokenizer
/// emits <see cref="GreaterGreater"/> as a single token for the
/// <c>&gt;&gt;</c> shift digram; the parser-side template depth tracker
/// re-interprets the digram as two <c>&gt;</c> closes when consumed inside
/// a template-parameter list. The tokenizer does NOT split the digram
/// because that would require speculative context-sensitive lookback
/// that the lexer layer does not own.
/// </para>
/// <para>
/// <b>String prefixes.</b> The wide / unicode / raw string prefixes
/// (<c>u8</c>, <c>u</c>, <c>U</c>, <c>L</c>) are absorbed into the
/// resulting <see cref="StringLiteral"/> or <see cref="RawStringLiteral"/>
/// kind; the prefix is preserved in the token's <c>Text</c>. Consumers
/// inspecting prefix semantics do so by looking at the leading chars of
/// <c>Text</c>.
/// </para>
/// </remarks>
public enum CppTokenKind
{
    // -----------------------------------------------------------------
    // Identifiers + keywords + literals.
    // -----------------------------------------------------------------

    /// <summary>A C++ identifier (not a keyword, not an XHT marker).</summary>
    Identifier,

    /// <summary>A C++ keyword recognised by <c>CppKeywordTable</c> (class, static, ...).</summary>
    Keyword,

    /// <summary>One of the 10 XHT reflection markers per Contract Section 1.1.</summary>
    XhtMarker,

    /// <summary>A decimal / hex / binary / octal integer literal with optional suffix.</summary>
    IntegerLiteral,

    /// <summary>A floating-point literal (decimal or hex-float) with optional suffix.</summary>
    FloatingLiteral,

    /// <summary>A character literal (<c>'X'</c> or <c>L'X'</c>).</summary>
    CharLiteral,

    /// <summary>A string literal (<c>"..."</c>) including optional prefix.</summary>
    StringLiteral,

    /// <summary>A raw string literal (<c>R"DELIM(...)DELIM"</c>) including optional prefix.</summary>
    RawStringLiteral,

    // -----------------------------------------------------------------
    // Preprocessor + comments.
    // -----------------------------------------------------------------

    /// <summary>A preprocessor directive line (the whole line including the leading <c>#</c>).</summary>
    PreprocessorDirective,

    /// <summary>A line or block comment (preserved or skipped per <c>SkipComments</c>).</summary>
    Comment,

    /// <summary>End-of-file sentinel; <c>Text</c> is empty.</summary>
    EndOfFile,

    // -----------------------------------------------------------------
    // Punctuators. One kind per shape; longest-match by the tokenizer.
    // -----------------------------------------------------------------

    /// <summary><c>(</c></summary>
    OpenParen,

    /// <summary><c>)</c></summary>
    CloseParen,

    /// <summary><c>{</c></summary>
    OpenBrace,

    /// <summary><c>}</c></summary>
    CloseBrace,

    /// <summary><c>[</c></summary>
    OpenBracket,

    /// <summary><c>]</c></summary>
    CloseBracket,

    /// <summary><c>,</c></summary>
    Comma,

    /// <summary><c>;</c></summary>
    Semicolon,

    /// <summary><c>.</c></summary>
    Dot,

    /// <summary><c>...</c></summary>
    Ellipsis,

    /// <summary><c>.*</c></summary>
    DotStar,

    /// <summary><c>::</c></summary>
    ColonColon,

    /// <summary><c>:</c></summary>
    Colon,

    /// <summary><c>?</c></summary>
    Question,

    /// <summary><c>-&gt;</c></summary>
    Arrow,

    /// <summary><c>-&gt;*</c></summary>
    ArrowStar,

    /// <summary><c>&lt;</c></summary>
    Less,

    /// <summary><c>&gt;</c></summary>
    Greater,

    /// <summary><c>&lt;=</c></summary>
    LessEq,

    /// <summary><c>&gt;=</c></summary>
    GreaterEq,

    /// <summary><c>&lt;=&gt;</c> (three-way comparison)</summary>
    Spaceship,

    /// <summary><c>&lt;&lt;</c></summary>
    LessLess,

    /// <summary><c>&gt;&gt;</c> (tokenised as a single token; parser splits in template depth context).</summary>
    GreaterGreater,

    /// <summary><c>=</c></summary>
    Equals,

    /// <summary><c>==</c></summary>
    EqualsEquals,

    /// <summary><c>!=</c></summary>
    NotEquals,

    /// <summary><c>!</c></summary>
    Bang,

    /// <summary><c>+</c></summary>
    Plus,

    /// <summary><c>++</c></summary>
    PlusPlus,

    /// <summary><c>+=</c></summary>
    PlusEq,

    /// <summary><c>-</c></summary>
    Minus,

    /// <summary><c>--</c></summary>
    MinusMinus,

    /// <summary><c>-=</c></summary>
    MinusEq,

    /// <summary><c>*</c></summary>
    Star,

    /// <summary><c>*=</c></summary>
    StarEq,

    /// <summary><c>/</c></summary>
    Slash,

    /// <summary><c>/=</c></summary>
    SlashEq,

    /// <summary><c>%</c></summary>
    Percent,

    /// <summary><c>%=</c></summary>
    PercentEq,

    /// <summary><c>&amp;</c></summary>
    Ampersand,

    /// <summary><c>&amp;&amp;</c></summary>
    AmpAmp,

    /// <summary><c>&amp;=</c></summary>
    AmpEq,

    /// <summary><c>|</c></summary>
    Pipe,

    /// <summary><c>||</c></summary>
    PipePipe,

    /// <summary><c>|=</c></summary>
    PipeEq,

    /// <summary><c>^</c></summary>
    Caret,

    /// <summary><c>^=</c></summary>
    CaretEq,

    /// <summary><c>~</c></summary>
    Tilde,

    /// <summary><c>&lt;&lt;=</c></summary>
    LessLessEq,

    /// <summary><c>&gt;&gt;=</c></summary>
    GreaterGreaterEq,

    /// <summary><c>#</c></summary>
    Hash,

    /// <summary><c>##</c></summary>
    HashHash,

    /// <summary>Any unrecognised punctuator. Emits XHT063 diagnostic.</summary>
    UnknownPunctuator,
}
