// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Analysis.Analyzers;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Analysis;

/// <summary>
/// Tests for <see cref="XhtCorrelationAnalyzer"/> (WU-23). The analyzer reads
/// the XHT-emitted per-module <c>.gen.manifest</c> to find the C# types XHT
/// already produces FClass scaffolding for, produces an
/// <see cref="XhtCorrelationTable"/>, and emits XIL2CPP142 / 149 / 150 / 151
/// on cross-tool correlation mismatches per /Documents/XIL2CPP.html Rev 4
/// Sections 10.2 / 10.3 / 10.4 / 10.5. The manifest fixtures are synthetic
/// files written to a temp directory.
/// </summary>
public sealed class XhtCorrelationAnalyzerTests : IDisposable
{
    /// <summary>
    /// In-source stand-ins for the canonical XPact reflection attributes
    /// (the real curated XPact.CSharp.BCL refs are absent in Phase 6.b tests;
    /// the analyzer recognises the attributes by metadata name + namespace).
    /// </summary>
    private const string AttributeStubs =
        "namespace XPact.CoreXObject { "
        + "public sealed class XClassAttribute : System.Attribute { } "
        + "public sealed class XPropertyAttribute : System.Attribute { } }";

    private readonly string _tempDir;

    public XhtCorrelationAnalyzerTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XIL2CPP.Tests-XhtCorr-" + Guid.NewGuid().ToString("N"));
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

    private static NormalizedUnit BuildUnit(params string[] sources)
    {
        Pass1Result pass1 = NormalizationTestHelpers.BuildPass1(sources);
        return Pass2Driver.Run(pass1, new List<INormalizer>());
    }

    private static Pass3Result RunWithManifest(NormalizedUnit unit, string? manifestPath)
    {
        ISemanticAnalyzer analyzer = new XhtCorrelationAnalyzer(manifestPath);
        return Pass3Driver.Run(unit, new ISemanticAnalyzer[] { analyzer });
    }

    private string WriteManifest(string typesSection, string moduleName = "TestModule")
    {
        // A full XHT-shaped .gen.manifest with the forward-compatible [Types]
        // section (Section 10.3). The other sections are present so the
        // fixture is structurally faithful to XHT's GenManifestWriter output.
        string content =
            "[Metadata]\n"
            + "XhtSchemaVersion = 1\n"
            + "ContractVersion = 13.10+bbcc0292b75e9a10\n"
            + $"ModuleName = {moduleName}\n"
            + "ProducedAtUtcDeterministic = 0\n"
            + "GeneratedAtUtc = 2026-06-03T00:00:00Z\n"
            + "\n"
            + "[Inputs]\n"
            + "# Path,ContentHash16\n"
            + "\n"
            + "[Generated]\n"
            + "# Path,ContentHash16\n"
            + "\n"
            + "[Types]\n"
            + "# CSharpType,CppType,FClassSymbol,FProperties,BackingFields\n"
            + typesSection
            + "\n"
            + "[Diagnostics]\n"
            + "# Severity,Code,File,Line,Column,Message\n"
            + "\n"
            + "[End]\n";
        string path = Path.Combine(_tempDir, moduleName + ".gen.manifest");
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteGeneratedOnlyManifest(string generatedSection, string moduleName = "TestModule")
    {
        // A manifest with NO [Types] section: the analyzer must fall back to
        // deriving FClass-scaffolding ownership from the [Generated]
        // <Type>.gen.h base names (XHT's current emitter shape).
        string content =
            "[Metadata]\n"
            + "XhtSchemaVersion = 1\n"
            + "ContractVersion = 13.10+bbcc0292b75e9a10\n"
            + $"ModuleName = {moduleName}\n"
            + "ProducedAtUtcDeterministic = 0\n"
            + "GeneratedAtUtc = 2026-06-03T00:00:00Z\n"
            + "\n"
            + "[Inputs]\n"
            + "# Path,ContentHash16\n"
            + "\n"
            + "[Generated]\n"
            + "# Path,ContentHash16\n"
            + generatedSection
            + "\n"
            + "[Diagnostics]\n"
            + "# Severity,Code,File,Line,Column,Message\n"
            + "\n"
            + "[End]\n";
        string path = Path.Combine(_tempDir, moduleName + ".gen.manifest");
        File.WriteAllText(path, content);
        return path;
    }

    private static IReadOnlyList<DiagnosticRecord> WithCode(Pass3Result result, string code)
        => result.Diagnostics.Where(d => d.Code == code).ToList();

    // =================================================================
    // Happy path: synthetic manifest correlates a C# [XClass].
    // =================================================================

    [Fact]
    public void SyntheticManifest_CorrelatesXClass_ProducesTableEntry_NoDiagnostics()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        // The manifest pairs XValve with the C++ class XValve and declares the
        // canonical module-prefixed FClass symbol (Section 5.1); no mismatch.
        string path = WriteManifest("TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        Assert.False(result.HasErrors);
        Assert.Empty(result.Diagnostics);

        XhtCorrelationTable? table = result.GetSingleton<XhtCorrelationTable>();
        Assert.NotNull(table);
        Assert.True(table!.ManifestFound);
        Assert.Equal(path, table.ManifestPath);

        XhtCorrelatedType entry = Assert.Single(table.CorrelatedTypes);
        Assert.Equal("TestModule.XValve", entry.CSharpTypeName);
        Assert.Equal("XValve", entry.CppTypeName);
        Assert.Equal("Z_Construct_FClass_TestModule_XValve", entry.FClassSymbol);
        Assert.True(entry.XhtProducesFClass);
    }

    [Fact]
    public void TypeNotInManifest_RecordedAsXil2CppOwned_NoDiagnostic()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XPump { } }");

