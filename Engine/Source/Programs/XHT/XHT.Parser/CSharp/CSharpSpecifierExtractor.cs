// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Tables;
using XhtDiagnosticSeverity = Simgenics.XPact.XHT.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XHT.Parser.CSharp;

/// <summary>
/// Extracts <see cref="Specifier"/> records from a Roslyn
/// <see cref="AttributeSyntax"/>'s argument list per
/// <c>/Documents/XHT.html</c> Rev 8 Section 3.2 (Roslyn-based C# parser)
/// + Section 7.2 (specifier parsing). The C# side mirrors the C++ side
/// (<c>CppSpecifierParser</c>) in role and diagnostic vocabulary:
/// </summary>
/// <remarks>
/// <para>
/// <b>Argument forms recognised:</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>[XClass(BlueprintReadOnly)]</c> --
///     <see cref="AttributeArgumentSyntax.Expression"/> is an
///     <see cref="IdentifierNameSyntax"/>. Flag form (no values).
///   </description></item>
///   <item><description>
///     <c>[XClass(Category = "Combat")]</c> --
///     <see cref="AttributeArgumentSyntax.NameEquals"/> is set; the
///     expression is a <see cref="LiteralExpressionSyntax"/> /
///     identifier / typeof. KeyEqValue form (one value).
///   </description></item>
///   <item><description>
///     <c>[XClass(BlueprintReadOnly, EditAnywhere)]</c> --
///     two positional flag arguments produce two flag specifiers.
///   </description></item>
///   <item><description>
///     <c>[XClass]</c> (no parens) --
///     <see cref="AttributeSyntax.ArgumentList"/> is null; the extractor
///     returns an empty list.
///   </description></item>
///   <item><description>
///     <c>[XClass()]</c> (empty parens) -- non-null argument list
///     with zero arguments; the extractor returns an empty list.
///   </description></item>
///   <item><description>
///     <c>[XClass(Within = typeof(XActor))]</c> -- the
///     <see cref="TypeOfExpressionSyntax"/> form. The type's authored
///     name is extracted as a Reference value string.
///   </description></item>
/// </list>
/// <para>
/// <b>Registry interaction.</b> Each parsed specifier is resolved against
/// the caller-supplied <see cref="ISpecifierRegistry"/> under the
/// caller-supplied <see cref="SpecifierContext"/>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     On registry miss (the specifier name is not registered at all):
///     emit <see cref="DiagUnknownSpecifier"/> (XHT110).
///   </description></item>
///   <item><description>
///     On context mismatch (registered but not legal in this context):
///     emit <see cref="DiagSpecifierIllegalInContext"/> (XHT066, per
///     C8 audit renumbering in XHT.html Rev 8 Section 12.3 -- relocated
///     from XHT111 to free the validator-band slot for
///     <see cref="DiagnosticCodes.FunctionSpecifierConflict"/>).
///   </description></item>
///   <item><description>
///     On grammar error inside the argument (e.g.
///     <c>[XClass(typeof(X) + 1)]</c>): emit
///     <see cref="DiagSpecifierSyntaxError"/> (XHT114) and skip that
///     argument; remaining arguments still parse.
///   </description></item>
/// </list>
/// <para>
/// The Specifier record is still emitted on registry miss / context
/// mismatch so the downstream AST shape is stable; the validator pass
/// owns the suppress / promote decisions.
/// </para>
/// </remarks>
public static class CSharpSpecifierExtractor
{
    /// <summary>Diagnostic code: specifier name not found in registry.</summary>
    public const string DiagUnknownSpecifier = "XHT110";

    /// <summary>
    /// Diagnostic code: specifier registered but not legal in this
    /// context. Per C8 audit (XHT.html Rev 8 Section 12.3): renumbered
    /// from XHT111 to <see cref="DiagnosticCodes.SpecifierIllegalInContext"/>
    /// (XHT066) to resolve a numeric-slot collision with the
    /// validator-band <c>DiagnosticCodes.FunctionSpecifierConflict</c>
    /// (Server+Client+NetMulticast mutex). Kept as a callsite alias for
    /// test-vocabulary stability; the wire value is sourced from the
    /// central catalog.
    /// </summary>
    public const string DiagSpecifierIllegalInContext = DiagnosticCodes.SpecifierIllegalInContext;

