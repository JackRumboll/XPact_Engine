// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# parenthesized expression
/// (<see cref="ParenthesizedExpressionSyntax"/>) to the identical C++
/// parenthesized form -- <c>(&lt;inner&gt;)</c> -- recursing the inner
/// expression through the parent emitter, per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 5 (expression mapping).
/// </summary>
/// <remarks>
/// The author-written parentheses are preserved verbatim. Combined with the
/// defensive parentheses the binary / unary / conditional rules add, the
/// emitted C++ precedence is always explicit; the redundant nesting is folded
/// away by the C++ toolchain.
/// </remarks>
public sealed class ParenthesizedExpressionLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "Expr.Parenthesized";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is ParenthesizedExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var paren = (ParenthesizedExpressionSyntax)node;

        writer.Append("(");
        parent.Expressions.EmitExpression(paren.Expression);
        writer.Append(")");
    }
}
