// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer that lowers expression-bodied members to their
/// block-bodied equivalents per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2 ("Expression-bodied members"):
/// <c>int Foo() =&gt; 42;</c> lowers to <c>int Foo() { return 42; }</c> and the
/// void / setter forms <c>void Bar() =&gt; Side();</c> lower to
/// <c>void Bar() { Side(); }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotate, do not rewrite.</b> Per the Section 3.1 + 3.2 hybrid design
/// this normalizer records its lowering decision as an additive
/// <see cref="ExpressionBodyAnnotation"/> keyed on the original
/// <see cref="ArrowExpressionClauseSyntax"/>; it never mutates the Pass-1
/// compilation or trees. A later emit pass reads the annotation to decide
/// whether to wrap the arrow expression in <c>{ return expr; }</c> (a
/// value-returning member) or <c>{ expr; }</c> (a void member /
/// property-or-indexer set accessor).
/// </para>
/// <para>
/// <b>Member kinds.</b> An <see cref="ArrowExpressionClauseSyntax"/> is the
/// body of one of: a method, a property get (the <c>P =&gt; ...</c>
/// shorthand or an explicit <c>get =&gt; ...</c> accessor), a property set
/// (<c>set =&gt; ...</c> / <c>init =&gt; ...</c>), an operator (a binary /
/// unary <c>operator</c> or a user-defined conversion), or an indexer
/// (the <c>this[...] =&gt; ...</c> shorthand or an explicit indexer
/// accessor). The annotation records which.
/// </para>
/// <para>
/// <b>Void vs value-returning.</b> The lowered body returns a value
/// (<c>return expr;</c>) when the member yields a value -- a property /
/// indexer getter, an operator, or a non-<c>void</c> method. It is an
/// expression-statement (<c>expr;</c>) when the member yields nothing -- a
/// <c>set</c> / <c>init</c> accessor or a <c>void</c> method. The
/// non-<c>void</c> decision for methods + operators is taken from the
/// authoritative Pass-1 semantic model (<see cref="IMethodSymbol.ReturnsVoid"/>),
/// not from syntax, so a <c>void</c> alias or an error-typed return binds
/// correctly.
/// </para>
/// <para>
/// <b>Determinism.</b> Nodes are visited in source order (the
/// <see cref="SyntaxNode.DescendantNodes(System.Func{SyntaxNode, bool}, bool)"/>
/// document order) across the parsed files in their canonical
/// <see cref="Pass1Result.ParsedFiles"/> order, with no dependence on
/// ambient hash ordering, <c>DateTime</c>, or <c>Random</c> (gates
/// X-IL2CPP-MANGLE-DET / X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// </remarks>
public sealed class ExpressionBodyNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Name => "ExpressionBodyNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        System.ArgumentNullException.ThrowIfNull(pass1);
        System.ArgumentNullException.ThrowIfNull(builder);

        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxNode root = file.Tree.GetRoot();
            SemanticModel? model = null;

            // DescendantNodes yields nodes in document (source) order, so the
            // recorded annotation order is itself deterministic.
            foreach (ArrowExpressionClauseSyntax arrow in
                root.DescendantNodes().OfType<ArrowExpressionClauseSyntax>())
            {
                if (!TryClassify(arrow, out ExpressionBodyMemberKind kind))
                {
                    // Not one of the recognised member forms (defensive: the
                    // C# grammar only attaches an arrow clause to the handled
                    // declarations, but never throw on unexpected shapes).
                    continue;
                }

                // The semantic model is only needed for method / operator
                // void-ness; build it lazily once per file.
                model ??= pass1.GetSemanticModel(file.Tree);
                bool isVoid = IsVoidBody(arrow, kind, model);

                builder.AnnotateNode(arrow, new ExpressionBodyAnnotation(kind, isVoid));
            }
        }
    }

    /// <summary>
    /// Classify the member an <see cref="ArrowExpressionClauseSyntax"/> is the
    /// expression body of, from its parent syntax. Returns false for the
    /// (grammatically impossible) case of an arrow clause not owned by a
    /// recognised member.
    /// </summary>
    private static bool TryClassify(ArrowExpressionClauseSyntax arrow, out ExpressionBodyMemberKind kind)
    {
        switch (arrow.Parent)
        {
            case MethodDeclarationSyntax:
                kind = ExpressionBodyMemberKind.Method;
                return true;

            // `int P => expr;` and `int this[int i] => expr;` shorthand: the
            // arrow clause hangs directly off the property / indexer
            // declaration and is the implicit getter.
            case PropertyDeclarationSyntax:
                kind = ExpressionBodyMemberKind.Getter;
                return true;

            case IndexerDeclarationSyntax:
                kind = ExpressionBodyMemberKind.Indexer;
                return true;

            // `operator +(...) => expr;` and `operator T(...) => expr;`
            // (user-defined conversion).
            case OperatorDeclarationSyntax:
            case ConversionOperatorDeclarationSyntax:
                kind = ExpressionBodyMemberKind.Operator;
                return true;

            // Explicit accessor: `get => ...`, `set => ...`, `init => ...`.
            // For an accessor that belongs to an indexer the kind is reported
            // as the accessor role (Getter / Setter); the indexer shorthand
            // (no accessor list) is reported as Indexer above.
            case AccessorDeclarationSyntax accessor:
                kind = accessor.Kind() switch
                {
                    SyntaxKind.GetAccessorDeclaration => ExpressionBodyMemberKind.Getter,
                    SyntaxKind.SetAccessorDeclaration => ExpressionBodyMemberKind.Setter,
                    SyntaxKind.InitAccessorDeclaration => ExpressionBodyMemberKind.Setter,
                    _ => ExpressionBodyMemberKind.Getter,
                };
                return true;

            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// Decide whether the lowered block body is an expression-statement
    /// (<c>expr;</c>, the void form) or a return statement
    /// (<c>return expr;</c>, the value form).
    /// </summary>
    private static bool IsVoidBody(
        ArrowExpressionClauseSyntax arrow,
        ExpressionBodyMemberKind kind,
        SemanticModel model)
    {
        switch (kind)
        {
            // A set / init accessor never yields a value.
            case ExpressionBodyMemberKind.Setter:
                return true;

            // A getter / indexer / operator always yields a value.
            case ExpressionBodyMemberKind.Getter:
            case ExpressionBodyMemberKind.Indexer:
            case ExpressionBodyMemberKind.Operator:
                return false;

            // A method is void iff its return type binds to void. Take this
            // from the authoritative semantic model rather than syntax.
            case ExpressionBodyMemberKind.Method:
            default:
                return model.GetDeclaredSymbol(arrow.Parent!) is IMethodSymbol method
                    && method.ReturnsVoid;
        }
    }
}

/// <summary>
/// The kind of member an expression body was lowered for. Drives the
/// block-body shape a later emit pass synthesizes.
/// </summary>
public enum ExpressionBodyMemberKind
{
    /// <summary>A method body: <c>T Foo() =&gt; expr;</c>.</summary>
    Method,

    /// <summary>
    /// A property get -- the <c>P =&gt; expr;</c> shorthand or an explicit
    /// <c>get =&gt; expr;</c> accessor (property or indexer).
    /// </summary>
    Getter,

    /// <summary>A property / indexer set or init accessor: <c>set =&gt; expr;</c>.</summary>
    Setter,

    /// <summary>A user-defined operator or conversion: <c>operator +(...) =&gt; expr;</c>.</summary>
    Operator,

    /// <summary>The indexer shorthand getter: <c>T this[...] =&gt; expr;</c>.</summary>
    Indexer,
}

/// <summary>
/// The per-node lowering decision for one expression-bodied member, recorded
/// on its <see cref="ArrowExpressionClauseSyntax"/>. Records the member
/// <see cref="MemberKind"/> and whether the lowered block body is the void
/// expression-statement form (<see cref="IsVoid"/> true) or the
/// value-returning <c>return expr;</c> form (false), per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2.
/// </summary>
public sealed class ExpressionBodyAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct an expression-body lowering annotation.
    /// </summary>
    /// <param name="memberKind">The member the expression body belongs to.</param>
    /// <param name="isVoid">
    /// True iff the lowered body is an expression-statement (<c>expr;</c>) --
    /// a set / init accessor or a void method. False iff the body returns a
    /// value (<c>return expr;</c>).
    /// </param>
    public ExpressionBodyAnnotation(ExpressionBodyMemberKind memberKind, bool isVoid)
    {
        MemberKind = memberKind;
        IsVoid = isVoid;
    }

    /// <inheritdoc />
    public override string Kind => "expression-body";

    /// <summary>The member the expression body belongs to.</summary>
    public ExpressionBodyMemberKind MemberKind { get; }

    /// <summary>
    /// True iff the lowered block body is an expression-statement
    /// (<c>expr;</c>) -- the member yields no value (a set / init accessor or
    /// a void method). False iff the lowered body is <c>return expr;</c>.
    /// </summary>
    public bool IsVoid { get; }
}
