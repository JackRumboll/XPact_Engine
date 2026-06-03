// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for the WU-E1 class emitter (<see cref="ClassEmitter"/> +
/// <see cref="LifecycleTableEmitter"/> + <see cref="ZConstructEmitter"/>) per
/// /Documents/XIL2CPP.html Rev 4 Section 5.1: the singleton-getter body, the
/// ClassConstructor body, the StaticClass body, the 8 lifecycle-slot bodies
/// (emitted only for declared overrides; null table slots otherwise), the
/// FXObjectLifecycleTable instance, and the XHT-coordination split (skip the
/// FClass constinit when XHT owns it; emit it when XHT does not / no table).
/// </summary>
public sealed class ClassEmitterTests : IDisposable
{
    /// <summary>
    /// In-source stand-ins for the canonical XPact reflection attributes (the
    /// curated XPact.CSharp.BCL refs are absent in the emit tests; the emitter
    /// recognises the attribute by metadata name + namespace).
    /// </summary>
    private const string AttributeStubs =
        "namespace XPact.CoreXObject { "
        + "public sealed class XClassAttribute : System.Attribute { } "
        + "public sealed class XPropertyAttribute : System.Attribute { } }";

    private readonly string _tempDir;

    public ClassEmitterTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XIL2CPP.Tests-ClassEmitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // -----------------------------------------------------------------
    // Harness.
    // -----------------------------------------------------------------

    /// <summary>
    /// Build an <see cref="EmitContext"/> over the supplied sources with NO XHT
    /// correlation table (the default analyzer set), so the emitter treats
    /// every type as XIL2CPP-owned.
    /// </summary>
    private static EmitContext BuildContext(params string[] sources)
        => EmitTestHelpers.BuildEmitContext(sources);

    /// <summary>
    /// Build an <see cref="EmitContext"/> over the supplied sources WITH an XHT
    /// correlation table sourced from a synthetic manifest at
    /// <paramref name="manifestPath"/> (so <see cref="EmitContext.XhtCorrelationTable"/>
    /// is populated and the XHT-ownership split is exercised).
    /// </summary>
    private static EmitContext BuildContextWithXht(string manifestPath, params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(isSimPath: false, sources);
        NormalizedUnit unit = Pass2Driver.Run(pass1, new List<INormalizer>());
        Pass3Result pass3 = Pass3Driver.Run(
            unit,
            new ISemanticAnalyzer[]
            {
                new CrossModuleNoThrowAnalyzer(),
                new XhtCorrelationAnalyzer(manifestPath),
            });
        TierTable tierTable = Pass4Driver.Run(unit, pass3);
        ManglingTable manglingTable = Pass5Driver.Run(
            unit, tierTable, EmitTestHelpers.ContractVersionTag);
        return new EmitContext(
            unit,
            pass3,
            tierTable,
            manglingTable,
            EmitTestHelpers.ContractVersionTag,
            EmitTestHelpers.GCRootAbi,
            EmitTestHelpers.ExceptionAbi,
            EmitTestHelpers.ManglingSchemeTag);
    }

    /// <summary>
    /// Write a synthetic XHT .gen.manifest with a single forward-compatible
    /// [Types] line correlating one C# type to XHT-owned FClass scaffolding.
    /// </summary>
    private string WriteManifestPairing(string typesLine)
    {
        string content =
            "[Metadata]\n"
            + "XhtSchemaVersion = 1\n"
            + "ModuleName = TestModule\n"
            + "\n"
            + "[Types]\n"
            + "# CSharpType,CppType,FClassSymbol,FProperties,BackingFields\n"
            + typesLine
            + "\n"
            + "[End]\n";
        string path = Path.Combine(_tempDir, "TestModule.gen.manifest");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Find a declared named type symbol by simple name in the unit.</summary>
    private static INamedTypeSymbol FindType(EmitContext context, string simpleName)
    {
        foreach ((_, INamedTypeSymbol symbol) in AnalyzerHelpers.EnumerateTypeDeclarations(context.Unit))
        {
            if (string.Equals(symbol.Name, simpleName, StringComparison.Ordinal))
            {
                return symbol;
            }
        }
        throw new InvalidOperationException($"Type '{simpleName}' not found in unit.");
    }

    private static string EmitClass(EmitContext context, INamedTypeSymbol symbol)
    {
        CppWriter writer = new();
        ClassEmitter emitter = new();
        emitter.EmitClass(symbol, context, writer, new BodyLoweringRuleRegistry(Array.Empty<IBodyLoweringRule>()));
        return writer.Build();
    }

    // =================================================================
    // Singleton-getter body shape (Section 5.1).
    // =================================================================

    [Fact]
    public void SingletonGetter_EmitsModulePrefixedSymbol_WithCachedRegistration()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        // Module-prefixed singleton-getter symbol (Section 5.1 authoritative).
        Assert.Contains(
            "extern \"C\" const ::XCore::Reflect::FClass* Z_Construct_FClass_TestModule_XValve() noexcept {",
            cpp);
        // Cached, first-call init lambda + safe-point + RegisterClass + cached return.
        Assert.Contains("static const ::XCore::Reflect::FClass* sCachedClass = []() noexcept {", cpp);
        Assert.Contains("XPACT_SAFEPOINT_CHECK();", cpp);
        Assert.Contains("const auto* cls = &XValve_Class;", cpp);
        Assert.Contains("::XCore::Reflect::XReflectionRuntime::RegisterClass(cls);", cpp);
        Assert.Contains("return cls;", cpp);
        Assert.Contains("return sCachedClass;", cpp);
    }

