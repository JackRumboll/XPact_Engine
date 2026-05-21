// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Parser.Cpp;

/// <summary>
/// Parser for the parenthesised specifier list following an XHT marker
/// per <c>/Documents/XHT.html</c> Rev 5 Section 7.2. Consumes
/// <c>(Foo, Bar=Baz, meta=(K="V"))</c> token sequences and emits a list
/// of <see cref="Specifier"/> records the resolver / validator pass
/// inspects in Phase 1d.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grammar.</b> The parser accepts:
/// </para>
/// <list type="bullet">
///   <item><description>Empty list: <c>()</c> -- emits zero specifiers.</description></item>
///   <item><description>Flag: <c>(EditAnywhere)</c> -- one <see cref="Specifier"/> with empty <see cref="Specifier.Values"/>.</description></item>
///   <item><description>Comma list: <c>(EditAnywhere, BlueprintReadWrite)</c> -- two specifiers.</description></item>
///   <item><description>Single value: <c>(Category="Combat")</c> -- one specifier with <c>Values=["Combat"]</c>.</description></item>
///   <item><description>Reference: <c>(Within=AActor)</c> -- one specifier with <c>Values=["AActor"]</c>.</description></item>
///   <item><description>Pipe list: <c>(BlueprintAuthorityOnly|BlueprintCallable)</c> -- split into two flag specifiers; emits XHT113 (DeprecatedPipeSyntax) diagnostic per the brief.</description></item>
///   <item><description>Nested meta: <c>(meta=(Tooltip="..", DisplayName=".."))</c> -- captured as a single specifier whose <see cref="Specifier.Values"/> array holds the inner key/value entries as alternating strings (Phase 1 simplification; the resolver lifts them in Phase 1d).</description></item>
///   <item><description>Parenthesised list: <c>HideCategories=("Foo","Bar")</c> -- captured as a single specifier whose <see cref="Specifier.Values"/> holds each entry.</description></item>
/// </list>
/// <para>
/// <b>Thread-local pattern (XHT.html Section 7.2).</b> UHT reuses one
/// specifier parser per thread to amortise allocations across a header
/// file's worth of markers. Phase 1c.2a constructs the parser per call;
/// the thread-local pool is a documented Phase 1d perf optimisation
/// (see the TODO at the top of the implementation). The public surface
/// is already shaped so callers can pool later without breaking.
/// </para>
/// <para>
/// <b>Context validation.</b> Each parsed specifier is resolved against
/// the registry under the caller-supplied
/// <see cref="SpecifierContext"/>. Resolution misses emit
/// <c>XHT110 (UnknownSpecifier)</c>; context-mismatch hits emit
/// <c>XHT111 (SpecifierIllegalInContext)</c>. The Specifier record is
/// still emitted on miss so the downstream AST shape is stable -- the
/// validator pass owns the suppress / promote decisions.
/// </para>
/// </remarks>
public sealed class CppSpecifierParser
{
    // TODO(Phase 1d): convert to thread-local pool. Today: instantiate
    // per call; the per-instance allocations are bounded by the
    // specifier-list size and are dwarfed by the surrounding parse cost.
    private readonly ISpecifierRegistry _registry;

    /// <summary>Diagnostic code: specifier name not found in registry (XHT.html Section 12.3 validator band).</summary>
    public const string DiagUnknownSpecifier = "XHT110";

    /// <summary>Diagnostic code: specifier registered but not legal in this context.</summary>
    public const string DiagSpecifierIllegalInContext = "XHT111";

    /// <summary>
    /// Diagnostic code: pipe-syntax alternative list (deprecated form).
    /// Per C7 audit (XHT.html Section 12.3): renumbered from XHT113 to
    /// XHT064 to avoid collision with the validator-band
    /// <c>DiagnosticCodes.RepNotifyInvalidSignature</c>.
    /// </summary>
    public const string DiagDeprecatedPipeSyntax = "XHT064";

    /// <summary>
    /// Diagnostic code: grammar error inside specifier list; consumed
    /// to next ',' or ')'. Per C7 audit: renumbered from XHT114 to
    /// XHT065 to avoid collision with the validator-band
    /// <c>DiagnosticCodes.ConfigConflictsWithNoExport</c>.
    /// </summary>
    public const string DiagSpecifierSyntaxError = "XHT065";

