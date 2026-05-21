// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Parser.Cpp;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Parser;

/// <summary>
/// Tests for <see cref="CppTokenizer"/>. Covers every token kind,
/// multi-line and line comments, raw strings, escape sequences,
/// numeric literal grammar (decimal, hex, binary, octal, hex-float,
/// digit separator), preprocessor directive line handling, position
/// tracking, error recovery, and determinism per
/// <c>/Documents/XHT.html</c> Rev 5 Section 3.1.
/// </summary>
public class CppTokenizerTests
{
    private const string Path = "Test.h";

    private static List<CppToken> ReadAll(string source)
    {
        CppTokenizer tok = new(Path, source);
        List<CppToken> list = new();
        while (true)
        {
            CppToken t = tok.Next();
            list.Add(t);
            if (t.Kind == CppTokenKind.EndOfFile) { break; }
        }
        return list;
    }

    // ===== Identifiers + keywords + markers =====================

    [Fact]
    public void Identifier_PlainLetters_TokenisedAsIdentifier()
    {
        List<CppToken> ts = ReadAll("foo");
        Assert.Equal(2, ts.Count);
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
        Assert.Equal("foo", ts[0].Text);
        Assert.Equal(CppTokenKind.EndOfFile, ts[1].Kind);
    }

    [Fact]
    public void Identifier_WithUnderscoresDigitsAndDollar_OK()
    {
        List<CppToken> ts = ReadAll("foo_bar123 $cash");
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
        Assert.Equal("foo_bar123", ts[0].Text);
        Assert.Equal(CppTokenKind.Identifier, ts[1].Kind);
        Assert.Equal("$cash", ts[1].Text);
    }

    [Fact]
    public void Keyword_IsTokenisedDistinctFromIdentifier()
    {
        List<CppToken> ts = ReadAll("class struct const virtual override");
        Assert.Equal(CppTokenKind.Keyword, ts[0].Kind);
        Assert.Equal(CppTokenKind.Keyword, ts[1].Kind);
        Assert.Equal(CppTokenKind.Keyword, ts[2].Kind);
        Assert.Equal(CppTokenKind.Keyword, ts[3].Kind);
        Assert.Equal(CppTokenKind.Keyword, ts[4].Kind);
    }

    [Theory]
    [InlineData("XCLASS")]
    [InlineData("XSTRUCT")]
    [InlineData("XENUM")]
    [InlineData("XINTERFACE")]
    [InlineData("XFUNCTION")]
    [InlineData("XPROPERTY")]
    [InlineData("XDELEGATE")]
    [InlineData("XPARAM")]
    [InlineData("XMETA")]
    [InlineData("XGENERATED_BODY")]
    public void XhtMarker_AllTenLockedMarkersAreRecognised(string marker)
    {
        List<CppToken> ts = ReadAll(marker);
        Assert.Equal(CppTokenKind.XhtMarker, ts[0].Kind);
        Assert.Equal(marker, ts[0].Text);
    }

    [Fact]
    public void XhtMarker_LowercaseIsNotMarker()
    {
        List<CppToken> ts = ReadAll("xclass");
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
    }

    // ===== Comments =============================================

    [Fact]
    public void LineComment_IsSkippedByDefault()
    {
        List<CppToken> ts = ReadAll("// comment\nfoo");
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
        Assert.Equal("foo", ts[0].Text);
    }

    [Fact]
    public void BlockComment_IsSkippedByDefault()
    {
        List<CppToken> ts = ReadAll("/* a block */ foo");
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
        Assert.Equal("foo", ts[0].Text);
    }

    [Fact]
    public void BlockComment_MultilineUpdatesLineNumber()
    {
        CppTokenizer tok = new(Path, "/* a\n b\n c */ foo");
        CppToken t = tok.Next();
        Assert.Equal(CppTokenKind.Identifier, t.Kind);
        Assert.Equal(3, t.Span.Line);
    }

    [Fact]
    public void BlockComment_Unterminated_EmitsDiagnostic_AndAdvancesToEof()
    {
        CppTokenizer tok = new(Path, "/* never closed");
        // Default SkipComments=true skips even the unterminated comment;
        // the diagnostic is still recorded.
        tok.Next();
        Assert.Contains(tok.Diagnostics, d => d.Code == CppTokenizer.DiagUnterminatedComment);
    }

