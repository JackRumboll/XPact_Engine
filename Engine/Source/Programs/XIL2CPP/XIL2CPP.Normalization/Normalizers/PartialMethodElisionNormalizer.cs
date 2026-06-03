// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Per-node lowering decision recorded on an
/// <see cref="InvocationExpressionSyntax"/> whose resolved target is an
/// <em>unbodied</em> C# <c>partial</c> method (a partial declaration with no
/// implementing body), per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.1.
/// The call is elided at code generation: the invocation itself produces no
/// emitted call, but any side-effecting arguments are still evaluated to
/// preserve their observable side effects.
/// </summary>
/// <remarks>
/// <para>
/// This is the annotation half of WU-12. Pass 2 does NOT rewrite the syntax
/// tree (the Pass-1 binding info stays authoritative); it records this
/// additive decision keyed on the original invocation node so Pass 3 / the
/// emitter can drop the call while still emitting the argument evaluations.
/// </para>
/// </remarks>
public sealed class PartialMethodElisionAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct an elision annotation for an unbodied partial-method call.
    /// </summary>
    /// <param name="targetDisplayName">
    /// The display name of the elided partial method (used in diagnostics /
    /// logging). Must not be null.
    /// </param>
    /// <param name="hasSideEffectingArguments">
    /// True iff at least one argument expression at the call site is
    /// side-effecting and therefore must still be evaluated even though the
    /// call is elided (the trigger for <c>XIL2CPP018</c>).
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="targetDisplayName"/> is null.</exception>
    public PartialMethodElisionAnnotation(string targetDisplayName, bool hasSideEffectingArguments)
    {
        ArgumentNullException.ThrowIfNull(targetDisplayName);
        TargetDisplayName = targetDisplayName;
        HasSideEffectingArguments = hasSideEffectingArguments;
    }

    /// <inheritdoc/>
    public override string Kind => "partial-method-elision";

    /// <summary>The display name of the elided unbodied partial method.</summary>
    public string TargetDisplayName { get; }

    /// <summary>
    /// True iff the call site has at least one side-effecting argument whose
    /// evaluation must be preserved even though the call is elided.
    /// </summary>
    public bool HasSideEffectingArguments { get; }
}

/// <summary>
/// Pass-2 normalizer that elides call sites to unbodied C# <c>partial</c>
/// methods, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.1
/// (FIX-B-MEDIUM-14). A partial method <em>declaration</em> with no
/// implementing body has every <c>obj.PartialMethod(args)</c> call elided at
/// code generation; the arguments are still evaluated so their side effects
/// are preserved. A partial method <em>with</em> a body is left untouched
/// (it emits normally). When an elided call's argument list is
/// side-effecting, the normalizer ALSO emits warning
/// <see cref="DiagnosticCodes.UnbodiedPartialMethodSideEffectingArgs"/>
/// (<c>XIL2CPP018</c>) at the call site.
/// </summary>
/// <remarks>
/// <para>
/// <b>Binding.</b> A call to a partial method binds (through
/// <c>SemanticModel.GetSymbolInfo</c>) to the partial <em>definition</em>
/// symbol. The definition is "unbodied"
/// when it is a partial definition
/// (<see cref="IMethodSymbol.IsPartialDefinition"/>) whose
/// <see cref="IMethodSymbol.PartialImplementationPart"/> is null. Roslyn
/// merges bodied / unbodied parts across files of the same partial class
/// before Pass 2 sees the unified symbol, so a definition with an
/// implementation in any file is correctly treated as bodied.
/// </para>
/// <para>
/// <b>Determinism.</b> Files are visited in <see cref="Pass1Result.ParsedFiles"/>
/// order; within a file invocations are visited in source span order
/// (<c>SyntaxNode.DescendantNodes</c> is document order). No ambient
/// state, <c>DateTime</c>, or <c>Random</c> is consulted.
/// </para>
/// </remarks>
public sealed class PartialMethodElisionNormalizer : INormalizer
{
    /// <inheritdoc/>
    public string Name => "PartialMethodElisionNormalizer";

