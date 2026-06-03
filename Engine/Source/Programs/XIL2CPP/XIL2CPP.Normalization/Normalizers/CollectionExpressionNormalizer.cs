// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer that lowers C# 12 collection expressions
/// (<c>[a, b, c]</c>, including spread elements <c>..xs</c>) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.3 + Section 5.13 (Q10
/// resolution). Each <see cref="CollectionExpressionSyntax"/> is annotated --
/// not rewritten -- with a <see cref="CollectionExpressionAnnotation"/> that
/// records the resolved target (converted) type, the chosen lowering form,
/// and the ordered elements (distinguishing expression elements from spread
/// elements) so Pass 3 emit can pick the right container construction.
/// </summary>
/// <remarks>
/// <para>
/// The target type is the collection expression's <em>converted</em> type
/// (its target type), obtained from the authoritative Pass-1 semantic model
/// via <c>GetTypeInfo(node).ConvertedType</c>. The lowering form is chosen
/// from that target type:
/// <list type="bullet">
///   <item><description>
///     <c>List&lt;T&gt;</c> -&gt; <see cref="CollectionLoweringForm.ListInitializer"/>
///     (<c>new List&lt;T&gt; { ... }</c> / TArray construction).
///   </description></item>
///   <item><description>
///     <c>T[]</c> -&gt; <see cref="CollectionLoweringForm.ArrayInitializer"/>
///     (<c>new T[] { ... }</c>).
///   </description></item>
///   <item><description>
///     <c>ReadOnlySpan&lt;T&gt;</c> / <c>Span&lt;T&gt;</c> -&gt;
///     <see cref="CollectionLoweringForm.StackallocSpan"/> (a stackalloc-backed
///     span, per Q10 Section 5.13).
///   </description></item>
///   <item><description>
///     anything else (or an unresolved target, e.g. on ill-formed input) -&gt;
///     <see cref="CollectionLoweringForm.Unknown"/>.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Determinism.</b> Nodes are visited in document span order
/// (<c>SyntaxNode.DescendantNodes</c> is a stable pre-order walk) and
/// the parsed files are consulted in their canonical Pass-1 order, so two runs
/// over identical input annotate identically (gates X-IL2CPP-MANGLE-DET /
/// X-IL2CPP-CSPATH-DET, Section 9.9). The normalizer never throws on
/// well-formed input; an unresolved target type yields the
/// <see cref="CollectionLoweringForm.Unknown"/> form rather than an exception.
/// </para>
/// </remarks>
public sealed class CollectionExpressionNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Name => "CollectionExpressionNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        System.ArgumentNullException.ThrowIfNull(pass1);
        System.ArgumentNullException.ThrowIfNull(builder);

        // Walk the parsed files in their canonical (ordinal) Pass-1 order.
        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxNode root = file.Tree.GetRoot();

            // DescendantNodes is a stable pre-order span-ordered walk.
            List<CollectionExpressionSyntax> collectionExpressions =
                root.DescendantNodes().OfType<CollectionExpressionSyntax>().ToList();
            if (collectionExpressions.Count == 0)
            {
                continue;
            }

            SemanticModel model = pass1.GetSemanticModel(file.Tree);

            foreach (CollectionExpressionSyntax collectionExpression in collectionExpressions)
            {
                CollectionExpressionAnnotation annotation =
                    BuildAnnotation(collectionExpression, model);
                builder.AnnotateNode(collectionExpression, annotation);
            }
        }
    }

    private static CollectionExpressionAnnotation BuildAnnotation(
        CollectionExpressionSyntax node,
        SemanticModel model)
    {
        // The target type of a collection expression is its converted type
        // (the type it is being assigned/initialized into). Type (the natural
        // type) is null for a collection expression, so ConvertedType is the
        // authoritative target.
        TypeInfo typeInfo = model.GetTypeInfo(node);
        ITypeSymbol? targetType = typeInfo.ConvertedType;

        CollectionLoweringForm form = ChooseLoweringForm(targetType, out ITypeSymbol? elementType);

        // Record the elements in source order, distinguishing expression
        // elements (a single value) from spread elements (.. enumerable).
        List<CollectionElementInfo> elements = new(node.Elements.Count);
        foreach (CollectionElementSyntax element in node.Elements)
        {
            switch (element)
            {
                case SpreadElementSyntax spread:
                    elements.Add(new CollectionElementInfo(
                        CollectionElementKind.Spread, spread.Expression));
                    break;
                case ExpressionElementSyntax expressionElement:
                    elements.Add(new CollectionElementInfo(
                        CollectionElementKind.Expression, expressionElement.Expression));
                    break;
                default:
                    // Defensive: any future element kind is recorded as an
                    // expression element carrying the whole element node so the
                    // information is not silently dropped.
                    elements.Add(new CollectionElementInfo(
                        CollectionElementKind.Expression, element));
                    break;
            }
        }

        return new CollectionExpressionAnnotation(
            targetType,
            elementType,
            form,
            elements);
    }

    private static CollectionLoweringForm ChooseLoweringForm(
        ITypeSymbol? targetType,
        out ITypeSymbol? elementType)
    {
        elementType = null;
        if (targetType is null || targetType.TypeKind == TypeKind.Error)
        {
            return CollectionLoweringForm.Unknown;
        }

        // T[] -> array-initializer form.
        if (targetType is IArrayTypeSymbol arrayType)
        {
            elementType = arrayType.ElementType;
            return CollectionLoweringForm.ArrayInitializer;
        }

        if (targetType is INamedTypeSymbol named)
        {
            // Match by the constructed generic's original-definition special
            // name so we do not depend on the using-aliased display name.
            string originalDefinition = named.OriginalDefinition.ToDisplayString();
            switch (originalDefinition)
            {
                case "System.Collections.Generic.List<T>":
                    elementType = named.TypeArguments.Length == 1 ? named.TypeArguments[0] : null;
                    return CollectionLoweringForm.ListInitializer;

                case "System.ReadOnlySpan<T>":
                case "System.Span<T>":
                    elementType = named.TypeArguments.Length == 1 ? named.TypeArguments[0] : null;
                    return CollectionLoweringForm.StackallocSpan;
            }
        }

        return CollectionLoweringForm.Unknown;
    }
}