    [Fact]
    public void LineComment_RetainedWhenSkipCommentsFalse()
    {
        CppTokenizer tok = new(Path, "// hi\nfoo") { SkipComments = false };
        CppToken first = tok.Next();
        Assert.Equal(CppTokenKind.Comment, first.Kind);
        Assert.Equal("// hi", first.Text);
        CppToken second = tok.Next();
        Assert.Equal(CppTokenKind.Identifier, second.Kind);
    }

    // ===== String + char literal ================================

    [Fact]
    public void StringLiteral_PlainAscii_OK()
    {
        List<CppToken> ts = ReadAll("\"hello world\"");
        Assert.Equal(CppTokenKind.StringLiteral, ts[0].Kind);
        Assert.Equal("\"hello world\"", ts[0].Text);
    }

    [Fact]
    public void StringLiteral_WithEscape_OK()
    {
        List<CppToken> ts = ReadAll("\"a\\nb\\\"c\"");
        Assert.Equal(CppTokenKind.StringLiteral, ts[0].Kind);
        Assert.Equal("\"a\\nb\\\"c\"", ts[0].Text);
    }

    [Fact]
    public void StringLiteral_Unterminated_EmitsDiagnostic()
    {
        CppTokenizer tok = new(Path, "\"unterminated");
        tok.Next();
        Assert.Contains(tok.Diagnostics, d => d.Code == CppTokenizer.DiagUnterminatedString);
    }

    [Fact]
    public void StringLiteral_PrefixedForms_TokenisedAsStringLiteral()
    {
        // u8 prefix.
        List<CppToken> ts = ReadAll("u8\"abc\"");
        Assert.Equal(CppTokenKind.StringLiteral, ts[0].Kind);
        Assert.Equal("u8\"abc\"", ts[0].Text);

        // u prefix.
        ts = ReadAll("u\"abc\"");
        Assert.Equal(CppTokenKind.StringLiteral, ts[0].Kind);
        Assert.Equal("u\"abc\"", ts[0].Text);

        // L prefix.
        ts = ReadAll("L\"abc\"");
        Assert.Equal(CppTokenKind.StringLiteral, ts[0].Kind);
        Assert.Equal("L\"abc\"", ts[0].Text);
    }

    [Fact]
    public void RawString_BasicDelimiter_OK()
    {
        List<CppToken> ts = ReadAll("R\"(hello)\"");
        Assert.Equal(CppTokenKind.RawStringLiteral, ts[0].Kind);
        Assert.Equal("R\"(hello)\"", ts[0].Text);
    }

    [Fact]
    public void RawString_NamedDelimiterAndEmbeddedQuote_OK()
    {
        string src = "R\"DELIM(he said \"hi\" yes)DELIM\"";
        List<CppToken> ts = ReadAll(src);
        Assert.Equal(CppTokenKind.RawStringLiteral, ts[0].Kind);
        Assert.Equal(src, ts[0].Text);
    }

    [Fact]
    public void RawString_WithU8Prefix_OK()
    {
        List<CppToken> ts = ReadAll("u8R\"(abc)\"");
        Assert.Equal(CppTokenKind.RawStringLiteral, ts[0].Kind);
    }

    [Fact]
    public void CharLiteral_PlainSingleQuote_OK()
    {
        List<CppToken> ts = ReadAll("'a'");
        Assert.Equal(CppTokenKind.CharLiteral, ts[0].Kind);
        Assert.Equal("'a'", ts[0].Text);
    }

    [Fact]
    public void CharLiteral_WithEscape_OK()
    {
        List<CppToken> ts = ReadAll(@"'\n'");
        Assert.Equal(CppTokenKind.CharLiteral, ts[0].Kind);
    }

    // ===== Numeric literal ======================================