    /// <summary>
    /// Construct a parser bound to a specifier registry. The registry is
    /// consulted on every parsed specifier for context-validation.
    /// </summary>
    /// <param name="registry">The specifier registry. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="registry"/> is null.</exception>
    public CppSpecifierParser(ISpecifierRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Parse a specifier list. The caller positions
    /// <paramref name="tokenizer"/> just AFTER the opening <c>(</c>;
    /// this method consumes through the matching <c>)</c> (which is also
    /// consumed). On grammar error the parser advances to the next
    /// <c>,</c> or <c>)</c> and emits <c>XHT114</c>.
    /// </summary>
    /// <param name="tokenizer">Token source. Must not be null.</param>
    /// <param name="context">Active syntactic context for validator lookup. Must not be <see cref="SpecifierContext.None"/>.</param>
    /// <param name="diagnostics">Sink for context-validation + grammar diagnostics. Must not be null.</param>
    /// <returns>The parsed specifier list. May be empty.</returns>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public IReadOnlyList<Specifier> ParseSpecifierList(
        CppTokenizer tokenizer,
        SpecifierContext context,
        List<DiagnosticRecord> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(diagnostics);

        List<Specifier> result = new();

        while (true)
        {
            CppToken peek = tokenizer.Peek(0);
            if (peek.Kind == CppTokenKind.EndOfFile)
            {
                // Unterminated specifier list; caller will surface as a
                // structural error. Stop emitting specifiers here.
                return result;
            }
            if (peek.Kind == CppTokenKind.CloseParen)
            {
                tokenizer.Next(); // consume ')'
                return result;
            }
            if (peek.Kind == CppTokenKind.Comma)
            {
                // Leading / consecutive comma: skip.
                tokenizer.Next();
                continue;
            }

            ParseOneSpecifier(tokenizer, context, diagnostics, result);
        }
    }

