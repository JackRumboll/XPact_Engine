// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Parser.Cpp;

/// <summary>
/// Single-pass, hand-written C++ tokenizer per
/// <c>/Documents/XHT.html</c> Rev 8 Section 3.1. Recognises the subset
/// of C++ relevant to XHT marker extraction: identifiers / keywords /
/// XHT markers, integer + floating literals (decimal, hex, binary,
/// octal, hex-float, digit-separator), char + string literals (including
/// raw <c>R"DELIM(...)DELIM"</c> form with all prefix variants),
/// preprocessor directives, line + block comments, and the full C++
/// punctuator vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the tokenizer is not.</b> No macro expansion, no preprocessor
/// conditional evaluation (directives are emitted as opaque whole-line
/// tokens), no template-context disambiguation of <c>&gt;&gt;</c>
/// (parser layer owns that), no semantic validation. XHT is not a C++
/// compiler -- Section 3.1's "XHT is not a full C++ compiler" rule.
/// </para>
/// <para>
/// <b>Position tracking.</b> Line / column are 1-based, UTF-16 code-unit
/// indexed (matching .NET's native <see cref="string"/> view). A UTF-16
/// surrogate pair counts as TWO columns per M1 audit (one per code
/// unit -- matches .NET <c>String.Length</c>). A tab character counts as
/// ONE column (the diagnostic-column number is the raw character count;
/// IDE tab-width settings are an IDE concern, not the tokenizer's).
/// Newlines reset column to 1 and advance line; <c>\r\n</c> is treated
/// as one logical newline. Bare <c>\r</c> (old-Mac) is treated as a
/// newline as well -- matches MSBuild diagnostic source-line handling.
/// A leading UTF-8 BOM at position 0 is silently skipped in the
/// constructor (M2 audit; defensive belt-and-suspenders to
/// <c>System.IO.File.ReadAllText</c>'s BOM handling).
/// </para>
/// <para>
/// <b>Lookahead model.</b> The tokenizer scans linearly from the source
/// string; it buffers tokens to satisfy <see cref="Peek(int)"/> via an
/// internal queue. Peeked tokens stay queued until <see cref="Next"/>
/// drains them.
/// </para>
/// <para>
/// <b>Error recovery (XHT.html Section 3.7).</b> On a malformed token --
/// unterminated string (XHT061), unterminated comment (XHT060),
/// malformed numeric literal (XHT062), unsupported digraph (XHT063) --
/// the tokenizer records a <see cref="DiagnosticRecord"/> and advances
/// to the next plausible token start rather than throwing. The token
/// stream is always well-formed (terminates with
/// <see cref="CppTokenKind.EndOfFile"/>); downstream consumers see
/// best-effort tokens for the rest of the file.
/// </para>
/// <para>
/// <b>Thread safety.</b> The tokenizer is single-threaded by design (one
/// scan position, one diagnostics list). Two <see cref="CppTokenizer"/>
/// instances on two threads do not share state and may run concurrently.
/// Per XHT.html Section 11.1 the per-source-file parser parallelism uses
/// one tokenizer per worker.
/// </para>
/// </remarks>
public sealed class CppTokenizer
{
    // -----------------------------------------------------------------
    // Diagnostic codes used by the tokenizer. The lexer band per
    // XHT.html Section 12.3 is XHT060-XHT069.
    // -----------------------------------------------------------------

    /// <summary>Diagnostic code: unterminated block comment (<c>/* ... EOF</c>).</summary>
    public const string DiagUnterminatedComment = "XHT060";

    /// <summary>Diagnostic code: unterminated string / char / raw-string literal.</summary>
    public const string DiagUnterminatedString = "XHT061";

    /// <summary>Diagnostic code: malformed numeric literal (e.g. <c>0x</c> with no hex digits).</summary>
    public const string DiagInvalidNumericLiteral = "XHT062";

    /// <summary>Diagnostic code: unsupported digraph / unknown punctuator.</summary>
    public const string DiagUnsupportedPunctuator = "XHT063";

    private readonly string _sourcePath;
    private readonly string _source;
    private readonly List<DiagnosticRecord> _diagnostics = new();
    private readonly Queue<CppToken> _peekQueue = new();

    // 0-based scan cursor in _source.
    private int _index;

    // 1-based line / column tracking at the current cursor position.
    private int _line = 1;
    private int _column = 1;

    /// <summary>
    /// True when the previous tokenizer transition crossed a physical
    /// newline (set by <see cref="SkipWhitespaceInternal"/> when it
    /// consumes a <c>\n</c> / <c>\r</c> sequence). The preprocessor-
    /// directive recognizer in <see cref="ReadNextRaw"/> consults this
    /// flag instead of <see cref="_column"/> alone, so a <c>#</c>
    /// appearing after a <c>\\&lt;newline&gt;</c> continuation
    /// (which RESETS <see cref="_column"/> to 1 but is logically still
    /// part of the previous token) is NOT mis-classified as a
    /// directive. Per C1 audit finding (XHT.html Section 3.1).
    /// </summary>
    private bool _atLogicalLineStart = true;

