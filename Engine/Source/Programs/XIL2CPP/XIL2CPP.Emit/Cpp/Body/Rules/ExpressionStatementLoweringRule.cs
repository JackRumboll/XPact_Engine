// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# <see cref="ExpressionStatementSyntax"/> (a statement-position
/// expression: a field / property store <c>Boss = a;</c>, a call <c>Foo();</c>,
/// a compound assignment, ...) per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 5.3 (statement lowering). Without this rule a statement-position
/// expression has NO matching <see cref="IBodyLoweringRule"/>, so the
/// <see cref="StatementEmitter"/> falls back to a <c>// TODO(6.e)</c> comment
/// &#8211; which (critically) means the
/// <see cref="AssignmentLoweringRule"/> write-barrier path NEVER fires from a
/// method body (a statement-position XObject field store would silently drop its
/// Phase-6.h <c>XPACT_GC_STORE</c>). This rule routes the inner expression
/// through the shared expression emitter so it dispatches to the right rule:
/// an assignment to <see cref="AssignmentLoweringRule"/> (so an XObject field
/// store now emits the barrier), an invocation to
/// <see cref="InvocationLoweringRule"/>, and so on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Statement termination (the <c>;</c>).</b> The inner expression is lowered
/// through <see cref="ExpressionEmitter.EmitExpression"/>. Most expression rules
/// emit a bare mid-line fragment with NO terminator (e.g.
/// <see cref="InvocationLoweringRule"/> emits <c>foo(...)</c>), so this rule
/// appends the statement terminator <c>";\n"</c>. The
/// <see cref="AssignmentLoweringRule"/> is the ONE exception: it is inherently
/// statement-form (its XObject reference-store path emits the
/// <c>XPACT_GC_STORE(...)</c> macro, which is a C++ statement &#8211; a
/// <c>do {} while(0)</c> form &#8211; that CANNOT be used as a sub-expression,
/// and even its plain-store path closes with <c>";\n"</c>). It therefore
/// terminates ITSELF; this rule recognises an assignment inner expression and
/// does NOT append a second terminator (a double <c>;</c> / blank line). This
/// keeps the assignment rule's contract (and its tests) intact while routing the
/// statement through it so the barrier fires from a body.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The lowering is a pure
/// function of the inner expression's own rule dispatch; the only literal
/// fragment is the fixed <c>";"</c> terminator; no ambient state,
/// <see cref="DateTime"/>, <see cref="Guid"/>, or culture-sensitive formatting.
/// </para>
/// </remarks>
public sealed class ExpressionStatementLoweringRule : IBodyLoweringRule
{
    /// <inheritdoc/>
    public string Name => "ExpressionStatement";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is ExpressionStatementSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var statement = (ExpressionStatementSyntax)node;
        ExpressionSyntax inner = statement.Expression;

        // Lower the inner expression through the shared expression emitter so it
        // dispatches to its own rule (assignment -> AssignmentLoweringRule,
        // invocation -> InvocationLoweringRule, ...).
        parent.Expressions.EmitExpression(inner);

        // The assignment rule is inherently statement-form: it already closes
        // with ";\n" (the XObject reference-store path emits the XPACT_GC_STORE
        // statement macro, which is NOT usable as a sub-expression). Appending a
        // second terminator would emit a stray ";" / blank line. Every other
        // expression rule emits a bare fragment, so this rule supplies the
        // terminating ";\n".
        if (inner is AssignmentExpressionSyntax)
        {
            return;
        }

        writer.Append(";");
        writer.AppendLine();
    }
}