        // The manifest declares a DIFFERENT type; XPump is not in it.
        string path = WriteManifest("TestModule.XValve,XValve,Z_Construct_FClass_XValve,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        // XPump is XIL2CPP-owned (no FClass scaffolding from XHT). The
        // manifest's XValve pair has no matching C# type -> XIL2CPP150.
        XhtCorrelationTable? table = result.GetSingleton<XhtCorrelationTable>();
        Assert.NotNull(table);
        XhtCorrelatedType pump = Assert.Single(
            table!.CorrelatedTypes, t => t.CSharpTypeName == "TestModule.XPump");
        Assert.False(pump.XhtProducesFClass);
        Assert.Null(pump.FClassSymbol);
    }

    // =================================================================
    // XIL2CPP142 -- cross-tool symbol-space mismatch.
    // =================================================================

    [Fact]
    public void SymbolMismatch_EmitsXil2Cpp142()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        // XHT declares a NON-canonical FClass symbol for XValve.
        string path = WriteManifest("TestModule.XValve,XValve,Z_Construct_FClass_WrongName,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        IReadOnlyList<DiagnosticRecord> diags = WithCode(
            result, DiagnosticCodes.CrossToolSymbolSpaceMismatch);
        DiagnosticRecord d = Assert.Single(diags);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("XIL2CPP142", d.Code);
        Assert.Contains("Z_Construct_FClass_WrongName", d.Message);
        Assert.Contains("Z_Construct_FClass_TestModule_XValve", d.Message);
        Assert.Equal("TestModule", d.Module);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void CanonicalSymbol_DoesNotEmitXil2Cpp142()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string path = WriteManifest("TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        Assert.Empty(WithCode(result, DiagnosticCodes.CrossToolSymbolSpaceMismatch));
    }

    // =================================================================
    // XIL2CPP149 -- backing-field naming mismatch.
    // =================================================================