    /// <summary>
    /// Construct a tokenizer for the named source.
    /// </summary>
    /// <param name="sourcePath">
    /// Absolute path of the source file. Stored verbatim into every
    /// emitted token's <see cref="SourceSpan.SourceFilePath"/>; may be
    /// empty for synthetic / in-memory sources. Must not be null.
    /// </param>
    /// <param name="sourceText">
    /// Source text as a .NET string (UTF-16 internally; engine-wide
    /// source-file UTF-8 is decoded by the caller before reaching this
    /// constructor per XHT.html Section 1.5 + Contract Section 6).
    /// Must not be null.
    /// </param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public CppTokenizer(string sourcePath, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceText);

        _sourcePath = sourcePath;
        _source = sourceText;

        // M2 audit: defensively skip a leading UTF-8 BOM. The U+FEFF
        // character (decoded as the single-char string starting with
        // ﻿) is produced when a caller forgets to strip it before
        // reaching the tokenizer. File.ReadAllText already strips BOMs
        // by default; this is a second line of defence for callers
        // that bypass it. The BOM is invisible: column stays at 1 so
        // the first real character of the source is at column 1.
        if (_source.Length > 0 && _source[0] == '﻿')
        {
            _index = 1;
        }
    }

    /// <summary>
    /// When true (default), comment tokens are skipped by
    /// <see cref="Next"/> / <see cref="Peek(int)"/>. Set false to receive
    /// comments inline (used by the tooltip-extraction pass per
    /// XHT.html Section 3.6).
    /// </summary>
    public bool SkipComments { get; set; } = true;

    /// <summary>
    /// When true (default), whitespace is consumed silently between
    /// tokens. The tokenizer does not emit whitespace tokens; this
    /// property exists for symmetry with <see cref="SkipComments"/> and
    /// to leave the door open for the Phase-2 token-recording
    /// reproduction pass. In Phase 1, only true is meaningful; set to
    /// false has no effect at the public surface.
    /// </summary>
    public bool SkipWhitespace { get; set; } = true;

    /// <summary>
    /// Diagnostics accumulated during tokenization. Each entry carries
    /// the source path + line + column + a code in the
    /// <c>XHT060</c>-<c>XHT069</c> range. Empty when no recovery events
    /// fired. The list is appended in token order.
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics => _diagnostics;

    /// <summary>
    /// Consume and return the next token. Returns a token with
    /// <see cref="CppTokenKind.EndOfFile"/> once the source is fully
    /// scanned; subsequent calls keep returning the EOF sentinel.
    /// </summary>
    /// <returns>The next non-skipped token in the stream.</returns>
    public CppToken Next()
    {
        if (_peekQueue.Count > 0)
        {
            return _peekQueue.Dequeue();
        }
        return ReadNextRespectingSkips();
    }

    /// <summary>
    /// Inspect a token at the given lookahead distance without consuming.
    /// <c>Peek(0)</c> returns the same token <see cref="Next"/> would
    /// return next; <c>Peek(1)</c> returns the one after.
    /// </summary>
    /// <param name="distance">Non-negative lookahead distance. Defaults to 0.</param>
    /// <returns>The token at the requested lookahead position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="distance"/> is negative.</exception>
    public CppToken Peek(int distance = 0)
    {
        if (distance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distance), distance, "distance must be >= 0.");
        }

        while (_peekQueue.Count <= distance)
        {
            CppToken next = ReadNextRespectingSkips();
            _peekQueue.Enqueue(next);
            if (next.Kind == CppTokenKind.EndOfFile)
            {
                // Stop enqueuing past EOF -- repeated peeks past EOF
                // return the EOF sentinel without growing the queue.
                if (_peekQueue.Count > distance)
                {
                    break;
                }
            }
        }

        // Read the element at the requested distance without dequeuing.
        int seen = 0;
        foreach (CppToken t in _peekQueue)
        {
            if (seen == distance)
            {
                return t;
            }
            seen++;
        }

        // Defensive: only reachable if EOF cap broke the enqueue loop
        // before reaching `distance`. Return the last seen value (the
        // tail of the queue, which is always EOF in that case).
        CppToken? last = null;
        foreach (CppToken t in _peekQueue) { last = t; }
        return last ?? new CppToken(CppTokenKind.EndOfFile, MakeSpan(_line, _column, 0), string.Empty);
    }

    // =================================================================
    // Internal driver.
    // =================================================================

    private CppToken ReadNextRespectingSkips()
    {
        while (true)
        {
            CppToken t = ReadNextRaw();
            if (t.Kind == CppTokenKind.Comment && SkipComments)
            {
                continue;
            }
            return t;
        }
    }

    private CppToken ReadNextRaw()
    {
        SkipWhitespaceInternal();

        if (_index >= _source.Length)
        {
            return new CppToken(CppTokenKind.EndOfFile, MakeSpan(_line, _column, 0), string.Empty);
        }

        // Preprocessor directive recognition: '#' at start of a logical
        // line. Two conditions BOTH need to hold per the C1 audit
        // finding (XHT.html Section 3.1):
        //   (1) we are at column 1 OR have only seen whitespace since a
        //       newline (covered by _atLogicalLineStart);
        //   (2) we are at the beginning of file (defensively allowed).
        // A bare column == 1 check is INSUFFICIENT because line
        // continuations ('\\' + '\n') wrap _column back to 1 even
        // though the next character is logically part of the previous
        // line's token stream. _atLogicalLineStart tracks the
        // logical-line transition explicitly and stays false until a
        // real (non-continuation) newline fires.
        if (_source[_index] == '#' && _atLogicalLineStart)
        {
            CppToken directive = ReadPreprocessorDirective();
            // Directive runs to end-of-line, leaving _atLogicalLineStart
            // set true by the trailing newline consume.
            return directive;
        }

        // From here on every real token consumed transitions us out of
        // the logical-line-start state. The flag will be re-armed when
        // SkipWhitespaceInternal next consumes a real newline.
        _atLogicalLineStart = false;

        // Comment recognition. Both /* ... */ and // ... line forms.
        if (_source[_index] == '/' && _index + 1 < _source.Length)
        {
            char next = _source[_index + 1];
            if (next == '/')
            {
                return ReadLineComment();
            }
            if (next == '*')
            {
                return ReadBlockComment();
            }
        }

        char c = _source[_index];

        // String / char / raw-string literal with possible prefix
        // (u8, u, U, L) and possible 'R' raw form.
        if (c == '"' || c == '\'')
        {
            return ReadCharOrStringLiteral();
        }

        // Prefixed string forms (u8"...", u"...", U"...", L"..., uR"...",
        // u8R"...", UR"...", LR"...").
        if (IsStringPrefixStart(c))
        {
            int consumed;
            if (TryReadPrefixedStringLiteral(out CppToken? prefixedTok, out consumed))
            {
                return prefixedTok!.Value;
            }
            // else fall through to identifier read.
        }

        if (IsIdentifierStart(c))
        {
            return ReadIdentifierOrKeyword();
        }

        if (IsDigit(c) || (c == '.' && _index + 1 < _source.Length && IsDigit(_source[_index + 1])))
        {
            return ReadNumericLiteral();
        }

        return ReadPunctuator();
    }

    // =================================================================
    // Whitespace + newlines.
    // =================================================================

    private void SkipWhitespaceInternal()
    {
        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == ' ' || c == '\t' || c == '\v' || c == '\f')
            {
                _index++;
                _column++;
                continue;
            }
            // Line-continuation: '\\' immediately followed by '\n' /
            // '\r\n' / bare '\r' folds the next physical line into the
            // current logical line per C1 audit + XHT.html Section 3.1.
            // The continuation does NOT re-arm
            // _atLogicalLineStart -- we are still inside the same
            // logical line, so a '#' that follows is mid-statement (not
            // a directive). Column is reset and line is advanced for
            // diagnostic accuracy.
            if (c == '\\' && _index + 1 < _source.Length)
            {
                char n1 = _source[_index + 1];
                if (n1 == '\n')
                {
                    _index += 2;
                    _line++;
                    _column = 1;
                    continue;
                }
                if (n1 == '\r')
                {
                    _index++; // '\\'
                    _index++; // '\r'
                    if (_index < _source.Length && _source[_index] == '\n')
                    {
                        _index++;
                    }
                    _line++;
                    _column = 1;
                    continue;
                }
                // '\\' followed by something else (e.g. a string-escape
                // mid-stream) is NOT a line continuation; fall through
                // and let the punctuator / literal reader consume it.
                break;
            }
            if (c == '\r')
            {
                // \r\n -> single newline; bare \r -> newline.
                _index++;
                if (_index < _source.Length && _source[_index] == '\n')
                {
                    _index++;
                }
                _line++;
                _column = 1;
                _atLogicalLineStart = true;
                continue;
            }
            if (c == '\n')
            {
                _index++;
                _line++;
                _column = 1;
                _atLogicalLineStart = true;
                continue;
            }
            break;
        }
    }

    // =================================================================
    // Identifier + keyword + XHT marker.
    // =================================================================

    private CppToken ReadIdentifierOrKeyword()
    {
        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        while (_index < _source.Length && IsIdentifierContinue(_source[_index]))
        {
            AdvanceOne();
        }

        string text = _source.Substring(startIndex, _index - startIndex);
        SourceSpan span = MakeSpan(startLine, startColumn, text.Length);

        // Marker check first: 10 locked uppercase marker macros per
        // Contract Section 1.1. CppKeywordTable.IsXhtMarker is exact /
        // case-sensitive; non-X-prefixed identifiers short-circuit.
        if (text.Length > 0 && text[0] == 'X' && CppKeywordTable.IsXhtMarker(text))
        {
            return new CppToken(CppTokenKind.XhtMarker, span, text);
        }

        // Keyword check: case-sensitive lookup.
        if (CppKeywordTable.Lookup(text) is not null)
        {
            return new CppToken(CppTokenKind.Keyword, span, text);
        }

        return new CppToken(CppTokenKind.Identifier, span, text);
    }

    // =================================================================
    // Numeric literal.
    // =================================================================

    private CppToken ReadNumericLiteral()
    {
        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        bool isFloat = false;
        bool hasDigit = false;

        // Leading-dot form: '.5'.
        if (_source[_index] == '.')
        {
            isFloat = true;
            AdvanceOne();
            // Consume fractional digits.
            while (_index < _source.Length && (IsDigit(_source[_index]) || _source[_index] == '\''))
            {
                if (IsDigit(_source[_index])) { hasDigit = true; }
                AdvanceOne();
            }
            ConsumeFloatExponentAndSuffix(ref isFloat);
            return EmitNumeric(startIndex, startLine, startColumn, isFloat, hasDigit);
        }

        // Hex / binary / octal / decimal disambiguation on leading '0'.
        if (_source[_index] == '0' && _index + 1 < _source.Length)
        {
            char next = _source[_index + 1];
            if (next == 'x' || next == 'X')
            {
                AdvanceOne(); // 0
                AdvanceOne(); // x/X
                bool hasHex = false;
                while (_index < _source.Length && (IsHexDigit(_source[_index]) || _source[_index] == '\''))
                {
                    if (IsHexDigit(_source[_index])) { hasHex = true; }
                    AdvanceOne();
                }
                // Hex-float: 0x1.8p10
                if (_index < _source.Length && _source[_index] == '.')
                {
                    isFloat = true;
                    AdvanceOne();
                    while (_index < _source.Length && (IsHexDigit(_source[_index]) || _source[_index] == '\''))
                    {
                        if (IsHexDigit(_source[_index])) { hasHex = true; }
                        AdvanceOne();
                    }
                }
                if (_index < _source.Length && (_source[_index] == 'p' || _source[_index] == 'P'))
                {
                    isFloat = true;
                    AdvanceOne();
                    if (_index < _source.Length && (_source[_index] == '+' || _source[_index] == '-'))
                    {
                        AdvanceOne();
                    }
                    while (_index < _source.Length && IsDigit(_source[_index]))
                    {
                        AdvanceOne();
                    }
                }
                ConsumeIntOrFloatSuffix(isFloat);
                hasDigit = hasHex;

                if (!hasHex)
                {
                    EmitDiagnostic(DiagInvalidNumericLiteral, startLine, startColumn,
                        "Hex literal must contain at least one hexadecimal digit.");
                }
                return EmitNumeric(startIndex, startLine, startColumn, isFloat, hasDigit);
            }
            if (next == 'b' || next == 'B')
            {
                AdvanceOne(); // 0
                AdvanceOne(); // b/B
                bool hasBin = false;
                while (_index < _source.Length && (IsBinaryDigit(_source[_index]) || _source[_index] == '\''))
                {
                    if (IsBinaryDigit(_source[_index])) { hasBin = true; }
                    AdvanceOne();
                }
                ConsumeIntOrFloatSuffix(false);
                if (!hasBin)
                {
                    EmitDiagnostic(DiagInvalidNumericLiteral, startLine, startColumn,
                        "Binary literal must contain at least one binary digit.");
                }
                return EmitNumeric(startIndex, startLine, startColumn, false, hasBin);
            }
            // Fall through: octal or decimal-starting-with-0.
        }

        // Decimal integer / float.
        while (_index < _source.Length && (IsDigit(_source[_index]) || _source[_index] == '\''))
        {
            if (IsDigit(_source[_index])) { hasDigit = true; }
            AdvanceOne();
        }
        if (_index < _source.Length && _source[_index] == '.')
        {
            // Distinguish '1.f' / '1.0' (float) from '1.member' on an
            // identifier following an integer (latter is two tokens).
            // C++ rule: the dot starts a float UNLESS the next character
            // is part of an identifier and there's no preceding/following
            // digit. We accept dot-followed-by-digit OR dot-followed-by-
            // exponent / float-suffix as float continuation.
            int peekIdx = _index + 1;
            char peekCh = peekIdx < _source.Length ? _source[peekIdx] : '\0';
            if (IsDigit(peekCh) || peekCh == 'e' || peekCh == 'E' || peekCh == 'f' || peekCh == 'F' || peekCh == 'l' || peekCh == 'L')
            {
                isFloat = true;
                AdvanceOne(); // dot
                while (_index < _source.Length && (IsDigit(_source[_index]) || _source[_index] == '\''))
                {
                    AdvanceOne();
                }
            }
            else
            {
                // No fractional / exponent / suffix following: this is
                // still a float per C++ rules (e.g. '1.' is a valid
                // double). Consume the dot but don't go further.
                isFloat = true;
                AdvanceOne();
            }
        }
        ConsumeFloatExponentAndSuffix(ref isFloat);
        return EmitNumeric(startIndex, startLine, startColumn, isFloat, hasDigit);
    }

    private void ConsumeFloatExponentAndSuffix(ref bool isFloat)
    {
        if (_index < _source.Length && (_source[_index] == 'e' || _source[_index] == 'E'))
        {
            isFloat = true;
            AdvanceOne();
            if (_index < _source.Length && (_source[_index] == '+' || _source[_index] == '-'))
            {
                AdvanceOne();
            }
            while (_index < _source.Length && IsDigit(_source[_index]))
            {
                AdvanceOne();
            }
        }
        ConsumeIntOrFloatSuffix(isFloat);
    }

    private void ConsumeIntOrFloatSuffix(bool isFloat)
    {
        // C++23-tolerant: u, U, l, L, ll, LL, z, Z, uz, zu, ull, ULL, ...
        // f, F, l, L for floats. Be permissive (consume any letter that
        // looks like a suffix); the parser layer doesn't introspect.
        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == 'u' || c == 'U' || c == 'l' || c == 'L'
                || c == 'z' || c == 'Z' || c == 'f' || c == 'F')
            {
                AdvanceOne();
                continue;
            }
            break;
        }
        // isFloat parameter currently informational; suffix recognition
        // is permissive enough that the kind decision is already made by
        // the dot / exponent path.
        _ = isFloat;
    }

    private CppToken EmitNumeric(int startIndex, int startLine, int startColumn, bool isFloat, bool hasDigit)
    {
        string text = _source.Substring(startIndex, _index - startIndex);
        SourceSpan span = MakeSpan(startLine, startColumn, text.Length);
        CppTokenKind kind = isFloat ? CppTokenKind.FloatingLiteral : CppTokenKind.IntegerLiteral;

        if (!hasDigit)
        {
            EmitDiagnostic(DiagInvalidNumericLiteral, startLine, startColumn,
                "Numeric literal contains no digits.");
        }

        return new CppToken(kind, span, text);
    }

    // =================================================================
    // Char + string literal (incl. prefixed and raw forms).
    // =================================================================

    private static bool IsStringPrefixStart(char c)
    {
        return c == 'u' || c == 'U' || c == 'L' || c == 'R';
    }

    /// <summary>
    /// Try to read a prefixed string / char literal: <c>u8"..."</c>,
    /// <c>u"..."</c>, <c>U"..."</c>, <c>L"..."</c>, <c>R"DELIM(...)DELIM"</c>,
    /// and the prefixed raw forms <c>u8R"..."</c> etc.
    /// </summary>
    /// <param name="token">The emitted token on success; null on miss.</param>
    /// <param name="consumed">Number of chars consumed before fall-through; unused on success.</param>
    /// <returns>True iff the cursor was advanced and a token emitted.</returns>
    private bool TryReadPrefixedStringLiteral(out CppToken? token, out int consumed)
    {
        token = null;
        consumed = 0;

        int savedIndex = _index;
        int savedLine = _line;
        int savedColumn = _column;

        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        // Read prefix letters: u8, u, U, L, R (singly or in combination).
        // After at most 3 letters, we expect a quote ('"' or '\'').
        int prefixLen = 0;
        bool sawU8 = false;
        bool sawR = false;

        if (_index < _source.Length && _source[_index] == 'u' && _index + 1 < _source.Length && _source[_index + 1] == '8')
        {
            AdvanceOne();
            AdvanceOne();
            prefixLen += 2;
            sawU8 = true;
        }
        else if (_index < _source.Length && (_source[_index] == 'u' || _source[_index] == 'U' || _source[_index] == 'L'))
        {
            AdvanceOne();
            prefixLen += 1;
        }

        if (_index < _source.Length && _source[_index] == 'R')
        {
            AdvanceOne();
            prefixLen += 1;
            sawR = true;
        }

        // If no prefix at all consumed, or what follows isn't a quote,
        // restore state and signal miss.
        bool isString = _index < _source.Length && _source[_index] == '"';
        bool isChar = !sawR && _index < _source.Length && _source[_index] == '\'';

        if (prefixLen == 0 || (!isString && !isChar))
        {
            _index = savedIndex;
            _line = savedLine;
            _column = savedColumn;
            return false;
        }

        if (isChar)
        {
            ReadCharLiteralBody();
            string charText = _source.Substring(startIndex, _index - startIndex);
            token = new CppToken(CppTokenKind.CharLiteral, MakeSpan(startLine, startColumn, charText.Length), charText);
            consumed = _index - savedIndex;
            return true;
        }

        // String body.
        if (sawR)
        {
            ReadRawStringBody(startIndex, startLine, startColumn, out CppToken raw);
            token = raw;
        }
        else
        {
            ReadRegularStringBody();
            string strText = _source.Substring(startIndex, _index - startIndex);
            token = new CppToken(CppTokenKind.StringLiteral, MakeSpan(startLine, startColumn, strText.Length), strText);
        }
        consumed = _index - savedIndex;
        _ = sawU8; // u8 prefix is preserved in token Text; consumers introspect.
        return true;
    }

    private CppToken ReadCharOrStringLiteral()
    {
        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        char quote = _source[_index];
        if (quote == '\'')
        {
            ReadCharLiteralBody();
            string text = _source.Substring(startIndex, _index - startIndex);
            return new CppToken(CppTokenKind.CharLiteral, MakeSpan(startLine, startColumn, text.Length), text);
        }

        // Plain double-quoted string.
        ReadRegularStringBody();
        string strText = _source.Substring(startIndex, _index - startIndex);
        return new CppToken(CppTokenKind.StringLiteral, MakeSpan(startLine, startColumn, strText.Length), strText);
    }

    private void ReadCharLiteralBody()
    {
        int startLine = _line;
        int startColumn = _column;

        // Opening '\''.
        AdvanceOne();
        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == '\\')
            {
                // Escape: consume the backslash + the escaped char.
                AdvanceOne();
                if (_index < _source.Length) { AdvanceOne(); }
                continue;
            }
            if (c == '\'')
            {
                AdvanceOne();
                return;
            }
            if (c == '\n')
            {
                EmitDiagnostic(DiagUnterminatedString, startLine, startColumn,
                    "Unterminated character literal at end of line.");
                return;
            }
            AdvanceOne();
        }
        EmitDiagnostic(DiagUnterminatedString, startLine, startColumn,
            "Unterminated character literal at end of file.");
    }

    private void ReadRegularStringBody()
    {
        int startLine = _line;
        int startColumn = _column;

        // Opening '"'.
        AdvanceOne();
        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == '\\')
            {
                AdvanceOne();
                if (_index < _source.Length)
                {
                    // Eat one escape char (handles \n, \", \\, \x.., \u....,
                    // \U........ permissively: we don't validate semantics).
                    AdvanceOne();
                }
                continue;
            }
            if (c == '"')
            {
                AdvanceOne();
                return;
            }
            if (c == '\n')
            {
                EmitDiagnostic(DiagUnterminatedString, startLine, startColumn,
                    "Unterminated string literal at end of line.");
                return;
            }
            AdvanceOne();
        }
        EmitDiagnostic(DiagUnterminatedString, startLine, startColumn,
            "Unterminated string literal at end of file.");
    }

    private void ReadRawStringBody(int startIndex, int startLine, int startColumn, out CppToken token)
    {
        // Cursor sits on the opening '"'. Consume it.
        AdvanceOne();

        // Read delimiter -- characters up to '('. Per M8 audit
        // (XHT.html Section 3.1): the C++ raw-string grammar forbids
        // ')' in the d-char sequence (so the terminator ')<delim>"'
        // is unambiguous); reject it here as a malformed-literal.
        int delimStartIndex = _index;
        while (_index < _source.Length
            && _source[_index] != '('
            && _source[_index] != '"'
            && _source[_index] != '\n')
        {
            if (_source[_index] == ')')
            {
                EmitDiagnostic(DiagUnsupportedPunctuator, _line, _column,
                    "Raw string-literal delimiter must not contain ')'; this is forbidden by the C++ grammar.");
                // Advance past ')' to keep scanning; the rest of the
                // line is most likely garbage but we don't bail.
                AdvanceOne();
                continue;
            }
            AdvanceOne();
        }
        if (_index >= _source.Length || _source[_index] != '(')
        {
            EmitDiagnostic(DiagUnterminatedString, startLine, startColumn,
                "Malformed raw string literal: missing opening parenthesis after delimiter.");
            string txt = _source.Substring(startIndex, _index - startIndex);
            token = new CppToken(CppTokenKind.RawStringLiteral, MakeSpan(startLine, startColumn, txt.Length), txt);
            return;
        }

        string delim = _source.Substring(delimStartIndex, _index - delimStartIndex);
        AdvanceOne(); // consume '('

        // Read body until ')<delim>"' sentinel.
        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == ')')
            {
                // Check if followed by delim + '"'.
                int peek = _index + 1;
                bool matches = true;
                for (int i = 0; i < delim.Length; i++)
                {
                    if (peek + i >= _source.Length || _source[peek + i] != delim[i])
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches && peek + delim.Length < _source.Length && _source[peek + delim.Length] == '"')
                {
                    AdvanceOne();   // ')'
                    for (int i = 0; i < delim.Length; i++) { AdvanceOne(); }
                    AdvanceOne();   // '"'
                    string okText = _source.Substring(startIndex, _index - startIndex);
                    token = new CppToken(CppTokenKind.RawStringLiteral, MakeSpan(startLine, startColumn, okText.Length), okText);
                    return;
                }
                AdvanceOne();
                continue;
            }
            AdvanceOne();
        }
        EmitDiagnostic(DiagUnterminatedString, startLine, startColumn,
            "Unterminated raw string literal at end of file.");
        string finalText = _source.Substring(startIndex, _index - startIndex);
        token = new CppToken(CppTokenKind.RawStringLiteral, MakeSpan(startLine, startColumn, finalText.Length), finalText);
    }

    // =================================================================
    // Comments + preprocessor.
    // =================================================================

    private CppToken ReadLineComment()
    {
        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        // Already at '/'. Consume "//" then up to (but not including) the
        // next newline.
        AdvanceOne();
        AdvanceOne();
        while (_index < _source.Length && _source[_index] != '\n' && _source[_index] != '\r')
        {
            AdvanceOne();
        }
        string text = _source.Substring(startIndex, _index - startIndex);
        return new CppToken(CppTokenKind.Comment, MakeSpan(startLine, startColumn, text.Length), text);
    }

    private CppToken ReadBlockComment()
    {
        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        // Consume "/*".
        AdvanceOne();
        AdvanceOne();

        while (_index < _source.Length)
        {
            if (_source[_index] == '*' && _index + 1 < _source.Length && _source[_index + 1] == '/')
            {
                AdvanceOne();
                AdvanceOne();
                string text = _source.Substring(startIndex, _index - startIndex);
                return new CppToken(CppTokenKind.Comment, MakeSpan(startLine, startColumn, text.Length), text);
            }
            AdvanceOne();
        }

        // EOF without close.
        EmitDiagnostic(DiagUnterminatedComment, startLine, startColumn,
            "Unterminated block comment at end of file.");
        string partial = _source.Substring(startIndex, _index - startIndex);
        return new CppToken(CppTokenKind.Comment, MakeSpan(startLine, startColumn, partial.Length), partial);
    }

    private CppToken ReadPreprocessorDirective()
    {
        int startIndex = _index;
        int startLine = _line;
        int startColumn = _column;

        // Consume to end of line, honouring backslash-newline
        // continuations. The trailing newline is NOT consumed here --
        // we leave it for SkipWhitespaceInternal so the
        // _atLogicalLineStart flag transitions correctly for the next
        // token.
        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == '\\')
            {
                int peek = _index + 1;
                if (peek < _source.Length && _source[peek] == '\n')
                {
                    AdvanceOne(); // '\\'
                    AdvanceOne(); // '\n'
                    continue;
                }
                if (peek + 1 < _source.Length && _source[peek] == '\r' && _source[peek + 1] == '\n')
                {
                    AdvanceOne();
                    AdvanceOne();
                    AdvanceOne();
                    continue;
                }
                if (peek < _source.Length && _source[peek] == '\r')
                {
                    AdvanceOne(); // '\\'
                    AdvanceOne(); // '\r'
                    continue;
                }
            }
            if (c == '\n' || c == '\r')
            {
                break;
            }
            AdvanceOne();
        }

        string text = _source.Substring(startIndex, _index - startIndex);
        // The directive token itself is on a logical line; the trailing
        // newline (which SkipWhitespaceInternal will consume next) will
        // re-arm _atLogicalLineStart for the following token.
        return new CppToken(CppTokenKind.PreprocessorDirective, MakeSpan(startLine, startColumn, text.Length), text);
    }

    // =================================================================
    // Punctuators.
    // =================================================================

    private CppToken ReadPunctuator()
    {
        int startLine = _line;
        int startColumn = _column;
        int startIndex = _index;
        char c0 = _source[_index];
        char c1 = _index + 1 < _source.Length ? _source[_index + 1] : '\0';
        char c2 = _index + 2 < _source.Length ? _source[_index + 2] : '\0';

        CppTokenKind kind;
        int len;

        switch (c0)
        {
            case '(': kind = CppTokenKind.OpenParen; len = 1; break;
            case ')': kind = CppTokenKind.CloseParen; len = 1; break;
            case '{': kind = CppTokenKind.OpenBrace; len = 1; break;
            case '}': kind = CppTokenKind.CloseBrace; len = 1; break;
            case '[': kind = CppTokenKind.OpenBracket; len = 1; break;
            case ']': kind = CppTokenKind.CloseBracket; len = 1; break;
            case ',': kind = CppTokenKind.Comma; len = 1; break;
            case ';': kind = CppTokenKind.Semicolon; len = 1; break;
            case '?': kind = CppTokenKind.Question; len = 1; break;
            case '~': kind = CppTokenKind.Tilde; len = 1; break;

            case '.':
                if (c1 == '.' && c2 == '.') { kind = CppTokenKind.Ellipsis; len = 3; }
                else if (c1 == '*') { kind = CppTokenKind.DotStar; len = 2; }
                else { kind = CppTokenKind.Dot; len = 1; }
                break;

            case ':':
                if (c1 == ':') { kind = CppTokenKind.ColonColon; len = 2; }
                else { kind = CppTokenKind.Colon; len = 1; }
                break;

            case '-':
                if (c1 == '>' && c2 == '*') { kind = CppTokenKind.ArrowStar; len = 3; }
                else if (c1 == '>') { kind = CppTokenKind.Arrow; len = 2; }
                else if (c1 == '-') { kind = CppTokenKind.MinusMinus; len = 2; }
                else if (c1 == '=') { kind = CppTokenKind.MinusEq; len = 2; }
                else { kind = CppTokenKind.Minus; len = 1; }
                break;

            case '+':
                if (c1 == '+') { kind = CppTokenKind.PlusPlus; len = 2; }
                else if (c1 == '=') { kind = CppTokenKind.PlusEq; len = 2; }
                else { kind = CppTokenKind.Plus; len = 1; }
                break;

            case '*':
                if (c1 == '=') { kind = CppTokenKind.StarEq; len = 2; }
                else { kind = CppTokenKind.Star; len = 1; }
                break;

            case '/':
                if (c1 == '=') { kind = CppTokenKind.SlashEq; len = 2; }
                else { kind = CppTokenKind.Slash; len = 1; }
                break;

            case '%':
                if (c1 == '=') { kind = CppTokenKind.PercentEq; len = 2; }
                else { kind = CppTokenKind.Percent; len = 1; }
                break;

            case '=':
                if (c1 == '=') { kind = CppTokenKind.EqualsEquals; len = 2; }
                else { kind = CppTokenKind.Equals; len = 1; }
                break;

            case '!':
                if (c1 == '=') { kind = CppTokenKind.NotEquals; len = 2; }
                else { kind = CppTokenKind.Bang; len = 1; }
                break;

            case '<':
                if (c1 == '=' && c2 == '>') { kind = CppTokenKind.Spaceship; len = 3; }
                else if (c1 == '<' && c2 == '=') { kind = CppTokenKind.LessLessEq; len = 3; }
                else if (c1 == '<') { kind = CppTokenKind.LessLess; len = 2; }
                else if (c1 == '=') { kind = CppTokenKind.LessEq; len = 2; }
                else { kind = CppTokenKind.Less; len = 1; }
                break;

            case '>':
                if (c1 == '>' && c2 == '=') { kind = CppTokenKind.GreaterGreaterEq; len = 3; }
                else if (c1 == '>') { kind = CppTokenKind.GreaterGreater; len = 2; }
                else if (c1 == '=') { kind = CppTokenKind.GreaterEq; len = 2; }
                else { kind = CppTokenKind.Greater; len = 1; }
                break;

            case '&':
                if (c1 == '&') { kind = CppTokenKind.AmpAmp; len = 2; }
                else if (c1 == '=') { kind = CppTokenKind.AmpEq; len = 2; }
                else { kind = CppTokenKind.Ampersand; len = 1; }
                break;

            case '|':
                if (c1 == '|') { kind = CppTokenKind.PipePipe; len = 2; }
                else if (c1 == '=') { kind = CppTokenKind.PipeEq; len = 2; }
                else { kind = CppTokenKind.Pipe; len = 1; }
                break;

            case '^':
                if (c1 == '=') { kind = CppTokenKind.CaretEq; len = 2; }
                else { kind = CppTokenKind.Caret; len = 1; }
                break;

            case '#':
                if (c1 == '#') { kind = CppTokenKind.HashHash; len = 2; }
                else { kind = CppTokenKind.Hash; len = 1; }
                break;

            default:
                EmitDiagnostic(DiagUnsupportedPunctuator, startLine, startColumn,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Unsupported punctuator: '{0}' (0x{1:X4}).", c0, (int)c0));
                kind = CppTokenKind.UnknownPunctuator;
                len = 1;
                break;
        }

        for (int i = 0; i < len; i++)
        {
            AdvanceOne();
        }

        string text = _source.Substring(startIndex, len);
        return new CppToken(kind, MakeSpan(startLine, startColumn, len), text);
    }

    // =================================================================
    // Character-class helpers.
    // =================================================================

    private static bool IsIdentifierStart(char c)
    {
        // C++ identifiers: [A-Za-z_]. We also accept '$' per UHT's
        // tolerant rule (Section 3.1 brief).
        return char.IsLetter(c) || c == '_' || c == '$';
    }

    private static bool IsIdentifierContinue(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '$';
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static bool IsBinaryDigit(char c) => c == '0' || c == '1';

    // =================================================================
    // Cursor advance + span helpers.
    // =================================================================

    private void AdvanceOne()
    {
        if (_index >= _source.Length) { return; }
        char c = _source[_index];

        // Surrogate pair: count as TWO columns (one per UTF-16 code
        // unit) per M1 audit + XHT.html Section 3.1. This matches the
        // .NET String.Length convention -- a single astral-plane
        // codepoint at position N has Length == 2. IDEs that render
        // each codepoint as one glyph will show a one-off column
        // mismatch for diagnostics, which is the documented trade-off.
        if (char.IsHighSurrogate(c) && _index + 1 < _source.Length && char.IsLowSurrogate(_source[_index + 1]))
        {
            _index += 2;
            _column += 2;
            return;
        }

        if (c == '\n')
        {
            _index++;
            _line++;
            _column = 1;
            return;
        }
        if (c == '\r')
        {
            _index++;
            if (_index < _source.Length && _source[_index] == '\n')
            {
                _index++;
            }
            _line++;
            _column = 1;
            return;
        }

        _index++;
        _column++;
    }

    private SourceSpan MakeSpan(int line, int column, int length)
    {
        return new SourceSpan(_sourcePath, line, column, length);
    }

    private void EmitDiagnostic(string code, int line, int column, string message)
    {
        _diagnostics.Add(new DiagnosticRecord(
            DiagnosticSeverity.Error,
            code,
            message,
            File: _sourcePath,
            Line: line,
            Column: column));
    }
}
