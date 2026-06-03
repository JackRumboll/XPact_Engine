// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Foundation tests for <see cref="Pass3Driver"/> + the
/// <see cref="Pass3Result"/> / <see cref="Pass3ResultBuilder"/> result bag
/// per /Documents/XIL2CPP.html Rev 4 Section 3.2. Proves reflection-discovery
/// is scoped to the production assembly, the driver is correct with zero
/// production analyzers, and the generic Add&lt;T&gt; / SetSingleton&lt;T&gt;
/// bag round-trips a custom result type defined entirely in the test
/// assembly (the parallel-safe extensibility contract).
/// </summary>
public sealed class Pass3DriverTests
{
    // ---------------------------------------------------------------
    // Test-only fake analyzer + custom result types, all in the TEST
    // assembly. None must be reflection-discovered by Pass3Driver. They
    // prove a later agent can introduce brand-new result types without
    // editing the foundation.
    // ---------------------------------------------------------------

    /// <summary>A custom append-list result type a later agent would define in its own file.</summary>
    private sealed record FakeSiteResult(string Symbol, int Line);

    /// <summary>A custom singleton result type a later agent would define in its own file.</summary>
    private sealed record FakeSummary(int TotalSites);

    /// <summary>A fake analyzer that lives in the test assembly only; must NOT be auto-discovered.</summary>
    private sealed class FakeTestAnalyzer : ISemanticAnalyzer
    {
        public string Name => "ZZ-Fake-Test-Analyzer";

        public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
        {
            // No-op; round-trip tests drive the builder directly.
        }
    }

    private static NormalizedUnit BuildUnit(bool isSimPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath, sources);
        return Pass2Driver.Run(pass1, new List<INormalizer>());
    }

    [Fact]
    public void DiscoverAnalyzers_ScopesToProductionAssembly_ExcludesTestFakes()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();

        Assert.DoesNotContain(discovered, a => a is FakeTestAnalyzer);
        Assert.All(discovered, a => Assert.Equal(
            "XIL2CPP.Analysis",
            a.GetType().Assembly.GetName().Name));
    }

    [Fact]
    public void DiscoverAnalyzers_IsSortedByNameOrdinal()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();
        List<string> names = discovered.Select(a => a.Name).ToList();
        List<string> sorted = names.OrderBy(s => s, System.StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void Run_WithZeroProductionAnalyzers_ReturnsValidEmptyResult()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false,
            "namespace M; public class A { public int F() => 1; }");

        Pass3Result result = Pass3Driver.Run(unit);

        Assert.NotNull(result);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Run_WithExplicitEmptyAnalyzerSet_ReturnsValidEmptyResult()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");

        Pass3Result result = Pass3Driver.Run(unit, new List<ISemanticAnalyzer>());

        Assert.NotNull(result);
        Assert.Empty(result.Diagnostics);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Run_RunsSuppliedAnalyzersInOrder()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");
        List<string> ran = new();

        ISemanticAnalyzer first = new DelegatingAnalyzer("AAA", (_, _) => ran.Add("AAA"));
        ISemanticAnalyzer second = new DelegatingAnalyzer("BBB", (_, _) => ran.Add("BBB"));

        Pass3Driver.Run(unit, new List<ISemanticAnalyzer> { first, second });

        Assert.Equal(new[] { "AAA", "BBB" }, ran);
    }

    [Fact]
    public void ResultBag_Add_RoundTrips_CustomTypeInAppendOrder()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");

        ISemanticAnalyzer analyzer = new DelegatingAnalyzer("AddsSites", (_, b) =>
        {
            b.Add(new FakeSiteResult("A.F", 1));
            b.Add(new FakeSiteResult("A.G", 2));
        });

        Pass3Result result = Pass3Driver.Run(unit, new List<ISemanticAnalyzer> { analyzer });

        IReadOnlyList<FakeSiteResult> sites = result.GetAll<FakeSiteResult>();
        Assert.Equal(2, sites.Count);
        Assert.Equal("A.F", sites[0].Symbol);
        Assert.Equal("A.G", sites[1].Symbol);

        // A type with nothing added returns empty, not null.
        Assert.Empty(result.GetAll<FakeSummary>());
    }

    [Fact]
    public void ResultBag_SetSingleton_RoundTrips_CustomType_LastWriteWins()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");

        ISemanticAnalyzer analyzer = new DelegatingAnalyzer("SetsSummary", (_, b) =>
        {
            b.SetSingleton(new FakeSummary(1));
            b.SetSingleton(new FakeSummary(7)); // overwrites
        });

        Pass3Result result = Pass3Driver.Run(unit, new List<ISemanticAnalyzer> { analyzer });

        FakeSummary? summary = result.GetSingleton<FakeSummary>();
        Assert.NotNull(summary);
        Assert.Equal(7, summary!.TotalSites);

        Assert.True(result.TryGetSingleton(out FakeSummary viaTry));
        Assert.Equal(7, viaTry.TotalSites);

        // Unset singleton returns default / false.
        Assert.Null(result.GetSingleton<FakeSiteResult>());
        Assert.False(result.TryGetSingleton(out FakeSiteResult _));
    }

    [Fact]
    public void Diagnostics_FlowThrough_AndDriveHasErrors()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");
        DiagnosticRecord errorRec = new(
            XilSeverity.Error, DiagnosticCodes.NewExpressionOnXObjectDerived, "new on XObject");

        ISemanticAnalyzer analyzer = new DelegatingAnalyzer(
            "AddsDiag", (_, b) => b.AddDiagnostic(errorRec));

        Pass3Result result = Pass3Driver.Run(unit, new List<ISemanticAnalyzer> { analyzer });

        Assert.Single(result.Diagnostics);
        Assert.Same(errorRec, result.Diagnostics[0]);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void HasErrors_FalseWhenOnlyWarnings()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");
        DiagnosticRecord warn = new(
            XilSeverity.Warning, DiagnosticCodes.ConservativeXGCRootSpan, "conservative");

        ISemanticAnalyzer analyzer = new DelegatingAnalyzer(
            "AddsWarn", (_, b) => b.AddDiagnostic(warn));

        Pass3Result result = Pass3Driver.Run(unit, new List<ISemanticAnalyzer> { analyzer });

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Builder_RejectsWritesAfterSeal()
    {
        NormalizedUnit unit = BuildUnit(isSimPath: false, "namespace M; public class A { }");
        Pass3ResultBuilder builder = new(unit);
        builder.Build(unit);

        Assert.Throws<System.InvalidOperationException>(() => builder.Add(new FakeSiteResult("x", 0)));
        Assert.Throws<System.InvalidOperationException>(
            () => builder.SetSingleton(new FakeSummary(0)));
        Assert.Throws<System.InvalidOperationException>(
            () => builder.AddDiagnostic(new DiagnosticRecord(
                XilSeverity.Info, DiagnosticCodes.LoggerSentinel, "x")));
    }

    private sealed class DelegatingAnalyzer : ISemanticAnalyzer
    {
        private readonly System.Action<NormalizedUnit, Pass3ResultBuilder> _body;

        public DelegatingAnalyzer(string name, System.Action<NormalizedUnit, Pass3ResultBuilder> body)
        {
            Name = name;
            _body = body;
        }

        public string Name { get; }

        public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder) => _body(unit, builder);
    }
}
