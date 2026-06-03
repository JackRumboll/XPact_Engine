// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Body-lowering rule for the C# null-conditional member access
/// (<c>a?.b</c>, a <see cref="ConditionalAccessExpressionSyntax"/> whose
/// <see cref="ConditionalAccessExpressionSyntax.WhenNotNull"/> is a simple
/// member binding) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5. It lowers
/// to the C++ ternary <c>(&lt;a&gt; != nullptr ? &lt;a&gt;-&gt;b : nullptr)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Member binding only.</b> The handled shape is <c>a?.b</c> -- the
/// <c>WhenNotNull</c> is a <see cref="MemberBindingExpressionSyntax"/>. A chained
/// / invoked / indexed continuation (<c>a?.b()</c>, <c>a?.b.c</c>, <c>a?[i]</c>)
/// is a richer lowering (it must thread the same not-null guard through the
/// whole continuation); per the WU-D5 spec the rule emits a deliberate
/// <c>// DEFERRED(6.e): ...</c> comment for those so the gap is visible and a
/// later wave handles the full continuation.
/// </para>
/// <para>
/// <b>Single evaluation of <c>a</c>.</b> As with <c>??</c>, the simple ternary
/// double-evaluates the receiver; when <c>a</c> is not side-effect-free the rule
/// still emits the simple form (the doc's accepted shape) preceded by a
/// DEFERRED note.
/// </para>
/// <para>
/// <b>Determinism.</b> Pure structural lowering; no clock / culture / random
/// (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// </remarks>
public sealed class NullConditionalLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "NullConditional";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node) => node is ConditionalAccessExpressionSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        ConditionalAccessExpressionSyntax access = (ConditionalAccessExpressionSyntax)node;
        ExpressionSyntax receiver = access.Expression;

        if (access.WhenNotNull is not MemberBindingExpressionSyntax binding)
        {
            // Chained / invoked / indexed continuation: not the simple a?.b shape.
            writer.AppendComment(
                $"DEFERRED(6.e): null-conditional continuation '{access.WhenNotNull.Kind()}' not yet lowered");
            return;
        }

        if (!IsSideEffectFree(receiver))
        {
            writer.AppendComment(
                "DEFERRED(6.e): '?.' receiver has side effects; the simple ternary double-evaluates it");
        }

        // (<a> != nullptr ? <a>->b : nullptr)
        writer.Append("(");
        parent.Expressions.EmitExpression(receiver);
        writer.Append(" != nullptr ? ");
        parent.Expressions.EmitExpression(receiver);
        writer.Append("->");
        writer.Append(binding.Name.Identifier.ValueText);
        writer.Append(" : nullptr)");
    }

    /// <summary>
    /// Conservative syntactic side-effect-free test (see
    /// <see cref="NullCoalescingLoweringRule"/>): identifier / literal /
    /// <c>this</c> / default / member access over a side-effect-free receiver /
    /// parenthesized side-effect-free expression; everything else has side
    /// effects.
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
