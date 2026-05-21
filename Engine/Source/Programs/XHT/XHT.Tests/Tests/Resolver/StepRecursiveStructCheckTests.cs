// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolvePhase.RecursiveStructCheck"/> via
/// <c>StepRecursiveStructCheck</c>. The cycle detector walks Super
/// pointers populated by the preceding BindSuperAndBases phase.
/// </summary>
public class StepRecursiveStructCheckTests
{
    private static ResolverPipeline Pipeline(SymbolTable symbols)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void NoCycle_LinearChain_EmitsNothing()
    {
        XhtStruct a = ResolverTestHarness.MakeStruct("A");
        XhtStruct b = ResolverTestHarness.MakeStruct("B", superIdentifier: "A");
        XhtStruct c = ResolverTestHarness.MakeStruct("C", superIdentifier: "B");

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);
        t.Register(c);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void TwoNodeCycle_AToBToA_EmitsXht105()
    {
        // Build A->B and B->A. The cycle check should detect this.
        // SymbolTable doesn't reject inserting both (different
        // caseless keys), and the resolver populates Super pointers
        // for both in BindSuperAndBases.
        XhtStruct a = ResolverTestHarness.MakeStruct("A", superIdentifier: "B");
        XhtStruct b = ResolverTestHarness.MakeStruct("B", superIdentifier: "A");

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void ThreeNodeCycle_AToBToCToA_EmitsXht105()
    {
        XhtStruct a = ResolverTestHarness.MakeStruct("A", superIdentifier: "B");
        XhtStruct b = ResolverTestHarness.MakeStruct("B", superIdentifier: "C");
        XhtStruct c = ResolverTestHarness.MakeStruct("C", superIdentifier: "A");

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);
        t.Register(c);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void Cycle_DiagnosticMessageNamesTheChain()
    {
        XhtStruct a = ResolverTestHarness.MakeStruct("A", superIdentifier: "B");
        XhtStruct b = ResolverTestHarness.MakeStruct("B", superIdentifier: "A");

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        var cycle = pipeline.Context.Diagnostics.First(d => d.Code == DiagnosticCodes.RecursiveStructCycle);
        Assert.Contains("->", cycle.Message, System.StringComparison.Ordinal);
        Assert.Contains("A", cycle.Message, System.StringComparison.Ordinal);
        Assert.Contains("B", cycle.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SingleStructNoSuper_EmitsNothing()
    {
        XhtStruct s = ResolverTestHarness.MakeStruct("Solo");
        SymbolTable t = new();
        t.Register(s);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void Cycle_EmittedOncePerCycle_NotPerMember()
    {
        XhtStruct a = ResolverTestHarness.MakeStruct("A", superIdentifier: "B");
        XhtStruct b = ResolverTestHarness.MakeStruct("B", superIdentifier: "C");
        XhtStruct c = ResolverTestHarness.MakeStruct("C", superIdentifier: "A");

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);
        t.Register(c);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        int cycleCount = pipeline.Context.Diagnostics.Count(d => d.Code == DiagnosticCodes.RecursiveStructCycle);
        Assert.Equal(1, cycleCount);
    }

    [Fact]
    public void ClassInheritanceCycle_AlsoDetected()
    {
        // The check walks any super chain, not just structs.
        XhtClass a = ResolverTestHarness.MakeClass("CA", superIdentifier: "CB");
        XhtClass b = ResolverTestHarness.MakeClass("CB", superIdentifier: "CA");

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    // -----------------------------------------------------------------
    // Round-2 audit C3: field-type cycle detection. The Rev 6 phase
    // ordering puts RecursiveStructCheck AFTER Properties so the walker
    // can traverse the resolved field-type graph.
    // -----------------------------------------------------------------

    [Fact]
    public void FieldCycle_MutualValueTypedStructs_EmitsXht105()
    {
        // struct A { B b; }; struct B { A a; };
        // Both fields are value-typed (no pointer/reference indirection)
        // so the walker MUST detect the cycle.
        XhtProperty bField = ResolverTestHarness.MakeProperty("b", "B");
        XhtProperty aField = ResolverTestHarness.MakeProperty("a", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bField });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { aField });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.Contains(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_SelfReference_ValueTypedStruct_EmitsXht105()
    {
        // struct A { A a; }; -- direct self-reference via value-typed
        // field. UHT calls this the most-basic recursion case.
        XhtProperty selfField = ResolverTestHarness.MakeProperty("self", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { selfField });

        SymbolTable t = new();
        t.Register(a);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.Contains(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_ThreeNodeRing_EmitsXht105()
    {
        // struct A { B b; }; struct B { C c; }; struct C { A a; };
        XhtProperty bf = ResolverTestHarness.MakeProperty("b", "B");
        XhtProperty cf = ResolverTestHarness.MakeProperty("c", "C");
        XhtProperty af = ResolverTestHarness.MakeProperty("a", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bf });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { cf });
        XhtStruct c = ResolverTestHarness.MakeStruct("C", properties: new[] { af });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);
        t.Register(c);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.Contains(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_PointerBreaksCycle_DoesNotEmitXht105()
    {
        // struct A { B* b; }; struct B { A a; };
        // The pointer on A->B breaks the value-typed cycle: B's bytes are
        // not embedded in A's footprint. UHT semantics: indirection
        // through a pointer is allowed; only value-typed cycles are an
        // error.
        XhtProperty bField = ResolverTestHarness.MakeProperty("b", "B*");
        XhtProperty aField = ResolverTestHarness.MakeProperty("a", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bField });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { aField });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.DoesNotContain(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_ReferenceBreaksCycle_DoesNotEmitXht105()
    {
        // Reference (Foo&) is also indirection; cycle does not fire.
        XhtProperty bField = ResolverTestHarness.MakeProperty("b", "B&");
        XhtProperty aField = ResolverTestHarness.MakeProperty("a", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bField });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { aField });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.DoesNotContain(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_ContainerBreaksCycle_DoesNotEmitXht105()
    {
        // TArray<B> stores B elements indirectly (heap allocation);
        // the container's own footprint is a pointer + size, so the
        // cycle does not fire.
        XhtProperty bField = ResolverTestHarness.MakeProperty(
            "bs", "TArray<B>", isContainer: true);
        XhtProperty aField = ResolverTestHarness.MakeProperty("a", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bField });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { aField });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.DoesNotContain(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_LinearChainNoLoop_DoesNotEmitXht105()
    {
        // struct A { B b; }; struct B { C c; }; struct C { int32 x; };
        // No cycle.
        XhtProperty bField = ResolverTestHarness.MakeProperty("b", "B");
        XhtProperty cField = ResolverTestHarness.MakeProperty("c", "C");
        XhtProperty xField = ResolverTestHarness.MakeProperty("x", "int32");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bField });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { cField });
        XhtStruct c = ResolverTestHarness.MakeStruct("C", properties: new[] { xField });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);
        t.Register(c);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        Assert.DoesNotContain(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
    }

    [Fact]
    public void FieldCycle_DiagnosticMessageNamesTheCycle()
    {
        XhtProperty bField = ResolverTestHarness.MakeProperty("b", "B");
        XhtProperty aField = ResolverTestHarness.MakeProperty("a", "A");
        XhtStruct a = ResolverTestHarness.MakeStruct("A", properties: new[] { bField });
        XhtStruct b = ResolverTestHarness.MakeStruct("B", properties: new[] { aField });

        SymbolTable t = new();
        t.Register(a);
        t.Register(b);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        var cycle = pipeline.Context.Diagnostics.First(
            d => d.Code == DiagnosticCodes.RecursiveStructCycle);
        Assert.Contains("A", cycle.Message, System.StringComparison.Ordinal);
        Assert.Contains("B", cycle.Message, System.StringComparison.Ordinal);
        Assert.Contains("->", cycle.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_ReorderedAfterProperties_ResolvedPropertyTypesAvailable()
    {
        // Round-2 audit C3: assert the resolver context's
        // ResolvedPropertyTypes is populated WHEN RecursiveStructCheck
        // runs. If the phase order regressed (e.g., a future maintainer
        // moved it back to between BindSuperAndBases and ResolveBases),
        // the map would be empty and field-cycle detection would silently
        // become a no-op. Catch the regression here.
        XhtClass referent = ResolverTestHarness.MakeClass("Actor");
        XhtProperty p = ResolverTestHarness.MakeProperty("a", "Actor");
        XhtClass owner = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(referent);
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.RecursiveStructCheck);

        // After the reordered phase runs, the property type map is
        // guaranteed populated because Properties ran first.
        Assert.True(pipeline.Context.ResolvedPropertyTypes.ContainsKey(p));
        Assert.Same(referent, pipeline.Context.ResolvedPropertyTypes[p]);
    }
}
