// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// The expression-level visitor over the bound tree + the
/// <see cref="Normalization.NormalizedUnit"/> annotations, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (Pass 6) + Section 5
/// (the expression mapping rules). It dispatches each expression node to the
/// first matching <see cref="IBodyLoweringRule"/> in the shared registry; a
/// node with NO matching rule emits the deliberate
/// <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c> comment.
/// </summary>
/// <remarks>
/// <para>
/// The expression emitter shares its owning <see cref="StatementEmitter"/>'s
/// context / writer / registry (a body-lowering rule and the statement /
/// expression emitters all thread one set). A rule lowers a child expression
/// through <c>parent.Expressions.EmitExpression(node)</c> and a child
/// statement through <c>parent.EmitStatement(node)</c>.
/// </para>
/// </remarks>
public sealed class ExpressionEmitter
{
    private readonly StatementEmitter _parent;

    /// <summary>
    /// Construct an expression emitter bound to its owning statement emitter
    /// (shares the statement emitter's context / writer / registry). Called by
    /// <see cref="StatementEmitter"/>'s constructor.
    /// </summary>
    /// <param name="parent">The owning statement emitter. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="parent"/> is null.</exception>
    internal ExpressionEmitter(StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _parent = parent;
    }

    /// <summary>The owning statement emitter (for recursion back into statements).</summary>
    public StatementEmitter Statements => _parent;

    /// <summary>The per-module emit context (shared with the statement emitter).</summary>
    public EmitContext Context => _parent.Context;

    /// <summary>The C++ writer (shared with the statement emitter).</summary>
    public CppWriter Writer => _parent.Writer;

    /// <summary>The body-lowering rule registry (shared with the statement emitter).</summary>
    public BodyLoweringRuleRegistry Registry => _parent.Registry;

    /// <summary>
    /// Lower one expression node: dispatch to the first matching rule, or emit
    /// the <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c> comment when no
    /// rule handles it.
    /// </summary>
    /// <param name="node">The expression node to lower. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public void EmitExpression(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        IBodyLoweringRule? rule = Registry.FindRule(node);
        if (rule is not null)
        {
            rule.Emit(node, Context, Writer, _parent);
            return;
        }

        _parent.EmitTodo(node);
    }
}
