// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolvePhase.Properties"/> via
/// <c>StepResolveProperties</c>: property-type resolution + RepNotify
/// signature validation (XHT113).
/// </summary>
public class StepResolvePropertiesTests
{
    private static ResolverPipeline Pipeline(SymbolTable symbols)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void PropertyType_ResolvedToReflectedType_PopulatesMap()
    {
        XhtClass referent = ResolverTestHarness.MakeClass("Actor");
        XhtProperty p = ResolverTestHarness.MakeProperty("Target", "Actor");
        XhtClass owner = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(referent);
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Same(referent, pipeline.Context.ResolvedPropertyTypes[p]);
        Assert.Same(owner, pipeline.Context.PropertyContainers[p]);
    }

    [Fact]
    public void PropertyType_Primitive_NotInResolvedMap()
    {
        XhtProperty p = ResolverTestHarness.MakeProperty("Count", "int32");
        XhtClass owner = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.False(pipeline.Context.ResolvedPropertyTypes.ContainsKey(p));
        // No XHT103 -- primitive misses do not error at this phase.
    }

    [Fact]
    public void PointerProperty_ResolvesAfterStrippingAsterisk()
    {
        XhtClass referent = ResolverTestHarness.MakeClass("Actor");
        XhtProperty p = ResolverTestHarness.MakeProperty("Target", "Actor*");
        XhtClass owner = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(referent);
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Same(referent, pipeline.Context.ResolvedPropertyTypes[p]);
    }

    [Fact]
    public void ContainerProperty_InnerTypeResolves()
    {
        XhtClass referent = ResolverTestHarness.MakeClass("Item");
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Items", "TArray<Item>", isContainer: true);
        XhtClass owner = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(referent);
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Same(referent, pipeline.Context.ResolvedPropertyTypes[p]);
    }

    [Fact]
    public void TMap_InnerValueTypeResolves()
    {
        XhtClass referent = ResolverTestHarness.MakeClass("Item");
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Items", "TMap<int32, Item>", isContainer: true);
        XhtClass owner = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(referent);
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        // Value side (Item) is the one resolved per the Phase 1d
        // container heuristic.
        Assert.Same(referent, pipeline.Context.ResolvedPropertyTypes[p]);
    }

    [Fact]
    public void RepNotify_ZeroParamCallback_Resolves()
    {
        XhtFunction callback = ResolverTestHarness.MakeFunction("OnRep_Count");
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Value("ReplicatedUsing", "OnRep_Count") });
        XhtClass owner = ResolverTestHarness.MakeClass(
            "Valve",
            functions: new[] { callback },
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Same(callback, pipeline.Context.ResolvedRepNotifyMethods[p]);
    }

    [Fact]
    public void RepNotify_OneParamMatchingType_Resolves()
    {
        XhtFunction callback = ResolverTestHarness.MakeFunction(
            "OnRep_Count",
            parameters: new[] { ResolverTestHarness.MakeParam("OldVal", "int32") });
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Value("ReplicatedUsing", "OnRep_Count") });
        XhtClass owner = ResolverTestHarness.MakeClass(
            "Valve",
            functions: new[] { callback },
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Same(callback, pipeline.Context.ResolvedRepNotifyMethods[p]);
    }

    [Fact]
    public void RepNotify_MissingCallback_EmitsXht113()
    {
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Value("ReplicatedUsing", "OnRep_NotPresent") });
        XhtClass owner = ResolverTestHarness.MakeClass(
            "Valve",
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        var diag = pipeline.Context.Diagnostics
            .First(d => d.Code == DiagnosticCodes.RepNotifyInvalidSignature);
        Assert.Contains("OnRep_NotPresent", diag.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RepNotify_TwoParamCallback_EmitsXht113()
    {
        XhtFunction callback = ResolverTestHarness.MakeFunction(
            "OnRep_Count",
            parameters: new[]
            {
                ResolverTestHarness.MakeParam("OldVal", "int32"),
                ResolverTestHarness.MakeParam("Extra", "int32"),
            });
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Value("ReplicatedUsing", "OnRep_Count") });
        XhtClass owner = ResolverTestHarness.MakeClass(
            "Valve",
            functions: new[] { callback },
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RepNotifyInvalidSignature);
    }

    [Fact]
    public void RepNotify_ParamTypeMismatch_EmitsXht113()
    {
        XhtFunction callback = ResolverTestHarness.MakeFunction(
            "OnRep_Count",
            parameters: new[] { ResolverTestHarness.MakeParam("OldVal", "FString") });
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Value("ReplicatedUsing", "OnRep_Count") });
        XhtClass owner = ResolverTestHarness.MakeClass(
            "Valve",
            functions: new[] { callback },
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.RepNotifyInvalidSignature);
    }

    [Fact]
    public void RepNotify_ConstRefParam_AcceptedAsCompatible()
    {
        // C++ "const int32&" should normalize to "int32" and match.
        XhtFunction callback = ResolverTestHarness.MakeFunction(
            "OnRep_Count",
            parameters: new[] { ResolverTestHarness.MakeParam("OldVal", "const int32&") });
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Value("ReplicatedUsing", "OnRep_Count") });
        XhtClass owner = ResolverTestHarness.MakeClass(
            "Valve",
            functions: new[] { callback },
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Properties);

        Assert.Same(callback, pipeline.Context.ResolvedRepNotifyMethods[p]);
    }
}
