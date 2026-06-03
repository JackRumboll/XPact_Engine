// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# binary expression (<see cref="BinaryExpressionSyntax"/>) to the
/// identical C++ infix operator, recursing both operands through the parent
/// emitter, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5 (expression
/// mapping). Covers the arithmetic (<c>+ - * / %</c>), comparison
/// (<c>== != &lt; &gt; &lt;= &gt;=</c>), logical (<c>&amp;&amp; ||</c>), and
/// bitwise (<c>&amp; | ^ &lt;&lt; &gt;&gt;</c>) operator groups -- every one
/// of which is spelled identically in C++.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence safety.</b> The whole expression is wrapped in a single pair
/// of parentheses: <c>(&lt;left&gt; &lt;op&gt; &lt;right&gt;)</c>. Because each
/// nested binary / unary / conditional sub-expression also wraps itself, the
/// emitted C++ associativity / precedence is fully explicit and never relies on
/// the C++ grammar matching C#'s -- so a re-parenthesizing C++ compiler can
/// never change the evaluation order the C# author wrote. The cost is redundant
/// parentheses, which the C++ toolchain folds away; correctness over brevity.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The operator text is the
/// verbatim source operator token text (a fixed mapping of C# operator to the
/// identical C++ operator); no ambient state participates.
/// </para>
/// </remarks>
public sealed class BinaryExpressionLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "Expr.Binary";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is BinaryExpressionSyntax && IsLoweredOperator(((BinaryExpressionSyntax)node).Kind());
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var binary = (BinaryExpressionSyntax)node;

        writer.Append("(");
        parent.Expressions.EmitExpression(binary.Left);
        writer.Append(" ");
        writer.Append(MapOperator(binary.OperatorToken));
        writer.Append(" ");
        parent.Expressions.EmitExpression(binary.Right);
        writer.Append(")");
    }

    /// <summary>
    /// True iff <paramref name="kind"/> is one of the arithmetic / comparison /
    /// logical / bitwise binary expression kinds this rule lowers (each spelled
    /// identically in C++). Other binary-shaped kinds (e.g. <c>??</c>,
    /// <c>is</c>, <c>as</c>) are intentionally NOT handled here -- they are
    /// either normalized away (Pass 2) or lowered by a dedicated rule -- so they
    /// fall to the foundation TODO marker rather than being silently mis-mapped.
    /// </summary>
    private static bool IsLoweredOperator(SyntaxKind kind) => kind switch
    {
        // Arithmetic.
        SyntaxKind.AddExpression => true,
        SyntaxKind.SubtractExpression => true,
        SyntaxKind.MultiplyExpression => true,
        SyntaxKind.DivideExpression => true,
        SyntaxKind.ModuloExpression => true,

        // Comparison.
        SyntaxKind.EqualsExpression => true,
        SyntaxKind.NotEqualsExpression => true,
        SyntaxKind.LessThanExpression => true,
        SyntaxKind.LessThanOrEqualExpression => true,
        SyntaxKind.GreaterThanExpression => true,
        SyntaxKind.GreaterThanOrEqualExpression => true,

        // Logical.
        SyntaxKind.LogicalAndExpression => true,
        SyntaxKind.LogicalOrExpression => true,

        // Bitwise / shift.
        SyntaxKind.BitwiseAndExpression => true,
        SyntaxKind.BitwiseOrExpression => true,
        SyntaxKind.ExclusiveOrExpression => true,
        SyntaxKind.LeftShiftExpression => true,
        SyntaxKind.RightShiftExpression => true,

        _ => false,
    };

    /// <summary>
    /// Map a C# binary operator token to its C++ spelling. Every operator this
    /// rule handles is spelled identically in C++, so the mapping is the
    /// operator token's verbatim text; the explicit switch nonetheless pins each
    /// kind so an unexpected token can never be emitted blind.
    /// </summary>
    private static string MapOperator(SyntaxToken op) => op.Kind() switch
    {
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.AsteriskToken => "*",
        SyntaxKind.SlashToken => "/",
        SyntaxKind.PercentToken => "%",
        SyntaxKind.EqualsEqualsToken => "==",
        SyntaxKind.ExclamationEqualsToken => "!=",
        SyntaxKind.LessThanToken => "<",
        SyntaxKind.LessThanEqualsToken => "<=",
        SyntaxKind.GreaterThanToken => ">",
        SyntaxKind.GreaterThanEqualsToken => ">=",
        SyntaxKind.AmpersandAmpersandToken => "&&",
        SyntaxKind.BarBarToken => "||",
        SyntaxKind.AmpersandToken => "&",
        SyntaxKind.BarToken => "|",
        SyntaxKind.CaretToken => "^",
        SyntaxKind.LessThanLessThanToken => "<<",
        SyntaxKind.GreaterThanGreaterThanToken => ">>",
        _ => throw new InvalidOperationException(
            $"BinaryExpressionLoweringRule reached an unmapped operator token '{op.Kind()}'."),
    };
}
