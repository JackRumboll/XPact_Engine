// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolverPipeline"/>: top-level orchestration,
/// phase-ordering enforcement, <see cref="ResolverPipeline.ResolveUpTo"/>
/// stop-points, and the deterministic walk order.
/// </summary>
public class ResolverPipelineTests
{
    private static (ResolverPipeline pipeline, SymbolTable symbols) MakePipeline(
        params XhtTypeBase[] types)
    {
        SymbolTable symbols = new();
        foreach (XhtTypeBase t in types)
        {
            symbols.Register(t);
        }
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        ResolverPipeline pipeline = new(symbols, registry, manifest, ResolverTestHarness.TestModule);
        return (pipeline, symbols);
    }

    [Fact]
    public void EmptySymbolTable_ResolveAll_ProducesNoDiagnostics()
    {
        var (pipeline, _) = MakePipeline();

        IReadOnlyList<DiagnosticRecord> diagnostics = pipeline.ResolveAll();

        Assert.Empty(diagnostics);
        Assert.Equal(ResolvePhase.Final, pipeline.Context.CurrentPhase);
    }

    [Fact]
    public void SingleClass_NoSuper_ProducesNoDiagnostics()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve");
        var (pipeline, _) = MakePipeline(cls);

        IReadOnlyList<DiagnosticRecord> diagnostics = pipeline.ResolveAll();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ResolveUpTo_None_RunsNoPhases()
    {
        var (pipeline, _) = MakePipeline();

        IReadOnlyList<DiagnosticRecord> diagnostics = pipeline.ResolveUpTo(ResolvePhase.None);

        Assert.Empty(diagnostics);
        Assert.Equal(ResolvePhase.None, pipeline.Context.CurrentPhase);
    }

    [Fact]
    public void ResolveUpTo_Pairings_StopsAtPairings()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", superIdentifier: "Missing");
        var (pipeline, _) = MakePipeline(cls);

        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        // We stopped before BindSuperAndBases; no XHT103 yet.
        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
        Assert.Equal(ResolvePhase.Pairings, pipeline.Context.CurrentPhase);
    }

    [Fact]
    public void ResolveUpTo_BindSuperAndBases_PopulatesResolvedSupers()
    {
        XhtClass parent = ResolverTestHarness.MakeClass("Actor");
        XhtClass child = ResolverTestHarness.MakeClass("Valve", superIdentifier: "Actor");
        var (pipeline, _) = MakePipeline(parent, child);

        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Single(pipeline.Context.ResolvedSupers);
        Assert.Same(parent, pipeline.Context.ResolvedSupers[child]);
        Assert.Equal(ResolvePhase.BindSuperAndBases, pipeline.Context.CurrentPhase);
    }

    [Fact]
    public void ResolveUpTo_ResolveBases_PopulatesInterfacesAndWithin()
    {
        // Use distinct caseless keys for interface / outer / class to
        // avoid symbol-table collisions.
        XhtInterface iface = ResolverTestHarness.MakeInterface("XRunnable");
        XhtClass outerCls = ResolverTestHarness.MakeClass("OuterClass");
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            interfaceIdentifiers: new[] { "XRunnable" },
            withinIdentifier: "OuterClass");

        var (pipeline, _) = MakePipeline(iface, outerCls, cls);

        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.True(pipeline.Context.ResolvedInterfaces.ContainsKey(cls));
        Assert.Same(iface, pipeline.Context.ResolvedInterfaces[cls][0]);
        Assert.Same(outerCls, pipeline.Context.ResolvedWithin[cls]);
    }

    [Fact]
    public void ResolveAll_RunsFinalPhase_DoesNotErrorOnEmptyConflicts()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve");
        var (pipeline, _) = MakePipeline(cls);

        IReadOnlyList<DiagnosticRecord> diagnostics = pipeline.ResolveAll();

        Assert.Empty(diagnostics);
        Assert.Equal(ResolvePhase.Final, pipeline.Context.CurrentPhase);
    }

    [Fact]
    public void OrderedTypesSnapshot_DeterministicByLangThenFqn()
    {
        XhtClass alpha = ResolverTestHarness.MakeClass("Alpha", lang: Language.Cpp);
        XhtClass beta = ResolverTestHarness.MakeClass("Beta", lang: Language.CSharp);
        XhtClass gamma = ResolverTestHarness.MakeClass("Gamma", lang: Language.Cpp);

        SymbolTable t = new();
        // Insertion order intentionally mixed.
        t.Register(beta);
        t.Register(gamma);
        t.Register(alpha);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(t);

        Assert.Equal(3, ordered.Count);
        Assert.Same(alpha, ordered[0]); // Cpp, "Alpha"
        Assert.Same(gamma, ordered[1]); // Cpp, "Gamma"
        Assert.Same(beta, ordered[2]);  // CSharp, "Beta"
    }

    [Fact]
    public void Diagnostics_AccessibleFromContextAndReturnValue_AreSame()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", superIdentifier: "Missing");
        var (pipeline, _) = MakePipeline(cls);

        IReadOnlyList<DiagnosticRecord> returned = pipeline.ResolveAll();

        Assert.NotEmpty(returned);
        Assert.Equal(pipeline.Context.Diagnostics.Count, returned.Count);
        for (int i = 0; i < returned.Count; i++)
        {
            Assert.Same(pipeline.Context.Diagnostics[i], returned[i]);
        }
    }

    [Fact]
    public void Pipeline_NullArgs_Throw()
    {
        SymbolTable s = new();
        SpecifierRegistry r = new(registerBuiltIns: false);
        XbtManifest m = ResolverTestHarness.MakeManifest();

        Assert.Throws<System.ArgumentNullException>(() => new ResolverPipeline(null!, r, m, "x"));
        Assert.Throws<System.ArgumentNullException>(() => new ResolverPipeline(s, null!, m, "x"));
        Assert.Throws<System.ArgumentNullException>(() => new ResolverPipeline(s, r, null!, "x"));
        Assert.Throws<System.ArgumentNullException>(() => new ResolverPipeline(s, r, m, null!));
    }

    [Fact]
    public void OrderedTypesSnapshot_Null_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() => ResolverPipeline.OrderedTypesSnapshot(null!));
    }

    [Fact]
    public void Diagnostics_ModuleField_PopulatedToConsumerModule()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", superIdentifier: "Missing");
        var (pipeline, _) = MakePipeline(cls);

        pipeline.ResolveAll();

        DiagnosticRecord first = pipeline.Context.Diagnostics.First();
        Assert.Equal(ResolverTestHarness.TestModule, first.Module);
    }
}
