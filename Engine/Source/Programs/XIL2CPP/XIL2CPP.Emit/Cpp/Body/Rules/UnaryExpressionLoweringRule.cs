// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# unary expression -- prefix
/// (<see cref="PrefixUnaryExpressionSyntax"/>: <c>++ -- ! - + ~</c>) or postfix
/// (<see cref="PostfixUnaryExpressionSyntax"/>: <c>++ --</c>) -- to the
/// identical C++ unary operator, recursing the operand through the parent
/// emitter, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (expression
/// mapping). Each handled operator is spelled identically in C++.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence safety.</b> The whole expression is wrapped in a single pair
/// of parentheses -- prefix as <c>(&lt;op&gt;&lt;operand&gt;)</c>, postfix as
/// <c>(&lt;operand&gt;&lt;op&gt;)</c> -- so the bound operator association is
/// explicit and never re-bound by the C++ grammar. The nested operand wraps
/// itself, so e.g. <c>!a == b</c> can never collapse to <c>!(a == b)</c>.
/// </para>
/// <para>
/// <b>Operator placement.</b> The C# token text is emitted verbatim, immediately
/// adjacent to the operand with no separating space, so <c>- -x</c> (a negation
/// of a negation) lowers to <c>(-(-x))</c> -- the inner parentheses keep the two
/// <c>-</c> tokens from fusing into a single <c>--</c> token in the C++ lexer.
/// </para>
/// <para>
/// <b>Scope.</b> Only the value-operator unary forms are lowered here. The
/// address-shaped unary forms (<c>&amp;x</c> address-of, <c>*p</c> pointer
/// indirection, <c>^i</c> index-from-end) and the suppress-null
/// (<c>x!</c>) form are intentionally not handled -- they are banned in the
/// sim path, normalized away, or lowered by a dedicated rule -- so they fall to
/// the foundation TODO marker rather than being silently mis-mapped.
/// </para>
/// </remarks>
public sealed class UnaryExpressionLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "Expr.Unary";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node switch
        {
            PrefixUnaryExpressionSyntax prefix => IsLoweredPrefix(prefix.Kind()),
            PostfixUnaryExpressionSyntax postfix => IsLoweredPostfix(postfix.Kind()),
            _ => false,
        };
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        switch (node)
        {
            case PrefixUnaryExpressionSyntax prefix:
                writer.Append("(");
                writer.Append(MapOperator(prefix.OperatorToken));
                parent.Expressions.EmitExpression(prefix.Operand);
                writer.Append(")");
                break;

            case PostfixUnaryExpressionSyntax postfix:
                writer.Append("(");
                parent.Expressions.EmitExpression(postfix.Operand);
                writer.Append(MapOperator(postfix.OperatorToken));
                writer.Append(")");
                break;

            default:
                throw new InvalidOperationException(
                    $"UnaryExpressionLoweringRule reached an unsupported node kind '{node.Kind()}'.");
        }
    }

    /// <summary>
    /// True iff <paramref name="kind"/> is a prefix unary form this rule lowers
    /// (logical-not, unary-plus, unary-minus, bitwise-complement,
    /// pre-increment, pre-decrement) -- each spelled identically in C++.
    /// </summary>
    private static bool IsLoweredPrefix(SyntaxKind kind) => kind switch
    {
        SyntaxKind.UnaryPlusExpression => true,
        SyntaxKind.UnaryMinusExpression => true,
        SyntaxKind.LogicalNotExpression => true,
        SyntaxKind.BitwiseNotExpression => true,
        SyntaxKind.PreIncrementExpression => true,
        SyntaxKind.PreDecrementExpression => true,
        _ => false,
    };

    /// <summary>
    /// True iff <paramref name="kind"/> is a postfix unary form this rule lowers
    /// (post-increment, post-decrement) -- each spelled identically in C++.
    /// </summary>
    private static bool IsLoweredPostfix(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PostIncrementExpression => true,
        SyntaxKind.PostDecrementExpression => true,
        _ => false,
    };

    /// <summary>
    /// Map a C# unary operator token to its (identical) C++ spelling. The
    /// explicit switch pins each handled token so an unexpected token can never
    /// be emitted blind.
    /// </summary>
    private static string MapOperator(SyntaxToken op) => op.Kind() switch
    {
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.ExclamationToken => "!",
        SyntaxKind.TildeToken => "~",
        SyntaxKind.PlusPlusToken => "++",
        SyntaxKind.MinusMinusToken => "--",
        _ => throw new InvalidOperationException(
            $"UnaryExpressionLoweringRule reached an unmapped operator token '{op.Kind()}'."),
    };
}
