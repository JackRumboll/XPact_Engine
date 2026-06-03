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
/// Tests for <see cref="LocalFunctionNormalizer"/> per
/// /Documents/XIL2CPP.html Rev 4 Section 5.9: each
/// <see cref="LocalFunctionStatementSyntax"/> is annotated with a
/// <see cref="LocalFunctionAnnotation"/> recording its captured-variable set
/// (sorted), whether it captures <c>this</c>, whether it is <c>static</c>,
/// and the non-capturing flag that selects anonymous-namespace free-function
/// emit (non-capturing) vs display-class emit (capturing). One fixture per
/// case in WU-10: non-capturing, capturing a value local, capturing an
/// XObject-typed local, static local function, recursive local function.
/// </summary>
public sealed class LocalFunctionNormalizerTests
{
    // A locally-declared stand-in for the engine root reference type (the
    // real curated XPact.CSharp.BCL XObject ref is absent in Phase 6.b tests,
    // which bind against Basic.Reference.Assemblies.Net80).
    private const string XObjectStub =
        "namespace XPact.CoreXObject { public abstract class XObject { } }";

    [Fact]
    public void NonCapturing_NoCaptures_NotThis_IsNonCapturing()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Run(int x)
                {
                    int Add(int a, int b) => a + b;
                    return Add(x, 1);
                }
            }
            """;

        (NormalizedUnit unit, LocalFunctionStatementSyntax local) = Annotate(source, "Add");
        LocalFunctionAnnotation a = AnnotationOf(unit, local);

        Assert.Empty(a.CapturedVariables);
        Assert.False(a.CapturesThis);
        Assert.False(a.IsStatic);
        Assert.True(a.IsNonCapturing);
        Assert.Equal("local-function", a.Kind);
    }

    [Fact]
    public void CapturingValueLocal_RecordsCapture_IsCapturing()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Run(int x)
                {
                    int bias = 10;
                    int AddBias(int v) => v + bias;
                    return AddBias(x);
                }
            }
            """;

        (NormalizedUnit unit, LocalFunctionStatementSyntax local) = Annotate(source, "AddBias");
        LocalFunctionAnnotation a = AnnotationOf(unit, local);

        Assert.Equal(new[] { "bias" }, a.CapturedVariables.Select(s => s.Name).ToArray());
        Assert.False(a.CapturesThis);
        Assert.False(a.IsStatic);
        Assert.False(a.IsNonCapturing);
    }

    [Fact]
    public void CapturingXObjectTypedLocal_RecordsCapture_IsCapturing()
    {
        // The captured local is of an XObject-derived reference type; it is
        // captured by reference exactly like any other local (Section 5.9 /
        // FIX-B-HIGH-06), so it appears in the captured set and the local
        // function is classified capturing.
        const string source = """
            namespace M;
            public class Node : XPact.CoreXObject.XObject { public int Value; }
            public class A
            {
                public int Run(Node n)
                {
                    Node captured = n;
                    int ReadValue() => captured.Value;
                    return ReadValue();
                }
            }
            """;

        (NormalizedUnit unit, LocalFunctionStatementSyntax local) =
            AnnotateSources("ReadValue", XObjectStub, source);
        LocalFunctionAnnotation a = AnnotationOf(unit, local);

        Assert.Equal(new[] { "captured" }, a.CapturedVariables.Select(s => s.Name).ToArray());
        Assert.IsAssignableFrom<ILocalSymbol>(a.CapturedVariables[0]);
        Assert.False(a.CapturesThis);
        Assert.False(a.IsStatic);
        Assert.False(a.IsNonCapturing);
    }

    [Fact]
    public void StaticLocalFunction_IsStatic_AndNonCapturing()
    {
        const string source = """
            namespace M;
            public class A
            {
                public int Run(int x)
                {
                    static int Square(int v) => v * v;
                    return Square(x);
                }
            }
            """;

        (NormalizedUnit unit, LocalFunctionStatementSyntax local) = Annotate(source, "Square");
        LocalFunctionAnnotation a = AnnotationOf(unit, local);

        Assert.True(a.IsStatic);
        Assert.Empty(a.CapturedVariables);
        Assert.False(a.CapturesThis);
        Assert.True(a.IsNonCapturing);
    }

    [Fact]
    public void RecursiveLocalFunction_SelfCallDoesNotCapture_IsNonCapturing()
    {
        // A non-static recursive local function whose only "outer" reference
        // is its own self-call captures nothing: the recursion target is the
        // local function symbol itself, not an enclosing local / this.
        const string source = """
            namespace M;
            public class A
            {
                public int Run(int n)
                {
                    int Fact(int v) => v <= 1 ? 1 : v * Fact(v - 1);
                    return Fact(n);
                }
            }
            """;

        (NormalizedUnit unit, LocalFunctionStatementSyntax local) = Annotate(source, "Fact");
        LocalFunctionAnnotation a = AnnotationOf(unit, local);

        Assert.Empty(a.CapturedVariables);
        Assert.False(a.CapturesThis);
        Assert.False(a.IsStatic);
        Assert.True(a.IsNonCapturing);
    }

    [Fact]
    public void CapturingThis_ViaInstanceMember_SetsCapturesThis_AndIsCapturing()
    {
        // Supplementary coverage (beyond the 5 WU-10 fixtures) proving the
        // CapturesThis field actually toggles: an unqualified reference to a
        // non-static instance member implicitly captures this.
        const string source = """
            namespace M;
            public class A
            {
                private int _field = 3;
                public int Run()
                {
                    int ReadField() => _field;
                    return ReadField();
                }
            }
            """;

        (NormalizedUnit unit, LocalFunctionStatementSyntax local) = Annotate(source, "ReadField");
        LocalFunctionAnnotation a = AnnotationOf(unit, local);

        Assert.True(a.CapturesThis);
        Assert.Empty(a.CapturedVariables);
        Assert.False(a.IsStatic);
        Assert.False(a.IsNonCapturing);
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    /// <summary>
    /// Build a Pass-1 result over the supplied sources, run JUST the
    /// LocalFunctionNormalizer in isolation, and return the resulting unit
    /// plus the local function named <paramref name="localName"/>.
    /// </summary>
    private static (NormalizedUnit Unit, LocalFunctionStatementSyntax Local) AnnotateSources(
        string localName, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(sources);
        NormalizedUnit unit = Pass2Driver.Run(
            pass1, new INormalizer[] { new LocalFunctionNormalizer() });

        LocalFunctionStatementSyntax local = pass1.ParsedFiles
            .SelectMany(f => f.Tree.GetRoot().DescendantNodes())
            .OfType<LocalFunctionStatementSyntax>()
            .Single(n => n.Identifier.ValueText == localName);

        return (unit, local);
    }

    /// <summary>Single-source convenience: (source, localName).</summary>
    private static (NormalizedUnit Unit, LocalFunctionStatementSyntax Local) Annotate(
        string source, string localName)
        => AnnotateSources(localName, source);

    private static LocalFunctionAnnotation AnnotationOf(
        NormalizedUnit unit, LocalFunctionStatementSyntax local)
    {
        LocalFunctionAnnotation? annotation = unit.GetAnnotation<LocalFunctionAnnotation>(local);
        Assert.NotNull(annotation);
        return annotation!;
    }
}
