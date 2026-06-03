// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;

/// <summary>
/// Body-lowering rule (WU-D1 / WU-6H) for a C# assignment expression, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Sections 5.3 + 6.3:
/// a non-reference LHS lowers to <c>&lt;lhs&gt; = &lt;rhs&gt;;</c>; an LHS that
/// resolves to an XObject-derived reference field / property slot lowers to the
/// Phase 6.h write barrier
/// <c>XPACT_GC_STORE(&lt;parent&gt;, &amp;(&lt;slot&gt;), &lt;value&gt;);</c>
/// INSTEAD of the plain assignment (the macro performs the store itself, so
/// emitting the plain <c>lhs = rhs;</c> too would double-write). Both the LHS
/// and the RHS are lowered by recursing into the parent expression emitter, so
/// this rule never re-implements operand lowering.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference-store recognition.</b> The LHS is a reference store iff its
/// bound symbol is a field or property whose type is XObject-derived (the same
/// test the Pass-3 <see cref="Analysis.ReferenceStoreAnalyzer"/> uses, via
/// <see cref="Analysis.AnalyzerHelpers.IsXObjectDerived(INamedTypeSymbol?)"/>).
/// A value-typed slot, a local, or a parameter is NOT a reference store and
/// lowers to the plain assignment with no barrier.
/// </para>
/// <para>
/// <b>The write barrier (Phase 6.h, gate X-IL2CPP-BARRIER-EMIT).</b> An
/// XObject reference store emits
/// <c>XPACT_GC_STORE(parent_obj, &amp;(slot), new_value);</c> per Section 6.3
/// (the doc's <c>XPACT_GC_STORE(actor, &amp;actor-&gt;Owner, newOwner)</c>
/// example). The macro both records the store with the collector AND performs
/// the slot write, so the plain <c>slot = value;</c> assignment is SUPPRESSED
/// at a reference store -- emitting it too would write the slot twice. The
/// three operands are:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>parent_obj</b> -- the directly-enclosing object whose slot is written:
///     the lowered receiver of a member-access LHS (<c>h.Slot = a</c> -&gt;
///     <c>h</c>), or the instance receiver token <see cref="SelfToken"/> when
///     the slot is written through a bare identifier (an implicit-<c>this</c>
///     instance member, <c>Slot = a</c> -&gt; <c>self</c>).
///   </description></item>
///   <item><description>
///     <b>&amp;(slot)</b> -- the address of the lowered LHS slot expression
///     (<c>&amp;(h-&gt;Slot)</c> / <c>&amp;(self-&gt;Slot)</c>). The parentheses
///     keep the address-of binding tight around the whole lowered slot.
///   </description></item>
///   <item><description>
///     <b>new_value</b> -- the lowered RHS expression.
///   </description></item>
/// </list>
/// <para>
/// All three operands are lowered through the shared expression emitter, so the
/// barrier carries real C++ (a compilable <c>self-&gt;Slot</c> / call form),
/// never raw C# syntax text.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Operand lowering is delegated
/// to the shared expression emitter (a pure function of the bound tree); the
/// only literal fragments are the fixed macro name + punctuation; no ambient
/// state, <see cref="DateTime"/>, <see cref="Guid"/>, or culture-sensitive
/// formatting.
/// </para>
/// </remarks>
public sealed class AssignmentLoweringRule : IBodyLoweringRule
{
    /// <summary>The Phase 6.h write-barrier macro this rule emits at an XObject reference store.</summary>
    public const string WriteBarrierMacro = "XPACT_GC_STORE";

    /// <summary>
    /// The instance receiver token used as <c>parent_obj</c> for an
    /// implicit-<c>this</c> reference store (matches the
    /// <c>self</c> token the identifier-lowering rule + method emitter thread
    /// the free-function receiver under).
    /// </summary>
    public const string SelfToken = "self";

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
            EmitWriteBarrier(lhs, rhs, writer, parent);
            return;
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
    /// Emit the Phase 6.h write barrier for an XObject reference store:
    /// <c>XPACT_GC_STORE(&lt;parent&gt;, &amp;(&lt;slot&gt;), &lt;value&gt;);</c>.
    /// The macro performs the store, so NO plain assignment follows (no
    /// double-write). The parent, slot, and value are lowered through the shared
    /// expression emitter.
    /// </summary>
    private static void EmitWriteBarrier(
        ExpressionSyntax lhs,
        ExpressionSyntax rhs,
        CppWriter writer,
        StatementEmitter parent)
    {
        writer.Append(WriteBarrierMacro);
        writer.Append("(");

        // parent_obj: the lowered receiver of a member-access LHS, or the `self`
        // instance receiver for a bare-identifier (implicit-this) slot.
        EmitParent(lhs, writer, parent);

        // &(slot): the address of the lowered LHS slot expression.
        writer.Append(", &(");
        parent.Expressions.EmitExpression(lhs);
        writer.Append("), ");

        // new_value: the lowered RHS.
        parent.Expressions.EmitExpression(rhs);

        writer.Append(");");
        writer.AppendLine();
    }

    /// <summary>
    /// Emit the <c>parent_obj</c> operand: the lowered receiver of a
    /// member-access LHS (<c>h.Slot</c> -&gt; lowered <c>h</c>), or the
    /// <see cref="SelfToken"/> instance receiver when the slot is written
    /// through a bare identifier (an implicit-<c>this</c> instance member).
    /// </summary>
    private static void EmitParent(ExpressionSyntax lhs, CppWriter writer, StatementEmitter parent)
    {
        if (lhs is MemberAccessExpressionSyntax memberAccess)
        {
            parent.Expressions.EmitExpression(memberAccess.Expression);
            return;
        }

        writer.Append(SelfToken);
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
}
