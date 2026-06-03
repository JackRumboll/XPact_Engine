// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Body-lowering rule for a C# <c>is</c>-pattern expression
/// (<see cref="IsPatternExpressionSyntax"/>) whose pattern is a SIMPLE type
/// pattern (<c>x is Foo</c>, a <see cref="TypePatternSyntax"/> with no variable
/// designation) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5. It lowers to
/// the C++ checked down-cast test
/// <c>dynamic_cast&lt;Target*&gt;(&lt;e&gt;) != nullptr</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Simple type pattern only.</b> The handled shape is a
/// <see cref="TypePatternSyntax"/> (optionally parenthesized) as the whole
/// <c>is</c>-pattern -- a pure type test with no binding. (Roslyn parses the
/// classic <c>x is Foo</c> as a legacy <c>IsExpression</c>
/// <see cref="BinaryExpressionSyntax"/>, not an
/// <see cref="IsPatternExpressionSyntax"/>; a top-level
/// <see cref="TypePatternSyntax"/> arises when a pattern is synthesized or
/// unwrapped from a parenthesized / combinator pattern.) A pattern that binds a
/// variable (<c>x is Foo f</c>, <see cref="DeclarationPatternSyntax"/>), a
/// constant / relational / recursive / list / <c>var</c> pattern, or a logical
/// combinator is a richer lowering (Pass 2's <c>PatternMatchNormalizer</c>
/// already rewrites most of those); per the WU-D5 spec this rule emits a
/// deliberate <c>// DEFERRED(6.e): ...</c> comment for any non-simple pattern so
/// the gap is visible.
/// </para>
/// <para>
/// <b>Checked cast.</b> Unlike the unconditional <see cref="CastLoweringRule"/>
/// (a <c>static_cast</c>), the type-test is a runtime check, so it uses
/// <c>dynamic_cast</c> against the pointer form (<c>Target*</c>) and compares to
/// <c>nullptr</c>.
/// </para>
/// <para>
/// <b>Determinism.</b> Pure structural lowering; no clock / culture / random
/// (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class SimpleTypePatternLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "SimpleTypePattern";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is IsPatternExpressionSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        IsPatternExpressionSyntax isPattern = (IsPatternExpressionSyntax)node;

        if (Unwrap(isPattern.Pattern) is not TypePatternSyntax typePattern)
        {
            // Declaration / constant / relational / recursive / var / combinator
            // pattern: not the simple type-test shape.
            writer.AppendComment(
                $"DEFERRED(6.e): is-pattern '{isPattern.Pattern.Kind()}' not yet lowered");
            return;
        }

        SemanticModel model = context.GetSemanticModel(isPattern.SyntaxTree);
        ITypeSymbol? targetType = model.GetTypeInfo(typePattern.Type).Type;
        string target = CppTypeName.Render(targetType);

        // dynamic_cast<Target*>(<e>) != nullptr
        writer.Append("dynamic_cast<");
        writer.Append(target);
        writer.Append("*>(");
        parent.Expressions.EmitExpression(isPattern.Expression);
        writer.Append(") != nullptr");
    }

    /// <summary>
    /// Unwrap a (possibly nested) <see cref="ParenthesizedPatternSyntax"/> so a
    /// parenthesized simple type pattern (<c>x is (Foo)</c>) is treated the same
    /// as the bare form.
    /// </summary>
    private static PatternSyntax Unwrap(PatternSyntax pattern)
    {
        while (pattern is ParenthesizedPatternSyntax parenthesized)
        {
            pattern = parenthesized.Pattern;
        }
        return pattern;
    }
}
