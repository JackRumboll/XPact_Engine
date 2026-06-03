// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer that lowers C# record <c>with</c>-expressions per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.4 (line 416): a
/// <c>rec with { X = v, ... }</c> expression lowers to the record's copy
/// constructor (the synthesized <c>&lt;Clone&gt;$()</c>) invocation followed
/// by per-property setter assignments to the clone, one per object-member
/// in the <c>with</c> initializer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotate, do not rewrite.</b> Following the Pass-2 contract, this
/// normalizer does NOT mutate the Pass-1 syntax trees. It records its
/// lowering decision additively as a <see cref="RecordWithAnnotation"/>
/// keyed on the original <see cref="WithExpressionSyntax"/> node so Pass 3
/// (write-barrier / reference-store enumeration, Section 5.4 lowered-form
/// walk) and Pass 6 (emit) can read the resolved target record type plus the
/// ordered <c>(property, value-expression)</c> mutation list. The Pass-1
/// compilation + semantic models stay authoritative.
/// </para>
/// <para>
/// <b>Determinism.</b> Trees are visited in the Pass-1 parsed-file canonical
/// (ordinal) order and, within each tree, in document order
/// (<c>SyntaxNode.DescendantNodes</c> is depth-first source order).
/// The mutation list for one expression preserves the source order of the
/// <c>with</c> initializer members. No ambient state, hashing,
/// <c>DateTime</c>, or <c>Random</c> is consulted (gates
/// X-IL2CPP-MANGLE-DET / X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// <para>
/// <b>Nesting.</b> A nested <c>with</c>-expression (the value-expression of
/// an outer mutation is itself a <see cref="WithExpressionSyntax"/>) is a
/// distinct syntax node and is annotated independently. The outer
/// annotation's mutation simply references the inner expression node verbatim
/// as its value; the inner node carries its own annotation.
/// </para>
/// </remarks>
public sealed class RecordWithExpressionNormalizer : INormalizer
{
    /// <inheritdoc/>
    public string Name => "RecordWithExpressionNormalizer";

    /// <inheritdoc/>
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        System.ArgumentNullException.ThrowIfNull(pass1);
        System.ArgumentNullException.ThrowIfNull(builder);

        // Visit files in canonical (ordinal) order; nodes within each tree in
        // document order. This keeps the recorded-annotation order stable.
        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (node is WithExpressionSyntax withExpression)
                {
                    AnnotateWithExpression(withExpression, model, builder);
                }
            }
        }
    }

    /// <summary>
    /// Resolve and record the lowering of a single <c>with</c>-expression.
    /// Never throws on well-formed input; an unresolvable target type or
    /// member binding (e.g. an error type in a malformed tree) simply means
    /// the node is left un-annotated for a later pass / diagnostic to handle.
    /// </summary>
    private static void AnnotateWithExpression(
        WithExpressionSyntax withExpression,
        SemanticModel model,
        NormalizedUnitBuilder builder)
    {
        // The target record type is the static type of the operand
        // expression (the receiver being copied). Per /Documents/XIL2CPP.html
        // line 1572 the copy is produced by the operand's synthesized
        // <Clone>$(); the operand's type is the record type to clone.
        if (model.GetTypeInfo(withExpression.Expression).Type is not INamedTypeSymbol targetType)
        {
            return;
        }

        List<RecordWithMutation> mutations = new();

        // InitializerExpressionSyntax.Expressions are the with-object members
        // in source order. Each well-formed member is an
        // AssignmentExpressionSyntax: Left = the property identifier, Right =
        // the value-expression assigned to the clone via the (init-)setter.
        if (withExpression.Initializer is { } initializer)
        {
            foreach (ExpressionSyntax memberExpression in initializer.Expressions)
            {
                if (memberExpression is not AssignmentExpressionSyntax assignment)
                {
                    // Malformed member; skip it rather than throw. Other
                    // members of this with-expression are still lowered.
                    continue;
                }

                IPropertySymbol? property =
                    model.GetSymbolInfo(assignment.Left).Symbol as IPropertySymbol;

                mutations.Add(new RecordWithMutation(
                    property,
                    assignment.Left,
                    assignment.Right,
                    property?.Type));
            }
        }

        builder.AnnotateNode(
            withExpression,
            new RecordWithAnnotation(targetType, mutations.AsReadOnly()));
    }
}

