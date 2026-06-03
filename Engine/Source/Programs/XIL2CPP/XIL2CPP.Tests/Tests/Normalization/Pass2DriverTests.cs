// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Foundation tests for <see cref="Pass2Driver"/> + the
/// <see cref="NormalizedUnit"/> / <see cref="NormalizedUnitBuilder"/>
/// annotation layer per /Documents/XIL2CPP.html Rev 4 Section 3.2. Proves
/// the reflection-discovery is scoped to the production assembly, that the
/// driver is correct with zero production normalizers, and that the generic
/// annotation + synthesized-data bag round-trips a custom type defined
/// entirely in the test assembly (the parallel-safe extensibility contract).
/// </summary>
public sealed class Pass2DriverTests
{
    // ---------------------------------------------------------------
    // A test-only fake normalizer + custom annotation + custom
    // synthesized type, all defined in the TEST assembly. None of these
    // must EVER be reflection-discovered by Pass2Driver (which scopes to
    // XIL2CPP.Normalization). They also prove a later agent can introduce a
    // brand-new annotation subclass + synthesized type without editing the
    // foundation.
    // ---------------------------------------------------------------

    /// <summary>A custom annotation type a later agent would define in its own file.</summary>
    private sealed class FakeLoweringAnnotation : LoweredAnnotation
    {
        public FakeLoweringAnnotation(string detail) => Detail = detail;

        public string Detail { get; }

        public override string Kind => "fake-lowering";
    }

    /// <summary>A second custom annotation type to prove multi-type-per-node retrieval.</summary>
    private sealed class OtherFakeAnnotation : LoweredAnnotation
    {
        public override string Kind => "other-fake";
    }

    /// <summary>A custom per-symbol synthesized type a later agent would define in its own file.</summary>
    private sealed record FakeSymbolFacts(int CaptureCount, string Note);

    /// <summary>A custom module-scoped synthesized record type.</summary>
    private sealed record FakeSiteRecord(string Where);

    /// <summary>
    /// A fake normalizer that lives in the test assembly only. Used to drive
    /// the builder explicitly; it must NOT be auto-discovered.
    /// </summary>
    private sealed class FakeTestNormalizer : INormalizer
    {
        public string Name => "ZZ-Fake-Test-Normalizer";

        public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
        {
            // No-op; the round-trip tests drive the builder directly.
        }
    }

    [Fact]
    public void DiscoverNormalizers_ScopesToProductionAssembly_ExcludesTestFakes()
    {
        IReadOnlyList<INormalizer> discovered = Pass2Driver.DiscoverNormalizers();

        // The test-only fake must never be discovered (it lives in
        // XIL2CPP.Tests, not XIL2CPP.Normalization).
        Assert.DoesNotContain(discovered, n => n is FakeTestNormalizer);
        Assert.All(discovered, n => Assert.Equal(
            "XIL2CPP.Normalization",
            n.GetType().Assembly.GetName().Name));
    }

    [Fact]
    public void DiscoverNormalizers_IsSortedByNameOrdinal()
    {
        IReadOnlyList<INormalizer> discovered = Pass2Driver.DiscoverNormalizers();
        List<string> names = discovered.Select(n => n.Name).ToList();
        List<string> sorted = names.OrderBy(s => s, System.StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void Run_WithZeroProductionNormalizers_ReturnsValidEmptyUnit()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { public int F() => 1; }");

        // Reflection-discovery path. Even if zero production normalizers
        // exist, the driver returns a valid unit exposing the Pass-1 result.
        NormalizedUnit unit = Pass2Driver.Run(pass1);

        Assert.NotNull(unit);
        Assert.Same(pass1, unit.Pass1);
        Assert.Empty(unit.Diagnostics);
    }

    [Fact]
    public void Run_WithExplicitEmptyNormalizerSet_ReturnsValidEmptyUnit()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { }");

        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());

