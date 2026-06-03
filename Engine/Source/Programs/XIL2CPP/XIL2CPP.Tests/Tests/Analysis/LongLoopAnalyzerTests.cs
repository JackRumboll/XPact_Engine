// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="LongLoopAnalyzer"/> (WU-6G-BACKEDGE) per
/// /Documents/XIL2CPP.html Rev 4 Section 6.5 (loop back-edge safe points +
/// long-loop detection). Drives synthetic C# sources through the real
/// Pass 1 -&gt; Pass 2 pipeline, runs JUST the LongLoopAnalyzer, and asserts
/// the recorded <see cref="LongLoopSite"/>s -- in particular that a loop is
/// classed short (its back-edge safe point omittable) ONLY when its iteration
/// count is provably bounded at or below
/// <see cref="LongLoopAnalyzer.ShortLoopThreshold"/>, and long otherwise.
/// </summary>
public sealed class LongLoopAnalyzerTests
{
    private static Pass3Result Analyze(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { new LongLoopAnalyzer() });
    }

    private static LongLoopSite SingleSite(string methodBody)
    {
        Pass3Result result = Analyze(
            "namespace M; public class A { public void F(int n, int[] xs) { " + methodBody + " } }");
        return Assert.Single(result.GetAll<LongLoopSite>());
    }

    // -----------------------------------------------------------------
    // for: provably-short (constant bound <= 16).
    // -----------------------------------------------------------------

    [Fact]
    public void For_ConstantBoundBelowThreshold_IsShort()
    {
        LongLoopSite site = SingleSite("for (int i = 0; i < 16; i++) { xs[i] = i; }");
        Assert.Equal(LoopKind.For, site.Kind);
        Assert.False(site.IsLong);
    }

    [Fact]
    public void For_ConstantBoundOne_IsShort()
    {
        LongLoopSite site = SingleSite("for (int i = 0; i < 1; i++) { xs[i] = i; }");
        Assert.False(site.IsLong);
    }

    [Fact]
    public void For_InclusiveBoundAtThresholdMinusOne_IsShort()
    {
        // i <= 15 runs 16 iterations (0..15) -> exactly at the threshold.
        LongLoopSite site = SingleSite("for (int i = 0; i <= 15; i++) { xs[i] = i; }");
        Assert.False(site.IsLong);
    }

    [Fact]
    public void For_ConstantOnLeftSide_NGreaterThanI_IsShort()
    {
        // 16 > i is the same upper bound as i < 16.
        LongLoopSite site = SingleSite("for (int i = 0; 16 > i; i++) { xs[i] = i; }");
        Assert.False(site.IsLong);
    }

    // -----------------------------------------------------------------
    // for: long (bound above threshold / non-constant / unbounded).
    // -----------------------------------------------------------------

    [Fact]
    public void For_ConstantBoundAboveThreshold_IsLong()
    {
        LongLoopSite site = SingleSite("for (int i = 0; i < 17; i++) { xs[i] = i; }");
        Assert.True(site.IsLong);
    }

    [Fact]
    public void For_InclusiveBoundAtThreshold_IsLong()
    {
        // i <= 16 runs 17 iterations (0..16) -> above the threshold.
        LongLoopSite site = SingleSite("for (int i = 0; i <= 16; i++) { xs[i] = i; }");
        Assert.True(site.IsLong);
    }

    [Fact]
    public void For_NonConstantBound_IsLong()
    {
        LongLoopSite site = SingleSite("for (int i = 0; i < n; i++) { xs[i] = i; }");
        Assert.True(site.IsLong);
    }

    [Fact]
    public void For_NoCondition_InfiniteLoop_IsLong()
    {
        LongLoopSite site = SingleSite("for (int i = 0; ; i++) { xs[0] = i; }");
        Assert.True(site.IsLong);
    }

    [Fact]
    public void For_CompoundCondition_IsLong()
    {
        // i < 4 && i < n is not the single-relational shape; not provably short.
        LongLoopSite site = SingleSite("for (int i = 0; i < 4 && i < n; i++) { xs[i] = i; }");
        Assert.True(site.IsLong);
    }

    [Fact]
    public void For_ConstBoundViaConstField_IsShort()
    {
        // A const symbol still binds to a compile-time constant value.
        Pass3Result result = Analyze(
            "namespace M; public class A { const int K = 8; "
            + "public void F(int[] xs) { for (int i = 0; i < K; i++) { xs[i] = i; } } }");
        LongLoopSite site = Assert.Single(result.GetAll<LongLoopSite>());
        Assert.False(site.IsLong);
    }

    // -----------------------------------------------------------------
    // while: short only when condition is constant false.
    // -----------------------------------------------------------------

    [Fact]
    public void While_ConstantFalse_IsShort()
    {
        LongLoopSite site = SingleSite("while (false) { xs[0] = 1; }");
        Assert.Equal(LoopKind.While, site.Kind);
        Assert.False(site.IsLong);
    }

    [Fact]
    public void While_ConstantTrue_IsLong()
    {
        LongLoopSite site = SingleSite("while (true) { xs[0] = 1; break; }");
        Assert.True(site.IsLong);
    }

    [Fact]
    public void While_RelationalCondition_IsLong()
    {
        // The loop-variable update lives in the (unread) body, not the
        // condition, so a while is not provably short from i < 4 alone.
        LongLoopSite site = SingleSite("int i = 0; while (i < 4) { xs[i] = i; i++; }");
        Assert.Equal(LoopKind.While, site.Kind);
        Assert.True(site.IsLong);
    }

    // -----------------------------------------------------------------
    // do/while: short only when condition is constant false.
    // -----------------------------------------------------------------

    [Fact]
    public void DoWhile_ConstantFalse_IsShort()
    {
        LongLoopSite site = SingleSite("do { xs[0] = 1; } while (false);");
        Assert.Equal(LoopKind.DoWhile, site.Kind);
        Assert.False(site.IsLong);
    }

    [Fact]
    public void DoWhile_RelationalCondition_IsLong()
    {
        LongLoopSite site = SingleSite("int i = 0; do { xs[i] = i; i++; } while (i < 4);");
        Assert.Equal(LoopKind.DoWhile, site.Kind);
        Assert.True(site.IsLong);
    }

    // -----------------------------------------------------------------
    // foreach: always long (count is a runtime property).
    // -----------------------------------------------------------------

    [Fact]
    public void ForEach_IsAlwaysLong()
    {
        LongLoopSite site = SingleSite("foreach (int x in xs) { xs[0] = x; }");
        Assert.Equal(LoopKind.ForEach, site.Kind);
        Assert.True(site.IsLong);
    }

    // -----------------------------------------------------------------
    // Multi-site + determinism.
    // -----------------------------------------------------------------

    [Fact]
    public void MultipleLoops_AreRecordedInDocumentOrder_WithCorrectKindsAndClassification()
    {
        Pass3Result result = Analyze(
            "namespace M; public class A { public void F(int n, int[] xs) { "
            + "for (int i = 0; i < 4; i++) { xs[i] = i; } "       // short for
            + "for (int i = 0; i < n; i++) { xs[i] = i; } "       // long for
            + "while (true) { break; } "                          // long while
            + "foreach (int x in xs) { xs[0] = x; } "             // long foreach
            + "} }");

        IReadOnlyList<LongLoopSite> sites = result.GetAll<LongLoopSite>();
        Assert.Equal(4, sites.Count);

        Assert.Equal(LoopKind.For, sites[0].Kind);
        Assert.False(sites[0].IsLong);

        Assert.Equal(LoopKind.For, sites[1].Kind);
        Assert.True(sites[1].IsLong);

        Assert.Equal(LoopKind.While, sites[2].Kind);
        Assert.True(sites[2].IsLong);

        Assert.Equal(LoopKind.ForEach, sites[3].Kind);
        Assert.True(sites[3].IsLong);
    }

    [Fact]
    public void NoLoops_RecordsNoSites()
    {
        Pass3Result result = Analyze("namespace M; public class A { public int F() => 1; }");
        Assert.Empty(result.GetAll<LongLoopSite>());
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Analysis_IsDeterministicAcrossTwoRuns()
    {
        string[] sources =
        {
            "namespace M; public class A { public void F(int n, int[] xs) { "
            + "for (int i = 0; i < 4; i++) { xs[i] = i; } "
            + "int j = 0; while (j < n) { j++; } "
            + "} }",
        };

        Pass3Result first = Analyze(sources);
        Pass3Result second = Analyze(sources);

        IReadOnlyList<LongLoopSite> firstSites = first.GetAll<LongLoopSite>();
        IReadOnlyList<LongLoopSite> secondSites = second.GetAll<LongLoopSite>();

        Assert.Equal(
            firstSites.Select(s => (s.Kind, s.IsLong, s.Span.StartLine, s.Span.StartColumn)).ToList(),
            secondSites.Select(s => (s.Kind, s.IsLong, s.Span.StartLine, s.Span.StartColumn)).ToList());
    }

    // -----------------------------------------------------------------
    // Discovery: the analyzer is reflection-picked-up by the driver.
    // -----------------------------------------------------------------

    [Fact]
    public void Discovery_PicksUpLongLoopAnalyzer()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();
        Assert.Contains(discovered, a => a is LongLoopAnalyzer);
    }

    [Fact]
    public void ShortLoopThreshold_Is16()
    {
        Assert.Equal(16, LongLoopAnalyzer.ShortLoopThreshold);
    }
}
