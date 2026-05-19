// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;

// The C# namespace Simgenics.XPact.XBT.Tests.Tests.Manifest collides with the
// imported type name `Manifest` -- when type-binding inside our namespace,
// C# sees `Manifest` and tries to resolve it as a namespace first. Alias to
// the fully-qualified record so the fixture code is unambiguous and readable.
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// Hand-built <see cref="Manifest"/> instances used by the JSON, FBS, and
/// cross-format round-trip tests. Fixtures are deterministic (no GUIDs,
/// no timestamps, no machine-local paths) so the determinism assertions
/// in <see cref="JsonRoundTripTests"/> hold across runs.
/// </summary>
internal static class ManifestFixtures
{
    /// <summary>
    /// A manifest that exercises every non-trivial field path: multiple
    /// modules, mixed languages, SimPath, ModuleType.Programs, and the
    /// optional nullable fields populated and unpopulated.
    /// </summary>
    public static ManifestRecord RichExample()
    {
        Module xCore = new(
            Name:                     "XCore",
            Tier:                     ModuleTier.Engine,
            ModuleType:               ModuleType.Runtime,
            Languages:                Languages.Cpp,
            BaseDirectory:            "Engine/Source/Runtime/XCore",
            SourceFiles:              new List<SourceFile>
            {
                new("Private/XCore.cpp",          IsCSharp: false, IsHeader: false, IsTestOnly: false),
                new("Public/XCore.h",             IsCSharp: false, IsHeader: true,  IsTestOnly: false),
                new("Private/Math/XVector.cpp",   IsCSharp: false, IsHeader: false, IsTestOnly: false),
                new("Public/Math/XVector.h",      IsCSharp: false, IsHeader: true,  IsTestOnly: false),
            },
            PublicHeaders:            new List<string> { "Public/XCore.h", "Public/Math/XVector.h" },
            PrivateHeaders:           new List<string> { "Private/XCoreInternal.h" },
            InternalHeaders:          new List<string>(),
            CSharpSources:            new List<string>(),
            IncludePaths:             new List<string> { "Public", "Private" },
            PublicDefines:            new List<string> { "XCORE_API=DLLIMPORT" },
            ModuleDependencies:       new List<ModuleDep>(),
            GeneratedCPPFilenameBase: "XCore.gen",
            SimPath:                  false,
            EngineVersionCompat:      "^0.1",
            SimdLevel:                SimdLevel.Default,
            PCHUsage:                 PCHUsageMode.UseSharedPCHs,
            ExcludeFromSharedPCH:     false,
            AllowHotReload:           false,
            IsTestModule:             false,
            DeprecationMessage:       null,
            MinimumToolchainVersion:  null);

        Module xScoring = new(
            Name:                     "XScoring",
            Tier:                     ModuleTier.Engine,
            ModuleType:               ModuleType.Runtime,
            Languages:                Languages.Both,
            BaseDirectory:            "Engine/Source/Runtime/XScoring",
            SourceFiles:              new List<SourceFile>
            {
                new("Private/XScoring.cpp",       IsCSharp: false, IsHeader: false, IsTestOnly: false),
                new("Private/Scoring.cs",         IsCSharp: true,  IsHeader: false, IsTestOnly: false),
                new("Private/Rubric.cs",          IsCSharp: true,  IsHeader: false, IsTestOnly: false),
            },
            PublicHeaders:            new List<string> { "Public/XScoring.h" },
            PrivateHeaders:           new List<string> { "Private/XScoringInternal.h" },
            InternalHeaders:          new List<string>(),
            CSharpSources:            new List<string> { "Private/Scoring.cs", "Private/Rubric.cs" },
            IncludePaths:             new List<string> { "Public", "Private" },
            PublicDefines:            new List<string> { "XSCORING_API=DLLIMPORT" },
            ModuleDependencies:       new List<ModuleDep>
            {
                new("XCore", InterfaceModule: false),
                new("XSerialization", InterfaceModule: true),
            },
            GeneratedCPPFilenameBase: "XScoring.gen",
            SimPath:                  true,
            EngineVersionCompat:      "^0.1",
            SimdLevel:                SimdLevel.SSE42,
            PCHUsage:                 PCHUsageMode.NoSharedPCHs,
            ExcludeFromSharedPCH:     true,
            AllowHotReload:           false,
            IsTestModule:             false,
            DeprecationMessage:       null,
            MinimumToolchainVersion:  null);

        Module xbtPrograms = new(
            Name:                     "XBT.Core",
            Tier:                     ModuleTier.Engine,
            ModuleType:               ModuleType.Programs,
            Languages:                Languages.CSharp,
            BaseDirectory:            "Engine/Source/Programs/XBT/XBT.Core",
            SourceFiles:              new List<SourceFile>
            {
                new("IoHash.cs",                  IsCSharp: true, IsHeader: false, IsTestOnly: false),
                new("FileItem.cs",                IsCSharp: true, IsHeader: false, IsTestOnly: false),
            },
            PublicHeaders:            new List<string>(),
            PrivateHeaders:           new List<string>(),
            InternalHeaders:          new List<string>(),
            CSharpSources:            new List<string> { "IoHash.cs", "FileItem.cs" },
            IncludePaths:             new List<string>(),
            PublicDefines:            new List<string>(),
            ModuleDependencies:       new List<ModuleDep>(),
            GeneratedCPPFilenameBase: "XBT.Core.gen",
            SimPath:                  false,
            EngineVersionCompat:      "*",
            SimdLevel:                SimdLevel.None,
            PCHUsage:                 PCHUsageMode.NoPCHs,
            ExcludeFromSharedPCH:     false,
            AllowHotReload:           false,
            IsTestModule:             false,
            DeprecationMessage:       null,
            MinimumToolchainVersion:  "1.7.0");

        return new ManifestRecord(
            ContractVersion:                 "v13-stub",
            EngineVersion:                   "0.1.0",
            TargetName:                      "MiningTrainingEditor",
            TargetType:                      BuildTargetType.Editor,
            Configuration:                   BuildConfiguration.Development,
            Platform:                        Platform.Win64,
            RootLocalPath:                   "C:/repo/XPact_Engine",
            ExternalDependenciesFile:        "Intermediate/Build/MiningTrainingEditor/Development/ExternalDeps.json",
            FipsMode:                        false,
            SimPathConservativeRootsAllowed: false,
            StationRole:                     StationRole.Engineer,
            SimdLevelDefault:                SimdLevel.SSE42,
            Modules:                         new List<Module> { xCore, xScoring, xbtPrograms });
    }

