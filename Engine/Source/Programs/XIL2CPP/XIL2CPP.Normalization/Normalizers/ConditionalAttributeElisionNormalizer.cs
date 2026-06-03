// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Records the per-call-site keep-vs-elide decision for invocations of
/// methods annotated <c>[System.Diagnostics.Conditional("SYM")]</c>, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.4 (the
/// <c>[Conditional("DEBUG")]</c> attribute row: "Supported (call-site elision
/// per per-module <c>conditional_symbols</c> manifest field)").
/// </summary>
/// <remarks>
/// <para>
/// C# semantics (mirroring the Roslyn compiler): a call to a method carrying
/// one or more <c>[Conditional("SYM")]</c> attributes is KEPT iff <em>at
/// least one</em> of those conditional symbols is defined in the compilation
/// (the preprocessor symbol set the file was parsed with); otherwise the
/// entire call statement is ELIDED. A method with no <c>[Conditional]</c>
/// attribute is unconditional and is never annotated by this normalizer.
/// </para>
/// <para>
/// <b>Annotate, do not rewrite.</b> The decision is recorded as an additive
/// <see cref="ConditionalElisionAnnotation"/> on the original
/// <see cref="InvocationExpressionSyntax"/>; the Pass-1 compilation and its
/// semantic models stay authoritative so Pass 3 binding info remains valid.
/// </para>
/// <para>
/// <b>Determinism.</b> Files are visited in
/// <see cref="Pass1Result.ParsedFiles"/> order (the canonical ordinal order),
/// invocations within a file in descendant (source-span) order, and the
/// recorded conditional-symbol list is ordinal-sorted + de-duplicated so two
/// runs over identical input produce identical annotations.
/// </para>
/// </remarks>
public sealed class ConditionalAttributeElisionNormalizer : INormalizer
{
    /// <summary>The fully-qualified metadata name of <c>System.Diagnostics.ConditionalAttribute</c>.</summary>
    private const string ConditionalAttributeMetadataName = "System.Diagnostics.ConditionalAttribute";

    /// <inheritdoc/>
    public string Name => "ConditionalAttributeElisionNormalizer";

    /// <inheritdoc/>
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxTree tree = file.Tree;

            // The preprocessor symbols the file was parsed with -- the
            // authoritative "is SYM defined" set for the [Conditional] decision.
            ImmutableHashSet<string> definedSymbols = GetDefinedSymbols(tree);

            SemanticModel model = pass1.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot();

