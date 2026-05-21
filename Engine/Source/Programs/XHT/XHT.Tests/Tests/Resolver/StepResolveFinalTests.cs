// Copyright Simgenics. All Rights Reserved.

using System.Linq;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Manifest;
using Simgenics.XPact.XHT.Resolver;
using Simgenics.XPact.XHT.Tables;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Resolver;

/// <summary>
/// Tests for <see cref="ResolvePhase.Final"/> via
/// <c>StepResolveFinal</c>: specifier-conflict validators
/// (XHT111-XHT117), cross-tier dep validation (XHT120/XHT121).
/// </summary>
public class StepResolveFinalTests
{
    private static ResolverPipeline Pipeline(
        SymbolTable symbols,
        XbtManifest? manifestOverride = null)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = manifestOverride ?? ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void CleanClass_NoSpecifierConflicts_NoErrors()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve");
        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void Xht115_IntrinsicWithGeneratedBody()
    {
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            specifiers: new[] { ResolverTestHarness.Flag("Intrinsic") },
            hasGeneratedBody: true);

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.IntrinsicWithGeneratedBody);
    }

    [Fact]
    public void Xht116_MinimalApiWithRequiredApi()
    {
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            specifiers: new[] { ResolverTestHarness.Flag("MinimalAPI") },
            requiredApiMacroName: "XSCORING_API");

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.MinimalApiWithRequiredApi);
    }

    [Fact]
    public void Xht117_NoExportWithBlueprintable()
    {
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            specifiers: new[]
            {
                ResolverTestHarness.Flag("NoExport"),
                ResolverTestHarness.Flag("Blueprintable"),
            });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.NoExportWithBlueprintable);
    }

    [Fact]
    public void Xht114_NoExportWithConfig()
    {
        XhtClass cls = ResolverTestHarness.MakeClass(
            "Valve",
            specifiers: new[]
            {
                ResolverTestHarness.Flag("NoExport"),
                ResolverTestHarness.Value("Config", "Game"),
            });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.ConfigConflictsWithNoExport);
    }

    [Fact]
    public void Xht111_ServerPlusClientPlusNetMulticast()
    {
        XhtFunction fn = ResolverTestHarness.MakeFunction(
            "Fire",
            specifiers: new[]
            {
                ResolverTestHarness.Flag("Server"),
                ResolverTestHarness.Flag("Client"),
                ResolverTestHarness.Flag("NetMulticast"),
            });
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", functions: new[] { fn });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.FunctionSpecifierConflict);
    }

    [Fact]
    public void Xht111_ServerAlone_NoError()
    {
        XhtFunction fn = ResolverTestHarness.MakeFunction(
            "Fire",
            specifiers: new[] { ResolverTestHarness.Flag("Server") });
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", functions: new[] { fn });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.FunctionSpecifierConflict);
    }

    [Fact]
    public void Xht112_EditAnywherePlusTransient()
    {
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[]
            {
                ResolverTestHarness.Flag("EditAnywhere"),
                ResolverTestHarness.Flag("Transient"),
            });
        XhtClass cls = ResolverTestHarness.MakeClass("Valve", properties: new[] { p });

        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.PropertySpecifierConflict);
    }

    [Fact]
    public void Xht112_ReplicatedOnStruct_EmitsConflict()
    {
        XhtProperty p = ResolverTestHarness.MakeProperty(
            "Count",
            "int32",
            specifiers: new[] { ResolverTestHarness.Flag("Replicated") });
        XhtStruct st = ResolverTestHarness.MakeStruct("Vec", properties: new[] { p });

        SymbolTable t = new();
        t.Register(st);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.PropertySpecifierConflict);
    }

    [Fact]
    public void Xht121_PropertyTypeInInterfaceOnlyDep()
    {
        // Set up two modules: consumer (XScoring) and producer (XCore).
        // XCore is declared as an interface-only dep -> property
        // references through it surface XHT121.
        XbtModule producer = ResolverTestHarness.MakeModule("XCore");
        XbtManifest manifest = ResolverTestHarness.MakeManifest(
            deps: new[] { new XbtModuleDep("XCore", InterfaceModule: true) },
            extraModules: new[] { producer });

        XhtClass producerType = ResolverTestHarness.MakeClass("BaseActor", module: "XCore");
        XhtProperty p = ResolverTestHarness.MakeProperty("Ref", "BaseActor");
        XhtClass consumer = ResolverTestHarness.MakeClass(
            "Valve",
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(producerType);
        t.Register(consumer);

        ResolverPipeline pipeline = Pipeline(t, manifestOverride: manifest);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.DynamicOnlyModuleReference);
    }

    [Fact]
    public void CrossModuleRef_WithProperDep_NoError()
    {
        // XCore declared as a LINK dep (InterfaceModule=false) -> property
        // ref through it is fine.
        XbtModule producer = ResolverTestHarness.MakeModule("XCore");
        XbtManifest manifest = ResolverTestHarness.MakeManifest(
            deps: new[] { new XbtModuleDep("XCore", InterfaceModule: false) },
            extraModules: new[] { producer });

        XhtClass producerType = ResolverTestHarness.MakeClass("BaseActor", module: "XCore");
        XhtProperty p = ResolverTestHarness.MakeProperty("Ref", "BaseActor");
        XhtClass consumer = ResolverTestHarness.MakeClass(
            "Valve",
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(producerType);
        t.Register(consumer);

        ResolverPipeline pipeline = Pipeline(t, manifestOverride: manifest);
        pipeline.ResolveAll();

        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.DynamicOnlyModuleReference);
        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.CrossLanguagePairingMismatch);
    }

    [Fact]
    public void CrossModuleRef_NoDepDeclared_EmitsXht120()
    {
        // No dep declared for XCore -> XHT120-style cross-module error.
        XbtModule producer = ResolverTestHarness.MakeModule("XCore");
        XbtManifest manifest = ResolverTestHarness.MakeManifest(
            deps: System.Array.Empty<XbtModuleDep>(),
            extraModules: new[] { producer });

        XhtClass producerType = ResolverTestHarness.MakeClass("BaseActor", module: "XCore");
        XhtProperty p = ResolverTestHarness.MakeProperty("Ref", "BaseActor");
        XhtClass consumer = ResolverTestHarness.MakeClass(
            "Valve",
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(producerType);
        t.Register(consumer);

        ResolverPipeline pipeline = Pipeline(t, manifestOverride: manifest);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.CrossLanguagePairingMismatch);
    }

    [Fact]
    public void Final_PhaseCurrent_AfterResolveAll()
    {
        XhtClass cls = ResolverTestHarness.MakeClass("Valve");
        SymbolTable t = new();
        t.Register(cls);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Equal(ResolvePhase.Final, pipeline.Context.CurrentPhase);
    }

    // -----------------------------------------------------------------
    // Round-2 audit M4: confirm A/U/I/F prefix is dropped from the
    // LooksLikeReflectableTypeName heuristic. Only X-prefix triggers
    // the "should be reflected" path; legacy UE prefixes don't.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("AActor")]
    [InlineData("UObject")]
    [InlineData("IInterface")]
    [InlineData("FCustomThunkTemplates")]
    public void LegacyUEPrefix_PropertyTypeReference_NoXht120(string typeName)
    {
        // A property type referencing a legacy A/U/I/F-prefixed name
        // that doesn't resolve in the symbol table must NOT emit XHT120
        // -- the heuristic only flags X-prefix names per the Round-1
        // user-locked decision (kept in Round-2 audit M4).
        XhtProperty p = ResolverTestHarness.MakeProperty("Target", typeName);
        XhtClass owner = ResolverTestHarness.MakeClass("Valve",
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.DoesNotContain(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.CrossLanguagePairingMismatch);
    }

    [Fact]
    public void XPrefix_UnresolvedPropertyType_EmitsXht120()
    {
        // Positive case: an X-prefixed type reference that doesn't
        // resolve in the symbol table fires XHT120 per the heuristic.
        XhtProperty p = ResolverTestHarness.MakeProperty("Target", "XPickup");
        XhtClass owner = ResolverTestHarness.MakeClass("Valve",
            properties: new[] { p });

        SymbolTable t = new();
        t.Register(owner);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveAll();

        Assert.Contains(pipeline.Context.Diagnostics,
            d => d.Code == DiagnosticCodes.CrossLanguagePairingMismatch);
    }
}
