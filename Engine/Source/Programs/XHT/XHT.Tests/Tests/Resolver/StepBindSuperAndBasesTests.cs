// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolvePhase.BindSuperAndBases"/> via
/// <c>StepBindSuperAndBases</c>.
/// </summary>
public class StepBindSuperAndBasesTests
{
    private static ResolverPipeline Pipeline(SymbolTable symbols)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void ClassSuper_Resolves_PopulatesResolvedSupers()
    {
        XhtClass parent = ResolverTestHarness.MakeClass("Actor");
        XhtClass child = ResolverTestHarness.MakeClass("Valve", superIdentifier: "Actor");

        SymbolTable t = new();
        t.Register(parent);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Same(parent, pipeline.Context.ResolvedSupers[child]);
        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
    }

    [Fact]
    public void StructSuper_Resolves_PopulatesResolvedSupers()
    {
        XhtStruct parent = ResolverTestHarness.MakeStruct("BaseVec");
        XhtStruct child = ResolverTestHarness.MakeStruct("Vec3", superIdentifier: "BaseVec");

        SymbolTable t = new();
        t.Register(parent);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Same(parent, pipeline.Context.ResolvedSupers[child]);
    }

    [Fact]
    public void InterfaceSuper_Resolves_PopulatesResolvedSupers()
    {
        XhtClass parentCompanion = ResolverTestHarness.MakeClass("UBaseIface");
        XhtClass childCompanion = ResolverTestHarness.MakeClass("UDerivedIface");
        XhtInterface parent = ResolverTestHarness.MakeInterface("XBaseIface");
        XhtInterface child = ResolverTestHarness.MakeInterface("XDerivedIface", superIdentifier: "XBaseIface");

        SymbolTable t = new();
        t.Register(parentCompanion);
        t.Register(childCompanion);
        t.Register(parent);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        // Note: the symbol table's Lookup returns the first-registered
        // hit for the caseless key. Both parentCompanion (UBaseIface)
        // and parent (XBaseIface) fold to different keys; the parent
        // interface resolution should find its own registered entry.
        Assert.Same(parent, pipeline.Context.ResolvedSupers[child]);
    }

    [Fact]
    public void MissingSuper_EmitsXht103()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", superIdentifier: "DoesNotExist");

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
        Assert.False(pipeline.Context.ResolvedSupers.ContainsKey(cls));
    }

    [Fact]
    public void WrongKindSuper_EmitsXht104()
    {
        // A class whose SuperIdentifier resolves to a struct entity.
        XhtStruct wrongKind = ResolverTestHarness.MakeStruct("MisplacedStruct");
        XhtClass child = ResolverTestHarness.MakeClass("Valve", superIdentifier: "MisplacedStruct");

        SymbolTable t = new();
        t.Register(wrongKind);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SuperWrongKind);
    }

    [Fact]
    public void EngineAnchor_AsSuper_NoErrorEvenWhenUnregistered()
    {
        // XObject is an engine anchor; it is intentionally not in the
        // parsed symbol table. A class extending XObject must NOT
        // produce XHT103.
        XhtClass cls = ResolverTestHarness.MakeClass("MyComponent", superIdentifier: "XObject");

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Empty(pipeline.Context.Diagnostics);
        Assert.False(pipeline.Context.ResolvedSupers.ContainsKey(cls));
    }

    [Fact]
    public void Chain_Depth3_AllResolve()
    {
        XhtClass grand = ResolverTestHarness.MakeClass("Actor");
        XhtClass mid = ResolverTestHarness.MakeClass("Pawn", superIdentifier: "Actor");
        XhtClass leaf = ResolverTestHarness.MakeClass("Valve", superIdentifier: "Pawn");

        SymbolTable t = new();
        t.Register(grand);
        t.Register(mid);
        t.Register(leaf);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Same(grand, pipeline.Context.ResolvedSupers[mid]);
        Assert.Same(mid, pipeline.Context.ResolvedSupers[leaf]);
        // Grand has no super -> no entry.
        Assert.False(pipeline.Context.ResolvedSupers.ContainsKey(grand));
    }

    [Fact]
    public void ClassWithoutSuper_NoEntryInResolvedSupers()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Root");
        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Empty(pipeline.Context.ResolvedSupers);
    }

    [Fact]
    public void MissingSuper_DiagnosticIncludesSuperName()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", superIdentifier: "AMissingActor");
        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        DiagnosticRecord d = pipeline.Context.Diagnostics
            .First(x => x.Code == DiagnosticCodes.SymbolNotFound);
        Assert.Contains("AMissingActor", d.Message, System.StringComparison.Ordinal);
        Assert.Contains("Valve", d.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void StructWithMissingSuper_EmitsXht103()
    {
        XhtStruct st = ResolverTestHarness.MakeStruct("Sub", superIdentifier: "MissingBase");
        SymbolTable t = new();
        t.Register(st);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.BindSuperAndBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
    }
}
