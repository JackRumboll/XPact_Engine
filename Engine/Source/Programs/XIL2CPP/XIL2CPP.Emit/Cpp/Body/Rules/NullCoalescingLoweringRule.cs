// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Body-lowering rule for the C# null-coalescing operator
/// (<c>a ?? b</c>, a <see cref="BinaryExpressionSyntax"/> of kind
/// <see cref="SyntaxKind.CoalesceExpression"/>) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5. It lowers to the C++ ternary
/// <c>(&lt;a&gt; != nullptr ? &lt;a&gt; : &lt;b&gt;)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single evaluation of <c>a</c>.</b> The simple ternary spelling evaluates
/// <c>a</c> twice. That is correct when <c>a</c> is side-effect-free (the doc's
/// simple form). When <c>a</c> is NOT side-effect-free (an invocation,
/// assignment, increment, object creation, ...) the simple form would
/// double-evaluate; per the WU-D5 spec the rule accepts the doc's simple form
/// here but precedes it with a deliberate
/// <c>// DEFERRED(6.e): ...</c> comment so the known double-evaluation is
/// visible and a later wave can introduce a temporary-binding lowering.
/// </para>
/// <para>
/// <b>Determinism.</b> Pure structural lowering; no clock / culture / random
/// (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class NullCoalescingLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "NullCoalescing";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
        => node is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.CoalesceExpression);

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        BinaryExpressionSyntax coalesce = (BinaryExpressionSyntax)node;
        ExpressionSyntax left = coalesce.Left;
        ExpressionSyntax right = coalesce.Right;

        if (!IsSideEffectFree(left))
        {
            writer.AppendComment(
                "DEFERRED(6.e): '??' left operand has side effects; the simple ternary double-evaluates it");
        }

        // (<a> != nullptr ? <a> : <b>)
        writer.Append("(");
        parent.Expressions.EmitExpression(left);
        writer.Append(" != nullptr ? ");
        parent.Expressions.EmitExpression(left);
        writer.Append(" : ");
        parent.Expressions.EmitExpression(right);
        writer.Append(")");
    }

    /// <summary>
    /// Conservative syntactic side-effect-free test: an identifier, a
    /// <c>this</c> / literal / default expression, a member access whose
    /// receiver is itself side-effect-free, or a parenthesized side-effect-free
    /// expression. Anything else (invocation, assignment, increment, object
    /// creation, await, ...) is treated as having side effects.
    /// </summary>
    private static bool IsSideEffectFree(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        LiteralExpressionSyntax => true,
        ThisExpressionSyntax => true,
        DefaultExpressionSyntax => true,
        MemberAccessExpressionSyntax member => IsSideEffectFree(member.Expression),
        ParenthesizedExpressionSyntax paren => IsSideEffectFree(paren.Expression),
        _ => false,
    };
}
