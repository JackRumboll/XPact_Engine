// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="RecordWithExpressionNormalizer"/> (XIL2CPP WU-8) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.4 (line 416): a
/// <c>rec with { X = v, ... }</c> expression lowers to the record copy
/// constructor invocation plus per-property setter assignments. Each fixture
/// builds a Pass-1 result from source, runs ONLY this normalizer in isolation
/// via <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>,
/// and asserts the recorded <see cref="RecordWithAnnotation"/> (target record
/// type + ordered (property, value-expression) mutations).
/// </summary>
public sealed class RecordWithExpressionNormalizerTests
{
    private static NormalizedUnit Run(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(sources);
        return Pass2Driver.Run(
            pass1, new INormalizer[] { new RecordWithExpressionNormalizer() });
    }

    private static IReadOnlyList<WithExpressionSyntax> WithExpressions(NormalizedUnit unit)
        => unit.Pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<WithExpressionSyntax>().ToList();

    // -----------------------------------------------------------------
    // Fixture 1: a simple with-expression mutating value-typed properties.
    // -----------------------------------------------------------------

    [Fact]
    public void SimpleWith_AnnotatesTargetTypeAndOrderedMutations()
    {
        const string source = @"
namespace M;
public record Point(int X, int Y);
public static class Use
{
    public static Point Shift(Point p) => p with { X = 10, Y = 20 };
}";

        NormalizedUnit unit = Run(source);
        IReadOnlyList<WithExpressionSyntax> withs = WithExpressions(unit);
        Assert.Single(withs);

        RecordWithAnnotation? annotation =
            unit.GetAnnotation<RecordWithAnnotation>(withs[0]);
        Assert.NotNull(annotation);
        Assert.Equal("record-with-expression", annotation!.Kind);

        // Target record type is the operand's static type.
        Assert.Equal("Point", annotation.TargetType.Name);

        // Ordered (property, value) mutations, in source order: X = 10, Y = 20.
        Assert.Equal(2, annotation.Mutations.Count);

        RecordWithMutation mx = annotation.Mutations[0];
        Assert.NotNull(mx.Property);
        Assert.Equal("X", mx.Property!.Name);
        Assert.Equal("10", mx.ValueExpression.ToString());
        Assert.Equal("X", mx.PropertyExpression.ToString());

        RecordWithMutation my = annotation.Mutations[1];
        Assert.NotNull(my.Property);
        Assert.Equal("Y", my.Property!.Name);
        Assert.Equal("20", my.ValueExpression.ToString());

        // No diagnostics for well-formed input.
        Assert.Empty(unit.Diagnostics);
    }

    // -----------------------------------------------------------------
    // Fixture 2: a with-expression mutating an XObject-typed property.
    // -----------------------------------------------------------------

    [Fact]
    public void WithMutatingXObjectTypedProperty_RecordsXObjectPropertyType()
    {
        const string source = @"
namespace XPact.CoreXObject { public abstract class XObject { } }
namespace M
{
    using XPact.CoreXObject;
    public sealed class Actor : XObject { }
    public record Holder
    {
        public Actor? Pawn { get; init; }
        public int Tag { get; init; }
    }
    public static class Use
    {
        public static Holder Reassign(Holder h, Actor a) => h with { Pawn = a };
    }
}";

        NormalizedUnit unit = Run(source);
        IReadOnlyList<WithExpressionSyntax> withs = WithExpressions(unit);
        Assert.Single(withs);

        RecordWithAnnotation? annotation =
            unit.GetAnnotation<RecordWithAnnotation>(withs[0]);
        Assert.NotNull(annotation);
        Assert.Equal("Holder", annotation!.TargetType.Name);

        RecordWithMutation mutation = Assert.Single(annotation.Mutations);
        Assert.NotNull(mutation.Property);
        Assert.Equal("Pawn", mutation.Property!.Name);
        Assert.Equal("a", mutation.ValueExpression.ToString());

        // The recorded property type is the XObject-derived Actor, so a later
        // pass can identify the mutation as a write-barrier site (Section 5.4).
        Assert.NotNull(mutation.PropertyType);
        Assert.Equal("Actor", mutation.PropertyType!.Name);
        Assert.True(AnalyzerHelpers.IsXObjectDerived(
            mutation.PropertyType as INamedTypeSymbol));
    }

    // -----------------------------------------------------------------
    // Fixture 3: a nested with-expression. The outer and inner expressions
    // are distinct nodes; each is annotated independently and the outer
    // mutation's value-expression is the inner WithExpressionSyntax.
    // -----------------------------------------------------------------

    [Fact]
    public void NestedWith_AnnotatesBothExpressionsIndependently()
    {
        const string source = @"
namespace M;
public record Inner(int V);
public record Outer(Inner Child, int Flag);
public static class Use
{
    public static Outer Rebuild(Outer o) =>
        o with { Child = o.Child with { V = 99 }, Flag = 1 };
}";

        NormalizedUnit unit = Run(source);
        IReadOnlyList<WithExpressionSyntax> withs = WithExpressions(unit);

        // Two with-expressions: the outer one and the inner one nested in the
        // outer's Child mutation. DescendantNodes is document order, so the
        // outer expression precedes the inner.
        Assert.Equal(2, withs.Count);
        WithExpressionSyntax outer = withs[0];
        WithExpressionSyntax inner = withs[1];

        // Outer annotation: target Outer, mutations Child = (inner-with), Flag = 1.
        RecordWithAnnotation? outerAnnotation =
            unit.GetAnnotation<RecordWithAnnotation>(outer);
        Assert.NotNull(outerAnnotation);
        Assert.Equal("Outer", outerAnnotation!.TargetType.Name);
        Assert.Equal(2, outerAnnotation.Mutations.Count);

        RecordWithMutation childMutation = outerAnnotation.Mutations[0];
        Assert.Equal("Child", childMutation.Property!.Name);
        // The outer Child mutation's value IS the inner with-expression node.
        Assert.Same(inner, childMutation.ValueExpression);

        RecordWithMutation flagMutation = outerAnnotation.Mutations[1];
        Assert.Equal("Flag", flagMutation.Property!.Name);
        Assert.Equal("1", flagMutation.ValueExpression.ToString());

        // Inner annotation: target Inner, single mutation V = 99.
        RecordWithAnnotation? innerAnnotation =
            unit.GetAnnotation<RecordWithAnnotation>(inner);
        Assert.NotNull(innerAnnotation);
        Assert.Equal("Inner", innerAnnotation!.TargetType.Name);

        RecordWithMutation vMutation = Assert.Single(innerAnnotation.Mutations);
        Assert.Equal("V", vMutation.Property!.Name);
        Assert.Equal("99", vMutation.ValueExpression.ToString());

        Assert.Empty(unit.Diagnostics);
    }
}
