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
/// Tests for <see cref="ResolvePhase.Pairings"/> via
/// <c>StepResolvePairings</c>. The partial-class merge tests cover the
/// C3 audit fix (XHT.html Section 3.3): duplicates collected in
/// <see cref="ResolverContext.ExtraPartials"/> are unioned into the
/// canonical entry.
/// </summary>
public class StepResolvePairingsTests
{
    private static ResolverPipeline Pipeline(SymbolTable symbols)
    {
        SpecifierRegistry registry = new(registerBuiltIns: true);
        XbtManifest manifest = ResolverTestHarness.MakeManifest();
        return new ResolverPipeline(symbols, registry, manifest, ResolverTestHarness.TestModule);
    }

    [Fact]
    public void Interface_WithExplicitPairedClass_Pairs()
    {
        // A parser-emitted "PairedClass=<name>" specifier explicitly
        // names the companion. The class is registered under its own
        // caseless key (different from the interface's), and the
        // resolver pairs them.
        XhtClass companion = ResolverTestHarness.MakeClass("Companion");
        XhtInterface iface = new(
            Name: "OrphanIface",
            FullyQualifiedName: "OrphanIface",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.Cpp,
            Span: ResolverTestHarness.Span(),
            Specifiers: new[] { ResolverTestHarness.Value("PairedClass", "Companion") },
            SuperIdentifier: null,
            Super: null,
            Functions: System.Array.Empty<XhtFunction>());

        SymbolTable t = new();
        t.Register(companion);
        t.Register(iface);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        Assert.Same(companion, pipeline.Context.InterfacePairings[iface]);
        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.InterfaceWithoutPairedClass);
    }

    [Fact]
    public void Interface_WithMissingPairedClass_EmitsXht100()
    {
        XhtInterface iface = new(
            Name: "OrphanIface",
            FullyQualifiedName: "OrphanIface",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.Cpp,
            Span: ResolverTestHarness.Span(),
            Specifiers: new[] { ResolverTestHarness.Value("PairedClass", "DoesNotExist") },
            SuperIdentifier: null,
            Super: null,
            Functions: System.Array.Empty<XhtFunction>());

        SymbolTable t = new();
        t.Register(iface);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        DiagnosticRecord d = Assert.Single(
            pipeline.Context.Diagnostics,
            x => x.Code == DiagnosticCodes.InterfaceWithoutPairedClass);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void Interface_WithoutPairingSpecifier_NoDiagnostic()
    {
        // Phase 1d treats no-companion as the normal case: an XhtInterface
        // carries both halves logically. No XHT100 should fire.
        XhtInterface iface = ResolverTestHarness.MakeInterface("ICleanInterface");

        SymbolTable t = new();
        t.Register(iface);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void CSharpInterface_NoCompanionNeeded()
    {
        // C# interfaces are first-class and don't need a companion.
        XhtInterface iface = ResolverTestHarness.MakeInterface("IRunnable", lang: Language.CSharp);

        SymbolTable t = new();
        t.Register(iface);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        Assert.Empty(pipeline.Context.Diagnostics);
    }

    [Fact]
    public void Partials_FromExtraPartials_MergedIntoCanonical_Properly()
    {
        // The walker registered partial A canonically and stashed B
        // in ExtraPartials. The pairings phase unions B's members
        // into the canonical and emits XHT143.
        XhtProperty pA = new(
            Name: "ValueA",
            TypeIdentifier: "int",
            Specifiers: System.Array.Empty<Specifier>(),
            IsContainer: false,
            RepNotifyFunctionName: null,
            Category: null,
            Span: ResolverTestHarness.Span());

        XhtProperty pB = new(
            Name: "ValueB",
            TypeIdentifier: "float",
            Specifiers: System.Array.Empty<Specifier>(),
            IsContainer: false,
            RepNotifyFunctionName: null,
            Category: null,
            Span: ResolverTestHarness.Span());

        XhtClass canonical = new(
            Name: "Inventory",
            FullyQualifiedName: "Sim.Inventory",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.CSharp,
            Span: new SourceSpan("Inventory.A.cs", 1, 1, 9),
            Specifiers: System.Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Functions: System.Array.Empty<XhtFunction>(),
            Properties: new[] { pA },
            InterfaceIdentifiers: System.Array.Empty<string>(),
            Interfaces: System.Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: null,
            HasGeneratedBody: true,
            IsPartial: true,
            PartialSourcePaths: new[] { "Inventory.A.cs" });

        XhtClass duplicate = canonical with
        {
            Span = new SourceSpan("Inventory.B.cs", 1, 1, 9),
            Properties = new[] { pB },
            PartialSourcePaths = new[] { "Inventory.B.cs" },
        };

        SymbolTable t = new();
        t.Register(canonical);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.Context.ExtraPartials.Add(duplicate);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        // The merged-symbol view holds the union, keyed by canonical.
        XhtTypeBase mergedRaw = pipeline.Context.GetEffectiveShape(t.Lookup("Inventory")!);
        XhtClass merged = Assert.IsType<XhtClass>(mergedRaw);

        // Properties from both partials are unioned.
        Assert.Equal(2, merged.Properties.Count);
        Assert.Contains(merged.Properties, p => p.Name == "ValueA");
        Assert.Contains(merged.Properties, p => p.Name == "ValueB");

        // PartialSourcePaths records both contributing files.
        Assert.NotNull(merged.PartialSourcePaths);
        Assert.Contains("Inventory.A.cs", merged.PartialSourcePaths!);
        Assert.Contains("Inventory.B.cs", merged.PartialSourcePaths!);

        // MergedPartials maps the duplicate back to the canonical.
        Assert.Same(canonical, pipeline.Context.MergedPartials[duplicate]);

        // Symbol table now holds the merged shape (not the canonical).
        Assert.Same(merged, t.Lookup("Inventory"));
    }

    [Fact]
    public void Partials_FromExtraPartials_EmitXht143InfoDiagnostic()
    {
        XhtClass canonical = new(
            Name: "Inventory",
            FullyQualifiedName: "Sim.Inventory",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.CSharp,
            Span: new SourceSpan("Inventory.A.cs", 1, 1, 9),
            Specifiers: System.Array.Empty<Specifier>(),
            SuperIdentifier: null,
            Super: null,
            Functions: System.Array.Empty<XhtFunction>(),
            Properties: System.Array.Empty<XhtProperty>(),
            InterfaceIdentifiers: System.Array.Empty<string>(),
            Interfaces: System.Array.Empty<XhtInterface>(),
            WithinIdentifier: null,
            WithinClass: null,
            RequiredAPIMacroName: null,
            HasGeneratedBody: true,
            IsPartial: true,
            PartialSourcePaths: new[] { "Inventory.A.cs" });

        XhtClass duplicate = canonical with
        {
            Span = new SourceSpan("Inventory.B.cs", 1, 1, 9),
            PartialSourcePaths = new[] { "Inventory.B.cs" },
        };

        SymbolTable t = new();
        t.Register(canonical);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.Context.ExtraPartials.Add(duplicate);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        DiagnosticRecord d = Assert.Single(
            pipeline.Context.Diagnostics,
            x => x.Code == DiagnosticCodes.PartialClassMerged);
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public void NoExtraPartials_NoMerge_NoDiagnostic()
    {
        XhtClass canonical = ResolverTestHarness.MakeClass("StandaloneClass", lang: Language.CSharp);

        SymbolTable t = new();
        t.Register(canonical);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        Assert.Empty(pipeline.Context.MergedPartials);
        Assert.Empty(pipeline.Context.MergedSymbolView);
        Assert.DoesNotContain(pipeline.Context.Diagnostics, d => d.Code == DiagnosticCodes.PartialClassMerged);
    }
}
