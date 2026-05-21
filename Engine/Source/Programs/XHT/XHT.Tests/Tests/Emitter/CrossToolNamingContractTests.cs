// Copyright Simgenics. All Rights Reserved.

using System.IO;
using Simgenics.XPact.XHT.Emitter;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Emitter;

/// <summary>
/// Cross-tool naming contract per <c>/Documents/XHT.html</c> Rev 7
/// Section 9 + <c>/Documents/XBT.html</c> Rev 10 Section 9.4. XBT
/// pre-discovers XHT's output filenames before XHT runs, so the two
/// tools' filename derivations MUST produce byte-identical results for
/// the same inputs. This test fixture inlines the XBT-side naming
/// helpers' logic (a small handful of <c>string.Concat</c> calls) and
/// verifies it matches the XHT-side <c>HeaderEmitter.DeriveGenHeaderFileName</c>
/// / <c>SourceEmitter.DeriveGenSourceFileName</c> /
/// <c>ModuleInitEmitter.DeriveModuleInitFileName</c> /
/// <c>ModuleEmitter.DeriveGenManifestFileName</c> output for every test
/// input.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why inline instead of ProjectReference.</b> XHT.Tests must not
/// reference XBT.ActionGraph (and XBT.ActionGraph must not reference
/// XHT.Emitter) -- the two tools are independent at the build-tool
/// layer per XHT.html Section 9 ("XBT does not parse .gen.h or .gen.cpp;
/// the manifest is the join"). The cross-tool contract is preserved by
/// duplicating the ~6 lines of naming logic on both sides and
/// regression-testing that duplication here. If either side's logic
/// changes, this test fails and the other side must be updated to
/// match.
/// </para>
/// <para>
/// The XBT-side logic this test mirrors lives at
/// <c>Engine/Source/Programs/XBT/XBT.ActionGraph/Actions/XhtOutputNaming.cs</c>;
/// updating one side without the other is a contract violation that
/// this test will surface.
/// </para>
/// </remarks>
public sealed class CrossToolNamingContractTests
{
    /// <summary>
    /// XBT-side mirror of <see cref="HeaderEmitter.DeriveGenHeaderFileName"/>.
    /// Duplicates the byte-identical naming logic from
    /// <c>XBT.ActionGraph.Actions.XhtOutputNaming.GenHeaderFileName</c>
    /// so the cross-tool contract can be verified without a
    /// ProjectReference.
    /// </summary>
    private static string XbtSide_GenHeaderFileName(string sourceRelativePath)
    {
        string normalized = sourceRelativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem + ".gen.h";
    }

    /// <summary>XBT-side mirror of <see cref="SourceEmitter.DeriveGenSourceFileName"/>.</summary>
    private static string XbtSide_GenSourceFileName(string sourceRelativePath)
    {
        string normalized = sourceRelativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem + ".gen.cpp";
    }

    /// <summary>XBT-side mirror of <see cref="ModuleInitEmitter.DeriveModuleInitFileName"/>.</summary>
    private static string XbtSide_ModuleInitFileName(string moduleName, string? generatedCppFilenameBase)
    {
        string baseName = string.IsNullOrEmpty(generatedCppFilenameBase)
            ? moduleName
            : generatedCppFilenameBase;
        return baseName + ".init.gen.cpp";
    }

    /// <summary>XBT-side mirror of <see cref="ModuleEmitter.DeriveGenManifestFileName"/>.</summary>
    private static string XbtSide_GenManifestFileName(string moduleName, string? generatedCppFilenameBase)
    {
        string baseName = string.IsNullOrEmpty(generatedCppFilenameBase)
            ? moduleName
            : generatedCppFilenameBase;
        return baseName + ".gen.manifest";
    }

    /// <summary>
    /// Verifies the gen.h derivation matches byte-for-byte between
    /// XHT-side and XBT-side helpers for representative input shapes.
    /// </summary>
    [Theory]
    [InlineData("Public/XValve.h")]
    [InlineData("Private/Subdir/XValve.h")]
    [InlineData("XValve.h")]
    [InlineData("Public\\XValve.h")]
    [InlineData("Source/Public/Items/XHotZone.h")]
    public void GenHeaderFileName_XhtAndXbt_Match(string headerRelative)
    {
        string xhtSide = HeaderEmitter.DeriveGenHeaderFileName(headerRelative);
        string xbtSide = XbtSide_GenHeaderFileName(headerRelative);
        Assert.Equal(xhtSide, xbtSide);
    }

    /// <summary>
    /// Verifies the gen.cpp derivation matches byte-for-byte.
    /// </summary>
    [Theory]
    [InlineData("Public/XValve.h")]
    [InlineData("Private/Subdir/XValve.h")]
    [InlineData("XValve.h")]
    public void GenSourceFileName_XhtAndXbt_Match(string headerRelative)
    {
        string xhtSide = SourceEmitter.DeriveGenSourceFileName(headerRelative);
        string xbtSide = XbtSide_GenSourceFileName(headerRelative);
        Assert.Equal(xhtSide, xbtSide);
    }

