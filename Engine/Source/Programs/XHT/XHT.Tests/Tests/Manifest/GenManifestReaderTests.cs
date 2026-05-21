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
}
