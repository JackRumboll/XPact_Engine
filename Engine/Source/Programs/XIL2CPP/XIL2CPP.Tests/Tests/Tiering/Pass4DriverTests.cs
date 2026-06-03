// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Tiering;

/// <summary>
/// Tests for <see cref="Pass4Driver"/> (XIL2CPP Phase 6.c) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.1 / 3.2 / 3.3 and
/// <c>/Documents/XIL2CPP-Constraints.md</c> Section 2.8. Each fixture drives a
/// synthetic C# source corpus through the real Pass 1 -&gt; Pass 2 -&gt;
/// Pass 3 -&gt; Pass 4 pipeline (<see cref="TieringTestHelpers.Classify"/>) and
/// asserts the per-function tier verdict + demotion reason. Cross-module
/// callees are modelled with the synthetic <c>[XExternalModule]</c> /
/// <c>[XFunction(NoThrow = ...)]</c> markers the
/// <c>CrossModuleNoThrowAnalyzer</c> recognizes.
/// </summary>
public sealed class Pass4DriverTests
{
    // ---------------------------------------------------------------
    // Scenario 1: an internal leaf method, no throw, no risky callee -> Tier 2.
    // ---------------------------------------------------------------

    [Fact]
    public void InternalLeaf_NoThrow_NoRiskyCallee_IsTier2()
    {
        const string source = """
            namespace M;
            internal class Leaf
            {
                internal int Compute() => 21 + 21;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".Compute(");
        Assert.Equal(FunctionTier.Tier2, c.Tier);
        Assert.Equal(string.Empty, c.DemotionReason);
    }

    // ---------------------------------------------------------------
    // Scenario 2: a method with a throw -> Tier 1 (reason: throws in body).
    // ---------------------------------------------------------------

    [Fact]
    public void MethodWithThrow_IsTier1_ReasonThrows()
    {
        const string source = """
            namespace M;
            internal class Leaf
            {
                internal void Boom()
                {
                    throw new System.InvalidOperationException();
                }
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".Boom(");
        Assert.Equal(FunctionTier.Tier1, c.Tier);
        Assert.Equal("throws in body", c.DemotionReason);
    }