    /// <summary>
    /// Diagnostic code: grammar error inside the attribute argument
    /// list. Per C7 audit (XHT.html Section 12.3): renumbered from
    /// XHT114 to XHT065 to avoid collision with the validator-band
    /// <c>DiagnosticCodes.ConfigConflictsWithNoExport</c>.
    /// </summary>
    public const string DiagSpecifierSyntaxError = "XHT065";

    /// <summary>
    /// Extract <see cref="Specifier"/> records from
    /// <paramref name="attribute"/>'s argument list. Empty / null argument
    /// lists return an empty list (canonical form of <c>[XClass]</c> and
    /// <c>[XClass()]</c>).
    /// </summary>
    /// <param name="attribute">The X-attribute to extract from. Must not be null.</param>
    /// <param name="context">Active syntactic context for registry validation.</param>
    /// <param name="registry">Specifier registry. Must not be null.</param>
    /// <param name="sourcePath">Source file path for diagnostic anchoring. Must not be null.</param>
    /// <param name="diagnostics">Sink for context-validation + grammar diagnostics. Must not be null.</param>
    /// <returns>The parsed specifier list. May be empty.</returns>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static IReadOnlyList<Specifier> Extract(
        AttributeSyntax attribute,
        SpecifierContext context,
        ISpecifierRegistry registry,
        string sourcePath,
        List<DiagnosticRecord> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        List<Specifier> result = new();
        AttributeArgumentListSyntax? argList = attribute.ArgumentList;
        if (argList is null || argList.Arguments.Count == 0)
        {
            return result;
        }

        foreach (AttributeArgumentSyntax arg in argList.Arguments)
        {
            ParseOneArgument(arg, context, registry, sourcePath, diagnostics, result);
        }