            // DescendantNodes yields nodes in source (span) order -- a stable,
            // deterministic visit order.
            foreach (InvocationExpressionSyntax invocation in
                root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                IMethodSymbol? method = ResolveMethod(model, invocation);
                if (method is null)
                {
                    continue;
                }

                // Collect the conditional symbols from every [Conditional]
                // attribute on the (original-definition) method.
                IReadOnlyList<string> conditionalSymbols = GetConditionalSymbols(method);
                if (conditionalSymbols.Count == 0)
                {
                    // Not a [Conditional] method -- nothing to lower.
                    continue;
                }

                // C# keep rule: keep iff ANY conditional symbol is defined.
                bool keep = false;
                foreach (string symbol in conditionalSymbols)
                {
                    if (definedSymbols.Contains(symbol))
                    {
                        keep = true;
                        break;
                    }
                }

                builder.AnnotateNode(
                    invocation,
                    new ConditionalElisionAnnotation(conditionalSymbols, keep));
            }
        }
    }

    /// <summary>
    /// Resolve the target method symbol of an invocation, falling back to the
    /// first candidate when the call did not bind to a single symbol (e.g. an
    /// overload group on incomplete input). Returns null when no method symbol
    /// is available.
    /// </summary>
    private static IMethodSymbol? ResolveMethod(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        SymbolInfo info = model.GetSymbolInfo(invocation);
        if (info.Symbol is IMethodSymbol bound)
        {
            return bound;
        }

        // Deterministic fallback: a single candidate (the typical
        // partially-bound case) is unambiguous enough to carry the decision.
        if (info.CandidateSymbols.Length == 1 && info.CandidateSymbols[0] is IMethodSymbol candidate)
        {
            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Collect the conditional symbol names declared by every
    /// <c>[System.Diagnostics.Conditional("SYM")]</c> attribute on the method
    /// (the original definition, so an overridden / constructed method still
    /// sees the declaring attributes). The result is de-duplicated and
    /// ordinal-sorted for determinism; empty when the method is not
    /// conditional.
    /// </summary>
    private static IReadOnlyList<string> GetConditionalSymbols(IMethodSymbol method)
    {
        SortedSet<string>? symbols = null;

        foreach (AttributeData attribute in method.OriginalDefinition.GetAttributes())
        {
            INamedTypeSymbol? attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (attributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                != "global::" + ConditionalAttributeMetadataName)
            {
                continue;
            }

            // [Conditional(string)] has exactly one positional string argument.
            if (attribute.ConstructorArguments.Length != 1)
            {
                continue;
            }

            TypedConstant arg = attribute.ConstructorArguments[0];
            if (arg.Kind != TypedConstantKind.Primitive || arg.Value is not string symbol)
            {
                continue;
            }

            if (string.IsNullOrEmpty(symbol))
            {
                continue;
            }

            (symbols ??= new SortedSet<string>(StringComparer.Ordinal)).Add(symbol);
        }

        return symbols is null
            ? Array.Empty<string>()
            : symbols.ToArray();
    }

    /// <summary>
    /// The defined preprocessor symbol set for a parsed tree (the parse
    /// options the file was parsed with), as an ordinal hash set.
    /// </summary>
    private static ImmutableHashSet<string> GetDefinedSymbols(SyntaxTree tree)
    {
        if (tree.Options is CSharpParseOptions options)
        {
            return options.PreprocessorSymbolNames.ToImmutableHashSet(StringComparer.Ordinal);
        }

        return ImmutableHashSet<string>.Empty;
    }
}

/// <summary>
/// The Pass-2 lowering decision for an invocation of a
/// <c>[System.Diagnostics.Conditional("SYM")]</c> method per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.4: the conditional
/// symbol(s) the call gates on and whether the call is kept or elided.
/// </summary>
/// <remarks>
/// Recorded by <see cref="ConditionalAttributeElisionNormalizer"/> on the
/// original <see cref="InvocationExpressionSyntax"/>. The C++ backend keeps
/// the call when <see cref="Keep"/> is true and omits the entire call
/// statement when it is false (matching C# / Roslyn <c>[Conditional]</c>
/// semantics).
/// </remarks>
public sealed class ConditionalElisionAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct a conditional-elision annotation.
    /// </summary>
    /// <param name="conditionalSymbols">
    /// The conditional symbol(s) declared by the called method's
    /// <c>[Conditional]</c> attribute(s), de-duplicated and ordinal-sorted.
    /// Must not be null or empty.
    /// </param>
    /// <param name="keep">
    /// True iff the call is kept (at least one conditional symbol is defined
    /// in the compilation); false iff the call is elided.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="conditionalSymbols"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="conditionalSymbols"/> is empty.</exception>
    public ConditionalElisionAnnotation(IReadOnlyList<string> conditionalSymbols, bool keep)
    {
        ArgumentNullException.ThrowIfNull(conditionalSymbols);
        if (conditionalSymbols.Count == 0)
        {
            throw new ArgumentException(
                "A conditional-elision annotation must carry at least one conditional symbol.",
                nameof(conditionalSymbols));
        }

        ConditionalSymbols = conditionalSymbols;
        Keep = keep;
    }

    /// <inheritdoc/>
    public override string Kind => "conditional-elision";

    /// <summary>
    /// The conditional symbol(s) the call gates on (the union of all
    /// <c>[Conditional("SYM")]</c> arguments on the called method),
    /// de-duplicated and ordinal-sorted for determinism.
    /// </summary>
    public IReadOnlyList<string> ConditionalSymbols { get; }

    /// <summary>
    /// True iff the call is KEPT (at least one
    /// <see cref="ConditionalSymbols"/> entry is defined in the compilation's
    /// preprocessor symbol set); false iff the call is ELIDED.
    /// </summary>
    public bool Keep { get; }
}
