// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// Body-lowering rule (WU-D1) for a C# assignment expression, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Sections 5.3 + 6.3:
/// a non-reference LHS lowers to <c>&lt;lhs&gt; = &lt;rhs&gt;;</c>; an LHS that
/// resolves to an XObject-derived reference field / property slot FIRST emits
/// the write-barrier hook comment
/// <c>// TODO(6.h): XPACT_GC_STORE(&lt;parent&gt;, &amp;&lt;slot&gt;, &lt;value&gt;)</c>
/// (the placeholder for the Phase 6.h
/// <c>XPACT_GC_STORE(parent_obj, &amp;slot, new_value)</c> inline expansion) and
/// THEN the plain assignment. Both the LHS and the RHS are lowered by recursing
/// into the parent expression emitter, so this rule never re-implements operand
/// lowering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference-store recognition.</b> The LHS is a reference store iff its
/// bound symbol is a field or property whose type is XObject-derived (the same
/// test the Pass-3 <see cref="Analysis.ReferenceStoreAnalyzer"/> uses, via
/// <see cref="Analysis.AnalyzerHelpers.IsXObjectDerived(INamedTypeSymbol?)"/>).
/// A value-typed slot, a local, or a parameter is NOT a reference store and
/// lowers to the plain assignment with no hook.
/// </para>
/// <para>
/// <b>Why only a hook comment here.</b> WU-D1 owns declaration / assignment
/// lowering; the actual <c>XPACT_GC_STORE</c> expansion (computing the stable
/// <c>parent_obj</c>, the slot address, and the multi-step chain promotion per
/// Section 5.3) is the Phase 6.h write-barrier unit. This rule emits the
/// deterministic hook marker so the barrier site is visible and never silently
/// dropped, then emits the assignment that 6.h will wrap.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The hook placeholders are the
/// syntactic operand texts (a pure function of the tree); operand lowering is
/// delegated to the shared expression emitter; no ambient state,
/// <see cref="DateTime"/>, <see cref="Guid"/>, or culture-sensitive formatting.
/// </para>
/// </remarks>
public sealed class AssignmentLoweringRule : IBodyLoweringRule
{
    /// <summary>The Phase 6.h write-barrier hook-comment prefix this rule emits at a reference store.</summary>
    public const string WriteBarrierHookPrefix = "TODO(6.h): ";

    /// <inheritdoc/>
    public string Name => "Assignment";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
        => node is AssignmentExpressionSyntax;

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var assignment = (AssignmentExpressionSyntax)node;
        ExpressionSyntax lhs = assignment.Left;
        ExpressionSyntax rhs = assignment.Right;

        SemanticModel model = context.GetSemanticModel(node.SyntaxTree);

        if (IsReferenceStore(model, lhs))
        {
            // The Phase 6.h write-barrier hook: parent_obj, &slot, new_value
            // per /Documents/XIL2CPP.html Rev 4 Section 6.3
            // (XPACT_GC_STORE(actor, &actor->Owner, newOwner)). The placeholders
            // are the syntactic operand texts; 6.h computes the stable parent /
            // slot address / promoted value.
            string parent_obj = ReceiverText(lhs);
            string slot = SlotText(lhs);
            string value = rhs.ToString();
            writer.AppendComment(
                $"{WriteBarrierHookPrefix}XPACT_GC_STORE({parent_obj}, &{slot}, {value})");
        }

        // The plain assignment: <lhs> = <rhs>; with both operands lowered
        // through the shared expression emitter (no trailing newline until the
        // closing ";").
        parent.Expressions.EmitExpression(lhs);
        writer.Append(" = ");
        parent.Expressions.EmitExpression(rhs);
        writer.Append(";");
        writer.AppendLine();
    }

    /// <summary>
    /// Return true iff <paramref name="lhs"/> resolves to a field / property
    /// slot whose type is XObject-derived (a reference store that requires the
    /// write barrier). A local, parameter, discard, or value-typed slot is not
    /// a reference store.
    /// </summary>
    private static bool IsReferenceStore(SemanticModel model, ExpressionSyntax lhs)
    {
        ISymbol? slot = model.GetSymbolInfo(lhs).Symbol;
        ITypeSymbol? slotType = slot switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };

        return slotType is INamedTypeSymbol namedSlotType
            && Analysis.AnalyzerHelpers.IsXObjectDerived(namedSlotType);
    }

    /// <summary>
    /// The directly-enclosing object expression of the slot, for the hook's
    /// <c>parent_obj</c> placeholder: the receiver of a member-access LHS, or
    /// <c>this</c> when the slot is written through a bare identifier (an
    /// implicit-<c>this</c> instance member).
    /// </summary>
    private static string ReceiverText(ExpressionSyntax lhs)
        => lhs is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Expression.ToString()
            : "this";

    /// <summary>
    /// The slot expression for the hook's address-of placeholder: the syntactic
    /// LHS text (e.g. <c>actor-&gt;Owner</c> in source form <c>actor.Owner</c>).
    /// </summary>
    private static string SlotText(ExpressionSyntax lhs)
        => lhs.ToString();
}
