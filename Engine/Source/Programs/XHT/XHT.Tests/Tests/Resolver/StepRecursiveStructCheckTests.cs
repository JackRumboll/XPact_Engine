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
}
