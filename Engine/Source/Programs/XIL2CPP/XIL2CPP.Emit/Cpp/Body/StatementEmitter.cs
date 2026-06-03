// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// The statement-level visitor over the bound tree + the
/// <see cref="Normalization.NormalizedUnit"/> annotations, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (Pass 6). It dispatches
/// each statement node to the first matching <see cref="IBodyLoweringRule"/>
/// in its <see cref="Registry"/>; a node with NO matching rule emits a
/// deliberate <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c> comment so
/// gaps are visible and never silently wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The recursion seam.</b> A body-lowering rule receives this emitter as
/// its <c>parent</c> and calls <see cref="EmitStatement"/> to lower a child
/// statement and <see cref="Expressions"/>'
/// <see cref="ExpressionEmitter.EmitExpression"/> to lower a child
/// expression. The statement + expression emitters share one
/// <see cref="Context"/>, one <see cref="Writer"/>, and one
/// <see cref="Registry"/>, so the whole lowering of a function body threads a
/// single context / writer / rule set.
/// </para>
/// <para>
/// <b>Zero-rule foundation.</b> With an empty <see cref="Registry"/> every
/// node falls to the TODO comment. This is the intended foundation state
/// before any concrete rule lands.
/// </para>
/// </remarks>
public sealed class StatementEmitter
{
    /// <summary>The TODO comment prefix emitted for an un-lowered node (the marker the next-wave rules retire).</summary>
    public const string TodoPrefix = "TODO(6.e): ";

    /// <summary>
    /// Construct a statement emitter over a shared context / writer / rule set.
    /// The companion <see cref="Expressions"/> emitter is created internally
    /// and bound back to this emitter.
    /// </summary>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <param name="registry">The body-lowering rule registry to dispatch through. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="context"/>, <paramref name="writer"/>, or <paramref name="registry"/> is null.</exception>
    public StatementEmitter(EmitContext context, CppWriter writer, BodyLoweringRuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(registry);

        Context = context;
        Writer = writer;
        Registry = registry;
        Expressions = new ExpressionEmitter(this);
    }

    /// <summary>The per-module emit context.</summary>
    public EmitContext Context { get; }

    /// <summary>The C++ writer this emitter writes into.</summary>
    public CppWriter Writer { get; }

    /// <summary>The body-lowering rule registry this emitter dispatches through.</summary>
    public BodyLoweringRuleRegistry Registry { get; }

    /// <summary>
    /// The companion expression emitter (shares this emitter's context /
    /// writer / registry). A rule lowers a child expression through
    /// <c>parent.Expressions.EmitExpression(node)</c>.
    /// </summary>
    public ExpressionEmitter Expressions { get; }

    /// <summary>
    /// Lower one statement node: dispatch to the first matching rule, or emit
    /// the <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c> comment when no
    /// rule handles it.
    /// </summary>
    /// <param name="node">The statement node to lower. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public void EmitStatement(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        IBodyLoweringRule? rule = Registry.FindRule(node);
        if (rule is not null)
        {
            rule.Emit(node, Context, Writer, this);
            return;
        }

        EmitTodo(node);
    }

    /// <summary>
    /// Emit the deliberate un-lowered marker for <paramref name="node"/>:
    /// <c>// TODO(6.e): &lt;NodeKind&gt; not yet lowered</c>. Shared with
    /// <see cref="ExpressionEmitter"/> so both emitters render the gap marker
    /// identically.
    /// </summary>
    /// <param name="node">The un-lowered node. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="node"/> is null.</exception>
    public void EmitTodo(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Writer.AppendComment($"{TodoPrefix}{node.Kind()} not yet lowered");
    }
}
