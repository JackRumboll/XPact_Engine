// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Parser.Cpp;

/// <summary>
/// Marker-driven AST builder per <c>/Documents/XHT.html</c> Rev 5
/// Section 3.4 + Section 7. Walks a <see cref="CppTokenizer"/> token
/// stream, tracks namespace + outer-class scope, recognises the 10 XHT
/// reflection markers (<c>XCLASS</c>, <c>XSTRUCT</c>, <c>XENUM</c>,
/// <c>XINTERFACE</c>, <c>XFUNCTION</c>, <c>XPROPERTY</c>, <c>XDELEGATE</c>,
/// <c>XPARAM</c>, <c>XMETA</c>, <c>XGENERATED_BODY</c>), parses the
/// parenthesised specifier list via <see cref="CppSpecifierParser"/>,
/// locates the following declaration, and emits the corresponding
/// AST node into the caller-supplied <see cref="SymbolTable"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope tracking.</b> The scanner maintains:
/// </para>
/// <list type="bullet">
///   <item><description>
///     A <em>namespace stack</em>. <c>namespace X { ... }</c> pushes the
///     identifier <c>X</c> on entry to the body; the matching <c>}</c>
///     pops it. Brace tracking is naive (counts <c>{</c> / <c>}</c>
///     unconditionally), which suffices because XHT does not pretend to
///     parse C++ bodies -- only the marker scope chain matters.
///   </description></item>
///   <item><description>
///     A <em>type stack</em>. A <c>class Foo { ... }</c> or
///     <c>struct Bar { ... }</c> body pushes the type name; pending
///     <c>XFUNCTION</c> / <c>XPROPERTY</c> markers inside the body
///     associate with the enclosing class entry. The matching <c>}</c>
///     finalises the class entry by registering its accumulated
///     functions + properties.
///   </description></item>
/// </list>
/// <para>
/// <b>Declaration discovery.</b> After parsing the specifier list, the
/// scanner peeks the next non-skipped tokens to discover the declaration:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>XCLASS(...)</c> expects <c>class</c> (optionally followed by
///     <c>&lt;MODULE&gt;_API</c>) then an identifier, optional super
///     chain <c>: public &lt;Super&gt;[, public &lt;IFace&gt;...]</c>,
///     then <c>{</c>. The <c>RequiredAPIMacroName</c> is recorded; the
///     class body is entered.
///   </description></item>
///   <item><description>
///     <c>XSTRUCT(...)</c> expects <c>struct</c> then an identifier and
///     an optional super; struct body entered.
///   </description></item>
///   <item><description>
///     <c>XENUM(...)</c> expects <c>enum</c> with optional <c>class</c>
///     keyword, an identifier, optional <c>: &lt;UnderlyingType&gt;</c>,
///     then a brace-delimited member list. The members are parsed as
///     identifier (optionally with <c>= &lt;expression&gt;</c>).
///   </description></item>
///   <item><description>
///     <c>XINTERFACE(...)</c> expects <c>class</c> + identifier per UHT
///     pairing; same shape as <c>XCLASS</c> but registered as
///     <c>XhtInterface</c>.
///   </description></item>
///   <item><description>
///     <c>XFUNCTION(...)</c> expects a method declaration inside the
///     current class scope. The scanner captures the function name +
///     return type + parameter list verbatim (the resolver lifts
///     types in Phase 1d).
///   </description></item>
///   <item><description>
///     <c>XPROPERTY(...)</c> expects a member declaration inside the
///     current class / struct scope: <c>&lt;type&gt; &lt;name&gt;;</c>.
///   </description></item>
///   <item><description>
///     <c>XDELEGATE(...)</c> expects a delegate macro
///     (<c>DECLARE_DYNAMIC_DELEGATE_*</c>); the scanner captures the
///     macro argument list verbatim.
///   </description></item>
///   <item><description>
///     <c>XGENERATED_BODY()</c> / <c>XGENERATED_BODY</c> -- sets
///     <c>HasGeneratedBody = true</c> on the enclosing class entry.
///   </description></item>
/// </list>
/// <para>
/// <b>Error recovery.</b> An unknown declaration shape after a marker
/// emits <c>XHT115 (MalformedMarkerDeclaration)</c> and the scanner
/// advances to the next <c>;</c> or <c>}</c>. The scan continues; one
/// malformed marker does not abort the file.
/// </para>
/// <para>
/// <b>Symbol-table registration.</b> Each top-level reflected type
/// registers via <see cref="SymbolTable.Register"/>. A caseless
/// collision is converted into <c>XHT040 (DuplicateType)</c> and the
/// later type is dropped; the earlier remains.
/// </para>
/// </remarks>
public sealed class CppMarkerScanner
{
    /// <summary>Diagnostic code: caseless symbol-table collision.</summary>
    public const string DiagDuplicateType = "XHT040";

    /// <summary>Diagnostic code: marker followed by declaration we don't recognise.</summary>
    public const string DiagMalformedMarkerDeclaration = "XHT115";

    /// <summary>Diagnostic code: marker context error (e.g. XFUNCTION outside class).</summary>
    public const string DiagMarkerContextError = "XHT116";

    private readonly string _sourcePath;
    private readonly string _sourceText;
    private readonly string _moduleName;
    private readonly ISpecifierRegistry _registry;
    private readonly SymbolTable _symbolTable;
    private readonly List<DiagnosticRecord> _diagnostics = new();
    private readonly CppSpecifierParser _specifierParser;

