// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="ExpressionBodyNormalizer"/> (WU-3) per
/// /Documents/XIL2CPP.html Rev 4 Section 3.2 (expression-bodied members).
/// Each fixture binds a real Pass-1 result, runs JUST this normalizer in
/// isolation via the explicit-set <see cref="Pass2Driver.Run(Pass1Result,
/// System.Collections.Generic.IReadOnlyList{INormalizer})"/> overload, and
/// asserts the <see cref="ExpressionBodyAnnotation"/> recorded on the
/// member's <see cref="ArrowExpressionClauseSyntax"/>.
/// </summary>
public sealed class ExpressionBodyNormalizerTests
{
    private static NormalizedUnit Run(Pass1Result pass1)
        => Pass2Driver.Run(pass1, new INormalizer[] { new ExpressionBodyNormalizer() });

    /// <summary>The single arrow clause in the first parsed file.</summary>
    private static ArrowExpressionClauseSyntax SingleArrow(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<ArrowExpressionClauseSyntax>().Single();

    [Fact]
    public void ExpressionBodiedMethod_Value_AnnotatedAsReturningMethod()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { public int Foo() => 42; }");

        NormalizedUnit unit = Run(pass1);
        ArrowExpressionClauseSyntax arrow = SingleArrow(pass1);

        ExpressionBodyAnnotation? ann = unit.GetAnnotation<ExpressionBodyAnnotation>(arrow);
        Assert.NotNull(ann);
        Assert.Equal("expression-body", ann!.Kind);
        Assert.Equal(ExpressionBodyMemberKind.Method, ann.MemberKind);
        Assert.False(ann.IsVoid);
    }

    [Fact]
    public void VoidExpressionBodiedMethod_AnnotatedAsVoidMethod()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { private int _n; public void Bar() => _n++; }");

        NormalizedUnit unit = Run(pass1);
        ArrowExpressionClauseSyntax arrow = SingleArrow(pass1);

        ExpressionBodyAnnotation? ann = unit.GetAnnotation<ExpressionBodyAnnotation>(arrow);
        Assert.NotNull(ann);
        Assert.Equal(ExpressionBodyMemberKind.Method, ann!.MemberKind);
        Assert.True(ann.IsVoid);
    }

    [Fact]
    public void ExpressionBodiedPropertyGetter_AnnotatedAsReturningGetter()
    {
        // The `P => expr;` shorthand is the implicit getter.
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { private int _n; public int P => _n; }");

        NormalizedUnit unit = Run(pass1);
        ArrowExpressionClauseSyntax arrow = SingleArrow(pass1);

        ExpressionBodyAnnotation? ann = unit.GetAnnotation<ExpressionBodyAnnotation>(arrow);
        Assert.NotNull(ann);
        Assert.Equal(ExpressionBodyMemberKind.Getter, ann!.MemberKind);
        Assert.False(ann.IsVoid);
    }

    [Fact]
    public void ExpressionBodiedPropertySetter_AnnotatedAsVoidSetter()
    {
        // `set => ...` is an explicit set accessor; the lowered body is an
        // expression-statement (void), not a return.
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { private int _n; public int P { get { return _n; } set => _n = value; } }");

        NormalizedUnit unit = Run(pass1);

        // Two members carry a `get { ... }` block (no arrow) and a `set => ...`
        // arrow; SingleArrow picks the lone arrow clause (the setter).
        ArrowExpressionClauseSyntax arrow = SingleArrow(pass1);
        AccessorDeclarationSyntax accessor = Assert.IsType<AccessorDeclarationSyntax>(arrow.Parent);
        Assert.Equal(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration, accessor.Kind());

        ExpressionBodyAnnotation? ann = unit.GetAnnotation<ExpressionBodyAnnotation>(arrow);
        Assert.NotNull(ann);
        Assert.Equal(ExpressionBodyMemberKind.Setter, ann!.MemberKind);
        Assert.True(ann.IsVoid);
    }

    [Fact]
    public void ExpressionBodiedOperator_AnnotatedAsReturningOperator()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { public static A operator +(A x, A y) => x; }");

        NormalizedUnit unit = Run(pass1);
        ArrowExpressionClauseSyntax arrow = SingleArrow(pass1);

        ExpressionBodyAnnotation? ann = unit.GetAnnotation<ExpressionBodyAnnotation>(arrow);
        Assert.NotNull(ann);
        Assert.Equal(ExpressionBodyMemberKind.Operator, ann!.MemberKind);
        Assert.False(ann.IsVoid);
    }

    [Fact]
    public void ExpressionBodiedIndexer_AnnotatedAsReturningIndexer()
    {
        // The `this[...] => expr;` shorthand is the implicit indexer getter.
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { public int this[int i] => i; }");

        NormalizedUnit unit = Run(pass1);
        ArrowExpressionClauseSyntax arrow = SingleArrow(pass1);

        ExpressionBodyAnnotation? ann = unit.GetAnnotation<ExpressionBodyAnnotation>(arrow);
        Assert.NotNull(ann);
        Assert.Equal(ExpressionBodyMemberKind.Indexer, ann!.MemberKind);
        Assert.False(ann.IsVoid);
    }
}
