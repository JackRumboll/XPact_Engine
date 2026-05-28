// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Manifest;

/// <summary>
/// Tests for <see cref="GenManifestWriter"/>. The writer produces the
/// XHT-side per-module text manifest per
/// <c>/Documents/XHT.html</c> Rev 8 Section 9.2 + Section 14
/// (byte-identical determinism).
/// </summary>
public class GenManifestWriterTests : IDisposable
{
    private readonly string _tempDir;

    public GenManifestWriterTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-GenW-" + Guid.NewGuid().ToString("N"));
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

    private static GenManifest BuildManifest(
        ImmutableArray<GenManifestEntry>? inputs = null,
        ImmutableArray<GenManifestEntry>? generated = null,
        ImmutableArray<GenManifestDiagnostic>? diagnostics = null,
        string moduleName = "XScoring")
    {
        return new GenManifest(
            XhtSchemaVersion: 1,
            ContractVersion: "13.9+381d8ef7a7770d9b",
            ModuleName: moduleName,
            GeneratedAtUtcIso: "2026-05-20T12:34:56Z",
            Inputs: inputs ?? ImmutableArray<GenManifestEntry>.Empty,
            Generated: generated ?? ImmutableArray<GenManifestEntry>.Empty,
            Diagnostics: diagnostics ?? ImmutableArray<GenManifestDiagnostic>.Empty);
    }

    [Fact]
    public void Render_EmitsAllRequiredSections_InOrder()
    {
        string rendered = GenManifestWriter.Render(BuildManifest());

        int metadataIdx = rendered.IndexOf("[Metadata]", StringComparison.Ordinal);
        int inputsIdx = rendered.IndexOf("[Inputs]", StringComparison.Ordinal);
        int generatedIdx = rendered.IndexOf("[Generated]", StringComparison.Ordinal);
        int diagnosticsIdx = rendered.IndexOf("[Diagnostics]", StringComparison.Ordinal);
        int endIdx = rendered.IndexOf("[End]", StringComparison.Ordinal);

        Assert.True(metadataIdx >= 0);
        Assert.True(metadataIdx < inputsIdx);
        Assert.True(inputsIdx < generatedIdx);
        Assert.True(generatedIdx < diagnosticsIdx);
        Assert.True(diagnosticsIdx < endIdx);
    }

    [Fact]
    public void Render_MetadataSection_HasRequiredKeys()
    {
        string rendered = GenManifestWriter.Render(BuildManifest());
        Assert.Contains("XhtSchemaVersion = 1", rendered);
        Assert.Contains("ContractVersion = 13.9+381d8ef7a7770d9b", rendered);
        Assert.Contains("ModuleName = XScoring", rendered);
        Assert.Contains("ProducedAtUtcDeterministic = 0", rendered);
        Assert.Contains("GeneratedAtUtc = 2026-05-20T12:34:56Z", rendered);
    }

    [Fact]
    public void Render_UsesLfLineEndings()
    {
        string rendered = GenManifestWriter.Render(BuildManifest());
        // No carriage returns anywhere.
        Assert.DoesNotContain("\r", rendered);
    }