    [Theory]
    [InlineData("123", CppTokenKind.IntegerLiteral)]
    [InlineData("0xDEADBEEF", CppTokenKind.IntegerLiteral)]
    [InlineData("0b1010", CppTokenKind.IntegerLiteral)]
    [InlineData("0123", CppTokenKind.IntegerLiteral)]
    [InlineData("1'000'000", CppTokenKind.IntegerLiteral)]
    [InlineData("42u", CppTokenKind.IntegerLiteral)]
    [InlineData("42ULL", CppTokenKind.IntegerLiteral)]
    public void Integer_VariousForms_OK(string source, CppTokenKind kind)
    {
        List<CppToken> ts = ReadAll(source);
        Assert.Equal(kind, ts[0].Kind);
        Assert.Equal(source, ts[0].Text);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e10")]
    [InlineData("1.5e-10")]
    [InlineData(".5")]
    [InlineData("1.0f")]
    [InlineData("0x1.8p10")]
    public void Float_VariousForms_OK(string source)
    {
        List<CppToken> ts = ReadAll(source);
        Assert.Equal(CppTokenKind.FloatingLiteral, ts[0].Kind);
    }

    [Fact]
    public void Hex_EmptyDigits_EmitsInvalidNumericDiagnostic()
    {
        CppTokenizer tok = new(Path, "0x");
        tok.Next();
        Assert.Contains(tok.Diagnostics, d => d.Code == CppTokenizer.DiagInvalidNumericLiteral);
    }

    // ===== Punctuators ==========================================

    [Theory]
    [InlineData("(", CppTokenKind.OpenParen)]
    [InlineData(")", CppTokenKind.CloseParen)]
    [InlineData("{", CppTokenKind.OpenBrace)]
    [InlineData("}", CppTokenKind.CloseBrace)]
    [InlineData("[", CppTokenKind.OpenBracket)]
    [InlineData("]", CppTokenKind.CloseBracket)]
    [InlineData(",", CppTokenKind.Comma)]
    [InlineData(";", CppTokenKind.Semicolon)]
    [InlineData("...", CppTokenKind.Ellipsis)]
    [InlineData("::", CppTokenKind.ColonColon)]
    [InlineData("->", CppTokenKind.Arrow)]
    [InlineData("->*", CppTokenKind.ArrowStar)]
    [InlineData("<=>", CppTokenKind.Spaceship)]
    [InlineData("<<=", CppTokenKind.LessLessEq)]
    [InlineData(">>=", CppTokenKind.GreaterGreaterEq)]
    [InlineData("<<", CppTokenKind.LessLess)]
    [InlineData(">>", CppTokenKind.GreaterGreater)]
    [InlineData("==", CppTokenKind.EqualsEquals)]
    [InlineData("!=", CppTokenKind.NotEquals)]
    [InlineData("&&", CppTokenKind.AmpAmp)]
    [InlineData("||", CppTokenKind.PipePipe)]
    public void Punctuator_LongestMatchWins(string source, CppTokenKind expected)
    {
        List<CppToken> ts = ReadAll(source);
        Assert.Equal(expected, ts[0].Kind);
        Assert.Equal(source, ts[0].Text);
    }

    [Fact]
    public void HashHash_NotAtLineStart_TokenisedAsPunctuator()
    {
        // ## at line start is parsed as a preprocessor directive (the
        // line-start # rule); the punctuator form fires only in the
        // middle of a line (typical use is inside macro bodies).
        List<CppToken> ts = ReadAll("foo ##");
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
        Assert.Equal(CppTokenKind.HashHash, ts[1].Kind);
    }

    [Fact]
    public void GreaterGreater_TokenisedAsSingleToken_NotSplit()
    {
        // Per Section 3.1: tokenizer emits >> as one token; parser-side
        // template-depth tracker handles disambiguation.
        List<CppToken> ts = ReadAll("TArray<TArray<int>>");
        bool sawGG = false;
        foreach (CppToken t in ts)
        {
            if (t.Kind == CppTokenKind.GreaterGreater) { sawGG = true; break; }
        }
        Assert.True(sawGG, "GreaterGreater should be emitted as a single token.");
    }

    // ===== Preprocessor =========================================

    [Fact]
    public void PreprocessorDirective_TakesEntireLine()
    {
        List<CppToken> ts = ReadAll("#include \"foo.h\"\nclass");
        Assert.Equal(CppTokenKind.PreprocessorDirective, ts[0].Kind);
        Assert.Equal("#include \"foo.h\"", ts[0].Text);
        Assert.Equal(CppTokenKind.Keyword, ts[1].Kind);
        Assert.Equal("class", ts[1].Text);
    }

    [Fact]
    public void PreprocessorDirective_LineContinuation_Extends()
    {
        string src = "#define FOO 1 \\\n2 \\\n3\nbar";
        List<CppToken> ts = ReadAll(src);
        Assert.Equal(CppTokenKind.PreprocessorDirective, ts[0].Kind);
        Assert.Contains("2", ts[0].Text);
        Assert.Contains("3", ts[0].Text);
    }

    [Fact]
    public void Hash_NotAtLineStart_TokenisedAsPunctuator()
    {
        // #stringify operator inside a macro body; in our tokenizer the
        // # is only a directive when at column 1. Here it's a punctuator.
        List<CppToken> ts = ReadAll("foo # bar");
        Assert.Equal(CppTokenKind.Identifier, ts[0].Kind);
        Assert.Equal(CppTokenKind.Hash, ts[1].Kind);
        Assert.Equal(CppTokenKind.Identifier, ts[2].Kind);
    }

    [Fact]
    public void PreprocessorDirective_HashWithSpace_StillRecognised()
    {
        // "# define" with whitespace between # and the directive name
        // is a valid (if unusual) C preprocessor form. Treat as a
        // directive token spanning the line.
        List<CppToken> ts = ReadAll("# define FOO 1\nbar");
        Assert.Equal(CppTokenKind.PreprocessorDirective, ts[0].Kind);
        Assert.Contains("define", ts[0].Text);
        Assert.Equal(CppTokenKind.Identifier, ts[1].Kind);
        Assert.Equal("bar", ts[1].Text);
    }

    [Fact]
    public void HashAfterLineContinuation_NotMisclassifiedAsDirective()
    {
        // Per C1 audit (XHT.html Section 3.1): after a '\\<LF>'
        // continuation, the wrap resets column to 1 but the next '#'
        // is logically mid-statement -- it must NOT be treated as a
        // preprocessor directive. Example: a macro body where the next
        // physical line begins with the # stringify operator.
        string src = "obj.method() \\\n# 5;";
        List<CppToken> ts = ReadAll(src);

        // Expected: identifier 'obj', '.', identifier 'method', '(',
        // ')', '#', integer-literal '5', ';', EOF. NO directive token.
        foreach (CppToken t in ts)
        {
            Assert.NotEqual(CppTokenKind.PreprocessorDirective, t.Kind);
        }
        // Verify the '#' is present as a Hash punctuator.
        bool sawHashPunct = false;
        foreach (CppToken t in ts)
        {
            if (t.Kind == CppTokenKind.Hash) { sawHashPunct = true; break; }
        }
        Assert.True(sawHashPunct, "Expected '#' to be tokenised as a Hash punctuator, not a directive.");
    }

    [Fact]
    public void PreprocessorDirective_DefineWithContinuedBody_RecognisedAsDirective()
    {
        // Sanity-check the inverse of the C1 fix: a real directive that
        // spans multiple lines via '\\<LF>' continuations is still
        // recognised as a single directive token. The continuations
        // fold within the directive body.
        string src = "#define FOO BAR \\\nBAZ\nrest";
        List<CppToken> ts = ReadAll(src);
        Assert.Equal(CppTokenKind.PreprocessorDirective, ts[0].Kind);
        Assert.Contains("BAZ", ts[0].Text);
        // After the directive's trailing newline, normal tokens resume.
        Assert.Equal(CppTokenKind.Identifier, ts[1].Kind);
        Assert.Equal("rest", ts[1].Text);
    }

    [Fact]
    public void HashAtLineStart_AfterContinuedPreviousLine_IsDirective()
    {
        // A continued line that REALLY ENDS at a fresh newline, followed
        // by a new logical line beginning with '#', IS a directive.
        // Continuation is "\\\n"; the directive that follows after the
        // *unescaped* "\n" is on its own logical line.
        string src = "int x = 1 \\\n+ 2;\n#include \"y.h\"\n";
        List<CppToken> ts = ReadAll(src);
        // Expect a directive somewhere in the stream.
        bool sawDirective = false;
        foreach (CppToken t in ts)
        {
            if (t.Kind == CppTokenKind.PreprocessorDirective)
            {
                sawDirective = true;
                Assert.Contains("include", t.Text);
                break;
            }
        }
        Assert.True(sawDirective, "Expected '#include' on a fresh logical line to be a directive.");
    }

    // ===== Position tracking ====================================

    [Fact]
    public void Position_NewlineResetsColumnAndAdvancesLine()
    {
        CppTokenizer tok = new(Path, "abc\nxyz");
        CppToken a = tok.Next();
        Assert.Equal(1, a.Span.Line);
        Assert.Equal(1, a.Span.Column);
        CppToken b = tok.Next();
        Assert.Equal(2, b.Span.Line);
        Assert.Equal(1, b.Span.Column);
    }

    [Fact]
    public void Position_CrlfTreatedAsSingleLineBreak()
    {
        CppTokenizer tok = new(Path, "abc\r\nxyz");
        tok.Next();
        CppToken b = tok.Next();
        Assert.Equal(2, b.Span.Line);
        Assert.Equal(1, b.Span.Column);
    }

    [Fact]
    public void Position_ColumnTracksUtf16CodeUnits()
    {
        CppTokenizer tok = new(Path, "abcd defgh");
        CppToken first = tok.Next();
        CppToken second = tok.Next();
        Assert.Equal(1, first.Span.Column);
        Assert.Equal(4, first.Span.Length);
        Assert.Equal(6, second.Span.Column);
    }

    // ===== Peek =================================================

    [Fact]
    public void Peek_DoesNotConsume_AndReturnsSameTokenAsNext()
    {
        CppTokenizer tok = new(Path, "foo bar");
        CppToken p = tok.Peek(0);
        CppToken n = tok.Next();
        Assert.Equal(p, n);
    }

    [Fact]
    public void Peek_DistanceOne_ReturnsSecondToken()
    {
        CppTokenizer tok = new(Path, "foo bar baz");
        CppToken first = tok.Peek(0);
        CppToken second = tok.Peek(1);
        Assert.Equal("foo", first.Text);
        Assert.Equal("bar", second.Text);
        // Consume; the queued ones come out in order.
        Assert.Equal("foo", tok.Next().Text);
        Assert.Equal("bar", tok.Next().Text);
        Assert.Equal("baz", tok.Next().Text);
    }

    [Fact]
    public void Peek_NegativeDistance_Throws()
    {
        CppTokenizer tok = new(Path, "foo");
        Assert.Throws<ArgumentOutOfRangeException>(() => tok.Peek(-1));
    }

    [Fact]
    public void Eof_IsIdempotent()
    {
        CppTokenizer tok = new(Path, "");
        for (int i = 0; i < 3; i++)
        {
            CppToken t = tok.Next();
            Assert.Equal(CppTokenKind.EndOfFile, t.Kind);
        }
    }

    // ===== Determinism ==========================================

    [Fact]
    public void Determinism_TwoRunsOverSameInput_ProduceIdenticalTokenStream()
    {
        string src = "class XCORE_API XValve : public XActor { XPROPERTY() int32 Health; };";
        List<CppToken> first = ReadAll(src);
        List<CppToken> second = ReadAll(src);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Kind, second[i].Kind);
            Assert.Equal(first[i].Text, second[i].Text);
            Assert.Equal(first[i].Span.Line, second[i].Span.Line);
            Assert.Equal(first[i].Span.Column, second[i].Span.Column);
            Assert.Equal(first[i].Span.Length, second[i].Span.Length);
        }
    }

    [Fact]
    public void Determinism_NoTokenStreamCarriesTime_OrTickCount()
    {
        // Indirect: every span has Line / Column that are deterministic
        // based on source layout, never on wall-clock. Asserting absence
        // is impossible directly, but a structural diff between two
        // runs is the right proxy and is already covered above. This
        // test asserts that consecutive integers in fixed positions
        // match what a hand-trace would predict, which would fail if
        // the tokenizer wandered.
        string src = "// a\nint x;";
        CppTokenizer tok = new(Path, src);
        CppToken keyword = tok.Next();
        Assert.Equal(2, keyword.Span.Line);
        Assert.Equal(1, keyword.Span.Column);
        Assert.Equal("int", keyword.Text);
    }
}