        Assert.NotNull(unit);
        Assert.Same(pass1, unit.Pass1);
        Assert.Empty(unit.Diagnostics);
    }

    [Fact]
    public void Run_RunsSuppliedNormalizersInOrder()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1("namespace M; public class A { }");
        List<string> ran = new();

        INormalizer first = new DelegatingNormalizer("AAA", (_, _) => ran.Add("AAA"));
        INormalizer second = new DelegatingNormalizer("BBB", (_, _) => ran.Add("BBB"));

        Pass2Driver.Run(pass1, new List<INormalizer> { first, second });

        Assert.Equal(new[] { "AAA", "BBB" }, ran);
    }

    [Fact]
    public void Annotation_RoundTrips_CustomSubclassByConcreteType()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { public int F() => 1; }");
        ClassDeclarationSyntax classNode = FirstClass(pass1);

        NormalizedUnitBuilder builder = new(pass1);
        builder.AnnotateNode(classNode, new FakeLoweringAnnotation("hello"));
        builder.AnnotateNode(classNode, new OtherFakeAnnotation());
        NormalizedUnit unit = builder.Build(pass1);

        // Retrieval by concrete type returns the right annotation.
        FakeLoweringAnnotation? fetched = unit.GetAnnotation<FakeLoweringAnnotation>(classNode);
        Assert.NotNull(fetched);
        Assert.Equal("hello", fetched!.Detail);
        Assert.Equal("fake-lowering", fetched.Kind);

        // Multiple annotations of different types coexist on one node.
        IReadOnlyList<LoweredAnnotation> all = unit.GetAnnotations(classNode);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a is FakeLoweringAnnotation);
        Assert.Contains(all, a => a is OtherFakeAnnotation);

        // Typed enumeration filters by concrete type.
        Assert.Single(unit.GetAnnotations<FakeLoweringAnnotation>(classNode));
        Assert.Single(unit.GetAnnotations<OtherFakeAnnotation>(classNode));
    }

    [Fact]
    public void Annotation_UnannotatedNode_ReturnsEmpty()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1("namespace M; public class A { }");
        ClassDeclarationSyntax classNode = FirstClass(pass1);

        NormalizedUnit unit = new NormalizedUnitBuilder(pass1).Build(pass1);

        Assert.Null(unit.GetAnnotation<FakeLoweringAnnotation>(classNode));
        Assert.Empty(unit.GetAnnotations(classNode));
        Assert.False(unit.TryGetAnnotations(classNode, out IReadOnlyList<LoweredAnnotation> none));
        Assert.Empty(none);
    }

    [Fact]
    public void Attach_RoundTrips_CustomPerSymbolType()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(
            "namespace M; public class A { public void F() { } }");
        ISymbol classSymbol = ClassSymbol(pass1);

        NormalizedUnitBuilder builder = new(pass1);
        builder.Attach(classSymbol, new FakeSymbolFacts(3, "captures"));
        NormalizedUnit unit = builder.Build(pass1);

        FakeSymbolFacts? facts = unit.GetAttached<FakeSymbolFacts>(classSymbol);
        Assert.NotNull(facts);
        Assert.Equal(3, facts!.CaptureCount);
        Assert.Equal("captures", facts.Note);

        Assert.True(unit.TryGetAttached(classSymbol, out FakeSymbolFacts viaTry));
        Assert.Equal(facts, viaTry);
    }

    [Fact]
    public void Attach_MissingKeyOrType_ReturnsDefault()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1("namespace M; public class A { }");
        ISymbol classSymbol = ClassSymbol(pass1);

        NormalizedUnit unit = new NormalizedUnitBuilder(pass1).Build(pass1);

        Assert.Null(unit.GetAttached<FakeSymbolFacts>(classSymbol));
        Assert.False(unit.TryGetAttached(classSymbol, out FakeSymbolFacts _));
    }

    [Fact]
    public void AddSynthesized_RoundTrips_CustomModuleScopedType_InAppendOrder()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1("namespace M; public class A { }");

        NormalizedUnitBuilder builder = new(pass1);
        builder.AddSynthesized(new FakeSiteRecord("one"));
        builder.AddSynthesized(new FakeSiteRecord("two"));
        NormalizedUnit unit = builder.Build(pass1);

        IReadOnlyList<FakeSiteRecord> records = unit.GetSynthesized<FakeSiteRecord>();
        Assert.Equal(2, records.Count);
        Assert.Equal("one", records[0].Where);
        Assert.Equal("two", records[1].Where);

        // A type with nothing added returns empty, not null.
        Assert.Empty(unit.GetSynthesized<FakeSymbolFacts>());
    }

    [Fact]
    public void Builder_RejectsWritesAfterSeal()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1("namespace M; public class A { }");
        ClassDeclarationSyntax classNode = FirstClass(pass1);

        NormalizedUnitBuilder builder = new(pass1);
        builder.Build(pass1);

        Assert.Throws<System.InvalidOperationException>(
            () => builder.AnnotateNode(classNode, new OtherFakeAnnotation()));
        Assert.Throws<System.InvalidOperationException>(
            () => builder.AddDiagnostic(new DiagnosticRecord(
                XilSeverity.Warning, DiagnosticCodes.LoggerSentinel, "x")));
    }

    [Fact]
    public void Diagnostics_FlowThroughToUnit()
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1("namespace M; public class A { }");
        DiagnosticRecord rec = new(
            XilSeverity.Warning, DiagnosticCodes.UnbodiedPartialMethodSideEffectingArgs, "elided");

        INormalizer normalizer = new DelegatingNormalizer(
            "AddsDiag", (_, b) => b.AddDiagnostic(rec));

        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer> { normalizer });

        Assert.Single(unit.Diagnostics);
        Assert.Same(rec, unit.Diagnostics[0]);
    }

    // ---------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------

    private sealed class DelegatingNormalizer : INormalizer
    {
        private readonly System.Action<Pass1Result, NormalizedUnitBuilder> _body;

        public DelegatingNormalizer(string name, System.Action<Pass1Result, NormalizedUnitBuilder> body)
        {
            Name = name;
            _body = body;
        }

        public string Name { get; }

        public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder) => _body(pass1, builder);
    }

    private static ClassDeclarationSyntax FirstClass(Pass1Result pass1)
        => pass1.ParsedFiles[0].Tree.GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>().First();

    private static ISymbol ClassSymbol(Pass1Result pass1)
    {
        ClassDeclarationSyntax node = FirstClass(pass1);
        SemanticModel model = pass1.GetSemanticModel(pass1.ParsedFiles[0].Tree);
        return model.GetDeclaredSymbol(node)!;
    }
}