    [Fact]
    public void Render_TwoWrites_AreByteIdentical_ForSameInput()
    {
        GenManifest m1 = BuildManifest(
            inputs: ImmutableArray.Create(
                new GenManifestEntry("Engine/Source/A.h", "a1b2c3d4e5f60718"),
                new GenManifestEntry("Engine/Source/B.h", "fedcba9876543210")),
            generated: ImmutableArray.Create(
                new GenManifestEntry("Intermediate/X.gen.h", "0011223344556677")),
            diagnostics: ImmutableArray.Create(
                new GenManifestDiagnostic("warning", "XHT070", "Engine/Source/A.h", 1, 1, "empty reflected header")));

        string r1 = GenManifestWriter.Render(m1);
        string r2 = GenManifestWriter.Render(m1);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void Render_SortsInputs_OrdinalAscending()
    {
        var inputs = ImmutableArray.Create(
            new GenManifestEntry("zzz.h", "0011223344556677"),
            new GenManifestEntry("aaa.h", "8899aabbccddeeff"),
            new GenManifestEntry("mmm.h", "0102030405060708"));

        string rendered = GenManifestWriter.Render(BuildManifest(inputs: inputs));
        int aPos = rendered.IndexOf("aaa.h", StringComparison.Ordinal);
        int mPos = rendered.IndexOf("mmm.h", StringComparison.Ordinal);
        int zPos = rendered.IndexOf("zzz.h", StringComparison.Ordinal);
        Assert.True(aPos < mPos && mPos < zPos);
    }

    [Fact]
    public void Render_PathContainingComma_Throws_WithXHT005()
    {
        var inputs = ImmutableArray.Create(
            new GenManifestEntry("path,with,comma.h", "0123456789abcdef"));
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => GenManifestWriter.Render(BuildManifest(inputs: inputs)));
        // Round 5 R4-CR1: the diagnostic code is carried on the
        // exception's DiagnosticCode property (catalog-anchored at
        // /Documents/XHT.html Rev 8 Section 23.2). Earlier the code was
        // prepended to the message string as "XHT005: ..."; the
        // structured property is now the source of truth.
        Assert.Equal("XHT005", ex.DiagnosticCode);
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void Render_InvalidHash_TooShort_Throws()
    {
        var generated = ImmutableArray.Create(new GenManifestEntry("x.gen.h", "short"));
        Assert.Throws<ManifestMalformedException>(
            () => GenManifestWriter.Render(BuildManifest(generated: generated)));
    }

    [Fact]
    public void Render_InvalidHash_UppercaseHex_Throws()
    {
        var generated = ImmutableArray.Create(new GenManifestEntry("x.gen.h", "ABCDEF0123456789"));
        Assert.Throws<ManifestMalformedException>(
            () => GenManifestWriter.Render(BuildManifest(generated: generated)));
    }

    [Fact]
    public void Render_InvalidHash_NonHexChars_Throws()
    {
        var generated = ImmutableArray.Create(new GenManifestEntry("x.gen.h", "ghijklmnopqrstuv"));
        Assert.Throws<ManifestMalformedException>(
            () => GenManifestWriter.Render(BuildManifest(generated: generated)));
    }

    [Fact]
    public void Render_DiagnosticWithCommaInCode_Throws()
    {
        var diags = ImmutableArray.Create(
            new GenManifestDiagnostic("error", "XHT,070", "X.h", 1, 1, "test"));
        Assert.Throws<ManifestMalformedException>(
            () => GenManifestWriter.Render(BuildManifest(diagnostics: diags)));
    }

    [Fact]
    public void Render_DiagnosticWithInvalidSeverity_Throws()
    {
        var diags = ImmutableArray.Create(
            new GenManifestDiagnostic("fatal", "XHT070", "X.h", 1, 1, "test"));
        Assert.Throws<ManifestMalformedException>(
            () => GenManifestWriter.Render(BuildManifest(diagnostics: diags)));
    }

    [Fact]
    public void Render_DiagnosticWithMultilineMessage_EscapesNewlines()
    {
        var diags = ImmutableArray.Create(
            new GenManifestDiagnostic("error", "XHT062", "X.h", 1, 1, "line1\nline2"));
        string rendered = GenManifestWriter.Render(BuildManifest(diagnostics: diags));
        Assert.Contains("line1\\nline2", rendered);
        // Make sure no raw newline made it into the diagnostic line.
        string[] lines = rendered.Split('\n');
        bool foundDiagLine = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("error,XHT062,", StringComparison.Ordinal))
            {
                Assert.Contains("line1\\nline2", line);
                foundDiagLine = true;
            }
        }
        Assert.True(foundDiagLine);
    }

    [Fact]
    public void Write_AtomicallyCreatesFile()
    {
        string path = Path.Combine(_tempDir, "Test.gen.manifest");
        GenManifestWriter.Write(BuildManifest(), path);
        Assert.True(File.Exists(path));

        // Should be exactly one file (no leftover temp).
        Assert.Single(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void Write_OutputIsUtf8WithoutBom()
    {
        string path = Path.Combine(_tempDir, "Test.gen.manifest");
        GenManifestWriter.Write(BuildManifest(), path);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void Write_TwoWrites_AreByteIdentical_ForSameInput()
    {
        string path1 = Path.Combine(_tempDir, "A.gen.manifest");
        string path2 = Path.Combine(_tempDir, "B.gen.manifest");
        GenManifest m = BuildManifest(
            inputs: ImmutableArray.Create(
                new GenManifestEntry("X.h", "0011223344556677")),
            generated: ImmutableArray.Create(
                new GenManifestEntry("X.gen.h", "8899aabbccddeeff")));
        GenManifestWriter.Write(m, path1);
        GenManifestWriter.Write(m, path2);
        Assert.Equal(File.ReadAllBytes(path1), File.ReadAllBytes(path2));
    }

    [Fact]
    public void Write_NullManifestThrows()
    {
        string path = Path.Combine(_tempDir, "x.gen.manifest");
        Assert.Throws<ArgumentNullException>(() => GenManifestWriter.Write(null!, path));
    }

    [Fact]
    public void Write_EmptyDestPathThrows()
    {
        Assert.Throws<ArgumentException>(() => GenManifestWriter.Write(BuildManifest(), ""));
    }

    [Fact]
    public void Render_BadSchemaVersion_Throws()
    {
        GenManifest m = new(
            XhtSchemaVersion: 2, // unsupported
            ContractVersion: "13.9+381d8ef7a7770d9b",
            ModuleName: "X",
            GeneratedAtUtcIso: "2026-05-20T12:34:56Z",
            Inputs: ImmutableArray<GenManifestEntry>.Empty,
            Generated: ImmutableArray<GenManifestEntry>.Empty,
            Diagnostics: ImmutableArray<GenManifestDiagnostic>.Empty);
        Assert.Throws<ManifestMalformedException>(() => GenManifestWriter.Render(m));
    }

    [Fact]
    public void Render_RoundTrip_ViaReader_PreservesAllFields()
    {
        // Determinism + bijection: render -> parse -> compare.
        GenManifest original = BuildManifest(
            inputs: ImmutableArray.Create(
                new GenManifestEntry("Engine/Source/Runtime/XScoring/Public/XValve.h", "a1b2c3d4e5f60718"),
                new GenManifestEntry("Engine/Source/Runtime/XScoring/Public/XValve.cs", "9876543210abcdef")),
            generated: ImmutableArray.Create(
                new GenManifestEntry("Intermediate/Build/XValve.gen.h", "fedcba9876543210"),
                new GenManifestEntry("Intermediate/Build/XValve.gen.cpp", "0011223344556677"),
                new GenManifestEntry("Intermediate/Build/XScoring.init.gen.cpp", "8899aabbccddeeff")),
            diagnostics: ImmutableArray.Create(
                new GenManifestDiagnostic("warning", "XHT070", "Public/XEmpty.h", 1, 1, "Header contains no reflected types")));

        string rendered = GenManifestWriter.Render(original);
        GenManifest parsed = GenManifestReader.Parse(rendered);

        Assert.Equal(original.XhtSchemaVersion, parsed.XhtSchemaVersion);
        Assert.Equal(original.ContractVersion, parsed.ContractVersion);
        Assert.Equal(original.ModuleName, parsed.ModuleName);
        Assert.Equal(original.GeneratedAtUtcIso, parsed.GeneratedAtUtcIso);
        Assert.Equal(original.Inputs.Length, parsed.Inputs.Length);
        Assert.Equal(original.Generated.Length, parsed.Generated.Length);
        Assert.Equal(original.Diagnostics.Length, parsed.Diagnostics.Length);

        // Per the determinism contract, the parsed entries match the
        // sorted form of the original. Re-sort the original locally
        // and compare against parsed (which is already sorted).
        var sortedInputs = new System.Collections.Generic.List<GenManifestEntry>(original.Inputs);
        sortedInputs.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        for (int i = 0; i < parsed.Inputs.Length; i++)
        {
            Assert.Equal(sortedInputs[i].RelativePath, parsed.Inputs[i].RelativePath);
            Assert.Equal(sortedInputs[i].ContentHash16, parsed.Inputs[i].ContentHash16);
        }
    }

    [Fact]
    public void DiagnosticMessage_WithCommas_RoundTripsLosslessly()
    {
        // Per M15 audit: messages containing commas must round-trip
        // losslessly through the .gen.manifest format. Previously the
        // emitter destructively replaced ',' with ';'.
        const string originalMessage = "Conflict between 'EditAnywhere', 'Transient', and 'NoExport': pick one.";

        GenManifestDiagnostic d = new(
            Severity: "error",
            Code: "XHT112",
            File: "Public/XValve.h",
            Line: 14,
            Column: 5,
            Message: originalMessage);

        GenManifest m = BuildManifest(
            diagnostics: ImmutableArray.Create(d));
        string path = Path.Combine(_tempDir, "comma-test.gen.manifest");
        GenManifestWriter.Write(m, path);

        GenManifest parsed = GenManifestReader.Read(path);
        Assert.Single(parsed.Diagnostics);
        Assert.Equal(originalMessage, parsed.Diagnostics[0].Message);
    }
}
