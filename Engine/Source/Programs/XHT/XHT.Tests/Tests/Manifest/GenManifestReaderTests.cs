// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Immutable;
using System.IO;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Manifest;

/// <summary>
/// Tests for <see cref="GenManifestReader"/> covering structural
/// validation paths (section order, missing keys, malformed entries).
/// </summary>
public class GenManifestReaderTests : IDisposable
{
    private readonly string _tempDir;

    public GenManifestReaderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-GenR-" + Guid.NewGuid().ToString("N"));
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

    private static string MinimalValid() => string.Join("\n", new[]
    {
        "[Metadata]",
        "XhtSchemaVersion = 1",
        "ContractVersion = 13.2+b04ae3cc84cdd9f3",
        "ModuleName = XScoring",
        "ProducedAtUtcDeterministic = 0",
        "GeneratedAtUtc = 2026-05-20T12:34:56Z",
        "",
        "[Inputs]",
        "# Path,ContentHash16",
        "",
        "[Generated]",
        "# Path,ContentHash16",
        "",
        "[Diagnostics]",
        "# Severity,Code,File,Line,Column,Message",
        "",
        "[End]",
        "",
    });

    [Fact]
    public void Parse_MinimalValid_Succeeds()
    {
        GenManifest m = GenManifestReader.Parse(MinimalValid());
        Assert.Equal(1, m.XhtSchemaVersion);
        Assert.Equal("13.2+b04ae3cc84cdd9f3", m.ContractVersion);
        Assert.Equal("XScoring", m.ModuleName);
        Assert.Equal("2026-05-20T12:34:56Z", m.GeneratedAtUtcIso);
        Assert.Empty(m.Inputs);
        Assert.Empty(m.Generated);
        Assert.Empty(m.Diagnostics);
    }

