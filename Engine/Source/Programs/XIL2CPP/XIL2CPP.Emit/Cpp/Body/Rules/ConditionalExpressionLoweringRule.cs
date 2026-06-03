// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# conditional (ternary) expression
/// (<see cref="ConditionalExpressionSyntax"/>) to the identical C++ ternary --
/// <c>(&lt;condition&gt; ? &lt;whenTrue&gt; : &lt;whenFalse&gt;)</c> -- recursing
/// all three operands through the parent emitter, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (expression mapping).
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence safety.</b> The whole expression is wrapped in a single pair
/// of parentheses so the ternary binds exactly as the C# author wrote it,
/// regardless of the surrounding C++ context. Each operand wraps itself, so the
/// three sub-expressions never re-associate across the <c>?</c> / <c>:</c>.
/// </para>
/// </remarks>
public sealed class ConditionalExpressionLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "Expr.Conditional";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is ConditionalExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var conditional = (ConditionalExpressionSyntax)node;

        writer.Append("(");
        parent.Expressions.EmitExpression(conditional.Condition);
        writer.Append(" ? ");
        parent.Expressions.EmitExpression(conditional.WhenTrue);
        writer.Append(" : ");
        parent.Expressions.EmitExpression(conditional.WhenFalse);
        writer.Append(")");
    }
}
