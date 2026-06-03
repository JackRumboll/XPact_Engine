// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="TupleNameNormalizer"/> per /Documents/XIL2CPP.html
/// Rev 4 Section 5.7 (tuple literals + named elements). Each fixture builds a
/// Pass-1 result from synthetic source, runs JUST the tuple normalizer in
/// isolation via <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>,
/// then asserts the canonical positional index + <c>Item-N</c> name recorded on
/// the relevant nodes.
/// </summary>
public sealed class TupleNameNormalizerTests
{
    private static NormalizedUnit RunNormalizer(Pass1Result pass1)
        => Pass2Driver.Run(pass1, new INormalizer[] { new TupleNameNormalizer() });

    private static SyntaxNode Root(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot();

    // -----------------------------------------------------------------
    // Fixture 1: named-tuple field access (t.X / t.Y -> Item1 / Item2).
    // -----------------------------------------------------------------

    [Fact]
    public void NamedTupleFieldAccess_ResolvesFriendlyNamesToCanonicalItems()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Use()
                {
                    (int X, string Y) t = (5, "hi");
                    int a = t.X;
                    int b = t.Y.Length;
                    return a + b;
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        List<MemberAccessExpressionSyntax> accesses = Root(pass1)
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .ToList();

        MemberAccessExpressionSyntax accessX =
            accesses.Single(m => m.Name.Identifier.Text == "X");
        MemberAccessExpressionSyntax accessY =
            accesses.Single(m => m.Name.Identifier.Text == "Y");

        TupleNameAnnotation? annX = unit.GetAnnotation<TupleNameAnnotation>(accessX);
        Assert.NotNull(annX);
        Assert.Equal(1, annX!.PositionalIndex);
        Assert.Equal("Item1", annX.CanonicalMemberName);
        Assert.Equal("tuple-name", annX.Kind);

        TupleNameAnnotation? annY = unit.GetAnnotation<TupleNameAnnotation>(accessY);
        Assert.NotNull(annY);
        Assert.Equal(2, annY!.PositionalIndex);
        Assert.Equal("Item2", annY.CanonicalMemberName);

        // The ordinary (non-tuple) member access t.Y.Length must NOT be
        // annotated -- "Length" is a string property, not a tuple element.
        MemberAccessExpressionSyntax accessLength =
            accesses.Single(m => m.Name.Identifier.Text == "Length");
        Assert.Null(unit.GetAnnotation<TupleNameAnnotation>(accessLength));
    }

    [Fact]
    public void NamedTupleLiteral_AnnotatesEachNamedElementPositionally()
    {
        const string source = """
            namespace M;
            public class A
            {
                public (int X, string Y) Make() => (X: 5, Y: "hi");
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        TupleExpressionSyntax literal = Root(pass1)
            .DescendantNodes()
            .OfType<TupleExpressionSyntax>()
            .Single();

        ArgumentSyntax argX = literal.Arguments[0];
        ArgumentSyntax argY = literal.Arguments[1];

        TupleNameAnnotation? annX = unit.GetAnnotation<TupleNameAnnotation>(argX);
        Assert.NotNull(annX);
        Assert.Equal(1, annX!.PositionalIndex);
        Assert.Equal("Item1", annX.CanonicalMemberName);

        TupleNameAnnotation? annY = unit.GetAnnotation<TupleNameAnnotation>(argY);
        Assert.NotNull(annY);
        Assert.Equal(2, annY!.PositionalIndex);
        Assert.Equal("Item2", annY.CanonicalMemberName);
    }

    // -----------------------------------------------------------------
    // Fixture 2: tuple deconstruction (var (a, b) = t -> positional binds).
    // -----------------------------------------------------------------

    [Fact]
    public void TupleDeconstruction_AnnotatesEachTargetPositionally()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Use()
                {
                    (int X, string Y) t = (5, "hi");
                    var (a, b) = t;
                    return a + b.Length;
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        ParenthesizedVariableDesignationSyntax designation = Root(pass1)
            .DescendantNodes()
            .OfType<ParenthesizedVariableDesignationSyntax>()
            .Single();

        VariableDesignationSyntax targetA = designation.Variables[0];
        VariableDesignationSyntax targetB = designation.Variables[1];

        TupleNameAnnotation? annA = unit.GetAnnotation<TupleNameAnnotation>(targetA);
        Assert.NotNull(annA);
        Assert.Equal(1, annA!.PositionalIndex);
        Assert.Equal("Item1", annA.CanonicalMemberName);

        TupleNameAnnotation? annB = unit.GetAnnotation<TupleNameAnnotation>(targetB);
        Assert.NotNull(annB);
        Assert.Equal(2, annB!.PositionalIndex);
        Assert.Equal("Item2", annB.CanonicalMemberName);
    }

    // -----------------------------------------------------------------
    // Fixture 3: plain positional access (t.Item1 -> already canonical).
    // -----------------------------------------------------------------

    [Fact]
    public void PlainPositionalAccess_AnnotatesWithSameCanonicalName()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Use()
                {
                    (int, int) t = (5, 7);
                    int a = t.Item1;
                    int b = t.Item2;
                    return a + b;
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        List<MemberAccessExpressionSyntax> accesses = Root(pass1)
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .ToList();

        MemberAccessExpressionSyntax access1 =
            accesses.Single(m => m.Name.Identifier.Text == "Item1");
        MemberAccessExpressionSyntax access2 =
            accesses.Single(m => m.Name.Identifier.Text == "Item2");

        TupleNameAnnotation? ann1 = unit.GetAnnotation<TupleNameAnnotation>(access1);
        Assert.NotNull(ann1);
        Assert.Equal(1, ann1!.PositionalIndex);
        Assert.Equal("Item1", ann1.CanonicalMemberName);

        TupleNameAnnotation? ann2 = unit.GetAnnotation<TupleNameAnnotation>(access2);
        Assert.NotNull(ann2);
        Assert.Equal(2, ann2!.PositionalIndex);
        Assert.Equal("Item2", ann2.CanonicalMemberName);
    }

    // -----------------------------------------------------------------
    // Fixture 4: nested tuple (t.Inner.X -> Item2.Item1 positionally).
    // -----------------------------------------------------------------

    [Fact]
    public void NestedTuple_ResolvesOuterAndInnerNamesPositionally()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Use()
                {
                    (int Head, (int X, int Y) Inner) t = (1, (2, 3));
                    int outer = t.Head;
                    int inner = t.Inner.X;
                    int inner2 = t.Inner.Y;
                    return outer + inner + inner2;
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        List<MemberAccessExpressionSyntax> accesses = Root(pass1)
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .ToList();

        // Outer element "Head" is position 1.
        MemberAccessExpressionSyntax accessHead =
            accesses.Single(m => m.Name.Identifier.Text == "Head");
        TupleNameAnnotation? annHead = unit.GetAnnotation<TupleNameAnnotation>(accessHead);
        Assert.NotNull(annHead);
        Assert.Equal(1, annHead!.PositionalIndex);
        Assert.Equal("Item1", annHead.CanonicalMemberName);

        // Outer element "Inner" is position 2. It appears twice (t.Inner.X and
        // t.Inner.Y both contain a t.Inner access); every occurrence resolves
        // to the same canonical position.
        List<MemberAccessExpressionSyntax> accessInner =
            accesses.Where(m => m.Name.Identifier.Text == "Inner").ToList();
        Assert.Equal(2, accessInner.Count);
        foreach (MemberAccessExpressionSyntax inner in accessInner)
        {
            TupleNameAnnotation? annInner = unit.GetAnnotation<TupleNameAnnotation>(inner);
            Assert.NotNull(annInner);
            Assert.Equal(2, annInner!.PositionalIndex);
            Assert.Equal("Item2", annInner.CanonicalMemberName);
        }

        // Inner element "X" is position 1 inside the nested tuple.
        MemberAccessExpressionSyntax accessX =
            accesses.Single(m => m.Name.Identifier.Text == "X");
        TupleNameAnnotation? annX = unit.GetAnnotation<TupleNameAnnotation>(accessX);
        Assert.NotNull(annX);
        Assert.Equal(1, annX!.PositionalIndex);
        Assert.Equal("Item1", annX.CanonicalMemberName);

        // Inner element "Y" is position 2 inside the nested tuple.
        MemberAccessExpressionSyntax accessY =
            accesses.Single(m => m.Name.Identifier.Text == "Y");
        TupleNameAnnotation? annY = unit.GetAnnotation<TupleNameAnnotation>(accessY);
        Assert.NotNull(annY);
        Assert.Equal(2, annY!.PositionalIndex);
        Assert.Equal("Item2", annY.CanonicalMemberName);
    }

    // -----------------------------------------------------------------
    // Determinism + isolation guards.
    // -----------------------------------------------------------------

    [Fact]
    public void Normalizer_IsDeterministic_AcrossRepeatedRuns()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Use()
                {
                    (int X, int Y) t = (5, 7);
                    return t.X + t.Y;
                }
            }
            """;

        Pass1Result pass1a = NormalizationTestHelpers.BuildPass1(source);
        Pass1Result pass1b = NormalizationTestHelpers.BuildPass1(source);

        NormalizedUnit unitA = RunNormalizer(pass1a);
        NormalizedUnit unitB = RunNormalizer(pass1b);

        List<(int, string)> a = CollectMemberAccessAnnotations(pass1a, unitA);
        List<(int, string)> b = CollectMemberAccessAnnotations(pass1b, unitB);

        Assert.Equal(a, b);
        Assert.Equal(new[] { (1, "Item1"), (2, "Item2") }, a);
    }

    [Fact]
    public void NonTupleMemberAccess_IsNeverAnnotated()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int X;
                public int Use()
                {
                    A obj = this;
                    return obj.X;
                }
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = RunNormalizer(pass1);

        foreach (MemberAccessExpressionSyntax access in Root(pass1)
                     .DescendantNodes()
                     .OfType<MemberAccessExpressionSyntax>())
        {
            Assert.Null(unit.GetAnnotation<TupleNameAnnotation>(access));
        }
    }

    private static List<(int Index, string Name)> CollectMemberAccessAnnotations(
        Pass1Result pass1, NormalizedUnit unit)
    {
        List<(int, string)> result = new();
        foreach (MemberAccessExpressionSyntax access in Root(pass1)
                     .DescendantNodes()
                     .OfType<MemberAccessExpressionSyntax>())
        {
            TupleNameAnnotation? ann = unit.GetAnnotation<TupleNameAnnotation>(access);
            if (ann is not null)
            {
                result.Add((ann.PositionalIndex, ann.CanonicalMemberName));
            }
        }
        return result;
    }
}