    /// <summary>
    /// Construct a scanner over the named source. Construction does not
    /// scan; call <see cref="Scan"/> to walk the source.
    /// </summary>
    /// <param name="sourcePath">Absolute source-file path. Must not be null.</param>
    /// <param name="sourceText">Source text (already UTF-8-decoded to .NET string). Must not be null.</param>
    /// <param name="moduleName">Owning module name from XBT manifest. Must not be null.</param>
    /// <param name="specifierRegistry">Specifier registry for context validation. Must not be null.</param>
    /// <param name="symbolTable">Caseless symbol table to populate. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public CppMarkerScanner(
        string sourcePath,
        string sourceText,
        string moduleName,
        ISpecifierRegistry specifierRegistry,
        SymbolTable symbolTable)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(moduleName);
        ArgumentNullException.ThrowIfNull(specifierRegistry);
        ArgumentNullException.ThrowIfNull(symbolTable);

        _sourcePath = sourcePath;
        _sourceText = sourceText;
        _moduleName = moduleName;
        _registry = specifierRegistry;
        _symbolTable = symbolTable;
        _specifierParser = new CppSpecifierParser(specifierRegistry);
    }

    /// <summary>
    /// Diagnostics accumulated during the scan. Includes tokenizer-side
    /// diagnostics (XHT060-XHT063 lexer band) + scanner-side diagnostics
    /// (XHT040 duplicate, XHT110-XHT114 specifier, XHT115-XHT116 marker
    /// band). The list is appended in scan order.
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics => _diagnostics;

    /// <summary>
    /// Walk the source and emit AST nodes into the symbol table. Returns
    /// the list of top-level reflected types emitted (in source-discovery
    /// order). Nested types are also pushed into the symbol table but do
    /// not appear in the returned list.
    /// </summary>
    /// <returns>The top-level reflected types emitted; never null.</returns>
    public IReadOnlyList<XhtTypeBase> Scan()
    {
        List<XhtTypeBase> roots = new();

        CppTokenizer tok = new(_sourcePath, _sourceText);
        ScanContext ctx = new(tok);

        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile)
            {
                break;
            }

            // Namespace tracking.
            if (t.Kind == CppTokenKind.Keyword && t.Text == "namespace")
            {
                HandleNamespaceOpener(ctx);
                continue;
            }

            // Anonymous brace tracking outside class/namespace bodies.
            if (t.Kind == CppTokenKind.OpenBrace)
            {
                tok.Next();
                ctx.PushAnonymousBrace();
                continue;
            }
            if (t.Kind == CppTokenKind.CloseBrace)
            {
                tok.Next();
                ctx.PopScope(_diagnostics, _symbolTable, _moduleName, _sourcePath, roots);
                continue;
            }

            if (t.Kind == CppTokenKind.XhtMarker)
            {
                HandleMarker(ctx, roots);
                continue;
            }

            // Default: advance.
            tok.Next();
        }

        // Append tokenizer diagnostics.
        foreach (DiagnosticRecord d in tok.Diagnostics)
        {
            _diagnostics.Add(d);
        }

        return roots;
    }

    // =================================================================
    // Namespace handling.
    // =================================================================

    private void HandleNamespaceOpener(ScanContext ctx)
    {
        CppTokenizer tok = ctx.Tokenizer;
        tok.Next(); // 'namespace'

        // Optional namespace name. Anonymous namespace: 'namespace { ... }'.
        string name = string.Empty;
        CppToken peek = tok.Peek(0);
        if (peek.Kind == CppTokenKind.Identifier)
        {
            name = peek.Text;
            tok.Next();
            // Possibly a nested namespace: 'namespace A::B::C { ... }'.
            while (tok.Peek(0).Kind == CppTokenKind.ColonColon)
            {
                tok.Next();
                CppToken next = tok.Next();
                if (next.Kind == CppTokenKind.Identifier)
                {
                    name = name + "::" + next.Text;
                }
                else
                {
                    break;
                }
            }
        }

        // Skip to '{'. Some forms use 'namespace X = Y;' (alias) -- we
        // treat that as a no-op (no scope push) and consume to ';'.
        while (true)
        {
            CppToken next = tok.Peek(0);
            if (next.Kind == CppTokenKind.EndOfFile)
            {
                return;
            }
            if (next.Kind == CppTokenKind.OpenBrace)
            {
                tok.Next();
                ctx.PushNamespace(name);
                return;
            }
            if (next.Kind == CppTokenKind.Semicolon)
            {
                tok.Next();
                return;
            }
            tok.Next();
        }
    }

    // =================================================================
    // Marker dispatch.
    // =================================================================

    private void HandleMarker(ScanContext ctx, List<XhtTypeBase> roots)
    {
        CppTokenizer tok = ctx.Tokenizer;
        CppToken markerTok = tok.Next();
        string marker = markerTok.Text;

        // Parse optional specifier list -- markers can appear without
        // parens (XGENERATED_BODY is the canonical case).
        List<Specifier> specifiers = new();
        CppToken afterMarker = tok.Peek(0);
        if (afterMarker.Kind == CppTokenKind.OpenParen)
        {
            tok.Next(); // consume '('
            SpecifierContext sctx = MarkerToContext(marker);
            IReadOnlyList<Specifier> parsed = _specifierParser.ParseSpecifierList(tok, sctx, _diagnostics);
            specifiers = new List<Specifier>(parsed);
        }

        switch (marker)
        {
            case "XCLASS":
                HandleXClass(ctx, markerTok.Span, specifiers, roots);
                break;
            case "XSTRUCT":
                HandleXStruct(ctx, markerTok.Span, specifiers, roots);
                break;
            case "XENUM":
                HandleXEnum(ctx, markerTok.Span, specifiers, roots);
                break;
            case "XINTERFACE":
                HandleXInterface(ctx, markerTok.Span, specifiers, roots);
                break;
            case "XFUNCTION":
                HandleXFunction(ctx, markerTok.Span, specifiers);
                break;
            case "XPROPERTY":
                HandleXProperty(ctx, markerTok.Span, specifiers);
                break;
            case "XDELEGATE":
                HandleXDelegate(ctx, markerTok.Span, specifiers, roots);
                break;
            case "XGENERATED_BODY":
                HandleXGeneratedBody(ctx);
                break;
            case "XPARAM":
            case "XMETA":
                // These mark inline metadata on the *next* parameter / enum
                // value. The marker itself emits no top-level node; we
                // attach the specifiers to a context slot the surrounding
                // declaration parser picks up. For Phase 1c.2a we capture
                // the marker-specifier triple and leave use to the
                // enclosing handler (XFUNCTION / XENUM) which reads the
                // pending-attach slot.
                ctx.PendingInlineSpecifiers.Add((marker, specifiers, markerTok.Span));
                break;
            default:
                // Unknown XHT marker -- shouldn't happen because
                // CppKeywordTable controls the vocabulary, but guard.
                _diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    DiagMalformedMarkerDeclaration,
                    $"Unknown XHT marker '{marker}'.",
                    File: markerTok.Span.SourceFilePath,
                    Line: markerTok.Span.Line,
                    Column: markerTok.Span.Column));
                break;
        }
    }

    private static SpecifierContext MarkerToContext(string marker) => marker switch
    {
        "XCLASS" => SpecifierContext.Class,
        "XSTRUCT" => SpecifierContext.Struct,
        "XENUM" => SpecifierContext.Enum,
        "XINTERFACE" => SpecifierContext.Interface,
        "XFUNCTION" => SpecifierContext.Function,
        "XPROPERTY" => SpecifierContext.Property | SpecifierContext.PropertyMember | SpecifierContext.PropertyArgument,
        "XDELEGATE" => SpecifierContext.Delegate,
        "XPARAM" => SpecifierContext.Param,
        "XMETA" => SpecifierContext.EnumValue,
        _ => SpecifierContext.None,
    };

    // =================================================================
    // XCLASS / XINTERFACE / XSTRUCT.
    // =================================================================

    private void HandleXClass(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers, List<XhtTypeBase> roots)
    {
        ParseClassOrStructHeader(ctx, expectKeyword: "class", out string? requiredApi, out string? name,
            out string? superId, out List<string> interfaces, out SourceSpan nameSpan);
        if (name is null) { return; }

        string? withinId = ExtractFirstValue(specifiers, "Within");
        string fqn = ComposeFqn(ctx, name);
        string? outerName = ctx.CurrentTypeName;

        XhtClass cls = new(
            Name: name,
            FullyQualifiedName: fqn,
            OuterName: outerName,
            ModuleName: _moduleName,
            Language: Language.Cpp,
            Span: nameSpan,
            Specifiers: specifiers,
            SuperIdentifier: superId,
            Super: null,
            Functions: new List<XhtFunction>(),
            Properties: new List<XhtProperty>(),
            InterfaceIdentifiers: interfaces,
            Interfaces: Array.Empty<XhtInterface>(),
            WithinIdentifier: withinId,
            WithinClass: null,
            RequiredAPIMacroName: requiredApi,
            HasGeneratedBody: false);

        ctx.PushClassDecl(cls, isTopLevel: ctx.CurrentTypeName is null, isRoot: true, roots, sink: this);
        EnterBodyOrSkip(ctx);
    }

    private void HandleXInterface(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers, List<XhtTypeBase> roots)
    {
        ParseClassOrStructHeader(ctx, expectKeyword: "class", out string? requiredApi, out string? name,
            out string? superId, out List<string> interfaces, out SourceSpan nameSpan);
        _ = requiredApi;
        _ = interfaces;
        if (name is null) { return; }

        string fqn = ComposeFqn(ctx, name);
        string? outerName = ctx.CurrentTypeName;

        XhtInterface iface = new(
            Name: name,
            FullyQualifiedName: fqn,
            OuterName: outerName,
            ModuleName: _moduleName,
            Language: Language.Cpp,
            Span: nameSpan,
            Specifiers: specifiers,
            SuperIdentifier: superId,
            Super: null,
            Functions: new List<XhtFunction>());

        ctx.PushInterfaceDecl(iface, roots, sink: this);
        EnterBodyOrSkip(ctx);
    }

    private void HandleXStruct(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers, List<XhtTypeBase> roots)
    {
        ParseClassOrStructHeader(ctx, expectKeyword: "struct", out string? requiredApi, out string? name,
            out string? superId, out List<string> interfaces, out SourceSpan nameSpan);
        _ = requiredApi;
        _ = interfaces;
        if (name is null) { return; }

        string fqn = ComposeFqn(ctx, name);
        string? outerName = ctx.CurrentTypeName;

        XhtStruct st = new(
            Name: name,
            FullyQualifiedName: fqn,
            OuterName: outerName,
            ModuleName: _moduleName,
            Language: Language.Cpp,
            Span: nameSpan,
            Specifiers: specifiers,
            SuperIdentifier: superId,
            Super: null,
            Properties: new List<XhtProperty>(),
            IsFastArraySerializer: false);

        ctx.PushStructDecl(st, roots, sink: this);
        EnterBodyOrSkip(ctx);
    }

    /// <summary>
    /// Parse <c>class &lt;NAME_API&gt;? IDENT (: ...)?</c> or
    /// <c>struct IDENT (: ...)?</c> up to (not including) <c>{</c>.
    /// </summary>
    private void ParseClassOrStructHeader(
        ScanContext ctx,
        string expectKeyword,
        out string? requiredApi,
        out string? name,
        out string? superId,
        out List<string> interfaces,
        out SourceSpan nameSpan)
    {
        requiredApi = null;
        name = null;
        superId = null;
        interfaces = new List<string>();
        nameSpan = SourceSpan.Synthetic;

        CppTokenizer tok = ctx.Tokenizer;

        // Skip an optional 'class' / 'struct' keyword. UHT precedent
        // allows the marker without the keyword in some legacy cases,
        // but the brief requires the keyword.
        CppToken peek = tok.Peek(0);
        if (peek.Kind == CppTokenKind.Keyword && peek.Text == expectKeyword)
        {
            tok.Next();
            peek = tok.Peek(0);
        }
        else if (peek.Kind == CppTokenKind.Keyword && (peek.Text == "class" || peek.Text == "struct"))
        {
            // The user wrote 'struct' on XCLASS or 'class' on XSTRUCT --
            // tolerate per UHT permissive behaviour but record context
            // mismatch.
            tok.Next();
            peek = tok.Peek(0);
        }

        // After 'class', the next token MAY be the per-module API macro
        // (<MODULE>_API) -- recognise heuristically: all-caps identifier
        // ending in _API.
        if (peek.Kind == CppTokenKind.Identifier && LooksLikeApiMacro(peek.Text))
        {
            requiredApi = peek.Text;
            tok.Next();
            peek = tok.Peek(0);
        }

        // Class identifier.
        if (peek.Kind != CppTokenKind.Identifier)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMalformedMarkerDeclaration,
                $"Expected class / struct identifier after '{expectKeyword}'; got '{peek.Text}'.",
                File: peek.Span.SourceFilePath,
                Line: peek.Span.Line,
                Column: peek.Span.Column));
            return;
        }
        name = peek.Text;
        nameSpan = peek.Span;
        tok.Next();

        // Optional final / override after class name -- consume.
        CppToken afterName = tok.Peek(0);
        if (afterName.Kind == CppTokenKind.Keyword && (afterName.Text == "final" || afterName.Text == "override"))
        {
            tok.Next();
        }

        // Inheritance: ': access-spec? identifier (, access-spec? identifier)*'
        if (tok.Peek(0).Kind == CppTokenKind.Colon)
        {
            tok.Next(); // ':'
            bool first = true;
            while (true)
            {
                CppToken inh = tok.Peek(0);
                if (inh.Kind == CppTokenKind.OpenBrace || inh.Kind == CppTokenKind.Semicolon || inh.Kind == CppTokenKind.EndOfFile)
                {
                    break;
                }
                if (inh.Kind == CppTokenKind.Comma)
                {
                    tok.Next();
                    continue;
                }
                if (inh.Kind == CppTokenKind.Keyword && (inh.Text == "public" || inh.Text == "private" || inh.Text == "protected" || inh.Text == "virtual"))
                {
                    tok.Next();
                    continue;
                }
                if (inh.Kind == CppTokenKind.Identifier)
                {
                    string typeIdent = ReadQualifiedIdent(tok);
                    if (first)
                    {
                        superId = typeIdent;
                        first = false;
                    }
                    else
                    {
                        interfaces.Add(typeIdent);
                    }
                    continue;
                }
                tok.Next();
            }
        }
    }

    private static bool LooksLikeApiMacro(string s)
    {
        if (string.IsNullOrEmpty(s)) { return false; }
        if (!s.EndsWith("_API", StringComparison.Ordinal)) { return false; }
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!(c == '_' || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }
        return true;
    }

    private static string ReadQualifiedIdent(CppTokenizer tok)
    {
        StringBuilder sb = new();
        CppToken peek = tok.Peek(0);
        while (peek.Kind == CppTokenKind.Identifier
            || peek.Kind == CppTokenKind.ColonColon)
        {
            sb.Append(peek.Text);
            tok.Next();
            peek = tok.Peek(0);
        }
        // Optional template arguments: '<...>'.
        if (peek.Kind == CppTokenKind.Less)
        {
            int depth = 0;
            while (true)
            {
                CppToken t = tok.Peek(0);
                if (t.Kind == CppTokenKind.EndOfFile) { break; }
                if (t.Kind == CppTokenKind.Less)
                {
                    sb.Append('<');
                    depth++;
                    tok.Next();
                    continue;
                }
                if (t.Kind == CppTokenKind.Greater)
                {
                    sb.Append('>');
                    depth--;
                    tok.Next();
                    if (depth <= 0) { break; }
                    continue;
                }
                if (t.Kind == CppTokenKind.GreaterGreater)
                {
                    sb.Append(">>");
                    depth -= 2;
                    tok.Next();
                    if (depth <= 0) { break; }
                    continue;
                }
                sb.Append(t.Text);
                tok.Next();
            }
        }
        return sb.ToString();
    }

    private void EnterBodyOrSkip(ScanContext ctx)
    {
        CppTokenizer tok = ctx.Tokenizer;
        CppToken t = tok.Peek(0);
        if (t.Kind == CppTokenKind.OpenBrace)
        {
            tok.Next();
            // The class / struct / interface scope has already been
            // pushed by the marker handler; the brace consumed here just
            // confirms we are inside the body. Subsequent '}' will pop.
            ctx.NotifyEnteredDeclaredBody();
            return;
        }
        if (t.Kind == CppTokenKind.Semicolon)
        {
            tok.Next();
            // Forward declaration -- pop the type entry without
            // registering body content.
            ctx.PopScope(_diagnostics, _symbolTable, _moduleName, _sourcePath, new List<XhtTypeBase>());
        }
    }

    // =================================================================
    // XENUM.
    // =================================================================

    private void HandleXEnum(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers, List<XhtTypeBase> roots)
    {
        CppTokenizer tok = ctx.Tokenizer;

        // 'enum' keyword.
        CppToken peek = tok.Peek(0);
        if (!(peek.Kind == CppTokenKind.Keyword && peek.Text == "enum"))
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMalformedMarkerDeclaration,
                $"Expected 'enum' after XENUM(...); got '{peek.Text}'.",
                File: peek.Span.SourceFilePath,
                Line: peek.Span.Line,
                Column: peek.Span.Column));
            return;
        }
        tok.Next();

        // Optional 'class' keyword.
        CppToken next = tok.Peek(0);
        if (next.Kind == CppTokenKind.Keyword && next.Text == "class")
        {
            tok.Next();
            next = tok.Peek(0);
        }

        if (next.Kind != CppTokenKind.Identifier)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMalformedMarkerDeclaration,
                $"Expected enum identifier; got '{next.Text}'.",
                File: next.Span.SourceFilePath,
                Line: next.Span.Line,
                Column: next.Span.Column));
            return;
        }
        string enumName = next.Text;
        SourceSpan enumSpan = next.Span;
        tok.Next();

        // Optional underlying type: ': <type>'.
        string? underlying = null;
        if (tok.Peek(0).Kind == CppTokenKind.Colon)
        {
            tok.Next();
            underlying = ReadQualifiedIdent(tok);
        }

        // Body.
        List<XhtEnumValue> values = new();
        if (tok.Peek(0).Kind == CppTokenKind.OpenBrace)
        {
            tok.Next();
            ParseEnumBody(ctx, values);
        }
        else if (tok.Peek(0).Kind == CppTokenKind.Semicolon)
        {
            tok.Next();
        }

        bool isFlags = HasSpecifier(specifiers, "Bitmask");
        string fqn = ComposeFqn(ctx, enumName);
        string? outer = ctx.CurrentTypeName;

        XhtEnum e = new(
            Name: enumName,
            FullyQualifiedName: fqn,
            OuterName: outer,
            ModuleName: _moduleName,
            Language: Language.Cpp,
            Span: enumSpan,
            Specifiers: specifiers,
            UnderlyingType: underlying,
            IsFlags: isFlags,
            Values: values);

        TryRegister(e, roots, outer is null);
    }

    private void ParseEnumBody(ScanContext ctx, List<XhtEnumValue> values)
    {
        CppTokenizer tok = ctx.Tokenizer;
        long next = 0;
        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile) { return; }
            if (t.Kind == CppTokenKind.CloseBrace)
            {
                tok.Next();
                // Optionally consume trailing ';'.
                if (tok.Peek(0).Kind == CppTokenKind.Semicolon)
                {
                    tok.Next();
                }
                return;
            }
            if (t.Kind == CppTokenKind.Comma)
            {
                tok.Next();
                continue;
            }
            if (t.Kind == CppTokenKind.XhtMarker)
            {
                // XMETA on next value.
                HandleMarker(ctx, new List<XhtTypeBase>());
                continue;
            }
            if (t.Kind == CppTokenKind.Identifier)
            {
                string vname = t.Text;
                SourceSpan vspan = t.Span;
                tok.Next();
                long vval = next;
                if (tok.Peek(0).Kind == CppTokenKind.Equals)
                {
                    tok.Next();
                    long? parsed = ReadIntExpression(tok);
                    if (parsed.HasValue) { vval = parsed.Value; }
                }

                List<Specifier> attach = ConsumeInlineSpecifiersFor("XMETA", ctx);
                values.Add(new XhtEnumValue(vname, vval, attach, vspan));
                next = vval + 1;
                continue;
            }
            tok.Next();
        }
    }

    private static long? ReadIntExpression(CppTokenizer tok)
    {
        // Read up to ',' or '}' as a literal expression. We only resolve
        // the simple cases: a single IntegerLiteral, optionally signed.
        long sign = 1;
        long? value = null;
        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.Comma || t.Kind == CppTokenKind.CloseBrace || t.Kind == CppTokenKind.EndOfFile)
            {
                break;
            }
            if (t.Kind == CppTokenKind.Minus) { sign = -sign; tok.Next(); continue; }
            if (t.Kind == CppTokenKind.Plus) { tok.Next(); continue; }
            if (t.Kind == CppTokenKind.IntegerLiteral && value is null)
            {
                value = ParseIntegerLiteral(t.Text);
                tok.Next();
                continue;
            }
            tok.Next();
        }
        return value.HasValue ? value.Value * sign : null;
    }

    private static long? ParseIntegerLiteral(string raw)
    {
        // Strip suffix letters + digit separators.
        StringBuilder sb = new();
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\'') { continue; }
            if (i > 0 && (c == 'u' || c == 'U' || c == 'l' || c == 'L' || c == 'z' || c == 'Z')) { break; }
            sb.Append(c);
        }
        string s = sb.ToString();
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt64(s.Substring(2), 16);
            }
            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt64(s.Substring(2), 2);
            }
            if (s.Length > 1 && s[0] == '0')
            {
                return Convert.ToInt64(s, 8);
            }
            return long.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    // =================================================================
    // XFUNCTION / XPROPERTY / XDELEGATE / XGENERATED_BODY.
    // =================================================================

    private void HandleXFunction(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers)
    {
        if (ctx.CurrentTypeName is null)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMarkerContextError,
                "XFUNCTION must appear inside a reflected class / struct / interface body.",
                File: markerSpan.SourceFilePath,
                Line: markerSpan.Line,
                Column: markerSpan.Column));
            SkipToDeclarationTerminator(ctx);
            return;
        }

        CppTokenizer tok = ctx.Tokenizer;

        // Capture function modifiers + return type + name + parameters.
        bool isStatic = false, isVirtual = false, isConst = false;
        List<CppToken> preNameTokens = new();
        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile) { break; }
            if (t.Kind == CppTokenKind.OpenParen) { break; }
            if (t.Kind == CppTokenKind.Semicolon) { break; }
            if (t.Kind == CppTokenKind.OpenBrace) { break; }
            if (t.Kind == CppTokenKind.Keyword && t.Text == "static") { isStatic = true; tok.Next(); continue; }
            if (t.Kind == CppTokenKind.Keyword && t.Text == "virtual") { isVirtual = true; tok.Next(); continue; }
            preNameTokens.Add(t);
            tok.Next();
        }

        // Identify the function-name token: last identifier before '('.
        string funcName = string.Empty;
        SourceSpan funcSpan = markerSpan;
        StringBuilder ret = new();
        if (preNameTokens.Count > 0)
        {
            int nameIdx = -1;
            for (int i = preNameTokens.Count - 1; i >= 0; i--)
            {
                if (preNameTokens[i].Kind == CppTokenKind.Identifier
                    || preNameTokens[i].Kind == CppTokenKind.Keyword)
                {
                    if (preNameTokens[i].Kind == CppTokenKind.Identifier)
                    {
                        nameIdx = i;
                        break;
                    }
                }
            }
            if (nameIdx >= 0)
            {
                funcName = preNameTokens[nameIdx].Text;
                funcSpan = preNameTokens[nameIdx].Span;
                for (int i = 0; i < nameIdx; i++)
                {
                    if (ret.Length > 0) { ret.Append(' '); }
                    ret.Append(preNameTokens[i].Text);
                }
            }
            else
            {
                // No identifier before '(' -- malformed function.
                _diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Error,
                    DiagMalformedMarkerDeclaration,
                    "XFUNCTION followed by declaration with no recognisable function name.",
                    File: markerSpan.SourceFilePath,
                    Line: markerSpan.Line,
                    Column: markerSpan.Column));
                SkipToDeclarationTerminator(ctx);
                return;
            }
        }

        // Parameter list.
        List<XhtParam> parameters = new();
        if (tok.Peek(0).Kind == CppTokenKind.OpenParen)
        {
            tok.Next();
            ParseParamList(ctx, parameters);
        }

        // Trailing 'const'.
        if (tok.Peek(0).Kind == CppTokenKind.Keyword && tok.Peek(0).Text == "const")
        {
            isConst = true;
            tok.Next();
        }

        // Skip to end of declaration (';' or '{...}').
        SkipFunctionTail(ctx);

        XhtFunction fn = new(
            Name: funcName,
            ReturnType: ret.ToString(),
            Parameters: parameters,
            Specifiers: specifiers,
            IsStatic: isStatic,
            IsVirtual: isVirtual,
            IsConst: isConst,
            Span: funcSpan);

        ctx.AttachFunctionToCurrentType(fn);
    }

    private void ParseParamList(ScanContext ctx, List<XhtParam> parameters)
    {
        CppTokenizer tok = ctx.Tokenizer;
        int depth = 1; // we just consumed the opening '('.
        StringBuilder current = new();
        List<CppToken> currentTokens = new();
        List<Specifier> pendingParamSpecs = new();

        void Flush()
        {
            if (currentTokens.Count == 0) { return; }
            // Last identifier-like token = param name; preceding = type.
            int nameIdx = -1;
            for (int i = currentTokens.Count - 1; i >= 0; i--)
            {
                if (currentTokens[i].Kind == CppTokenKind.Identifier)
                {
                    nameIdx = i;
                    break;
                }
            }
            string pname;
            string ptype;
            SourceSpan pspan;
            bool isRef = false;
            bool isOut = false;
            if (nameIdx >= 0)
            {
                pname = currentTokens[nameIdx].Text;
                pspan = currentTokens[nameIdx].Span;
                StringBuilder t = new();
                for (int i = 0; i < nameIdx; i++)
                {
                    if (t.Length > 0) { t.Append(' '); }
                    t.Append(currentTokens[i].Text);
                    if (currentTokens[i].Kind == CppTokenKind.Ampersand) { isRef = true; }
                }
                ptype = t.ToString();
            }
            else
            {
                pname = string.Empty;
                ptype = current.ToString();
                pspan = currentTokens[0].Span;
            }
            // Honour XPARAM Out / Ref attached specifiers.
            foreach (Specifier s in pendingParamSpecs)
            {
                if (string.Equals(s.Key, "Out", StringComparison.OrdinalIgnoreCase)) { isOut = true; }
                if (string.Equals(s.Key, "Ref", StringComparison.OrdinalIgnoreCase)) { isRef = true; }
            }
            parameters.Add(new XhtParam(pname, ptype, pendingParamSpecs.ToArray(), isOut, isRef, pspan));
            currentTokens.Clear();
            current.Clear();
            pendingParamSpecs.Clear();
        }

        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile) { return; }
            if (t.Kind == CppTokenKind.OpenParen) { depth++; }
            if (t.Kind == CppTokenKind.CloseParen)
            {
                depth--;
                if (depth == 0)
                {
                    tok.Next();
                    Flush();
                    return;
                }
            }
            if (depth == 1 && t.Kind == CppTokenKind.Comma)
            {
                tok.Next();
                Flush();
                continue;
            }
            if (t.Kind == CppTokenKind.XhtMarker && t.Text == "XPARAM")
            {
                // Consume the XPARAM(specs) inline.
                tok.Next();
                if (tok.Peek(0).Kind == CppTokenKind.OpenParen)
                {
                    tok.Next();
                    IReadOnlyList<Specifier> p = _specifierParser.ParseSpecifierList(tok, SpecifierContext.Param, _diagnostics);
                    foreach (Specifier s in p) { pendingParamSpecs.Add(s); }
                }
                continue;
            }
            currentTokens.Add(t);
            if (current.Length > 0) { current.Append(' '); }
            current.Append(t.Text);
            tok.Next();
        }
    }

    private void SkipFunctionTail(ScanContext ctx)
    {
        CppTokenizer tok = ctx.Tokenizer;
        // Skip everything until ';' OR a balanced '{' '}' body.
        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile) { return; }
            if (t.Kind == CppTokenKind.Semicolon)
            {
                tok.Next();
                return;
            }
            if (t.Kind == CppTokenKind.OpenBrace)
            {
                tok.Next();
                int depth = 1;
                while (depth > 0)
                {
                    CppToken b = tok.Next();
                    if (b.Kind == CppTokenKind.EndOfFile) { return; }
                    if (b.Kind == CppTokenKind.OpenBrace) { depth++; }
                    if (b.Kind == CppTokenKind.CloseBrace) { depth--; }
                }
                return;
            }
            tok.Next();
        }
    }

    private void HandleXProperty(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers)
    {
        if (ctx.CurrentTypeName is null)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMarkerContextError,
                "XPROPERTY must appear inside a reflected class / struct body.",
                File: markerSpan.SourceFilePath,
                Line: markerSpan.Line,
                Column: markerSpan.Column));
            SkipToDeclarationTerminator(ctx);
            return;
        }

        CppTokenizer tok = ctx.Tokenizer;

        List<CppToken> declTokens = new();
        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile) { break; }
            if (t.Kind == CppTokenKind.Semicolon)
            {
                tok.Next();
                break;
            }
            if (t.Kind == CppTokenKind.OpenBrace)
            {
                // Default-initialiser block: skip to matching '}'.
                tok.Next();
                int depth = 1;
                while (depth > 0)
                {
                    CppToken b = tok.Next();
                    if (b.Kind == CppTokenKind.EndOfFile) { break; }
                    if (b.Kind == CppTokenKind.OpenBrace) { depth++; }
                    if (b.Kind == CppTokenKind.CloseBrace) { depth--; }
                }
                continue;
            }
            if (t.Kind == CppTokenKind.Equals)
            {
                // Skip the initialiser.
                tok.Next();
                while (true)
                {
                    CppToken b = tok.Peek(0);
                    if (b.Kind == CppTokenKind.EndOfFile) { break; }
                    if (b.Kind == CppTokenKind.Semicolon) { break; }
                    tok.Next();
                }
                continue;
            }
            declTokens.Add(t);
            tok.Next();
        }

        if (declTokens.Count == 0)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMalformedMarkerDeclaration,
                "XPROPERTY followed by empty declaration.",
                File: markerSpan.SourceFilePath,
                Line: markerSpan.Line,
                Column: markerSpan.Column));
            return;
        }

        // Last identifier = property name; preceding = type spelling.
        int nameIdx = -1;
        for (int i = declTokens.Count - 1; i >= 0; i--)
        {
            if (declTokens[i].Kind == CppTokenKind.Identifier)
            {
                nameIdx = i;
                break;
            }
        }
        if (nameIdx < 0)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagMalformedMarkerDeclaration,
                "XPROPERTY declaration missing identifier.",
                File: markerSpan.SourceFilePath,
                Line: markerSpan.Line,
                Column: markerSpan.Column));
            return;
        }
        string propName = declTokens[nameIdx].Text;
        SourceSpan propSpan = declTokens[nameIdx].Span;
        StringBuilder typeSb = new();
        for (int i = 0; i < nameIdx; i++)
        {
            if (typeSb.Length > 0) { typeSb.Append(' '); }
            typeSb.Append(declTokens[i].Text);
        }
        string typeStr = typeSb.ToString();

        bool isContainer = typeStr.Contains("TArray", StringComparison.Ordinal)
            || typeStr.Contains("TMap", StringComparison.Ordinal)
            || typeStr.Contains("TSet", StringComparison.Ordinal);

        string? category = ExtractFirstValue(specifiers, "Category");
        string? repNotify = ExtractFirstValue(specifiers, "ReplicatedUsing");

        XhtProperty prop = new(
            Name: propName,
            TypeIdentifier: typeStr,
            Specifiers: specifiers,
            IsContainer: isContainer,
            RepNotifyFunctionName: repNotify,
            Category: category,
            Span: propSpan);

        ctx.AttachPropertyToCurrentType(prop);
    }

    private void HandleXDelegate(ScanContext ctx, SourceSpan markerSpan, List<Specifier> specifiers, List<XhtTypeBase> roots)
    {
        CppTokenizer tok = ctx.Tokenizer;

        // XDELEGATE(...) is typically followed by a DECLARE_DYNAMIC_DELEGATE_*
        // macro invocation. Capture the macro's argument list as a single
        // raw blob.
        StringBuilder allTokens = new();
        bool isMulticast = false;
        string? delegateName = null;
        SourceSpan nameSpan = markerSpan;
        List<XhtParam> parameters = new();
        string returnType = "void";

        CppToken macroTok = tok.Peek(0);
        if (macroTok.Kind == CppTokenKind.Identifier)
        {
            allTokens.Append(macroTok.Text);
            if (macroTok.Text.Contains("MULTICAST", StringComparison.OrdinalIgnoreCase))
            {
                isMulticast = true;
            }
            if (macroTok.Text.Contains("RetVal", StringComparison.OrdinalIgnoreCase))
            {
                // Return-value-carrying delegate.
            }
            tok.Next();
        }

        if (tok.Peek(0).Kind == CppTokenKind.OpenParen)
        {
            tok.Next();
            int depth = 1;
            bool sawReturnType = false;
            StringBuilder currentArg = new();
            List<string> args = new();
            while (true)
            {
                CppToken t = tok.Peek(0);
                if (t.Kind == CppTokenKind.EndOfFile) { break; }
                if (t.Kind == CppTokenKind.OpenParen) { depth++; }
                if (t.Kind == CppTokenKind.CloseParen)
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (currentArg.Length > 0) { args.Add(currentArg.ToString().Trim()); }
                        tok.Next();
                        break;
                    }
                }
                if (depth == 1 && t.Kind == CppTokenKind.Comma)
                {
                    args.Add(currentArg.ToString().Trim());
                    currentArg.Clear();
                    tok.Next();
                    continue;
                }
                if (currentArg.Length > 0) { currentArg.Append(' '); }
                currentArg.Append(t.Text);
                tok.Next();
            }
            // The first arg of DECLARE_DYNAMIC_DELEGATE is the delegate
            // name (no return type). DECLARE_*_RetVal_* puts the return
            // type first, then the name. We use the macro name to choose.
            int nameIndex = 0;
            if (macroTok.Text.Contains("RetVal", StringComparison.OrdinalIgnoreCase) && args.Count >= 2)
            {
                returnType = args[0];
                nameIndex = 1;
                sawReturnType = true;
            }
            if (args.Count > nameIndex)
            {
                delegateName = args[nameIndex];
            }
            // Remaining args alternate type/name in pairs (per UE macro
            // conventions). Capture as parameters.
            for (int i = nameIndex + 1; i + 1 < args.Count; i += 2)
            {
                parameters.Add(new XhtParam(
                    Name: args[i + 1],
                    TypeIdentifier: args[i],
                    Specifiers: Array.Empty<Specifier>(),
                    IsOut: false,
                    IsRef: false,
                    Span: markerSpan));
            }
            _ = sawReturnType;
        }

        // Skip to ';'.
        SkipToDeclarationTerminator(ctx);

        if (delegateName is null) { return; }

        string fqn = ComposeFqn(ctx, delegateName);
        XhtDelegate del = new(
            Name: delegateName,
            FullyQualifiedName: fqn,
            OuterName: ctx.CurrentTypeName,
            ModuleName: _moduleName,
            Language: Language.Cpp,
            Span: nameSpan,
            Specifiers: specifiers,
            ReturnType: returnType,
            Parameters: parameters,
            IsMulticast: isMulticast);

        TryRegister(del, roots, ctx.CurrentTypeName is null);
        _ = allTokens;
    }

    private void HandleXGeneratedBody(ScanContext ctx)
    {
        if (ctx.CurrentTypeName is null) { return; }
        ctx.MarkCurrentTypeGeneratedBody();
    }

    private void SkipToDeclarationTerminator(ScanContext ctx)
    {
        CppTokenizer tok = ctx.Tokenizer;
        while (true)
        {
            CppToken t = tok.Peek(0);
            if (t.Kind == CppTokenKind.EndOfFile) { return; }
            if (t.Kind == CppTokenKind.Semicolon) { tok.Next(); return; }
            if (t.Kind == CppTokenKind.CloseBrace) { return; }
            tok.Next();
        }
    }

    // =================================================================
    // Symbol-table registration.
    // =================================================================

    internal void TryRegister(XhtTypeBase t, List<XhtTypeBase> roots, bool addToRoots)
    {
        try
        {
            _symbolTable.Register(t);
            if (addToRoots) { roots.Add(t); }
        }
        catch (InvalidOperationException ex)
        {
            _diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Error,
                DiagDuplicateType,
                $"Duplicate type '{t.Name}' (caseless engine-name collision): {ex.Message}",
                File: t.Span.SourceFilePath,
                Line: t.Span.Line,
                Column: t.Span.Column,
                Module: t.ModuleName));
        }
    }

    // =================================================================
    // Helpers.
    // =================================================================

    private static bool HasSpecifier(IReadOnlyList<Specifier> specs, string key)
    {
        foreach (Specifier s in specs)
        {
            if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    private static string? ExtractFirstValue(IReadOnlyList<Specifier> specs, string key)
    {
        foreach (Specifier s in specs)
        {
            if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)
                && s.Values.Count > 0)
            {
                return s.Values[0];
            }
        }
        return null;
    }

    private string ComposeFqn(ScanContext ctx, string name)
    {
        StringBuilder sb = new();
        foreach (string ns in ctx.NamespaceStack)
        {
            if (ns.Length == 0) { continue; }
            sb.Append(ns);
            sb.Append("::");
        }
        foreach (string typeName in ctx.TypeStack)
        {
            sb.Append(typeName);
            sb.Append("::");
        }
        sb.Append(name);
        return sb.ToString();
    }

    private List<Specifier> ConsumeInlineSpecifiersFor(string markerKind, ScanContext ctx)
    {
        List<Specifier> attach = new();
        for (int i = ctx.PendingInlineSpecifiers.Count - 1; i >= 0; i--)
        {
            (string m, List<Specifier> s, SourceSpan _) entry = ctx.PendingInlineSpecifiers[i];
            if (entry.m == markerKind)
            {
                attach.AddRange(entry.s);
                ctx.PendingInlineSpecifiers.RemoveAt(i);
            }
        }
        return attach;
    }

    // =================================================================
    // Scan context (scope stacks etc.).
    // =================================================================

    /// <summary>
    /// Mutable scan-time scope tracking. Held privately by the scanner
    /// and threaded through the handler entry points.
    /// </summary>
    private sealed class ScanContext
    {
        public CppTokenizer Tokenizer { get; }

        // Namespace stack: name of each open namespace scope.
        public List<string> NamespaceStack { get; } = new();

        // Type stack: name of each open class / struct / interface scope.
        public List<string> TypeStack { get; } = new();

        // Per-scope kind discriminator.
        private readonly List<ScopeKind> _scopeKinds = new();

        // Pending types: when we enter a class body, the in-progress
        // XhtClass / XhtStruct / XhtInterface lives here so subsequent
        // XFUNCTION / XPROPERTY markers can attach. We hold mutable
        // builder lists (functions, properties) here and finalise the
        // record on scope close.
        private readonly List<TypeBuilder> _typeBuilders = new();

        public List<(string Marker, List<Specifier> Specifiers, SourceSpan Span)> PendingInlineSpecifiers { get; } = new();

        public string? CurrentTypeName => _typeBuilders.Count > 0 ? _typeBuilders[^1].Name : null;

        public ScanContext(CppTokenizer tok)
        {
            Tokenizer = tok;
        }

        public void PushNamespace(string name)
        {
            NamespaceStack.Add(name);
            _scopeKinds.Add(ScopeKind.Namespace);
        }

        public void PushAnonymousBrace()
        {
            _scopeKinds.Add(ScopeKind.AnonymousBrace);
        }

        public void PushClassDecl(XhtClass cls, bool isTopLevel, bool isRoot, List<XhtTypeBase> roots, CppMarkerScanner sink)
        {
            TypeStack.Add(cls.Name);
            _scopeKinds.Add(ScopeKind.Type);
            _typeBuilders.Add(new TypeBuilder(cls.Name, TypeBuilderKind.Class, cls, addToRoots: isTopLevel));
            _ = isRoot;
            _ = roots;
            _ = sink;
        }

        public void PushStructDecl(XhtStruct st, List<XhtTypeBase> roots, CppMarkerScanner sink)
        {
            TypeStack.Add(st.Name);
            _scopeKinds.Add(ScopeKind.Type);
            _typeBuilders.Add(new TypeBuilder(st.Name, TypeBuilderKind.Struct, st, addToRoots: _typeBuilders.Count == 0));
            _ = roots;
            _ = sink;
        }

        public void PushInterfaceDecl(XhtInterface iface, List<XhtTypeBase> roots, CppMarkerScanner sink)
        {
            TypeStack.Add(iface.Name);
            _scopeKinds.Add(ScopeKind.Type);
            _typeBuilders.Add(new TypeBuilder(iface.Name, TypeBuilderKind.Interface, iface, addToRoots: _typeBuilders.Count == 0));
            _ = roots;
            _ = sink;
        }

        public void NotifyEnteredDeclaredBody()
        {
            // No-op for now; presence of brace already pushed scope at
            // the marker handler. We keep this hook for Phase 1d when
            // body-vs-forward-decl bookkeeping may tighten.
        }

        public void AttachFunctionToCurrentType(XhtFunction fn)
        {
            if (_typeBuilders.Count == 0) { return; }
            _typeBuilders[^1].Functions.Add(fn);
        }

        public void AttachPropertyToCurrentType(XhtProperty prop)
        {
            if (_typeBuilders.Count == 0) { return; }
            _typeBuilders[^1].Properties.Add(prop);
        }

        public void MarkCurrentTypeGeneratedBody()
        {
            if (_typeBuilders.Count == 0) { return; }
            _typeBuilders[^1].HasGeneratedBody = true;
        }

        public void PopScope(List<DiagnosticRecord> diagnostics, SymbolTable symbolTable, string moduleName, string sourcePath, List<XhtTypeBase> roots)
        {
            if (_scopeKinds.Count == 0) { return; }
            ScopeKind kind = _scopeKinds[^1];
            _scopeKinds.RemoveAt(_scopeKinds.Count - 1);

            switch (kind)
            {
                case ScopeKind.Namespace:
                    if (NamespaceStack.Count > 0)
                    {
                        NamespaceStack.RemoveAt(NamespaceStack.Count - 1);
                    }
                    break;
                case ScopeKind.Type:
                    if (TypeStack.Count > 0)
                    {
                        TypeStack.RemoveAt(TypeStack.Count - 1);
                    }
                    if (_typeBuilders.Count > 0)
                    {
                        TypeBuilder tb = _typeBuilders[^1];
                        _typeBuilders.RemoveAt(_typeBuilders.Count - 1);
                        XhtTypeBase finalised = tb.Finalise();
                        bool isRoot = _typeBuilders.Count == 0;
                        try
                        {
                            symbolTable.Register(finalised);
                            if (isRoot && tb.AddToRoots) { roots.Add(finalised); }
                        }
                        catch (InvalidOperationException ex)
                        {
                            diagnostics.Add(new DiagnosticRecord(
                                DiagnosticSeverity.Error,
                                DiagDuplicateType,
                                $"Duplicate type '{finalised.Name}' (caseless engine-name collision): {ex.Message}",
                                File: finalised.Span.SourceFilePath,
                                Line: finalised.Span.Line,
                                Column: finalised.Span.Column,
                                Module: moduleName));
                        }
                    }
                    break;
                case ScopeKind.AnonymousBrace:
                    break;
            }
            _ = sourcePath;
        }

        private enum ScopeKind
        {
            Namespace,
            Type,
            AnonymousBrace,
        }

        private enum TypeBuilderKind
        {
            Class,
            Struct,
            Interface,
        }

        private sealed class TypeBuilder
        {
            public string Name { get; }
            public TypeBuilderKind Kind { get; }
            public XhtTypeBase Initial { get; }
            public bool AddToRoots { get; }
            public List<XhtFunction> Functions { get; } = new();
            public List<XhtProperty> Properties { get; } = new();
            public bool HasGeneratedBody { get; set; }

            public TypeBuilder(string name, TypeBuilderKind kind, XhtTypeBase initial, bool addToRoots)
            {
                Name = name;
                Kind = kind;
                Initial = initial;
                AddToRoots = addToRoots;
            }

            public XhtTypeBase Finalise()
            {
                switch (Kind)
                {
                    case TypeBuilderKind.Class:
                    {
                        XhtClass c = (XhtClass)Initial;
                        return c with
                        {
                            Functions = Functions,
                            Properties = Properties,
                            HasGeneratedBody = HasGeneratedBody,
                        };
                    }
                    case TypeBuilderKind.Struct:
                    {
                        XhtStruct s = (XhtStruct)Initial;
                        return s with
                        {
                            Properties = Properties,
                        };
                    }
                    case TypeBuilderKind.Interface:
                    {
                        XhtInterface i = (XhtInterface)Initial;
                        return i with
                        {
                            Functions = Functions,
                        };
                    }
                    default:
                        return Initial;
                }
            }
        }
    }
}
