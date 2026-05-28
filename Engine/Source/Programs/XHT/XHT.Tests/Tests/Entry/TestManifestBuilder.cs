// Copyright Simgenics. All Rights Reserved.

using System.IO;
using System.Text;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Helper for constructing valid XBT-manifest JSON in tests. Mirrors
/// the schema XBT's writer emits (Contract Section 10.2).
/// </summary>
internal static class TestManifestBuilder
{
    /// <summary>
    /// One-module manifest used by the parse-module / emit-module /
    /// validate-only mode tests. Writes the manifest to a fresh temp file
    /// in <paramref name="tempDir"/> and returns the path.
    /// </summary>
    /// <param name="tempDir">Pre-created temp directory.</param>
    /// <param name="moduleName">Module name to include (default <c>XScoring</c>).</param>
    /// <returns>Absolute path to the written manifest.</returns>
    public static string WriteOneModuleManifest(string tempDir, string moduleName = "XScoring")
    {
        string path = Path.Combine(tempDir, "Manifest.json");
        File.WriteAllText(path, OneModuleJson(moduleName), Encoding.UTF8);
        return path;
    }

    /// <summary>Empty-modules manifest used by the missing-module exit-50 test.</summary>
    /// <param name="tempDir">Pre-created temp directory.</param>
    /// <returns>Absolute path to the written manifest.</returns>
    public static string WriteEmptyManifest(string tempDir)
    {
        string path = Path.Combine(tempDir, "Manifest.json");
        File.WriteAllText(path, EmptyModulesJson(), Encoding.UTF8);
        return path;
    }

    private static string EmptyModulesJson() => """
        {
          "ContractVersion": "13.9+381d8ef7a7770d9b",
          "EngineVersion": "0.1.0",
          "Target": {
            "Name": "MiningTrainingEditor",
            "Type": "Editor",
            "Platform": "Win64",
            "Configuration": "Development",
            "Architecture": "x86_64",
            "GCRootABI": "Span-based v1",
            "ExceptionABI": "Tier1-Shim/Tier2-Direct",
            "ManglingScheme": "Itanium-LengthPrefixed-v1",
            "FipsMode": false,
            "SimPathConservativeRootsAllowed": false,
            "SimdLevelDefault": "SSE42",
            "StationRole": "None"
          },
          "RootLocalPath": "C:/repo",
          "ExternalDependenciesFile": null,
          "Modules": []
        }
        """;

    private static string OneModuleJson(string moduleName) => $$"""
        {
          "ContractVersion": "13.9+381d8ef7a7770d9b",
          "EngineVersion": "0.1.0",
          "Target": {
            "Name": "MiningTrainingEditor",
            "Type": "Editor",
            "Platform": "Win64",
            "Configuration": "Development",
            "Architecture": "x86_64",
            "GCRootABI": "Span-based v1",
            "ExceptionABI": "Tier1-Shim/Tier2-Direct",
            "ManglingScheme": "Itanium-LengthPrefixed-v1",
            "FipsMode": false,
            "SimPathConservativeRootsAllowed": false,
            "SimdLevelDefault": "SSE42",
            "StationRole": "None"
          },
          "RootLocalPath": "C:/repo",
          "ExternalDependenciesFile": null,
          "Modules": [
            {
              "Name": "{{moduleName}}",
              "Tier": "Engine",
              "ModuleType": "Runtime",
              "Languages": "Both",
              "BaseDirectory": "Engine/Source/Runtime/{{moduleName}}",
              "SourceFiles": [
                {
                  "RelativePath": "Public/{{moduleName}}.h",
                  "IsCSharp": false,
                  "IsHeader": true,
                  "IsTestOnly": false
                },
                {
                  "RelativePath": "Private/{{moduleName}}Logic.cs",
                  "IsCSharp": true,
                  "IsHeader": false,
                  "IsTestOnly": false
                }
              ],
              "PublicHeaders": [],
              "PrivateHeaders": [],
              "InternalHeaders": [],
              "CSharpSources": [],
              "IncludePaths": [],
              "PublicDefines": [],
              "ModuleDependencies": [
                { "Name": "XCore", "InterfaceModule": false }
              ],
              "GeneratedCPPFilenameBase": "{{moduleName}}",
              "SimPath": false,
              "EngineVersionCompat": "0.1.0",
              "SimdLevel": "Default",
              "PCHUsage": "Default",
              "ExcludeFromSharedPCH": false,
              "AllowHotReload": false,
              "IsTestModule": false,
              "DeprecationMessage": null,
              "MinimumToolchainVersion": null
            }
          ]
        }
        """;
}