/// <summary>
/// The lowering form chosen for a collection expression, derived from its
/// target (converted) type per <c>/Documents/XIL2CPP.html</c> Rev 4 Section
/// 5.3 + 5.13.
/// </summary>
public enum CollectionLoweringForm
{
    /// <summary>
    /// The target type could not be resolved to a supported collection shape
    /// (e.g. ill-formed input, or an interface / custom collection-builder
    /// target outside the MVP-lowered set).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Target <c>List&lt;T&gt;</c>: lower to
    /// <c>new List&lt;T&gt; { ... }</c> (TArray Reserve + Add construction),
    /// Section 5.3.
    /// </summary>
    ListInitializer,

    /// <summary>
    /// Target <c>T[]</c>: lower to <c>new T[] { ... }</c>
    /// (<c>T tmp[N] = { ... }</c>), Section 5.3.
    /// </summary>
    ArrayInitializer,

    /// <summary>
    /// Target <c>ReadOnlySpan&lt;T&gt;</c> / <c>Span&lt;T&gt;</c>: lower to a
    /// stackalloc-backed span (a stack array wrapped in a span ctor) per the
    /// Q10 resolution, Section 5.13.
    /// </summary>
    StackallocSpan,
}

/// <summary>
/// Discriminates the two kinds of collection-expression element a Pass-2
/// annotation records.
/// </summary>
public enum CollectionElementKind
{
    /// <summary>A single-value expression element (e.g. <c>a</c> in <c>[a, b]</c>).</summary>
    Expression = 0,

    /// <summary>A spread element (e.g. <c>..xs</c> in <c>[a, ..xs, b]</c>).</summary>
    Spread,
}

/// <summary>
/// One ordered element of a lowered collection expression: its kind plus the
/// underlying expression (for a spread element, the enumerable being spread;
/// for an expression element, the value).
/// </summary>
public sealed class CollectionElementInfo
{
    /// <summary>
    /// Construct an element record.
    /// </summary>
    /// <param name="kind">Whether this is a single-value expression or a spread element.</param>
    /// <param name="expression">
    /// The underlying expression: the value for an expression element, or the
    /// spread source enumerable for a spread element. Must not be null.
    /// </param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="expression"/> is null.</exception>
    public CollectionElementInfo(CollectionElementKind kind, SyntaxNode expression)
    {
        System.ArgumentNullException.ThrowIfNull(expression);
        Kind = kind;
        Expression = expression;
    }

    /// <summary>Whether this is a single-value expression element or a spread element.</summary>
    public CollectionElementKind Kind { get; }

    /// <summary>
    /// The underlying expression: the value (expression element) or the spread
    /// source enumerable (spread element). For an expression element this is
    /// the <see cref="ExpressionElementSyntax.Expression"/>; for a spread
    /// element the <see cref="SpreadElementSyntax.Expression"/>.
    /// </summary>
    public SyntaxNode Expression { get; }
}

/// <summary>
/// The Pass-2 lowering decision recorded on each
/// <see cref="CollectionExpressionSyntax"/> by
/// <see cref="CollectionExpressionNormalizer"/>: the resolved target
/// (converted) type, the inferred element type, the chosen lowering form, and
/// the ordered elements (expression vs. spread).
/// </summary>
public sealed class CollectionExpressionAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct the annotation.
    /// </summary>
    /// <param name="targetType">
    /// The resolved target (converted) type of the collection expression, or
    /// null when it could not be resolved (ill-formed input).
    /// </param>
    /// <param name="elementType">
    /// The inferred element type (<c>T</c> of <c>List&lt;T&gt;</c> /
    /// <c>T[]</c> / <c>ReadOnlySpan&lt;T&gt;</c>), or null when not applicable.
    /// </param>
    /// <param name="form">The chosen lowering form.</param>
    /// <param name="elements">The ordered elements. Must not be null.</param>
    /// <exception cref="System.ArgumentNullException">If <paramref name="elements"/> is null.</exception>
    public CollectionExpressionAnnotation(
        ITypeSymbol? targetType,
        ITypeSymbol? elementType,
        CollectionLoweringForm form,
        IReadOnlyList<CollectionElementInfo> elements)
    {
        System.ArgumentNullException.ThrowIfNull(elements);
        TargetType = targetType;
        ElementType = elementType;
        Form = form;
        Elements = elements;
    }

    /// <inheritdoc />
    public override string Kind => "collection-expression";

    /// <summary>
    /// The resolved target (converted) type of the collection expression, or
    /// null when unresolved.
    /// </summary>
    public ITypeSymbol? TargetType { get; }

    /// <summary>
    /// The inferred element type (<c>T</c>), or null when not applicable.
    /// </summary>
    public ITypeSymbol? ElementType { get; }

    /// <summary>The chosen lowering form.</summary>
    public CollectionLoweringForm Form { get; }

    /// <summary>
    /// The ordered elements of the collection expression, distinguishing
    /// expression elements from spread elements, in source order.
    /// </summary>
    public IReadOnlyList<CollectionElementInfo> Elements { get; }
}