    private void ParseOneSpecifier(
        CppTokenizer tokenizer,
        SpecifierContext context,
        List<DiagnosticRecord> diagnostics,
        List<Specifier> sink)
    {
        CppToken keyTok = tokenizer.Next();
        if (keyTok.Kind != CppTokenKind.Identifier
            && keyTok.Kind != CppTokenKind.Keyword
            && keyTok.Kind != CppTokenKind.XhtMarker)
        {
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagSpecifierSyntaxError,
                $"Expected specifier name; got '{keyTok.Text}' (kind={keyTok.Kind}).",
                File: keyTok.Span.SourceFilePath,
                Line: keyTok.Span.Line,
                Column: keyTok.Span.Column));
            RecoverToNextSpecifier(tokenizer);
            return;
        }

        string key = keyTok.Text;
        List<string> values = new();
        SourceSpan keySpan = keyTok.Span;

        CppToken afterKey = tokenizer.Peek(0);

        // Pipe-list deprecated alternative: (A|B|C). Split into individual
        // flag specifiers + emit XHT113 diagnostic for the originator.
        if (afterKey.Kind == CppTokenKind.Pipe)
        {
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Warning,
                DiagDeprecatedPipeSyntax,
                $"Pipe-syntax alternative list '({key}|...)' is deprecated; use comma-separated form.",
                File: keySpan.SourceFilePath,
                Line: keySpan.Line,
                Column: keySpan.Column));

            // Emit the first key as a flag.
            EmitSpecifier(sink, diagnostics, key, Array.Empty<string>(), keySpan, context);

            while (tokenizer.Peek(0).Kind == CppTokenKind.Pipe)
            {
                tokenizer.Next(); // consume '|'
                CppToken altTok = tokenizer.Next();
                if (altTok.Kind != CppTokenKind.Identifier
                    && altTok.Kind != CppTokenKind.Keyword
                    && altTok.Kind != CppTokenKind.XhtMarker)
                {
                    diagnostics.Add(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        DiagSpecifierSyntaxError,
                        $"Expected specifier name after '|'; got '{altTok.Text}'.",
                        File: altTok.Span.SourceFilePath,
                        Line: altTok.Span.Line,
                        Column: altTok.Span.Column));
                    RecoverToNextSpecifier(tokenizer);
                    return;
                }
                EmitSpecifier(sink, diagnostics, altTok.Text, Array.Empty<string>(), altTok.Span, context);
            }
            return;
        }

        if (afterKey.Kind == CppTokenKind.Equals)
        {
            tokenizer.Next(); // consume '='
            ParseValueAfterEquals(tokenizer, diagnostics, values);
        }

        EmitSpecifier(sink, diagnostics, key, values, keySpan, context);
    }

    private void ParseValueAfterEquals(
        CppTokenizer tokenizer,
        List<DiagnosticRecord> diagnostics,
        List<string> values)
    {
        CppToken peek = tokenizer.Peek(0);

        if (peek.Kind == CppTokenKind.OpenParen)
        {
            // (k="v", k2="v2", ...) form. Capture each comma-separated
            // entry as a value string. For nested key=value pairs we
            // store them as "K=V" so Phase 1d can re-split.
            tokenizer.Next(); // consume '('
            while (true)
            {
                CppToken inner = tokenizer.Peek(0);
                if (inner.Kind == CppTokenKind.CloseParen)
                {
                    tokenizer.Next();
                    return;
                }
                if (inner.Kind == CppTokenKind.EndOfFile)
                {
                    diagnostics.Add(new DiagnosticRecord(
                        DiagnosticSeverity.Error,
                        DiagSpecifierSyntaxError,
                        "Unterminated value list inside specifier.",
                        File: peek.Span.SourceFilePath,
                        Line: peek.Span.Line,
                        Column: peek.Span.Column));
                    return;
                }
                if (inner.Kind == CppTokenKind.Comma)
                {
                    tokenizer.Next();
                    continue;
                }
                values.Add(ReadOneValueEntry(tokenizer, diagnostics));
            }
        }

        // Single value.
        string single = ReadOneValueEntry(tokenizer, diagnostics);
        values.Add(single);
    }

    /// <summary>
    /// Read one entry inside a value list, which can be:
    /// <list type="bullet">
    ///   <item><description>A quoted string literal -- unquoted value returned.</description></item>
    ///   <item><description>An identifier (possibly with <c>::</c> qualifier or template angle brackets).</description></item>
    ///   <item><description>A numeric literal.</description></item>
    ///   <item><description>A <c>K=V</c> pair (nested meta) -- returned as <c>"K=V"</c>.</description></item>
    /// </list>
    /// </summary>
    private string ReadOneValueEntry(CppTokenizer tokenizer, List<DiagnosticRecord> diagnostics)
    {
        StringBuilder sb = new();
        int depth = 0;

        while (true)
        {
            CppToken t = tokenizer.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile)
            {
                return sb.ToString();
            }
            if (depth == 0)
            {
                if (t.Kind == CppTokenKind.Comma || t.Kind == CppTokenKind.CloseParen)
                {
                    return sb.ToString();
                }
            }

            tokenizer.Next();

            switch (t.Kind)
            {
                case CppTokenKind.StringLiteral:
                {
                    sb.Append(UnquoteString(t.Text));
                    break;
                }
                case CppTokenKind.RawStringLiteral:
                {
                    sb.Append(UnquoteRawString(t.Text));
                    break;
                }
                case CppTokenKind.OpenParen:
                    sb.Append('(');
                    depth++;
                    break;
                case CppTokenKind.CloseParen:
                    sb.Append(')');
                    depth--;
                    break;
                default:
                    sb.Append(t.Text);
                    break;
            }
        }
    }

    private static string UnquoteString(string raw)
    {
        // Strip leading prefix letters (u8, u, U, L) and the surrounding
        // double quotes. Preserve escape sequences as authored; the
        // resolver decodes them if it needs the literal byte form. This
        // is intentional -- XHT does not interpret escape semantics.
        int start = 0;
        while (start < raw.Length && raw[start] != '"')
        {
            start++;
        }
        if (start >= raw.Length) { return raw; }
        int end = raw.Length - 1;
        if (raw[end] == '"') { end--; }
        if (start + 1 > end + 1) { return string.Empty; }
        return raw.Substring(start + 1, end - start);
    }

    private static string UnquoteRawString(string raw)
    {
        // Form: prefix? R "DELIM( body )DELIM"
        int idx = raw.IndexOf("R\"", StringComparison.Ordinal);
        if (idx < 0) { return raw; }
        int parenOpen = raw.IndexOf('(', idx);
        if (parenOpen < 0) { return raw; }
        int parenClose = raw.LastIndexOf(')');
        if (parenClose < parenOpen) { return raw; }
        return raw.Substring(parenOpen + 1, parenClose - parenOpen - 1);
    }

    private void EmitSpecifier(
        List<Specifier> sink,
        List<DiagnosticRecord> diagnostics,
        string key,
        IReadOnlyList<string> values,
        SourceSpan span,
        SpecifierContext context)
    {
        // Registry lookup: emit XHT110 / XHT111 on miss / context-mismatch
        // but still record the specifier so downstream AST shape holds.
        if (_registry.Resolve(key, context) is null)
        {
            // Try a context-blind lookup to distinguish "unknown name"
            // (XHT110) from "wrong context" (XHT111).
            if (_registry.Resolve(key, SpecifierContext.All) is null)
            {
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    DiagUnknownSpecifier,
                    $"Unknown specifier '{key}'.",
                    File: span.SourceFilePath,
                    Line: span.Line,
                    Column: span.Column));
            }
            else
            {
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    DiagSpecifierIllegalInContext,
                    $"Specifier '{key}' is not legal in this context ({context}).",
                    File: span.SourceFilePath,
                    Line: span.Line,
                    Column: span.Column));
            }
        }

        sink.Add(new Specifier(key, values, span));
    }

    private static void RecoverToNextSpecifier(CppTokenizer tokenizer)
    {
        // Skip tokens until we hit the next ',' or ')' or EOF. Leave
        // the terminator in place; the caller loop handles it.
        while (true)
        {
            CppToken t = tokenizer.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile
                || t.Kind == CppTokenKind.Comma
                || t.Kind == CppTokenKind.CloseParen)
            {
                return;
            }
            tokenizer.Next();
        }
    }
}