    /// <inheritdoc/>
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxTree tree = file.Tree;
            SyntaxNode root = tree.GetRoot();
            SemanticModel model = pass1.GetSemanticModel(tree);

            // DescendantNodes() yields nodes in document (span) order, which
            // is a stable, deterministic visit order.
            foreach (InvocationExpressionSyntax invocation in
                root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                if (!IsUnbodiedPartial(method))
                {
                    continue;
                }

                bool hasSideEffectingArgs = HasSideEffectingArgument(invocation, model);

                builder.AnnotateNode(
                    invocation,
                    new PartialMethodElisionAnnotation(
                        method.Name,
                        hasSideEffectingArgs));

                if (hasSideEffectingArgs)
                {
                    builder.AddDiagnostic(BuildSideEffectDiagnostic(
                        pass1, file, invocation, method));
                }
            }
        }
    }

    /// <summary>
    /// True iff <paramref name="method"/> resolves to an unbodied partial
    /// method: the partial <em>definition</em> part exists but no
    /// <em>implementation</em> part does. A call binds to the definition
    /// symbol; a definition with an implementation in any file is bodied.
    /// </summary>
    private static bool IsUnbodiedPartial(IMethodSymbol method)
    {
        // Normalize to the definition part. A call site binds to the
        // definition; defensively handle the symbol already being either
        // part by walking to the definition when present.
        IMethodSymbol definition = method.PartialDefinitionPart ?? method;

        // Only genuine partial definitions are candidates. A non-partial
        // method, or a partial method that has an implementation part, emits
        // normally.
        return definition.IsPartialDefinition
            && definition.PartialImplementationPart is null;
    }

    /// <summary>
    /// True iff any argument expression at <paramref name="invocation"/> is
    /// side-effecting (its evaluation can change observable state), and so
    /// must still be evaluated even though the call is elided.
    /// </summary>
    private static bool HasSideEffectingArgument(
        InvocationExpressionSyntax invocation,
        SemanticModel model)
    {
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            if (IsSideEffectingExpression(argument.Expression, model))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Conservatively decide whether <paramref name="expression"/> (including
    /// its sub-expressions) can produce an observable side effect. Recognizes
    /// invocations, object / collection creation, assignments (including
    /// compound), pre/post increment-decrement, <c>await</c>, and <c>with</c>
    /// initializers as side-effecting; pure reads (identifiers, literals,
    /// member access, casts of pure operands, etc.) are not.
    /// </summary>
    private static bool IsSideEffectingExpression(
        ExpressionSyntax expression,
        SemanticModel model)
    {
        foreach (SyntaxNode node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InvocationExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case ArrayCreationExpressionSyntax:
                case ImplicitArrayCreationExpressionSyntax:
                case AssignmentExpressionSyntax:
                case AwaitExpressionSyntax:
                case WithExpressionSyntax:
                    return true;

                case PrefixUnaryExpressionSyntax prefix
                    when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                        || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    return true;

                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression)
                        || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Build the <c>XIL2CPP018</c> warning anchored at the invocation's start
    /// location (1-based) for an elided unbodied-partial call whose arguments
    /// are side-effecting.
    /// </summary>
    private static DiagnosticRecord BuildSideEffectDiagnostic(
        Pass1Result pass1,
        ModuleParser.ParsedFile file,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        // Roslyn reports 0-based line/character; SourceSpan is 1-based.
        FileLinePositionSpan lineSpan = invocation.GetLocation().GetLineSpan();
        int startLine = lineSpan.StartLinePosition.Line + 1;
        int startColumn = lineSpan.StartLinePosition.Character + 1;

        SourceSpan span = SourceSpan.Point(file.AbsolutePath, startLine, startColumn);

        string message = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "call site to unbodied partial method '{0}' has side-effecting arguments; arguments are evaluated but the call is elided",
            method.Name);

        return span.ToDiagnostic(
            DiagnosticSeverity.Warning,
            DiagnosticCodes.UnbodiedPartialMethodSideEffectingArgs,
            message,
            pass1.ModuleName);
    }
}
