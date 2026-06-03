// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Tests for <see cref="StringInterpolationNormalizer"/> (WU-5) per
/// /Documents/XIL2CPP.html Rev 4 Section 5.5. Each fixture builds a Pass-1
/// result from a synthetic source, runs ONLY the string-interpolation
/// normalizer in isolation via the explicit-set
/// <see cref="Pass2Driver.Run(Pass1Result, IReadOnlyList{INormalizer})"/>
/// overload, and asserts the recorded
/// <see cref="StringInterpolationAnnotation"/> (ordered parts, composite-
/// format template, ordered hole expressions, raw-string flag, per-hole
/// alignment / format specifiers).
/// </summary>
public sealed class StringInterpolationNormalizerTests
{
    // -----------------------------------------------------------------
    // Fixture 1: simple one-hole interpolation.
    // -----------------------------------------------------------------

    [Fact]
    public void SimpleOneHole_RecordsLiteralThenHole_AndTemplate()
    {
        const string source = """
            namespace M;
            public class A
            {
                public string F(string name) => $"hello {name}";
            }
            """;

        StringInterpolationAnnotation ann = AnnotateSingle(source);

        Assert.Equal("string-interpolation", ann.Kind);
        Assert.False(ann.IsRaw);
        Assert.Equal("hello {0}", ann.Template);

        Assert.Equal(2, ann.Parts.Count);
        AssertLiteral(ann.Parts[0], "hello ");
        AssertHole(ann.Parts[1], index: 0, expr: "name", alignment: null, format: null);

        Assert.Single(ann.Holes);
        Assert.Equal("name", ann.Holes[0].ToString());
    }

    // -----------------------------------------------------------------
    // Fixture 2: multi-arg interpolation with alignment + format
    // specifiers, exercising the composite-format placeholder forms.
    // -----------------------------------------------------------------

    [Fact]
    public void MultiArg_RecordsAllHoles_WithAlignmentAndFormatInTemplate()
    {
        const string source = """
            namespace M;
            public class A
            {
                public string F(string name, int count, System.DateTime dt) =>
                    $"{name} has {count} items, at {dt:yyyy-MM-dd} pad {count,-5}";
            }
            """;

        StringInterpolationAnnotation ann = AnnotateSingle(source);

        Assert.False(ann.IsRaw);
        Assert.Equal("{0} has {1} items, at {2:yyyy-MM-dd} pad {3,-5}", ann.Template);

        Assert.Equal(7, ann.Parts.Count);
        AssertHole(ann.Parts[0], index: 0, expr: "name", alignment: null, format: null);
        AssertLiteral(ann.Parts[1], " has ");
        AssertHole(ann.Parts[2], index: 1, expr: "count", alignment: null, format: null);
        AssertLiteral(ann.Parts[3], " items, at ");
        AssertHole(ann.Parts[4], index: 2, expr: "dt", alignment: null, format: "yyyy-MM-dd");
        AssertLiteral(ann.Parts[5], " pad ");
        AssertHole(ann.Parts[6], index: 3, expr: "count", alignment: "-5", format: null);

        Assert.Equal(
            new[] { "name", "count", "dt", "count" },
            ann.Holes.Select(h => h.ToString()).ToArray());
    }

    // -----------------------------------------------------------------
    // Fixture 3: hole with a null-conditional access expression.
    // -----------------------------------------------------------------

    [Fact]
    public void NullConditionalHole_RecordsConditionalAccessExpression()
    {
        const string source = """
            namespace M;
            public class A
            {
                public string F(string name) => $"len {name?.Length}";
            }
            """;

        StringInterpolationAnnotation ann = AnnotateSingle(source);

        Assert.False(ann.IsRaw);
        Assert.Equal("len {0}", ann.Template);

        Assert.Equal(2, ann.Parts.Count);
        AssertLiteral(ann.Parts[0], "len ");
        AssertHole(ann.Parts[1], index: 0, expr: "name?.Length", alignment: null, format: null);

        Assert.Single(ann.Holes);
        Assert.Equal("name?.Length", ann.Holes[0].ToString());
        Assert.IsType<ConditionalAccessExpressionSyntax>(ann.Holes[0]);
    }

    // -----------------------------------------------------------------
    // Fixture 4: nested interpolation (an interpolated string inside a
    // hole of the outer interpolated string). BOTH nodes are annotated.
    // -----------------------------------------------------------------