/// <summary>
/// The Pass-2 lowering decision recorded on a record <c>with</c>-expression:
/// the target record type to copy-construct and the ordered list of
/// property-setter mutations applied to the copy.
/// </summary>
public sealed class RecordWithAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct the annotation.
    /// </summary>
    /// <param name="targetType">The resolved record type being copied (the operand's static type). Must not be null.</param>
    /// <param name="mutations">The ordered <c>(property, value)</c> mutations applied to the copy, in source order. Must not be null.</param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="targetType"/> or <paramref name="mutations"/> is null.</exception>
    public RecordWithAnnotation(
        INamedTypeSymbol targetType,
        IReadOnlyList<RecordWithMutation> mutations)
    {
        System.ArgumentNullException.ThrowIfNull(targetType);
        System.ArgumentNullException.ThrowIfNull(mutations);

        TargetType = targetType;
        Mutations = mutations;
    }

    /// <inheritdoc/>
    public override string Kind => "record-with-expression";

    /// <summary>
    /// The record type the <c>with</c>-expression copies: the static type of
    /// the operand expression, which owns the synthesized copy constructor
    /// (<c>&lt;Clone&gt;$()</c>) the lowering invokes.
    /// </summary>
    public INamedTypeSymbol TargetType { get; }

    /// <summary>
    /// The ordered property-setter mutations applied to the copy, in the
    /// source order of the <c>with</c> initializer's members.
    /// </summary>
    public IReadOnlyList<RecordWithMutation> Mutations { get; }
}

/// <summary>
/// One <c>(property, value-expression)</c> mutation a record
/// <c>with</c>-expression applies to its copy: the resolved property the
/// member targets, the original left/right syntax, and the property's type.
/// </summary>
public sealed class RecordWithMutation
{
    /// <summary>
    /// Construct a mutation record.
    /// </summary>
    /// <param name="property">
    /// The resolved property the member assigns to, or null when the member's
    /// left-hand side does not bind to a property (malformed source; left
    /// un-resolved rather than throwing).
    /// </param>
    /// <param name="propertyExpression">The original left-hand-side property-name syntax. Must not be null.</param>
    /// <param name="valueExpression">The original right-hand-side value-expression assigned to the property. Must not be null.</param>
    /// <param name="propertyType">The property's type, or null when <paramref name="property"/> is null.</param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="propertyExpression"/> or <paramref name="valueExpression"/> is null.</exception>
    public RecordWithMutation(
        IPropertySymbol? property,
        ExpressionSyntax propertyExpression,
        ExpressionSyntax valueExpression,
        ITypeSymbol? propertyType)
    {
        System.ArgumentNullException.ThrowIfNull(propertyExpression);
        System.ArgumentNullException.ThrowIfNull(valueExpression);

        Property = property;
        PropertyExpression = propertyExpression;
        ValueExpression = valueExpression;
        PropertyType = propertyType;
    }

    /// <summary>
    /// The resolved property the mutation targets (its setter / init-accessor
    /// is invoked on the copy), or null when the left-hand side did not bind
    /// to a property.
    /// </summary>
    public IPropertySymbol? Property { get; }

    /// <summary>
    /// The original left-hand-side property-name syntax from the <c>with</c>
    /// initializer member.
    /// </summary>
    public ExpressionSyntax PropertyExpression { get; }

    /// <summary>
    /// The original right-hand-side value-expression assigned to the property
    /// on the copy. For a nested <c>with</c>-expression this is itself a
    /// <see cref="WithExpressionSyntax"/> that carries its own annotation.
    /// </summary>
    public ExpressionSyntax ValueExpression { get; }

    /// <summary>
    /// The property's type (so a later pass can tell, e.g., an XObject-typed
    /// mutation that needs a write barrier from a value-typed one), or null
    /// when <see cref="Property"/> is null.
    /// </summary>
    public ITypeSymbol? PropertyType { get; }
}
