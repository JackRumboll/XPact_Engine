// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XBT.ActionGraph.Actions;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.ActionGraph.Actions;

/// <summary>
/// Verifies the XBT-side mirror of XHT.Emitter's filename-derivation
/// helpers per <c>/Documents/XHT.html</c> Rev 5 Section 9.3.1 +
/// <c>/Documents/XBT.html</c> Rev 10 Section 9.4. The cross-tool
/// byte-identical contract is verified separately in XHT.Tests's
/// <c>CrossToolNamingContractTests</c>; these tests cover the XBT-side
/// behaviour (format, determinism, fall-back rules).
/// </summary>
public sealed class XhtOutputNamingTests
{
    /// <summary>
    /// <c>GenHeaderFileName</c> strips the leading directory components
    /// + extension, then appends <c>.gen.h</c>.
    /// </summary>
    [Theory]
    [InlineData("Public/XValve.h", "XValve.gen.h")]
    [InlineData("Private/Subdir/XValve.h", "XValve.gen.h")]
    [InlineData("XValve.h", "XValve.gen.h")]
    [InlineData(@"Public\XValve.h", "XValve.gen.h")] // Windows-style backslash
    public void GenHeaderFileName_StripsDirectoryAndAppendsGenH(string input, string expected)
    {
        Assert.Equal(expected, XhtOutputNaming.GenHeaderFileName(input));
    }

    /// <summary>
    /// <c>GenSourceFileName</c> follows the same rule but produces
    /// <c>.gen.cpp</c>.
    /// </summary>
    [Theory]
    [InlineData("Public/XValve.h", "XValve.gen.cpp")]
    [InlineData("Private/Subdir/XValve.h", "XValve.gen.cpp")]
    [InlineData("XValve.h", "XValve.gen.cpp")]
    public void GenSourceFileName_StripsDirectoryAndAppendsGenCpp(string input, string expected)
    {
        Assert.Equal(expected, XhtOutputNaming.GenSourceFileName(input));
    }

    /// <summary>
    /// <c>ModuleInitFileName</c> uses <c>GeneratedCPPFilenameBase</c>
    /// when set, falls back to the module name otherwise.
    /// </summary>
    [Theory]
    [InlineData("XScoring", null, "XScoring.init.gen.cpp")]
    [InlineData("XScoring", "", "XScoring.init.gen.cpp")]
    [InlineData("XScoring", "XScoring.gen", "XScoring.gen.init.gen.cpp")]
    public void ModuleInitFileName_HonoursBaseWithModuleFallback(
        string moduleName, string? generatedBase, string expected)
    {
        Assert.Equal(expected, XhtOutputNaming.ModuleInitFileName(moduleName, generatedBase));
    }

    /// <summary>
    /// <c>GenManifestFileName</c> uses <c>GeneratedCPPFilenameBase</c>
    /// when set, falls back to the module name otherwise.
    /// </summary>
    [Theory]
    [InlineData("XScoring", null, "XScoring.gen.manifest")]
    [InlineData("XScoring", "", "XScoring.gen.manifest")]
    [InlineData("XScoring", "XScoring.gen", "XScoring.gen.gen.manifest")]
    public void GenManifestFileName_HonoursBaseWithModuleFallback(
        string moduleName, string? generatedBase, string expected)
    {
        Assert.Equal(expected, XhtOutputNaming.GenManifestFileName(moduleName, generatedBase));
    }

    /// <summary>
    /// Every helper is deterministic across repeated invocations with
    /// the same inputs (no clock / RNG / environment leakage).
    /// </summary>
    [Fact]
    public void AllHelpers_AreDeterministic()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal("XValve.gen.h", XhtOutputNaming.GenHeaderFileName("Public/XValve.h"));
            Assert.Equal("XValve.gen.cpp", XhtOutputNaming.GenSourceFileName("Public/XValve.h"));
            Assert.Equal("XScoring.init.gen.cpp", XhtOutputNaming.ModuleInitFileName("XScoring"));
            Assert.Equal("XScoring.gen.manifest", XhtOutputNaming.GenManifestFileName("XScoring"));
            Assert.Equal("XScoring.tokens.bin", XhtOutputNaming.TokensBinFileName("XScoring"));
        }
    }

    /// <summary>
    /// Null / whitespace inputs raise on every helper.
    /// </summary>
    /// <remarks>
    /// <see cref="ArgumentException.ThrowIfNullOrWhiteSpace"/> raises
    /// <see cref="ArgumentNullException"/> for null and
    /// <see cref="ArgumentException"/> for empty / whitespace.
    /// <see cref="ArgumentNullException"/> derives from
    /// <see cref="ArgumentException"/>, so a <c>ThrowsAny</c> assertion
    /// covers both shapes without false-positive failures.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespace_Throws(string? input)
    {
        Assert.ThrowsAny<ArgumentException>(() => XhtOutputNaming.GenHeaderFileName(input!));
        Assert.ThrowsAny<ArgumentException>(() => XhtOutputNaming.GenSourceFileName(input!));
        Assert.ThrowsAny<ArgumentException>(() => XhtOutputNaming.ModuleInitFileName(input!));
        Assert.ThrowsAny<ArgumentException>(() => XhtOutputNaming.GenManifestFileName(input!));
        Assert.ThrowsAny<ArgumentException>(() => XhtOutputNaming.TokensBinFileName(input!));
    }
}