    /// <summary>
    /// Build a minimal XbtModule for the naming tests. The naming
    /// helpers only consume <see cref="XbtModule.Name"/> and
    /// <see cref="XbtModule.GeneratedCPPFilenameBase"/>; every other
    /// field is filled with a sensible default.
    /// </summary>
    private static XbtModule MakeModule(string name, string? generatedBase)
    {
        return new XbtModule(
            Name: name,
            Tier: ModuleTier.Engine,
            ModuleType: ModuleType.Runtime,
            Languages: Languages.Both,
            BaseDirectory: "Engine/Source/Runtime/" + name,
            SourceFiles: System.Array.Empty<XbtSourceFile>(),
            PublicHeaders: System.Array.Empty<string>(),
            PrivateHeaders: System.Array.Empty<string>(),
            InternalHeaders: System.Array.Empty<string>(),
            CSharpSources: System.Array.Empty<string>(),
            IncludePaths: System.Array.Empty<string>(),
            PublicDefines: System.Array.Empty<string>(),
            ModuleDependencies: System.Array.Empty<XbtModuleDep>(),
            GeneratedCPPFilenameBase: generatedBase ?? string.Empty,
            SimPath: false,
            EngineVersionCompat: "0.0.0",
            SimdLevel: SimdLevel.Default,
            PCHUsage: PCHUsageMode.Default,
            ExcludeFromSharedPCH: false,
            AllowHotReload: false,
            IsTestModule: false,
            DeprecationMessage: null,
            MinimumToolchainVersion: null);
    }

    /// <summary>
    /// Verifies the .init.gen.cpp derivation matches byte-for-byte for
    /// both the module-name-fallback and explicit-base paths.
    /// </summary>
    [Theory]
    [InlineData("XScoring", null)]
    [InlineData("XScoring", "")]
    [InlineData("XScoring", "XCustomBase")]
    public void ModuleInitFileName_XhtAndXbt_Match(string moduleName, string? generatedBase)
    {
        XbtModule module = MakeModule(moduleName, generatedBase);
        string xhtSide = ModuleInitEmitter.DeriveModuleInitFileName(module);
        string xbtSide = XbtSide_ModuleInitFileName(moduleName, generatedBase);
        Assert.Equal(xhtSide, xbtSide);
    }

    /// <summary>
    /// Verifies the .gen.manifest derivation matches byte-for-byte.
    /// </summary>
    [Theory]
    [InlineData("XScoring", null)]
    [InlineData("XScoring", "")]
    [InlineData("XScoring", "XCustomBase")]
    public void GenManifestFileName_XhtAndXbt_Match(string moduleName, string? generatedBase)
    {
        XbtModule module = MakeModule(moduleName, generatedBase);
        string xhtSide = ModuleEmitter.DeriveGenManifestFileName(module);
        string xbtSide = XbtSide_GenManifestFileName(moduleName, generatedBase);
        Assert.Equal(xhtSide, xbtSide);
    }

    /// <summary>
    /// Smoke test: a full per-module output set composed by the XBT-side
    /// helpers matches the per-module set composed by the XHT-side
    /// emitters for a typical mixed input.
    /// </summary>
    [Fact]
    public void FullModuleOutputSet_XhtAndXbt_Match()
    {
        // Module: XScoring with two reflected headers.
        XbtModule module = MakeModule("XScoring", null);

        string xhtH1 = HeaderEmitter.DeriveGenHeaderFileName("Public/XValve.h");
        string xhtH2 = HeaderEmitter.DeriveGenHeaderFileName("Private/XInstructor.h");
        string xhtCpp1 = SourceEmitter.DeriveGenSourceFileName("Public/XValve.h");
        string xhtCpp2 = SourceEmitter.DeriveGenSourceFileName("Private/XInstructor.h");
        string xhtInit = ModuleInitEmitter.DeriveModuleInitFileName(module);
        string xhtManifest = ModuleEmitter.DeriveGenManifestFileName(module);

        string xbtH1 = XbtSide_GenHeaderFileName("Public/XValve.h");
        string xbtH2 = XbtSide_GenHeaderFileName("Private/XInstructor.h");
        string xbtCpp1 = XbtSide_GenSourceFileName("Public/XValve.h");
        string xbtCpp2 = XbtSide_GenSourceFileName("Private/XInstructor.h");
        string xbtInit = XbtSide_ModuleInitFileName("XScoring", null);
        string xbtManifest = XbtSide_GenManifestFileName("XScoring", null);

        Assert.Equal(xhtH1, xbtH1);
        Assert.Equal(xhtH2, xbtH2);
        Assert.Equal(xhtCpp1, xbtCpp1);
        Assert.Equal(xhtCpp2, xbtCpp2);
        Assert.Equal(xhtInit, xbtInit);
        Assert.Equal(xhtManifest, xbtManifest);
    }
}