    [Fact]
    public void Parse_MissingEndMarker_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            // [End] omitted
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_MissingSchemaVersion_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_MissingContractVersion_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_SectionOrderViolation_Throws()
    {
        // Inputs before Metadata.
        string text = string.Join("\n", new[]
        {
            "[Inputs]",
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Generated]",
            "[Diagnostics]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_DiagnosticsBeforeGenerated_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Diagnostics]", // out of order
            "[Generated]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_InputsEntry_InvalidHashLength_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "X.h,deadbeef", // hash too short
            "[Generated]",
            "[Diagnostics]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_InputsEntry_NonHexHashChar_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "X.h,GGGGGGGGGGGGGGGG", // uppercase + non-hex chars
            "[Generated]",
            "[Diagnostics]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_DiagnosticsEntry_ValidEntry_RoundTrips()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            "warning,XHT070,Path/To/File.h,5,10,header empty",
            "[End]",
        });
        GenManifest m = GenManifestReader.Parse(text);
        Assert.Single(m.Diagnostics);
        GenManifestDiagnostic d = m.Diagnostics[0];
        Assert.Equal("warning", d.Severity);
        Assert.Equal("XHT070", d.Code);
        Assert.Equal("Path/To/File.h", d.File);
        Assert.Equal(5, d.Line);
        Assert.Equal(10, d.Column);
        Assert.Equal("header empty", d.Message);
    }

    [Fact]
    public void Parse_DiagnosticsEntry_InvalidSeverity_Throws()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            "fatal,XHT070,X.h,1,1,nope",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    [Fact]
    public void Parse_DiagnosticsEntry_EscapedNewline_UnescapesCorrectly()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            @"error,XHT062,X.h,1,1,line1\nline2",
            "[End]",
        });
        GenManifest m = GenManifestReader.Parse(text);
        Assert.Equal("line1\nline2", m.Diagnostics[0].Message);
    }

    [Fact]
    public void Read_FileDoesNotExist_Throws()
    {
        string missing = Path.Combine(_tempDir, "nope.manifest");
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => GenManifestReader.Read(missing));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void Read_RoundTripsThroughDiskViaWriter()
    {
        // Full path: build a manifest, write atomically, read back.
        GenManifest m = new(
            XhtSchemaVersion: 1,
            ContractVersion: "13.2+b04ae3cc84cdd9f3",
            ModuleName: "XScoring",
            GeneratedAtUtcIso: "2026-05-20T12:34:56Z",
            Inputs: ImmutableArray.Create(new GenManifestEntry("A.h", "0011223344556677")),
            Generated: ImmutableArray.Create(new GenManifestEntry("A.gen.h", "8899aabbccddeeff")),
            Diagnostics: ImmutableArray<GenManifestDiagnostic>.Empty);

        string path = Path.Combine(_tempDir, "Test.gen.manifest");
        GenManifestWriter.Write(m, path);
        GenManifest parsed = GenManifestReader.Read(path);

        Assert.Equal(m.ModuleName, parsed.ModuleName);
        Assert.Equal(m.Inputs.Length, parsed.Inputs.Length);
        Assert.Equal("A.h", parsed.Inputs[0].RelativePath);
        Assert.Equal("0011223344556677", parsed.Inputs[0].ContentHash16);
        Assert.Equal("A.gen.h", parsed.Generated[0].RelativePath);
    }

    [Fact]
    public void Parse_TolerantToCrLfLineEndings()
    {
        string text = MinimalValid().Replace("\n", "\r\n");
        // Reader tolerates CRLF (trims \r from line end) even though the
        // writer only emits LF.
        GenManifest m = GenManifestReader.Parse(text);
        Assert.Equal(1, m.XhtSchemaVersion);
    }

    [Fact]
    public void Parse_ProducedAtUtcDeterministic_MustBeZero()
    {
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            "ContractVersion = 13.2+b04ae3cc84cdd9f3",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 1234567890", // non-zero
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            "[End]",
        });
        Assert.Throws<ManifestMalformedException>(() => GenManifestReader.Parse(text));
    }

    // ---------------------------------------------------------------------
    // Round 5 R4-MA5: GenManifestReader.Parse now validates ContractVersion
    // against XhtVersion.ContractVersion (the symmetric counterpart of the
    // XbtManifestReader R3 fix). A stale cached .gen.manifest written by
    // an older XHT cannot be silently accepted after a Contract bump.
    // Diagnostic XHT006 per /Documents/XHT.html Rev 7 Section 23.2.
    // ---------------------------------------------------------------------

    [Fact]
    public void Parse_ContractVersionMatchesXhtCompileTime_Succeeds()
    {
        // Sanity: a manifest with the literal current ContractVersion
        // parses cleanly. Uses XhtVersion.ContractVersion to guarantee
        // this test tracks the compile-time pin without drift.
        string text = MinimalValidWithContractVersion(XhtVersion.ContractVersion);
        GenManifest m = GenManifestReader.Parse(text);
        Assert.Equal(XhtVersion.ContractVersion, m.ContractVersion);
    }

    [Fact]
    public void Parse_ContractVersionMismatch_ThrowsXHT006()
    {
        const string mismatchedVersion = "99.99+deadbeefcafebabe";
        string text = MinimalValidWithContractVersion(mismatchedVersion);
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => GenManifestReader.Parse(text));
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
        Assert.Equal("XHT006", ex.DiagnosticCode);
        // Operator-actionable diagnostic must name BOTH versions so they
        // know which side (the stale cached manifest or the current XHT
        // build) to rebuild.
        Assert.Contains(mismatchedVersion, ex.Message, StringComparison.Ordinal);
        Assert.Contains(XhtVersion.ContractVersion, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ContractVersionDifferOnlyInHashSuffix_ThrowsXHT006()
    {
        // The structure-hash suffix is load-bearing: two manifests with
        // the same semantic tag but different structure hashes describe
        // different contract surfaces. The check must reject the
        // mismatched one even when the prefix matches.
        const string sameTagDifferentHash = "13.2+0000000000000000";
        Assert.NotEqual(XhtVersion.ContractVersion, sameTagDifferentHash);
        string text = MinimalValidWithContractVersion(sameTagDifferentHash);
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => GenManifestReader.Parse(text));
        Assert.Equal("XHT006", ex.DiagnosticCode);
    }

    [Fact]
    public void Read_GenManifestNotFound_CarriesXHT001DiagnosticCode()
    {
        // R4-CR1 anchor verification: the missing-file branch now carries
        // a catalog-anchored DiagnosticCode (XHT001 per Section 23.2)
        // instead of relying on the legacy XHT050 entry-point shim.
        string missing = Path.Combine(_tempDir, "absent.gen.manifest");
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => GenManifestReader.Read(missing));
        Assert.Equal("XHT001", ex.DiagnosticCode);
        Assert.Equal(ExitCodes.ManifestMalformed, ex.ExitCode);
    }

    [Fact]
    public void Parse_MissingEndMarker_CarriesXHT007DiagnosticCode()
    {
        // R4-CR1 anchor verification for the structural-rejection
        // branches: every malformed-shape throw site in
        // GenManifestReader.Parse now anchors XHT007.
        string text = string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            $"ContractVersion = {XhtVersion.ContractVersion}",
            "ModuleName = X",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "[Inputs]",
            "[Generated]",
            "[Diagnostics]",
            // [End] omitted
        });
        ManifestMalformedException ex = Assert.Throws<ManifestMalformedException>(
            () => GenManifestReader.Parse(text));
        Assert.Equal("XHT007", ex.DiagnosticCode);
    }

    private static string MinimalValidWithContractVersion(string contractVersion) =>
        string.Join("\n", new[]
        {
            "[Metadata]",
            "XhtSchemaVersion = 1",
            $"ContractVersion = {contractVersion}",
            "ModuleName = XScoring",
            "ProducedAtUtcDeterministic = 0",
            "GeneratedAtUtc = 2026-05-20T12:34:56Z",
            "",
            "[Inputs]",
            "",
            "[Generated]",
            "",
            "[Diagnostics]",
            "",
            "[End]",
            "",
        });
}
