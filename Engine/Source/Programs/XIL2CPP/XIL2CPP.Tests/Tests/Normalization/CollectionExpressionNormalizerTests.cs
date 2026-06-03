// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="CollectionExpressionNormalizer"/> (WU-9): collection
/// expressions <c>[a, b, c]</c> are annotated -- not rewritten -- with the
/// resolved target type, the chosen lowering form, and the ordered elements
/// (expression vs. spread) per /Documents/XIL2CPP.html Rev 4 Section 5.3 +
/// 5.13. Each test builds a Pass-1 result from a fixture source, runs JUST
/// this normalizer in isolation through <see cref="Pass2Driver"/>, and asserts
/// the recorded annotation.
/// </summary>
public sealed class CollectionExpressionNormalizerTests
{
    [Fact]
    public void NameIsStableAndUnique()
    {
        Assert.Equal(
            "CollectionExpressionNormalizer",
            new CollectionExpressionNormalizer().Name);
    }

    [Fact]
    public void IsReflectionDiscoveredByPass2Driver()
    {
        Assert.Contains(
            Pass2Driver.DiscoverNormalizers(),
            n => n is CollectionExpressionNormalizer);
    }

    [Fact]
    public void ListTarget_LowersToListInitializer()
    {
        const string source = """
            using System.Collections.Generic;
            namespace M;
            public class A
            {
                public List<int> F() => [1, 2, 3];
            }
            """;

        CollectionExpressionAnnotation annotation = AnnotateOne(source);

        Assert.Equal(CollectionLoweringForm.ListInitializer, annotation.Form);
        Assert.NotNull(annotation.TargetType);
        Assert.Equal(
            "System.Collections.Generic.List<int>",
            annotation.TargetType!.ToDisplayString());
        Assert.NotNull(annotation.ElementType);
        Assert.Equal("int", annotation.ElementType!.ToDisplayString());

        Assert.Equal(3, annotation.Elements.Count);
        Assert.All(annotation.Elements, e =>
            Assert.Equal(CollectionElementKind.Expression, e.Kind));
        Assert.Equal(
            new[] { "1", "2", "3" },
            annotation.Elements.Select(e => e.Expression.ToString()));
    }

    [Fact]
    public void ArrayTarget_LowersToArrayInitializer()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int[] F() => [1, 2, 3];
            }
            """;

        CollectionExpressionAnnotation annotation = AnnotateOne(source);

        Assert.Equal(CollectionLoweringForm.ArrayInitializer, annotation.Form);
        Assert.NotNull(annotation.TargetType);
        Assert.Equal("int[]", annotation.TargetType!.ToDisplayString());
        Assert.IsAssignableFrom<IArrayTypeSymbol>(annotation.TargetType);
        Assert.NotNull(annotation.ElementType);
        Assert.Equal("int", annotation.ElementType!.ToDisplayString());

        Assert.Equal(3, annotation.Elements.Count);
        Assert.All(annotation.Elements, e =>
            Assert.Equal(CollectionElementKind.Expression, e.Kind));
    }

    [Fact]
    public void ReadOnlySpanTarget_LowersToStackallocSpan()
    {
        const string source = """
            using System;
            namespace M;
            public class A
            {
                public int Sum()
                {
                    ReadOnlySpan<int> s = [1, 2, 3];
                    return s[0];
                }
            }
            """;

        CollectionExpressionAnnotation annotation = AnnotateOne(source);

        Assert.Equal(CollectionLoweringForm.StackallocSpan, annotation.Form);
        Assert.NotNull(annotation.TargetType);
        Assert.Equal(
            "System.ReadOnlySpan<int>",
            annotation.TargetType!.ToDisplayString());
        Assert.NotNull(annotation.ElementType);
        Assert.Equal("int", annotation.ElementType!.ToDisplayString());

        Assert.Equal(3, annotation.Elements.Count);
        Assert.All(annotation.Elements, e =>
            Assert.Equal(CollectionElementKind.Expression, e.Kind));
    }

    [Fact]
    public void SpreadElement_IsRecordedDistinctFromExpressionElements()
    {
        const string source = """
            using System.Collections.Generic;
            namespace M;
            public class A
            {
                public List<int> F(List<int> xs) => [1, ..xs, 2];
            }
            """;

        CollectionExpressionAnnotation annotation = AnnotateOne(source);

        Assert.Equal(CollectionLoweringForm.ListInitializer, annotation.Form);
        Assert.Equal(3, annotation.Elements.Count);

        Assert.Equal(CollectionElementKind.Expression, annotation.Elements[0].Kind);
        Assert.Equal("1", annotation.Elements[0].Expression.ToString());

        Assert.Equal(CollectionElementKind.Spread, annotation.Elements[1].Kind);
        // The recorded expression for a spread element is the spread SOURCE
        // (the enumerable after `..`), not the `..xs` syntax as a whole.
        Assert.Equal("xs", annotation.Elements[1].Expression.ToString());

        Assert.Equal(CollectionElementKind.Expression, annotation.Elements[2].Kind);
        Assert.Equal("2", annotation.Elements[2].Expression.ToString());
    }

    [Fact]
    public void EmptyCollectionExpression_IsAnnotatedWithNoElements()
    {
        const string source = """
            using System.Collections.Generic;
            namespace M;
            public class A
            {
                public List<int> F() => [];
            }
            """;

        CollectionExpressionAnnotation annotation = AnnotateOne(source);

        Assert.Equal(CollectionLoweringForm.ListInitializer, annotation.Form);
        Assert.NotNull(annotation.TargetType);
        Assert.Equal(
            "System.Collections.Generic.List<int>",
            annotation.TargetType!.ToDisplayString());
        Assert.Empty(annotation.Elements);
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    /// <summary>
    /// Build a Pass-1 result from <paramref name="source"/>, run JUST the
    /// collection-expression normalizer in isolation, and return the single
    /// annotation recorded on the single collection expression in the source.
    /// </summary>
    private static CollectionExpressionAnnotation AnnotateOne(string source)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);

        // Guard: the fixture must bind cleanly, otherwise a target-type
        // resolution miss would silently weaken the assertion. (The test
        // helper does not seed binder diagnostics onto the Pass1Result, so
        // assert directly against the compilation's diagnostics.)
        System.Collections.Generic.List<Diagnostic> errors = pass1.Compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "Fixture source produced binder errors: "
                + string.Join("; ", errors.Select(d => d.GetMessage())));

        NormalizedUnit unit = Pass2Driver.Run(
            pass1,
            new INormalizer[] { new CollectionExpressionNormalizer() });

        CollectionExpressionSyntax node = pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<CollectionExpressionSyntax>().Single();

        CollectionExpressionAnnotation? annotation =
            unit.GetAnnotation<CollectionExpressionAnnotation>(node);
        Assert.NotNull(annotation);
        return annotation!;
    }
}