    [Fact]
    public void StaticClass_BodyDelegatesToSingletonGetter()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        Assert.Contains("const ::XCore::Reflect::FClass* XValve::StaticClass() {", cpp);
        Assert.Contains("return Z_Construct_FClass_TestModule_XValve();", cpp);
    }

    [Fact]
    public void ClassConstructor_EmitsPlacementNewBody()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        Assert.Contains(
            "extern \"C\" ::XCore::Reflect::XObject* Z_ClassConstructor_XValve("
            + "::XCore::Reflect::XObject* memory, ::XCore::Reflect::FXObjectInitializer& init) {",
            cpp);
        Assert.Contains("auto* obj = new (memory) XValve();", cpp);
        Assert.Contains("return obj;", cpp);
    }

    // =================================================================
    // Lifecycle slots: 8 entries; null when not declared; body when declared.
    // =================================================================

    [Fact]
    public void LifecycleTable_HasEightSlots_AllNull_WhenNoOverridesDeclared()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        Assert.Contains(
            "constinit const ::XCore::Reflect::FXObjectLifecycleTable XValve_LifecycleTable = {",
            cpp);
        // No override declared -> every slot is nullptr; Capabilities = 0.
        Assert.Contains("/* Capabilities */ 0u,", cpp);

        // All 8 slots present, in ABI order, each nullptr.
        Assert.Contains("nullptr, // [0] PostInitProperties", cpp);
        Assert.Contains("nullptr, // [1] BeginDestroy", cpp);
        Assert.Contains("nullptr, // [2] IsReadyForFinishDestroy", cpp);
        Assert.Contains("nullptr, // [3] FinishDestroy", cpp);
        Assert.Contains("nullptr, // [4] Serialize", cpp);
        Assert.Contains("nullptr, // [5] AddReferencedObjects", cpp);
        Assert.Contains("nullptr, // [6] PostLoad", cpp);
        Assert.Contains("nullptr, // [7] PreSave", cpp);

        // No slot bodies emitted when no override is declared.
        Assert.DoesNotContain("Z_PostInitProperties_XValve(", cpp);
        Assert.DoesNotContain("Z_BeginDestroy_XValve(", cpp);
    }

    [Fact]
    public void LifecycleTable_AlwaysEmitsExactlyEightSlotEntries()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        int slotEntries = Enumerable.Range(0, 8)
            .Count(i => cpp.Contains($"// [{i}] ", StringComparison.Ordinal));
        Assert.Equal(8, slotEntries);
    }

    [Fact]
    public void LifecycleSlot_DeclaredOverride_EmitsBody_AndWiresSlotPointer()
    {
        // The class declares PostInitProperties + IsReadyForFinishDestroy.
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { "
            + "public void PostInitProperties() { } "
            + "public bool IsReadyForFinishDestroy() { return false; } } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        // Declared slot bodies are emitted with the correct signatures.
        Assert.Contains(
            "extern \"C\" void Z_PostInitProperties_XValve(::XCore::Reflect::XObject* self) {", cpp);
        Assert.Contains(
            "extern \"C\" bool Z_IsReadyForFinishDestroy_XValve(::XCore::Reflect::XObject* self) {", cpp);
        Assert.Contains("auto* obj = static_cast<XValve*>(self);", cpp);

        // The table wires the declared slots to their bodies; others nullptr.
        Assert.Contains(
            "reinterpret_cast<void(*)()>(&Z_PostInitProperties_XValve), // [0] PostInitProperties", cpp);
        Assert.Contains(
            "reinterpret_cast<void(*)()>(&Z_IsReadyForFinishDestroy_XValve), // [2] IsReadyForFinishDestroy",
            cpp);
        Assert.Contains("nullptr, // [1] BeginDestroy", cpp);

        // Capabilities mask: bit 0 (PostInitProperties) + bit 2 (IsReadyForFinishDestroy) = 5.
        Assert.Contains("/* Capabilities */ 5u,", cpp);
    }

    [Fact]
    public void SerializeSlot_DeclaredOverride_EmitsNoexceptSignatureWithExtraArgs()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { "
            + "public void Serialize() { } } }");

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        // Serialize is the only noexcept slot (FIX-A-HIGH-13) + carries the
        // FArchive& / FArchiveContext* extra args.
        Assert.Contains(
            "extern \"C\" void Z_Serialize_XValve(::XCore::Reflect::XObject* self, "
            + "::XCore::Reflect::FArchive& Ar, const ::XCore::Reflect::FArchiveContext* Ctx) noexcept {",
            cpp);
        // Capabilities mask: bit 4 (Serialize) = 16.
        Assert.Contains("/* Capabilities */ 16u,", cpp);
    }

    // =================================================================
    // XHT coordination (Section 5.1 / 10.4).
    // =================================================================

    [Fact]
    public void NoXhtTable_EmitsFullSet_IncludingFClassConstinit()
    {
        // Default context: no XHT analyzer ran -> XhtCorrelationTable is null.
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");
        Assert.Null(ctx.XhtCorrelationTable);

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        // XIL2CPP owns the FClass descriptor when XHT does not.
        Assert.Contains("constinit const ::XCore::Reflect::FClass XValve_Class = {", cpp);
        Assert.Contains(".LifecycleTable = &XValve_LifecycleTable,", cpp);
    }

    [Fact]
    public void XhtOwnsFClass_SkipsFClassConstinit_StillEmitsRest()
    {
        // The manifest declares XHT-owned FClass scaffolding for XValve.
        string manifest = WriteManifestPairing(
            "TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,\n");
        EmitContext ctx = BuildContextWithXht(
            manifest,
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        Assert.NotNull(ctx.XhtCorrelationTable);
        Assert.True(ClassEmitter.XhtOwnsFClass(FindType(ctx, "XValve"), ctx));

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        // XHT owns the FClass descriptor -> XIL2CPP must NOT emit it.
        Assert.DoesNotContain("constinit const ::XCore::Reflect::FClass XValve_Class", cpp);

        // ...but XIL2CPP still emits the getter + StaticClass + lifecycle table
        // + ClassConstructor.
        Assert.Contains("Z_Construct_FClass_TestModule_XValve() noexcept {", cpp);
        Assert.Contains("XValve::StaticClass() {", cpp);
        Assert.Contains(
            "constinit const ::XCore::Reflect::FXObjectLifecycleTable XValve_LifecycleTable = {", cpp);
        Assert.Contains("Z_ClassConstructor_XValve(", cpp);
    }

    [Fact]
    public void XhtTablePresentButTypeNotOwned_EmitsFClassConstinit()
    {
        // The manifest declares scaffolding for a DIFFERENT type; XValve is
        // recorded as XIL2CPP-owned (XhtProducesFClass = false).
        string manifest = WriteManifestPairing(
            "TestModule.XOther,XOther,Z_Construct_FClass_TestModule_XOther,,\n");
        EmitContext ctx = BuildContextWithXht(
            manifest,
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        Assert.NotNull(ctx.XhtCorrelationTable);
        Assert.False(ClassEmitter.XhtOwnsFClass(FindType(ctx, "XValve"), ctx));

        string cpp = EmitClass(ctx, FindType(ctx, "XValve"));

        // XValve is XIL2CPP-owned -> the FClass descriptor IS emitted.
        Assert.Contains("constinit const ::XCore::Reflect::FClass XValve_Class = {", cpp);
    }

    // =================================================================
    // Detection + determinism.
    // =================================================================

    [Fact]
    public void IsXClass_RecognisesXClassAttribute()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } "
            + "public class Plain { } }");

        Assert.True(ClassEmitter.IsXClass(FindType(ctx, "XValve")));
        Assert.False(ClassEmitter.IsXClass(FindType(ctx, "Plain")));
        Assert.False(ClassEmitter.IsXClass(null));
    }

    [Fact]
    public void EmitClass_IsDeterministic_AcrossRuns()
    {
        EmitContext ctx = BuildContext(
            AttributeStubs,
            "namespace TestModule { [XPact.CoreXObject.XClassAttribute] public class XValve { "
            + "public void PostInitProperties() { } "
            + "public void PreSave() { } } }");

        INamedTypeSymbol symbol = FindType(ctx, "XValve");
        string first = EmitClass(ctx, symbol);
        string second = EmitClass(ctx, symbol);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SymbolHelpers_ComposeModulePrefixedGetter_AndBareInstanceSymbols()
    {
        Assert.Equal(
            "Z_Construct_FClass_TestModule_XValve",
            ZConstructEmitter.ConstructFClassSymbol("TestModule", "XValve"));
        Assert.Equal("XValve_Class", ZConstructEmitter.FClassInstanceSymbol("XValve"));
        Assert.Equal("XValve_LifecycleTable", LifecycleTableEmitter.LifecycleTableSymbol("XValve"));
        Assert.Equal(8, LifecycleTableEmitter.Slots.Count);
    }
}
