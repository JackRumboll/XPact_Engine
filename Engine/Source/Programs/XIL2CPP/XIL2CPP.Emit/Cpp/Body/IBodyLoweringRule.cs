// Copyright Simgenics. All Rights Reserved.

using Microsoft.CodeAnalysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// A single body-lowering rule: lowers one kind of C# syntax node to its C++
/// form, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 (Pass 6) +
/// Section 5 (the statement / expression mapping rules). Each rule
/// (assignment, invocation, <c>if</c>, <c>foreach</c>, local declaration,
/// member access, ...) is implemented as one class in its own file in the
/// <c>XIL2CPP.Emit</c> assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auto-discovery (mirrors <see cref="Analysis.Pass3Driver"/>).</b>
/// <see cref="BodyLoweringRuleRegistry"/> reflection-discovers every
/// non-abstract <see cref="IBodyLoweringRule"/> implementation in the
/// <c>XIL2CPP.Emit</c> assembly that has a public parameterless constructor,
/// sorts them deterministically by <see cref="Name"/> (ordinal), and
/// dispatches to the first whose <see cref="CanHandle"/> returns true. To add
/// a rule a later agent simply declares a class implementing this interface in
/// the production assembly -- no registration edit, no driver edit.
/// </para>
/// <para>
/// <b>Recursion through the parent emitter.</b> A rule lowers its node and
/// calls back into the supplied <see cref="StatementEmitter"/> (the
/// <c>parent</c>) to lower child statements
/// (<see cref="StatementEmitter.EmitStatement"/>) and child expressions
/// (<see cref="StatementEmitter.Expressions"/> /
/// <see cref="ExpressionEmitter.EmitExpression"/>). The rule never constructs
/// its own emitter; it reuses the parent so the shared
/// <see cref="EmitContext"/> + <see cref="CppWriter"/> + the discovered
/// rule set are threaded through consistently.
/// </para>
/// <para>
/// <b>Determinism.</b> A rule MUST emit byte-deterministically (no
/// <see cref="System.DateTime"/>, <see cref="System.Guid"/>,
/// <see cref="System.Random"/>; ordinal string ordering; invariant culture)
/// per gate X-IL2CPP-CSPATH-DET.
/// </para>
/// </remarks>
public interface IBodyLoweringRule
{
    /// <summary>
    /// A stable, unique, human-readable name. It is the deterministic ordering
    /// key <see cref="BodyLoweringRuleRegistry"/> sorts by (ordinal), so two
    /// builds dispatch the rules in an identical order (and so the first
    /// matching rule for a node is stable). Must be non-null / non-empty and
    /// unique across the assembly.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// True iff this rule lowers <paramref name="node"/>. The registry
    /// dispatches to the first rule (in <see cref="Name"/> order) whose
    /// <see cref="CanHandle"/> returns true.
    /// </summary>
    /// <param name="node">The candidate syntax node. Never null at the dispatch site.</param>
    /// <returns>True iff this rule handles the node.</returns>
    bool CanHandle(SyntaxNode node);

    /// <summary>
    /// Lower <paramref name="node"/> into <paramref name="writer"/>, reading
    /// the per-module inputs from <paramref name="context"/> and recursing
    /// into child statements / expressions through <paramref name="parent"/>.
    /// </summary>
    /// <param name="node">The syntax node to lower (one this rule's <see cref="CanHandle"/> accepted). Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <param name="parent">The dispatching statement emitter, for recursion into children. Must not be null.</param>
    void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent);
}
