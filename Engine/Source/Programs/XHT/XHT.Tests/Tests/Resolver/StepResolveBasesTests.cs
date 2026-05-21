// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolvePhase.ResolveBases"/> via
/// <c>StepResolveBases</c>: interface-list resolution + Within
/// resolution + XHT119 within-compatible-with-super validation.
/// </summary>
public class StepResolveBasesTests
{
    private static ResolverPipeline Pipeline(SymbolTable symbols)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void SingleInterface_Resolves()
    {
        XhtClass companion = ResolverTestHarness.MakeClass("URunnable");
        XhtInterface iface = ResolverTestHarness.MakeInterface("XRunnable");
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            interfaceIdentifiers: new[] { "XRunnable" });

        SymbolTable t = new();
        t.Register(companion);
        t.Register(iface);
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.Same(iface, pipeline.Context.ResolvedInterfaces[cls][0]);
    }

    [Fact]
    public void MultipleInterfaces_AllResolve_InDeclarationOrder()
    {
        XhtClass companionA = ResolverTestHarness.MakeClass("UAlpha");
        XhtClass companionB = ResolverTestHarness.MakeClass("UBeta");
        XhtInterface a = ResolverTestHarness.MakeInterface("XAlpha");
        XhtInterface b = ResolverTestHarness.MakeInterface("XBeta");
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            interfaceIdentifiers: new[] { "XAlpha", "XBeta" });

        SymbolTable t = new();
        t.Register(companionA);
        t.Register(companionB);
        t.Register(a);
        t.Register(b);
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        var resolved = pipeline.Context.ResolvedInterfaces[cls];
        Assert.Equal(2, resolved.Count);
        Assert.Same(a, resolved[0]);
        Assert.Same(b, resolved[1]);
    }

    [Fact]
    public void MissingInterface_EmitsXht103()
    {
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            interfaceIdentifiers: new[] { "DoesNotExist" });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
    }

    [Fact]
    public void InterfaceIdentifier_ResolvesToClass_EmitsXht104()
    {
        // "RunnableImpl" is a class, not an interface; the resolver
        // must reject it as the wrong kind.
        XhtClass wrongKind = ResolverTestHarness.MakeClass("RunnableImpl");
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            interfaceIdentifiers: new[] { "RunnableImpl" });

        SymbolTable t = new();
        t.Register(wrongKind);
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SuperWrongKind);
    }

    [Fact]
    public void WithinResolves()
    {
        XhtClass outer = ResolverTestHarness.MakeClass("World");
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Inventory",
            withinIdentifier: "World");

        SymbolTable t = new();
        t.Register(outer);
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.Same(outer, pipeline.Context.ResolvedWithin[cls]);
    }

    [Fact]
    public void WithinUnknown_EmitsXht103()
    {
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Inventory",
            withinIdentifier: "DoesNotExist");

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.SymbolNotFound);
    }

    [Fact]
    public void WithinCompatibleWithSuper_NoXht119()
    {
        // Super has Within=World; child has Within=World too -> OK.
        XhtClass world = ResolverTestHarness.MakeClass("World");
        XhtClass parent = ResolverTestHarness.MakeClass("Container", withinIdentifier: "World");
        XhtClass child = ResolverTestHarness.MakeClass(
            "Inventory",
            superIdentifier: "Container",
            withinIdentifier: "World");

        SymbolTable t = new();
        t.Register(world);
        t.Register(parent);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.ClassWithinIncompatibleWithSuper);
    }

    [Fact]
    public void WithinIncompatibleWithSuper_EmitsXht119()
    {
        // Super has Within=World; child declares Within=SomethingElse
        // which is not derived-from-or-equal-to World -> XHT119.
        XhtClass world = ResolverTestHarness.MakeClass("World");
        XhtClass other = ResolverTestHarness.MakeClass("Universe");
        XhtClass parent = ResolverTestHarness.MakeClass("Container", withinIdentifier: "World");
        XhtClass child = ResolverTestHarness.MakeClass(
            "Inventory",
            superIdentifier: "Container",
            withinIdentifier: "Universe");

        SymbolTable t = new();
        t.Register(world);
        t.Register(other);
        t.Register(parent);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.ClassWithinIncompatibleWithSuper);
    }

    [Fact]
    public void WithinDerivedFromSuperWithin_NoXht119()
    {
        // World <- Region; parent has Within=World, child has Within=Region.
        // Region IS derived from World -> child compatible.
        // We need to set up: Region's super = World, then child's
        // Within = Region; parent's Within = World.
        XhtClass world = ResolverTestHarness.MakeClass("World");
        XhtClass region = ResolverTestHarness.MakeClass("Region", superIdentifier: "World");
        XhtClass parent = ResolverTestHarness.MakeClass("Container", withinIdentifier: "World");
        XhtClass child = ResolverTestHarness.MakeClass(
            "Inventory",
            superIdentifier: "Container",
            withinIdentifier: "Region");

        SymbolTable t = new();
        t.Register(world);
        t.Register(region);
        t.Register(parent);
        t.Register(child);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        // Child Within=Region; super (Container) Within=World.
        // Region's super chain reaches World -> compatible (derived-from).
        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.ClassWithinIncompatibleWithSuper);
    }

    [Fact]
    public void ClassWithoutWithin_NoEntryInResolvedWithin()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("NoWithin");
        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.ResolveBases);

        Assert.False(pipeline.Context.ResolvedWithin.ContainsKey(cls));
    }
}
