// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolvePhase.InvalidCheck"/> via
/// <c>StepResolveInvalidCheck</c>. The check is narrow: it verifies
/// parser invariants (non-null lists on containers) and consolidates
/// the orphan-interface signal Pairings already emitted.
/// </summary>
public class StepResolveInvalidCheckTests
{
    private static ResolverPipeline Pipeline(SymbolTable symbols)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void ClassWithMembers_PassesInvalidCheck()
    {
        XhtFunction fn = ResolverTestHarness.MakeFunction("DoThing");
        XhtProperty p = ResolverTestHarness.MakeProperty("Count", "int32");
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            functions: new[] { fn },
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.InvalidCheck);

        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void StructWithProperties_PassesInvalidCheck()
    {
        XhtProperty p = ResolverTestHarness.MakeProperty("X", "float");
        XhtStruct st = ResolverTestHarness.MakeStruct("Vec3", properties: new[] { p });

        SymbolTable t = new();
        t.Register(st);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.InvalidCheck);

        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void InterfaceWithFunctions_PassesInvalidCheck()
    {
        XhtFunction fn = ResolverTestHarness.MakeFunction("OnTick");
        XhtInterface iface = ResolverTestHarness.MakeInterface("ITickable", functions: new[] { fn });

        SymbolTable t = new();
        t.Register(iface);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.InvalidCheck);

        // Phase 1d treats an interface without a separate companion
        // class as the normal case (the XhtInterface carries both
        // halves logically). Neither Pairings nor InvalidCheck emits.
        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void EnumTypes_AreNotChecked()
    {
        XhtEnum e = new(
            Name: "EColor",
            FullyQualifiedName: "EColor",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.Cpp,
            Span: ResolverTestHarness.Span(),
            Specifiers: System.Array.Empty<Specifier>(),
            UnderlyingType: "uint8",
            IsFlags: false,
            Values: System.Array.Empty<XhtEnumValue>());

        SymbolTable t = new();
        t.Register(e);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.InvalidCheck);

        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void EmptyClass_PassesInvalidCheck()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Empty");

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.InvalidCheck);

        Assert.Empty(pipeline.Context.Diagnostics);
    }
}
