// Copyright Simgenics. All Rights Reserved.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# member access (<see cref="MemberAccessExpressionSyntax"/>) that
/// resolves to a field or a property, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.2:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///     <b>Field access.</b> A reference-typed receiver lowers to the C++ arrow
///     form <c>recv-&gt;member</c>; a value-typed receiver lowers to the C++ dot
///     form <c>recv.member</c> (the receiver pointer-ness mirrors the receiver
///     type's C# reference / value classification).
///   </description></item>
///   <item><description>
///     <b>Property access.</b> A property read maps to a call of the property's
///     <c>get_</c> accessor as a free function with the receiver threaded as the
///     explicit <c>self</c> argument (Contract Section 2.3 free-function form):
///     <c>&lt;get_LinkerSymbol&gt;(&lt;recv&gt;)</c>. A static property omits the
///     receiver.
///   </description></item>
/// </list>
/// <para>
/// This rule lowers a member access dispatched DIRECTLY (a read of a field /
/// property value). The receiver-as-<c>self</c> threading for a method CALL
/// (<c>obj.Method(...)</c>) is owned by <see cref="InvocationLoweringRule"/>,
/// which lowers the receiver itself and never re-dispatches the
/// member-access-of-the-callee through this rule.
/// </para>
/// <para>
/// <b>Missing-mangling fallback.</b> When a property's getter has no Pass-5
/// mangling row the rule emits the deliberate <c>// TODO(6.e)</c> gap marker
/// rather than a wrong symbol.
/// </para>
/// </remarks>
public sealed class MemberAccessLoweringRule : IBodyLoweringRule
{
    /// <summary>The C++ arrow member-access operator for a reference-typed receiver.</summary>
    public const string ArrowOperator = "->";

    /// <summary>The C++ dot member-access operator for a value-typed receiver.</summary>
    public const string DotOperator = ".";

    /// <inheritdoc/>
    public string Name => "MemberAccessLowering";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is MemberAccessExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var memberAccess = (MemberAccessExpressionSyntax)node;
        SemanticModel model = context.GetSemanticModel(memberAccess.SyntaxTree);

        ISymbol? member = model.GetSymbolInfo(memberAccess).Symbol;
        switch (member)
        {
            case IFieldSymbol field:
                EmitField(memberAccess, field, model, writer, parent);
                return;
            case IPropertySymbol property:
                EmitProperty(memberAccess, property, context, writer, parent);
                return;
            default:
                parent.EmitTodo(node);
                return;
        }
    }

    private static void EmitField(
        MemberAccessExpressionSyntax memberAccess,
        IFieldSymbol field,
        SemanticModel model,
        CppWriter writer,
        StatementEmitter parent)
    {
        // Static field: no receiver pointer-ness applies; emit the field name.
        // (A qualified static field name is lowered by a later type-reference
        // rule; here the deterministic, gap-visible form is the field name.)
        if (field.IsStatic)
        {
            writer.Append(field.Name);
            return;
        }

        // Lower the receiver, then choose arrow (reference-typed receiver) or
        // dot (value-typed receiver), then the C++ member name.
        parent.Expressions.EmitExpression(memberAccess.Expression);
        writer.Append(IsReferenceReceiver(memberAccess.Expression, model) ? ArrowOperator : DotOperator);
        writer.Append(field.Name);
    }

    private static void EmitProperty(
        MemberAccessExpressionSyntax memberAccess,
        IPropertySymbol property,
        EmitContext context,
        CppWriter writer,
        StatementEmitter parent)
    {
        IMethodSymbol? getter = property.GetMethod;
        if (getter is null)
        {
            parent.EmitTodo(memberAccess);
            return;
        }

        StableId id = StableId.FromSymbol(getter);
        ManglingRecord? mangling = context.FindMangling(id);
        if (mangling is null)
        {
            parent.EmitTodo(memberAccess);
            return;
        }

        // <get_LinkerSymbol>(<recv>) -- the receiver is the explicit `self`
        // first argument for an instance property; a static property omits it.
        writer.Append(mangling.Value.LinkerSymbol);
        writer.Append("(");
        if (!getter.IsStatic)
        {
            parent.Expressions.EmitExpression(memberAccess.Expression);
        }
        writer.Append(")");
    }

    private static bool IsReferenceReceiver(ExpressionSyntax receiver, SemanticModel model)
    {
        ITypeSymbol? receiverType = model.GetTypeInfo(receiver).Type;

        // Unknown receiver type: default to the arrow form. The XObject /
        // reference-type case is the dominant one for member access in the
        // transpiled surface, and the conservative choice keeps an unresolved
        // receiver from silently emitting value semantics.
        if (receiverType is null)
        {
            return true;
        }

        // A type parameter without a value-type constraint is treated as a
        // reference type (the common XObject generic case), matching the
        // mangler's IsReferenceMangleType discipline.
        if (receiverType.TypeKind == TypeKind.TypeParameter)
        {
            return !receiverType.IsValueType;
        }

        return receiverType.IsReferenceType;
    }
}
