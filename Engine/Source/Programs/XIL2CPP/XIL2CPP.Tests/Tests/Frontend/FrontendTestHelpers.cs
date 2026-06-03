// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Manifest;
using Platform = Simgenics.XPact.XIL2CPP.Manifest.Platform;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

/// <summary>
/// Shared helpers for the Pass-1 front-end tests: the deterministic BCL
/// reference set (from <c>Basic.Reference.Assemblies.Net80</c>) and minimal
/// synthetic manifest / module builders.
/// </summary>
/// <remarks>
/// Product code never references <c>Basic.Reference.Assemblies</c> (that
/// would break the X-IL2CPP-CSPATH-DET determinism gate); the package is a
/// test-only dependency that supplies a pinned in-package .NET 8 reference
/// set so the tests bind a CSharpCompilation without the build machine's
/// installed SDK. Per /Documents/XIL2CPP.html Rev 4 Section 9.8.
/// </remarks>
internal static class FrontendTestHelpers
{
    /// <summary>
    /// The pinned .NET 8 BCL reference set as a plain list of
    /// <see cref="MetadataReference"/>.
    /// </summary>
    public static IReadOnlyList<MetadataReference> BclReferences()
        => Basic.Reference.Assemblies.Net80.References.All
            .Cast<MetadataReference>()
            .ToList();

    /// <summary>
    /// Build a minimal valid <see cref="XbtTargetInfo"/> for a synthetic
    /// manifest.
    /// </summary>
    public static XbtTargetInfo MinimalTarget() => new(
        Name: "MiningTrainingEditor",
        Type: BuildTargetType.Editor,
        Platform: Platform.Win64,
        Configuration: BuildConfiguration.Development,
        Architecture: "x86_64",
        GCRootABI: "Span-based v1",
        ExceptionABI: "Tier1-Shim/Tier2-Direct",
        ManglingScheme: "Itanium-LengthPrefixed-v1",
        FipsMode: false,
        SimPathConservativeRootsAllowed: false,
        SimdLevelDefault: SimdLevel.SSE42,
        StationRole: StationRole.None);

    /// <summary>
    /// Build a synthetic module whose C# sources are the supplied relative
    /// paths and whose dependencies are the supplied module names.
    /// </summary>
    public static XbtModule Module(
        string name,
        string baseDirectory,
        IReadOnlyList<string> csharpSources,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<string>? publicDefines = null)
    {
        List<XbtModuleDep> deps = new();
        if (dependencies is not null)
        {
            foreach (string dep in dependencies)
            {
                deps.Add(new XbtModuleDep(dep, InterfaceModule: false));
            }
        }

        return new XbtModule(
            Name: name,
            Tier: ModuleTier.Engine,
            ModuleType: ModuleType.Runtime,
            Languages: Languages.CSharp,
            BaseDirectory: baseDirectory,
            SourceFiles: new List<XbtSourceFile>(),
            PublicHeaders: new List<string>(),
            PrivateHeaders: new List<string>(),
            InternalHeaders: new List<string>(),
            CSharpSources: csharpSources,
            IncludePaths: new List<string>(),
            PublicDefines: publicDefines ?? new List<string>(),
            ModuleDependencies: deps,
            GeneratedCPPFilenameBase: name,
            SimPath: false,
            EngineVersionCompat: "0.1.0",
            SimdLevel: SimdLevel.Default,
            PCHUsage: PCHUsageMode.Default,
            ExcludeFromSharedPCH: false,
            AllowHotReload: false,
            IsTestModule: false,
            DeprecationMessage: null,
            MinimumToolchainVersion: null);
    }

    /// <summary>
    /// Build a synthetic manifest rooted at <paramref name="rootLocalPath"/>
    /// containing the supplied modules. The ContractVersion is set to
    /// XIL2CPP's compile-time pin so the reader's ContractVersion gate passes.
    /// </summary>
    public static XbtManifest Manifest(string rootLocalPath, params XbtModule[] modules) => new(
        ContractVersion: Simgenics.XPact.XIL2CPP.Core.Xil2CppVersion.ContractVersion,
        EngineVersion: "0.1.0",
        Target: MinimalTarget(),
        RootLocalPath: rootLocalPath,
        ExternalDependenciesFile: null,
        Modules: modules.ToList());
}
