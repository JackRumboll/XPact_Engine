// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="NewExpressionAnalyzer"/> enforcing Locked Commitment
/// 3 (the explicit <c>XObject.New&lt;T&gt;</c> factory) per
/// /Documents/XIL2CPP.html Rev 4 Sections 2.3, 5.3, 6.4, 12: XIL2CPP001 for a
/// <c>new</c>-expression on an XObject-derived type (suppressed inside the
/// <c>[XObjectInternalConstructor]</c> factory), XIL2CPP002 for a null Outer
/// factory call, and XIL2CPP003 for an abstract / non-XObject factory T.
/// </summary>
/// <remarks>
/// Fixtures bind against Basic.Reference.Assemblies.Net80 with a locally
/// declared stand-in for the engine root reference type
/// (<c>abstract class XObject</c> in namespace <c>XPact.CoreXObject</c>) that
/// also hosts the <c>New&lt;T&gt;</c> / <c>TryNew&lt;T&gt;</c> static factory
/// methods, so the analyzer's metadata-name + namespace matching exercises
/// without the curated XPact BCL ref. The factory's <c>where T : XObject</c>
/// constraint is intentionally omitted so the analyzer (not the binder) is
/// what surfaces XIL2CPP003 on a non-XObject T.
/// </remarks>
public sealed class NewExpressionAnalyzerTests
{
    /// <summary>
    /// A locally declared stand-in for the engine root reference type plus its
    /// <c>New</c> / <c>TryNew</c> static factory and the
    /// <c>[XObjectInternalConstructor]</c> suppression attribute. Mirrors the
    /// surface /Documents/XIL2CPP.html Section 2.3 documents.
    /// </summary>
    private const string XObjectStub = """
        namespace XPact.CoreXObject
        {
            public sealed class XObjectInternalConstructorAttribute : System.Attribute { }

            public abstract class XObject
            {
                // Factory surface (constraint intentionally omitted so the
                // analyzer, not the binder, enforces the XObject-derived rule).
                public static T New<T>(XObject outer, string name, int flags) => default!;
                public static T TryNew<T>(XObject outer, string name, int flags) => default!;
            }
        }
        """;

    private static IReadOnlyList<DiagnosticRecord> RunAnalyzer(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result result = Pass3Driver.Run(
            unit, new ISemanticAnalyzer[] { new NewExpressionAnalyzer() });
        return result.Diagnostics;
    }

