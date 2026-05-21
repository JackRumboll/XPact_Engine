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
/// <c>StepResolvePairings</c>.
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
    public void PartialClasses_SameFqn_MergeIntoCanonical()
    {
        // Two C# classes with identical FullyQualifiedName but different
        // caseless keys (via different source names) populate the
        // symbol table independently. The Pairings phase records the
        // second as a partial-class duplicate.
        XhtClass canonical = new(
            Name: "InventoryA",
            FullyQualifiedName: "Sim.Inventory",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.CSharp,
            Span: ResolverTestHarness.Span(),
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
            HasGeneratedBody: false);

        XhtClass duplicate = new(
            Name: "InventoryB",
            FullyQualifiedName: "Sim.Inventory",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.CSharp,
            Span: ResolverTestHarness.Span(),
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
            HasGeneratedBody: false);

        SymbolTable t = new();
        t.Register(canonical);
        t.Register(duplicate);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        Assert.Single(pipeline.Context.MergedPartials);
        // The first-encountered (in deterministic walk order) is the
        // canonical; the second is the duplicate.
        var entry = pipeline.Context.MergedPartials.Single();
        Assert.NotSame(entry.Key, entry.Value);
        Assert.Equal("Sim.Inventory", entry.Key.FullyQualifiedName);
        Assert.Equal("Sim.Inventory", entry.Value.FullyQualifiedName);
    }

    [Fact]
    public void PartialClasses_EmitXht143InfoDiagnostic()
    {
        XhtClass canonical = new(
            Name: "PartialA",
            FullyQualifiedName: "Sim.Partial",
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.CSharp,
            Span: ResolverTestHarness.Span(),
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
            HasGeneratedBody: false);

        XhtClass duplicate = canonical with { Name = "PartialB" };

        SymbolTable t = new();
        t.Register(canonical);
        t.Register(duplicate);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        DiagnosticRecord d = Assert.Single(
            pipeline.Context.Diagnostics,
            x => x.Code == DiagnosticCodes.PartialClassMerged);
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
    }

    [Fact]
    public void CppPartialClasses_DoNotMerge()
    {
        // Partial-class merge is a C#-only behaviour. C++ does not
        // have partial classes; two C++ classes with the same FQN
        // would be a parser-side collision and should not be merged
        // here even if they sneak through.
        XhtClass canonical = ResolverTestHarness.MakeClass("FooA", lang: Language.Cpp);
        XhtClass other = new(
            Name: "FooB",
            FullyQualifiedName: canonical.FullyQualifiedName,
            OuterName: null,
            ModuleName: ResolverTestHarness.TestModule,
            Language: Language.Cpp,
            Span: ResolverTestHarness.Span(),
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
            HasGeneratedBody: false);

        SymbolTable t = new();
        t.Register(canonical);
        t.Register(other);

        ResolverPipeline pipeline = Pipeline(t);
        pipeline.ResolveUpTo(ResolvePhase.Pairings);

        Assert.Empty(pipeline.Context.MergedPartials);
    }
}