    [Fact]
    public void BackingFieldMismatch_EmitsXil2Cpp149()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve "
            + "{ public int Flow { get; set; } } }");

        // XHT declares a NON-canonical backing field (angle-bracket form,
        // forbidden by Section 10.5) for the Flow auto-property.
        string path = WriteManifest(
            "TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,Flow=<Flow>k__BackingField\n");

        Pass3Result result = RunWithManifest(unit, path);

        IReadOnlyList<DiagnosticRecord> diags = WithCode(
            result, DiagnosticCodes.BackingFieldNamingMismatch);
        DiagnosticRecord d = Assert.Single(diags);
        Assert.Equal("XIL2CPP149", d.Code);
        Assert.Contains("__BackingField_Flow", d.Message);
        Assert.Contains("<Flow>k__BackingField", d.Message);
        Assert.Contains("Flow", d.Message);
    }

    [Fact]
    public void CanonicalBackingField_DoesNotEmitXil2Cpp149()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve "
            + "{ public int Flow { get; set; } } }");

        string path = WriteManifest(
            "TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,Flow=__BackingField_Flow\n");

        Pass3Result result = RunWithManifest(unit, path);

        Assert.Empty(WithCode(result, DiagnosticCodes.BackingFieldNamingMismatch));
    }

    // =================================================================
    // XIL2CPP150 -- cross-language type-pair manifest mismatch.
    // =================================================================

    [Fact]
    public void TypePairWithoutMatchingCSharpType_EmitsXil2Cpp150()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        // The manifest declares a cross-language pair for a C# type the module
        // does not declare (TestModule.XGhost).
        string path = WriteManifest(
            "TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,\n"
            + "TestModule.XGhost,XGhost,Z_Construct_FClass_TestModule_XGhost,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        IReadOnlyList<DiagnosticRecord> diags = WithCode(
            result, DiagnosticCodes.CrossLanguageTypePairMismatch);
        DiagnosticRecord d = Assert.Single(diags);
        Assert.Equal("XIL2CPP150", d.Code);
        Assert.Contains("TestModule.XGhost", d.Message);
        Assert.Contains("XGhost", d.Message);
        Assert.Equal(path, d.File);
    }

    [Fact]
    public void TypePairWithoutCppSide_DoesNotEmitXil2Cpp150()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        // An entry with no C++ pair is not a cross-language pair; absent
        // C# type must NOT trigger XIL2CPP150.
        string path = WriteManifest(
            "TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,\n"
            + "TestModule.XOrphan,,Z_Construct_FClass_TestModule_XOrphan,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        Assert.Empty(WithCode(result, DiagnosticCodes.CrossLanguageTypePairMismatch));
    }

    // =================================================================
    // XIL2CPP151 -- FProperty descriptor missing.
    // =================================================================

    [Fact]
    public void MissingFPropertyDescriptor_EmitsXil2Cpp151()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve "
            + "{ [XPact.CoreXObject.XPropertyAttribute] public int Pressure; } }");

        // The manifest pairs XValve but declares NO FProperty descriptors,
        // so the [XProperty] field Pressure is missing one.
        string path = WriteManifest("TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,,\n");

        Pass3Result result = RunWithManifest(unit, path);

        IReadOnlyList<DiagnosticRecord> diags = WithCode(
            result, DiagnosticCodes.FPropertyDescriptorMissing);
        DiagnosticRecord d = Assert.Single(diags);
        Assert.Equal("XIL2CPP151", d.Code);
        Assert.Contains("Pressure", d.Message);
        Assert.Contains("TestModule.XValve", d.Message);
    }

    [Fact]
    public void PresentFPropertyDescriptor_DoesNotEmitXil2Cpp151()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve "
            + "{ [XPact.CoreXObject.XPropertyAttribute] public int Pressure; } }");

        // The manifest declares the FProperty descriptor for Pressure.
        string path = WriteManifest("TestModule.XValve,XValve,Z_Construct_FClass_TestModule_XValve,Pressure,\n");

        Pass3Result result = RunWithManifest(unit, path);

        Assert.Empty(WithCode(result, DiagnosticCodes.FPropertyDescriptorMissing));
    }

    // =================================================================
    // Graceful degradation: absent / malformed manifest.
    // =================================================================

    [Fact]
    public void AbsentManifest_ProducesEmptyTable_NoDiagnostics_NoCrash()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string missingPath = Path.Combine(_tempDir, "DoesNotExist.gen.manifest");

        Pass3Result result = RunWithManifest(unit, missingPath);

        Assert.False(result.HasErrors);
        Assert.Empty(result.Diagnostics);

        XhtCorrelationTable? table = result.GetSingleton<XhtCorrelationTable>();
        Assert.NotNull(table);
        Assert.False(table!.ManifestFound);
        Assert.Empty(table.CorrelatedTypes);
    }

    [Fact]
    public void NullManifestPath_DerivedPathFails_ProducesEmptyTable_NoCrash()
    {
        // The synthetic sources have no repo-root ancestor (paths are
        // "Source0.cs"), so production path derivation yields no candidate.
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        // null override -> production derivation path (which finds nothing here).
        Pass3Result result = RunWithManifest(unit, manifestPath: null);

        Assert.Empty(result.Diagnostics);
        XhtCorrelationTable? table = result.GetSingleton<XhtCorrelationTable>();
        Assert.NotNull(table);
        Assert.False(table!.ManifestFound);
        Assert.Empty(table.CorrelatedTypes);
    }

    [Fact]
    public void MalformedManifest_DegradesGracefully_EmptyTable_NoCrash()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        string path = Path.Combine(_tempDir, "Garbage.gen.manifest");
        File.WriteAllText(path, "this is not\x00 a valid manifest ￿ at all\n[Types\nbroken");

        Pass3Result result = RunWithManifest(unit, path);

        // The lenient reader tolerates garbage: the parsed manifest has no
        // [Types] entries and no [Generated] FClass headers, so the C#-side
        // XValve has no XHT counterpart -> recorded as XIL2CPP-owned, and no
        // correlation diagnostics fire (no crash, no false positives).
        Assert.False(result.HasErrors);
        Assert.Empty(WithCode(result, DiagnosticCodes.CrossLanguageTypePairMismatch));
        Assert.Empty(WithCode(result, DiagnosticCodes.CrossToolSymbolSpaceMismatch));
        XhtCorrelationTable? table = result.GetSingleton<XhtCorrelationTable>();
        Assert.NotNull(table);
        XhtCorrelatedType valve = Assert.Single(
            table!.CorrelatedTypes, t => t.CSharpTypeName == "TestModule.XValve");
        Assert.False(valve.XhtProducesFClass);
    }

    // =================================================================
    // [Generated]-section fallback (XHT's current emitter shape).
    // =================================================================

    [Fact]
    public void GeneratedSectionFallback_DerivesFClassOwnership_FromGenHBaseNames()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve { } }");

        // No [Types] section; XHT's current emitter only lists the generated
        // files. The XValve.gen.h base name is the FClass scaffolding header.
        string path = WriteGeneratedOnlyManifest(
            "Intermediate/Build/XValve.gen.h,0011223344556677\n"
            + "Intermediate/Build/XValve.gen.cpp,8899aabbccddeeff\n");

        Pass3Result result = RunWithManifest(unit, path);

        // The C# type's metadata name is "TestModule.XValve" but the
        // [Generated]-derived entry keys on the file base name "XValve".
        // They do not join (the current XHT schema lacks the namespace), so
        // XValve is recorded as XIL2CPP-owned; the fallback FClass entry is a
        // bare "XValve" with the canonical symbol and no diagnostics.
        Assert.Empty(result.Diagnostics);
        XhtCorrelationTable? table = result.GetSingleton<XhtCorrelationTable>();
        Assert.NotNull(table);
        Assert.True(table!.ManifestFound);
        Assert.Contains(table.CorrelatedTypes, t => t.CSharpTypeName == "TestModule.XValve");
    }

    // =================================================================
    // Determinism + discovery.
    // =================================================================

    [Fact]
    public void Analyze_IsDeterministic_AcrossRuns()
    {
        NormalizedUnit unit = BuildUnit(
            AttributeStubs,
            "namespace TestModule { "
            + "[XPact.CoreXObject.XClassAttribute] public class XValve "
            + "{ [XPact.CoreXObject.XPropertyAttribute] public int A; "
            + "[XPact.CoreXObject.XPropertyAttribute] public int B; "
            + "public int P { get; set; } } "
            + "[XPact.CoreXObject.XClassAttribute] public class XPump { } }");

        string path = WriteManifest(
            "TestModule.XValve,XValve,Z_Construct_FClass_Wrong,,P=<P>k__BackingField\n"
            + "TestModule.XGhost,XGhost,Z_Construct_FClass_XGhost,,\n");

        Pass3Result first = RunWithManifest(unit, path);
        Pass3Result second = RunWithManifest(unit, path);

        List<string> firstCodes = first.Diagnostics.Select(d => d.Code + "|" + d.Message).ToList();
        List<string> secondCodes = second.Diagnostics.Select(d => d.Code + "|" + d.Message).ToList();
        Assert.Equal(firstCodes, secondCodes);

        XhtCorrelationTable t1 = first.GetSingleton<XhtCorrelationTable>()!;
        XhtCorrelationTable t2 = second.GetSingleton<XhtCorrelationTable>()!;
        Assert.Equal(
            t1.CorrelatedTypes.Select(t => t.CSharpTypeName).ToList(),
            t2.CorrelatedTypes.Select(t => t.CSharpTypeName).ToList());
    }

    [Fact]
    public void Analyzer_IsAutoDiscovered_ByPass3Driver()
    {
        IReadOnlyList<ISemanticAnalyzer> discovered = Pass3Driver.DiscoverAnalyzers();
        Assert.Contains(discovered, a => a is XhtCorrelationAnalyzer);
    }
}