    [Fact]
    public void MethodWithThrowExpression_IsTier1_ReasonThrows()
    {
        // A throw EXPRESSION (not statement) also demotes (clause d).
        const string source = """
            namespace M;
            internal class Leaf
            {
                internal int OrThrow(int? x) => x ?? throw new System.ArgumentNullException();
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".OrThrow(");
        Assert.Equal(FunctionTier.Tier1, c.Tier);
        Assert.Equal("throws in body", c.DemotionReason);
    }

    // ---------------------------------------------------------------
    // Scenario 3: a public/exported method -> Tier 1 (reason: exported).
    // ---------------------------------------------------------------

    [Fact]
    public void PublicExportedMethod_IsTier1_ReasonExported()
    {
        const string source = """
            namespace M;
            public class Surface
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".Add(");
        Assert.Equal(FunctionTier.Tier1, c.Tier);
        Assert.Equal("exported", c.DemotionReason);
    }

    [Fact]
    public void PrivateMethodInPublicClass_IsNotExported_StaysTier2()
    {
        // A private member of a public type is NOT externally reachable, so it
        // is not exported: the public-type exposure does not export it.
        const string source = """
            namespace M;
            public class Surface
            {
                private int Helper() => 7;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".Helper(");
        Assert.Equal(FunctionTier.Tier2, c.Tier);
    }

    // ---------------------------------------------------------------
    // Scenario 4: [XFunction(CanThrow = true)] / [CanThrow] -> Tier 1.
    // ---------------------------------------------------------------

    [Fact]
    public void XFunctionCanThrowMethod_IsTier1_ReasonCanThrow()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            internal class C
            {
                [XFunction(CanThrow = true)]
                internal int MayThrow() => 1;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".MayThrow(");
        Assert.Equal(FunctionTier.Tier1, c.Tier);
        Assert.Equal("[XFunction(CanThrow = true)]", c.DemotionReason);
    }

    [Fact]
    public void StandaloneCanThrowMethod_IsTier1_ReasonCanThrow()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            internal class C
            {
                [CanThrow]
                internal int MayThrow() => 1;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification c = TieringTestHelpers.ById(table, ".MayThrow(");
        Assert.Equal(FunctionTier.Tier1, c.Tier);
        Assert.Equal("[CanThrow]", c.DemotionReason);
    }

    // ---------------------------------------------------------------
    // Scenario 5: Tier-1 PROPAGATION (fixpoint clause c). A calls B; B throws
    // -> both Tier 1. B's reason is "throws in body"; A's reason names B.
    // ---------------------------------------------------------------

    [Fact]
    public void Tier1Propagation_CallerOfThrowingCallee_IsTier1()
    {
        const string source = """
            namespace M;
            internal class C
            {
                internal void A() => B();
                internal void B() { throw new System.InvalidOperationException(); }
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification b = TieringTestHelpers.ById(table, ".B(");
        Assert.Equal(FunctionTier.Tier1, b.Tier);
        Assert.Equal("throws in body", b.DemotionReason);

        TierClassification a = TieringTestHelpers.ById(table, ".A(");
        Assert.Equal(FunctionTier.Tier1, a.Tier);
        Assert.Contains("Tier-1 callee", a.DemotionReason);
        Assert.Contains(".B(", a.DemotionReason);
    }

    // ---------------------------------------------------------------
    // Scenario 6: cross-module callee WITHOUT NoThrow -> caller conservative
    // Tier 1; WITH NoThrow -> caller may stay Tier 2.
    // ---------------------------------------------------------------

    [Fact]
    public void CrossModuleCallee_WithoutNoThrow_DemotesCallerToTier1()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            internal static class Dep
            {
                [XExternalModule]
                public static void PlainCallee() { }
            }
            internal class C
            {
                internal void Run() => Dep.PlainCallee();
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification run = TieringTestHelpers.ById(table, ".Run(");
        Assert.Equal(FunctionTier.Tier1, run.Tier);
        Assert.Contains("cross-module", run.DemotionReason);
        Assert.Contains("PlainCallee", run.DemotionReason);
    }

    [Fact]
    public void CrossModuleCallee_WithNoThrow_LeavesCallerTier2()
    {
        const string source = """
            using XPact.CoreXObject;
            namespace M;
            internal static class Dep
            {
                [XExternalModule]
                [XFunction(NoThrow = true)]
                public static void SafeCallee() { }
            }
            internal class C
            {
                internal void Run() => Dep.SafeCallee();
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification run = TieringTestHelpers.ById(table, ".Run(");
        Assert.Equal(FunctionTier.Tier2, run.Tier);
        Assert.Equal(string.Empty, run.DemotionReason);
    }

    // ---------------------------------------------------------------
    // Scenario 7: fixpoint CONVERGENCE over a multi-link chain. A -> B -> C,
    // C throws -> all three Tier 1 (the demotion propagates up the chain).
    // ---------------------------------------------------------------

    [Fact]
    public void FixpointConvergence_OverMultiLinkChain_DemotesEntireChain()
    {
        const string source = """
            namespace M;
            internal class Chain
            {
                internal void A() => B();
                internal void B() => C();
                internal void C() => D();
                internal void D() { throw new System.InvalidOperationException(); }
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(table, ".D(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(table, ".C(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(table, ".B(").Tier);
        Assert.Equal(FunctionTier.Tier1, TieringTestHelpers.ById(table, ".A(").Tier);

        // D is the root cause (throws); the rest are clause-(c) propagation.
        Assert.Equal("throws in body", TieringTestHelpers.ById(table, ".D(").DemotionReason);
        Assert.Contains("Tier-1 callee", TieringTestHelpers.ById(table, ".A(").DemotionReason);
    }

    [Fact]
    public void FixpointConvergence_CleanChain_StaysAllTier2()
    {
        // The mirror image: a clean internal chain with no throw / risky callee
        // converges with every link Tier 2.
        const string source = """
            namespace M;
            internal class Chain
            {
                internal int A() => B();
                internal int B() => C();
                internal int C() => 3;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        Assert.Equal(FunctionTier.Tier2, TieringTestHelpers.ById(table, ".A(").Tier);
        Assert.Equal(FunctionTier.Tier2, TieringTestHelpers.ById(table, ".B(").Tier);
        Assert.Equal(FunctionTier.Tier2, TieringTestHelpers.ById(table, ".C(").Tier);
    }

    // ---------------------------------------------------------------
    // Accessors, local functions, lambdas are enumerated as emittable
    // functions and classified independently.
    // ---------------------------------------------------------------

    [Fact]
    public void Accessor_ExpressionBodied_Internal_IsTier2()
    {
        const string source = """
            namespace M;
            internal class C
            {
                internal int Value => 5;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        // The getter accessor symbol renders as M.C.Value.get.
        TierClassification getter = TieringTestHelpers.ById(table, ".Value.get");
        Assert.Equal(FunctionTier.Tier2, getter.Tier);
    }

    [Fact]
    public void LocalFunction_ThatThrows_IsClassifiedSeparately_AndDoesNotDemoteEnclosing()
    {
        // A nested local function that throws is its own Tier-1 node; the
        // enclosing method, which does not itself throw and does not CALL the
        // local function, stays Tier 2 (the throw is attributed to the local
        // function only, never to the enclosing body).
        const string source = """
            namespace M;
            internal class C
            {
                internal int Outer()
                {
                    int Local() { throw new System.InvalidOperationException(); }
                    return 0;
                }
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        TierClassification local = TieringTestHelpers.ById(table, "Local(");
        Assert.Equal(FunctionTier.Tier1, local.Tier);
        Assert.Equal("throws in body", local.DemotionReason);

        TierClassification outer = TieringTestHelpers.ById(table, ".Outer(");
        Assert.Equal(FunctionTier.Tier2, outer.Tier);
    }
}