    private static Pass3Result RunAnalyzerResult(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new NewExpressionAnalyzer() });
    }

    // -----------------------------------------------------------------
    // XIL2CPP001 -- new-expression on XObject-derived type.
    // -----------------------------------------------------------------

    [Fact]
    public void NewOnXObjectDerived_EmitsXIL2CPP001()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C { public void Make() { var f = new Foo(); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.NewExpressionOnXObjectDerived, diag.Code);
        Assert.Equal(Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("Foo", diag.Message);
    }

    [Fact]
    public void NewOnTransitivelyDerived_EmitsXIL2CPP001()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; " +
            "public class Foo : XObject { } public class Bar : Foo { } " +
            "public class C { public void Make() { var b = new Bar(); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.NewExpressionOnXObjectDerived, diag.Code);
        Assert.Contains("Bar", diag.Message);
    }

    [Fact]
    public void NewWithObjectInitializer_OnXObjectDerived_EmitsXIL2CPP001()
    {
        // An object initializer does not exempt the diagnostic (Section 5.4).
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; " +
            "public class Foo : XObject { public int X; } " +
            "public class C { public void Make() { var f = new Foo { X = 1 }; } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.NewExpressionOnXObjectDerived, diag.Code);
    }

    [Fact]
    public void NewOnXObjectDerived_RecordsViolation()
    {
        Pass3Result result = RunAnalyzerResult(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C { public void Make() { var f = new Foo(); } } }");

        NewExpressionViolation violation = Assert.Single(result.GetAll<NewExpressionViolation>());
        Assert.Equal(DiagnosticCodes.NewExpressionOnXObjectDerived, violation.Code);
        Assert.Equal(NewExpressionRule.NewOnXObjectDerived, violation.Rule);
        Assert.Equal("Foo", violation.TypeName);
        Assert.True(violation.Span.StartLine >= 1);
        Assert.True(violation.Span.StartColumn >= 1);
    }

    // -----------------------------------------------------------------
    // XIL2CPP001 suppression inside the [XObjectInternalConstructor] factory.
    // -----------------------------------------------------------------

    [Fact]
    public void NewInsideInternalConstructorMethod_IsSuppressed()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class Runtime { " +
            "  [XObjectInternalConstructor] " +
            "  public static Foo Construct() { return new Foo(); } } }");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NewInsideInternalConstructorType_IsSuppressed()
    {
        // The attribute on the enclosing TYPE suppresses every new inside it.
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "[XObjectInternalConstructor] " +
            "public class Runtime { public static Foo Construct() { return new Foo(); } } }");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NewInPlainMethod_IsNotSuppressed_WhenSiblingHasAttribute()
    {
        // Only the attributed member is exempt; a sibling plain method is not.
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class Runtime { " +
            "  [XObjectInternalConstructor] public static Foo Internal() { return new Foo(); } " +
            "  public static Foo Public() { return new Foo(); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.NewExpressionOnXObjectDerived, diag.Code);
    }

    // -----------------------------------------------------------------
    // XIL2CPP002 -- XObject.New<T> with null Outer.
    // -----------------------------------------------------------------

    [Fact]
    public void FactoryNewWithNullOuter_EmitsXIL2CPP002()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C { public void Make() { var f = XObject.New<Foo>(null, \"n\", 0); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.XObjectNewNullOuter, diag.Code);
        Assert.Equal(Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void FactoryNewWithNamedNullOuter_EmitsXIL2CPP002()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C { public void Make() { " +
            "var f = XObject.New<Foo>(name: \"n\", flags: 0, outer: null); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.XObjectNewNullOuter, diag.Code);
    }

    [Fact]
    public void FactoryNewWithNonNullOuter_NoDiagnostic()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C : XObject { public void Make() { " +
            "var f = XObject.New<Foo>(this, \"n\", 0); } } }");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FactoryNewWithNullOuter_RecordsViolation()
    {
        Pass3Result result = RunAnalyzerResult(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C { public void Make() { var f = XObject.New<Foo>(null, \"n\", 0); } } }");

        NewExpressionViolation violation = Assert.Single(result.GetAll<NewExpressionViolation>());
        Assert.Equal(DiagnosticCodes.XObjectNewNullOuter, violation.Code);
        Assert.Equal(NewExpressionRule.FactoryNullOuter, violation.Rule);
        Assert.Equal("Foo", violation.TypeName);
    }

    // -----------------------------------------------------------------
    // XIL2CPP003 -- XObject.New<T> with abstract / non-XObject T.
    // -----------------------------------------------------------------

    [Fact]
    public void FactoryNewWithAbstractT_EmitsXIL2CPP003()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public abstract class Abstr : XObject { } " +
            "public class C : XObject { public void Make() { " +
            "var f = XObject.New<Abstr>(this, \"n\", 0); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.XObjectNewTypeMustBeConcrete, diag.Code);
        Assert.Equal(Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void FactoryNewWithNonXObjectT_EmitsXIL2CPP003()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Plain { } " +
            "public class C : XObject { public void Make() { " +
            "var f = XObject.New<Plain>(this, \"n\", 0); } } }");

        DiagnosticRecord diag = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticCodes.XObjectNewTypeMustBeConcrete, diag.Code);
    }

    [Fact]
    public void FactoryNewWithConcreteXObjectT_NoDiagnostic()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C : XObject { public void Make() { " +
            "var f = XObject.New<Foo>(this, \"n\", 0); } } }");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void FactoryNewWithAbstractT_RecordsViolation()
    {
        Pass3Result result = RunAnalyzerResult(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public abstract class Abstr : XObject { } " +
            "public class C : XObject { public void Make() { " +
            "var f = XObject.New<Abstr>(this, \"n\", 0); } } }");

        NewExpressionViolation violation = Assert.Single(result.GetAll<NewExpressionViolation>());
        Assert.Equal(DiagnosticCodes.XObjectNewTypeMustBeConcrete, violation.Code);
        Assert.Equal(NewExpressionRule.FactoryTypeMustBeConcrete, violation.Rule);
        Assert.Equal("Abstr", violation.TypeName);
    }

    [Fact]
    public void FactoryNewWithNullOuterAndAbstractT_EmitsBoth002And003()
    {
        // Both conditions hold on a single call: each is its own diagnostic.
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public abstract class Abstr : XObject { } " +
            "public class C { public void Make() { " +
            "var f = XObject.New<Abstr>(null, \"n\", 0); } } }");

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.XObjectNewNullOuter);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.XObjectNewTypeMustBeConcrete);
    }

    // -----------------------------------------------------------------
    // Plain non-XObject construction -- no diagnostic.
    // -----------------------------------------------------------------

    [Fact]
    public void NewOnPlainType_NoDiagnostic()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            "namespace M { public class Plain { } " +
            "public class C { public void Make() { var p = new Plain(); } } }");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NewOnBclType_NoDiagnostic()
    {
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(
            "namespace M { public class C { " +
            "public void Make() { var s = new System.Collections.Generic.List<int>(); } } }");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void XObjectItself_IsNotConstructedDirectly_NoFalsePositive()
    {
        // No new-expression on XObject-derived anywhere; only the factory
        // surface exists. The stub alone produces nothing.
        IReadOnlyList<DiagnosticRecord> diagnostics = RunAnalyzer(XObjectStub);
        Assert.Empty(diagnostics);
    }

    // -----------------------------------------------------------------
    // Determinism + analyzer identity.
    // -----------------------------------------------------------------

    [Fact]
    public void Analyzer_HasStableName()
    {
        Assert.Equal("NewExpressionAnalyzer", new NewExpressionAnalyzer().Name);
    }

    [Fact]
    public void Analysis_IsDeterministic_AcrossRuns()
    {
        string[] sources =
        {
            XObjectStub,
            "namespace M { using XPact.CoreXObject; public class Foo : XObject { } " +
            "public class C { public void A() { var x = new Foo(); } " +
            "public void B() { var y = new Foo(); } } }",
        };

        List<string> first = RunAnalyzer(sources)
            .Select(d => $"{d.Code}:{d.Line}:{d.Column}").ToList();
        List<string> second = RunAnalyzer(sources)
            .Select(d => $"{d.Code}:{d.Line}:{d.Column}").ToList();

        Assert.Equal(first, second);
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public void IsDiscoveredByPass3Driver()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();
        Assert.Contains(discovered, a => a is NewExpressionAnalyzer);
    }
}