    [Fact]
    public void NestedInterpolation_AnnotatesBothOuterAndInner()
    {
        const string source = """
            namespace M;
            public class A
            {
                public string F(string name, int count) =>
                    $"outer {(count > 0 ? $"inner {name}" : "zero")} end";
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new StringInterpolationNormalizer() });

        List<InterpolatedStringExpressionSyntax> nodes = AllInterpolatedStrings(pass1);
        Assert.Equal(2, nodes.Count);

        // DescendantNodes yields the outer node before the inner one (span
        // order), so nodes[0] is the outer interpolation, nodes[1] the inner.
        StringInterpolationAnnotation outer =
            unit.GetAnnotation<StringInterpolationAnnotation>(nodes[0])!;
        StringInterpolationAnnotation inner =
            unit.GetAnnotation<StringInterpolationAnnotation>(nodes[1])!;
        Assert.NotNull(outer);
        Assert.NotNull(inner);

        // Outer: literal "outer ", one hole (the conditional), literal " end".
        Assert.Equal("outer {0} end", outer.Template);
        Assert.Equal(3, outer.Parts.Count);
        AssertLiteral(outer.Parts[0], "outer ");
        Assert.False(outer.Parts[1].IsLiteral);
        Assert.Equal(0, outer.Parts[1].HoleIndex);
        AssertLiteral(outer.Parts[2], " end");
        Assert.Single(outer.Holes);
        Assert.IsType<ParenthesizedExpressionSyntax>(outer.Holes[0]);

        // Inner: literal "inner ", one hole (name).
        Assert.Equal("inner {0}", inner.Template);
        Assert.Equal(2, inner.Parts.Count);
        AssertLiteral(inner.Parts[0], "inner ");
        AssertHole(inner.Parts[1], index: 0, expr: "name", alignment: null, format: null);
        Assert.Single(inner.Holes);
        Assert.Equal("name", inner.Holes[0].ToString());
    }

    // -----------------------------------------------------------------
    // Fixture 5: raw-string interpolation ($"""...""").
    // -----------------------------------------------------------------

    [Fact]
    public void RawStringInterpolation_SetsIsRaw_AndRecordsParts()
    {
        // A single-line raw interpolated string with one hole. The fixture
        // SOURCE is a normal escaped C# string literal (not a raw literal)
        // so the inner triple-quote delimiters do not collide with an outer
        // raw-string fence; the parsed module text is the raw interpolated
        // string  $"""Raw {name} text""" .
        const string source =
            "namespace M;\n" +
            "public class A\n" +
            "{\n" +
            "    public string F(string name) => $\"\"\"Raw {name} text\"\"\";\n" +
            "}\n";

        StringInterpolationAnnotation ann = AnnotateSingle(source);

        Assert.True(ann.IsRaw);
        Assert.Equal("Raw {0} text", ann.Template);

        Assert.Equal(3, ann.Parts.Count);
        AssertLiteral(ann.Parts[0], "Raw ");
        AssertHole(ann.Parts[1], index: 0, expr: "name", alignment: null, format: null);
        AssertLiteral(ann.Parts[2], " text");

        Assert.Single(ann.Holes);
        Assert.Equal("name", ann.Holes[0].ToString());
    }

    // -----------------------------------------------------------------
    // Cross-cutting: the normalizer only annotates interpolated strings,
    // and the run is deterministic (re-running yields the same template).
    // -----------------------------------------------------------------

    [Fact]
    public void PlainStringLiteral_IsNotAnnotated()
    {
        const string source = """
            namespace M;
            public class A
            {
                public string F() => "plain";
            }
            """;

        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new StringInterpolationNormalizer() });

        Assert.Empty(AllInterpolatedStrings(pass1));
        LiteralExpressionSyntax plain = pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<LiteralExpressionSyntax>().First();
        Assert.Null(unit.GetAnnotation<StringInterpolationAnnotation>(plain));
    }

    [Fact]
    public void Run_IsDeterministic_AcrossRepeatedRuns()
    {
        const string source = """
            namespace M;
            public class A
            {
                public string F(string name, int count) => $"{name}-{count,3}-{count:X2}";
            }
            """;

        string first = AnnotateSingle(source).Template;
        string second = AnnotateSingle(source).Template;
        Assert.Equal(first, second);
        Assert.Equal("{0}-{1,3}-{2:X2}", first);
    }

    // -----------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------

    /// <summary>
    /// Build a Pass-1 result over <paramref name="source"/>, run only the
    /// string-interpolation normalizer, and return the single recorded
    /// annotation (the source must contain exactly one interpolated string).
    /// </summary>
    private static StringInterpolationAnnotation AnnotateSingle(string source)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(source);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new StringInterpolationNormalizer() });

        List<InterpolatedStringExpressionSyntax> nodes = AllInterpolatedStrings(pass1);
        Assert.Single(nodes);

        StringInterpolationAnnotation? ann =
            unit.GetAnnotation<StringInterpolationAnnotation>(nodes[0]);
        Assert.NotNull(ann);
        return ann!;
    }

    private static List<InterpolatedStringExpressionSyntax> AllInterpolatedStrings(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes()
            .OfType<InterpolatedStringExpressionSyntax>()
            .ToList();

    private static void AssertLiteral(StringInterpolationPart part, string expectedText)
    {
        Assert.True(part.IsLiteral, "expected a literal part");
        Assert.Equal(expectedText, part.Text);
        Assert.Equal(-1, part.HoleIndex);
        Assert.Null(part.Expression);
        Assert.Null(part.Alignment);
        Assert.Null(part.Format);
    }

    private static void AssertHole(
        StringInterpolationPart part,
        int index,
        string expr,
        string? alignment,
        string? format)
    {
        Assert.False(part.IsLiteral, "expected a hole part");
        Assert.Null(part.Text);
        Assert.Equal(index, part.HoleIndex);
        Assert.NotNull(part.Expression);
        Assert.Equal(expr, part.Expression!.ToString());
        Assert.Equal(alignment, part.Alignment);
        Assert.Equal(format, part.Format);
    }
}
