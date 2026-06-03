// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Tiering;

/// <summary>
/// Tests for <see cref="StableId"/> + the overall Pass-4 determinism (the
/// classification + serialization are byte-deterministic across runs) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 / 3.3 + the
/// X-IL2CPP-CSPATH-DET acceptance gate.
/// </summary>
public sealed class StableIdAndDeterminismTests
{
    [Fact]
    public void StableId_DistinguishesOverloads_ByParameterTypes()
    {
        const string source = """
            namespace M;
            internal class C
            {
                internal int F(int x) => x;
                internal int F(string s) => 0;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        // Two distinct stable ids for the two overloads (parameter types
        // disambiguate them).
        var fIds = table.Classifications
            .Where(c => c.Id.Value.Contains(".F("))
            .Select(c => c.Id.Value)
            .ToList();

        Assert.Equal(2, fIds.Count);
        Assert.Equal(2, fIds.Distinct().Count());
        Assert.Contains(fIds, id => id.Contains("int"));
        Assert.Contains(fIds, id => id.Contains("string"));
    }

    [Fact]
    public void StableId_FunctionDisplay_MatchesIdValue_ForPass4Functions()
    {
        const string source = """
            namespace M;
            internal class C
            {
                internal int G() => 1;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification g = TieringTestHelpers.ById(table, ".G(");
        // Pass 4 uses the same display string for the stable id and the
        // human-readable function display.
        Assert.Equal(g.Id.Value, g.FunctionDisplay);
    }

    [Fact]
    public void Classification_IsDeterministic_AcrossRuns()
    {
        // A corpus that touches every clause: exported, throws, CanThrow,
        // cross-module (both NoThrow and not), and a propagation chain.
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            internal static class Dep
            {
                [XExternalModule] public static void Plain() { }
                [XExternalModule] [XFunction(NoThrow = true)] public static void Safe() { }
            }
            public class Surface
            {
                public int Exported() => 1;
                private int Clean() => 2;
                private void Throws() { throw new System.InvalidOperationException(); }
                private void CallsThrows() => Throws();
                [CanThrow] private void Marked() { }
                private void CallsPlain() => Dep.Plain();
                private void CallsSafe() => Dep.Safe();
            }
            """;

        TierTable first = TieringTestHelpers.Classify(source);
        TierTable second = TieringTestHelpers.Classify(source);

        // Byte-identical serialization across two independent pipeline runs.
        Assert.Equal(first.Serialize(), second.Serialize());

        // Spot-check the verdicts the corpus encodes.
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(first, ".Exported(").Tier);
        Assert.Equal(FunctionTier.Tier2, TieringTestHelpers.ById(first, ".Clean(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(first, ".Throws(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(first, ".CallsThrows(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(first, ".Marked(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(first, ".CallsPlain(").Tier);
        Assert.Equal(FunctionTier.Tier2, TieringTestHelpers.ById(first, ".CallsSafe(").Tier);
    }

    [Fact]
    public void TierTable_ModuleName_IsTheUnitModuleName()
    {
        const string source = """
            namespace M;
            internal class C { internal int F() => 1; }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        // NormalizationTestHelpers builds the unit as module "TestModule".
        Assert.Equal("TestModule", table.ModuleName);
    }
}