        return result;
    }

    private static void ParseOneArgument(
        AttributeArgumentSyntax arg,
        SpecifierContext context,
        ISpecifierRegistry registry,
        string sourcePath,
        List<DiagnosticRecord> diagnostics,
        List<Specifier> sink)
    {
        // Determine the specifier key + values.
        string? key = null;
        List<string> values = new();
        SourceSpan span = SpanFromNode(arg, sourcePath);

        // C# attribute argument forms:
        //   [XClass(Flag)]                  -> Expression=IdentifierNameSyntax  (flag)
        //   [XClass(K = "v")]               -> NameEquals != null, Expression=LiteralExpressionSyntax / etc.
        //   [XClass(K = typeof(T))]         -> NameEquals != null, Expression=TypeOfExpressionSyntax
        //   [XClass("v")]                   -> Expression=LiteralExpressionSyntax (rare; treat as positional flag with empty key error)
        if (arg.NameEquals is not null)
        {
            key = arg.NameEquals.Name.Identifier.Text;
            span = SpanFromNode(arg.NameEquals.Name, sourcePath);
            string? v = ExtractValueExpression(arg.Expression, sourcePath, diagnostics);
            if (v is null)
            {
                // Grammar error already emitted by ExtractValueExpression;
                // skip this argument but continue with siblings.
                return;
            }
            values.Add(v);
        }
        else if (arg.NameColon is not null)
        {
            // [XClass(named: value)] form -- positional with name colon.
            // Treat the colon name as the key.
            key = arg.NameColon.Name.Identifier.Text;
            span = SpanFromNode(arg.NameColon.Name, sourcePath);
            string? v = ExtractValueExpression(arg.Expression, sourcePath, diagnostics);
            if (v is null) { return; }
            values.Add(v);
        }
        else
        {
            // Positional argument: must be an identifier (flag form) or
            // -- per the brief's permissive forms -- a typeof / literal
            // that we can store as a value-less anonymous specifier.
            // Per the brief: IdentifierNameSyntax -> flag form.
            if (arg.Expression is IdentifierNameSyntax id)
            {
                key = id.Identifier.Text;
                span = SpanFromNode(id, sourcePath);
            }
            else
            {
                diagnostics.Add(new DiagnosticRecord(
                    XhtDiagnosticSeverity.Error,
                    DiagSpecifierSyntaxError,
                    $"Expected specifier name (identifier) in attribute argument; got '{arg.Expression}'.",
                    File: sourcePath,
                    Line: span.Line,
                    Column: span.Column));
                return;
            }
        }

        if (string.IsNullOrEmpty(key))
        {
            diagnostics.Add(new DiagnosticRecord(
                XhtDiagnosticSeverity.Error,
                DiagSpecifierSyntaxError,
                "Empty specifier key in attribute argument.",
                File: sourcePath,
                Line: span.Line,
                Column: span.Column));
            return;
        }

        EmitSpecifier(sink, diagnostics, key, values, span, context, registry);
    }

    private static string? ExtractValueExpression(
        ExpressionSyntax expr,
        string sourcePath,
        List<DiagnosticRecord> diagnostics)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax lit:
            {
                // String literal -- strip quotes; other literals (int,
                // bool, etc.) use the token's text verbatim.
                if (lit.Token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken))
                {
                    return (string?)lit.Token.Value ?? lit.Token.ValueText;
                }
                // Bool, int, char, null, etc. -- emit the literal's text.
                return lit.Token.ValueText;
            }
            case IdentifierNameSyntax id:
            {
                // Identifier value -- e.g. (Key = SomeEnumValue). Capture
                // the identifier text.
                return id.Identifier.Text;
            }
            case TypeOfExpressionSyntax typeOf:
            {
                // typeof(T) -- extract T's authored name as a string. We
                // use ToString() on the type syntax to preserve generics
                // / nested-type spellings.
                return typeOf.Type.ToString();
            }
            case MemberAccessExpressionSyntax member:
            {
                // (Key = Namespace.Type) form -- emit as the full
                // dotted path.
                return member.ToString();
            }
            case PrefixUnaryExpressionSyntax unary:
            {
                // (Key = -5) -- preserve the prefix and recurse.
                string? inner = ExtractValueExpression(unary.Operand, sourcePath, diagnostics);
                if (inner is null) { return null; }
                return unary.OperatorToken.Text + inner;
            }
            default:
            {
                FileLinePositionSpan pos = expr.GetLocation().GetLineSpan();
                diagnostics.Add(new DiagnosticRecord(
                    XhtDiagnosticSeverity.Error,
                    DiagSpecifierSyntaxError,
                    $"Unsupported attribute argument expression '{expr}' ({expr.Kind()}).",
                    File: sourcePath,
                    Line: pos.StartLinePosition.Line + 1,
                    Column: pos.StartLinePosition.Character + 1));
                return null;
            }
        }
    }

    private static void EmitSpecifier(
        List<Specifier> sink,
        List<DiagnosticRecord> diagnostics,
        string key,
        IReadOnlyList<string> values,
        SourceSpan span,
        SpecifierContext context,
        ISpecifierRegistry registry)
    {
        // Registry lookup mirrors CppSpecifierParser: distinguish miss
        // (XHT110) from context-mismatch (XHT066; was XHT111 pre-C8
        // audit, see XHT.html Rev 8 Section 12.3).
        if (registry.Resolve(key, context) is null)
        {
            if (registry.Resolve(key, SpecifierContext.All) is null)
            {
                diagnostics.Add(new DiagnosticRecord(
                    XhtDiagnosticSeverity.Error,
                    DiagUnknownSpecifier,
                    $"Unknown specifier '{key}'.",
                    File: span.SourceFilePath,
                    Line: span.Line,
                    Column: span.Column));
            }
            else
            {
                diagnostics.Add(new DiagnosticRecord(
                    XhtDiagnosticSeverity.Error,
                    DiagSpecifierIllegalInContext,
                    $"Specifier '{key}' is not legal in this context ({context}).",
                    File: span.SourceFilePath,
                    Line: span.Line,
                    Column: span.Column));
            }
        }

        sink.Add(new Specifier(key, values, span));
    }

    private static SourceSpan SpanFromNode(SyntaxNode node, string sourcePath)
    {
        FileLinePositionSpan pos = node.GetLocation().GetLineSpan();
        int line = pos.StartLinePosition.Line + 1;
        int col = pos.StartLinePosition.Character + 1;
        int length = node.Span.Length;
        return new SourceSpan(sourcePath, line, col, length);
    }
}