    /// <summary>
    /// A manifest with one module per tier (Engine, Studio, Project) plus
    /// one Programs-typed module. Smaller than <see cref="RichExample"/> --
    /// used by JSON-only tier-coverage tests.
    /// </summary>
    public static ManifestRecord OnePerTier()
    {
        Module engineMod = MakeMinimalModule("XCore",         ModuleTier.Engine,  ModuleType.Runtime);
        Module studioMod = MakeMinimalModule("StudioShared",  ModuleTier.Studio,  ModuleType.Runtime);
        Module projectMod = MakeMinimalModule("ProjectGame",  ModuleTier.Project, ModuleType.Runtime);
        Module programsMod = MakeMinimalModule("XBT.Manifest", ModuleTier.Engine, ModuleType.Programs);

        return new ManifestRecord(
            ContractVersion:                 "v13-stub",
            EngineVersion:                   "0.1.0",
            TargetName:                      "SmokeTarget",
            TargetType:                      BuildTargetType.Game,
            Configuration:                   BuildConfiguration.Development,
            Platform:                        Platform.Win64,
            RootLocalPath:                   "C:/repo/XPact_Engine",
            ExternalDependenciesFile:        null,
            FipsMode:                        false,
            SimPathConservativeRootsAllowed: false,
            StationRole:                     StationRole.Instructor,
            SimdLevelDefault:                SimdLevel.SSE42,
            Modules:                         new List<Module> { engineMod, studioMod, projectMod, programsMod });
    }

    private static Module MakeMinimalModule(string name, ModuleTier tier, ModuleType moduleType)
    {
        return new Module(
            Name:                     name,
            Tier:                     tier,
            ModuleType:               moduleType,
            Languages:                Languages.Cpp,
            BaseDirectory:            $"Engine/Source/{tier}/{name}",
            SourceFiles:              new List<SourceFile>(),
            PublicHeaders:            new List<string>(),
            PrivateHeaders:           new List<string>(),
            InternalHeaders:          new List<string>(),
            CSharpSources:            new List<string>(),
            IncludePaths:             new List<string>(),
            PublicDefines:            new List<string>(),
            ModuleDependencies:       new List<ModuleDep>(),
            GeneratedCPPFilenameBase: $"{name}.gen",
            SimPath:                  false,
            EngineVersionCompat:      "*",
            SimdLevel:                SimdLevel.Default,
            PCHUsage:                 PCHUsageMode.Default,
            ExcludeFromSharedPCH:     false,
            AllowHotReload:           false,
            IsTestModule:             false,
            DeprecationMessage:       null,
            MinimumToolchainVersion:  null);
    }
}
