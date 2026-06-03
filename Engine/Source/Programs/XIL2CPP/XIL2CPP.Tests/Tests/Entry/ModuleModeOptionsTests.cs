// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XIL2CPP.Entry;
using Simgenics.XPact.XIL2CPP.Entry.Modes;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="ModuleModeOptions.Parse"/>: required-flag
/// validation, the <c>-ManifestBin=</c> vs <c>-Manifest=</c> prefix
/// disambiguation, and the <c>-JsonFd=</c> validation. Per
/// /Documents/XIL2CPP.html Rev 4 Section 15. The type is internal; the test
/// assembly sees it via XIL2CPP.Entry's InternalsVisibleTo.
/// </summary>
public sealed class ModuleModeOptionsTests
{
    [Fact]
    public void Parse_AllRequiredFlags_Succeeds()
    {
        ModuleModeOptions opts = ModuleModeOptions.Parse(
            new[] { "-Manifest=C:/m.json", "-Module=XScoring" },
            requireOutput: false);

        Assert.Equal("C:/m.json", opts.ManifestPath);
        Assert.Equal("XScoring", opts.ModuleName);
        Assert.Null(opts.ManifestBinPath);
        Assert.Null(opts.JsonFd);
    }

    [Fact]
    public void Parse_MissingManifest_Throws()
    {
        CliArgumentException ex = Assert.Throws<CliArgumentException>(
            () => ModuleModeOptions.Parse(new[] { "-Module=XScoring" }, requireOutput: false));
        Assert.Contains("-Manifest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingModule_Throws()
    {
        CliArgumentException ex = Assert.Throws<CliArgumentException>(
            () => ModuleModeOptions.Parse(new[] { "-Manifest=C:/m.json" }, requireOutput: false));
        Assert.Contains("-Module", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequireOutput_MissingOut_Throws()
    {
        CliArgumentException ex = Assert.Throws<CliArgumentException>(
            () => ModuleModeOptions.Parse(
                new[] { "-Manifest=C:/m.json", "-Module=XScoring" },
                requireOutput: true));
        Assert.Contains("-Out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ManifestBin_DoesNotMatchManifestPrefix()
    {
        // -ManifestBin= must NOT populate ManifestPath; the longer prefix
        // wins. -Manifest= is still required separately.
        ModuleModeOptions opts = ModuleModeOptions.Parse(
            new[] { "-Manifest=C:/m.json", "-ManifestBin=C:/m.bin", "-Module=X" },
            requireOutput: false);

        Assert.Equal("C:/m.json", opts.ManifestPath);
        Assert.Equal("C:/m.bin", opts.ManifestBinPath);
    }

    [Fact]
    public void Parse_ValidJsonFd_Captured()
    {
        ModuleModeOptions opts = ModuleModeOptions.Parse(
            new[] { "-Manifest=C:/m.json", "-Module=X", "-JsonFd=5" },
            requireOutput: false);
        Assert.Equal(5, opts.JsonFd);
    }

    [Fact]
    public void Parse_InvalidJsonFd_Throws()
    {
        CliArgumentException ex = Assert.Throws<CliArgumentException>(
            () => ModuleModeOptions.Parse(
                new[] { "-Manifest=C:/m.json", "-Module=X", "-JsonFd=nope" },
                requireOutput: false));
        Assert.Contains("JsonFd", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        CliArgumentException ex = Assert.Throws<CliArgumentException>(
            () => ModuleModeOptions.Parse(
                new[] { "-Manifest=C:/m.json", "-Module=X", "-Bogus=1" },
                requireOutput: false));
        Assert.Contains("Unknown argument", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NullArgs_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ModuleModeOptions.Parse(null!, requireOutput: false));
    }
}
