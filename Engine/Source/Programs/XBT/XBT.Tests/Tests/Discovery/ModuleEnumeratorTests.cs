// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Discovery;

/// <summary>
/// Exercises <see cref="ModuleEnumerator"/> against a scaffolded
/// directory tree mimicking the canonical layout per
/// <c>/Documents/XBT.html</c> Section 3.1.
/// </summary>
public sealed class ModuleEnumeratorTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly RecordingDiagnostics _diagnostics;

    public ModuleEnumeratorTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ModuleEnumerator",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
        _diagnostics = new RecordingDiagnostics();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public void Enumerate_Single_Module_Round_Trips_Through_Catalog()
    {
        string moduleDir = Path.Combine(_scratchDir, "Engine", "Source", "Runtime", "XCore");
        Directory.CreateDirectory(moduleDir);
        WriteTomlDescriptor(moduleDir, "XCore", ModuleTier.Engine);

        ModuleCatalog catalog = ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("XCore", out ModuleRecord rec));
        Assert.Equal(ModuleTier.Engine, rec.Rules.Tier);
        Assert.Empty(_diagnostics.ParseFailures);
    }

    [Fact]
    public void Enumerate_Three_Tier_Layout_Finds_All_Modules()
    {
        // Engine/Source/Runtime/XCore + XMath
        WriteModule("Engine/Source/Runtime/XCore", "XCore", ModuleTier.Engine);
        WriteModule("Engine/Source/Runtime/XMath", "XMath", ModuleTier.Engine);

        // Studio/Source/Sim/StudioSim
        WriteModule("Studio/Source/Sim/StudioSim", "StudioSim", ModuleTier.Studio);

        // Projects/MyProj/Source/Game/GameFeature
        WriteModule("Projects/MyProj/Source/Game/GameFeature", "GameFeature", ModuleTier.Project);

        ModuleCatalog catalog = ModuleEnumerator.Enumerate(
            new[]
            {
                Path.Combine(_scratchDir, "Engine", "Source"),
                Path.Combine(_scratchDir, "Studio", "Source"),
                Path.Combine(_scratchDir, "Projects", "MyProj", "Source"),
            },
            _diagnostics);

        Assert.Equal(4, catalog.Count);
        // Modules sorted alphabetically by name -> determinism.
        Assert.Equal(
            new[] { "GameFeature", "StudioSim", "XCore", "XMath" },
            catalog.Modules.Select(m => m.Rules.Name).ToArray());
        Assert.Empty(_diagnostics.ParseFailures);
    }

    [Fact]
    public void Malformed_Toml_Reported_And_Skipped()
    {
        string moduleDir = Path.Combine(_scratchDir, "Engine", "Source", "Runtime", "Bad");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(
            Path.Combine(moduleDir, "Bad.Build.toml"),
            "this isn't toml = {{ unterminated",
            new UTF8Encoding(false));

        // Add a good module so we can confirm enumeration continues.
        WriteModule("Engine/Source/Runtime/Good", "Good", ModuleTier.Engine);

        ModuleCatalog catalog = ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics);

        Assert.Equal(1, catalog.Count);
        Assert.Equal("Good", catalog.Modules[0].Rules.Name);
        Assert.Single(_diagnostics.ParseFailures);
        Assert.Contains("Bad", _diagnostics.ParseFailures[0].path);
    }

    [Fact]
    public void Duplicate_Module_Name_Across_Tiers_Rejected_By_Catalog()
    {
        WriteModule("Engine/Source/Runtime/XCore", "XCore", ModuleTier.Engine);
        WriteModule("Studio/Source/Sim/XCore", "XCore", ModuleTier.Studio);

        Assert.Throws<DescriptorParseException>(() =>
            ModuleEnumerator.Enumerate(
                new[]
                {
                    Path.Combine(_scratchDir, "Engine", "Source"),
                    Path.Combine(_scratchDir, "Studio", "Source"),
                },
                _diagnostics));
    }

    [Fact]
    public void RoslynFallback_Diagnostic_Fires_For_BuildCs_Outside_XBT_Source()
    {
        string moduleDir = Path.Combine(_scratchDir, "Engine", "Source", "Runtime", "Legacy");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(
            Path.Combine(moduleDir, "Legacy.Build.cs"),
            "// Copyright Simgenics. All Rights Reserved.\n// stub",
            new UTF8Encoding(false));

        // No target supplied -> legacy pending-diagnostic path.
        ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics);

        Assert.Single(_diagnostics.RoslynPending);
    }

    [Fact]
    public void BuildCsOnly_RoslynFallback_Lands_In_Catalog_When_Target_Supplied()
    {
        // A module shipping only a .Build.cs (Phase 1 escape hatch).
        string moduleDir = Path.Combine(_scratchDir, "Engine", "Source", "Runtime", "XCryptoFIPS");
        Directory.CreateDirectory(moduleDir);
        WriteBuildCs(moduleDir, "XCryptoFIPS",
            tier: "Engine",
            extraCtorBody: "if (target.FipsMode) { PublicDefinitions.Add(\"X_FIPS=1\"); }");

        string cacheDir = Path.Combine(_scratchDir, "Cache");
        Directory.CreateDirectory(cacheDir);
        TargetRules target = MakeTarget(fipsMode: true);

        ModuleCatalog catalog = ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics,
            target,
            buildCsCacheDirectory: cacheDir);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("XCryptoFIPS", out ModuleRecord rec));
        Assert.Equal(ModuleTier.Engine, rec.Rules.Tier);
        Assert.Contains("X_FIPS=1", rec.Rules.PublicDefinitions);
        // Legacy pending diagnostic must NOT fire when a target is supplied.
        Assert.Empty(_diagnostics.RoslynPending);
        Assert.Empty(_diagnostics.ParseFailures);
    }

    [Fact]
    public void Enumerate_CaseSensitiveFilesystem_DistinguishesCaseDifferences()
    {
        // On case-sensitive filesystems (Linux, macOS with case-sensitive
        // APFS) two directories /Engine/Source/Runtime/Foo and
        // /Engine/Source/Runtime/foo are distinct, and each may carry
        // its own .Build.toml. The enumerator's per-directory grouping
        // must NOT collapse them via an ordinal-ignore-case comparer
        // (which would arbitrarily drop one descriptor).
        //
        // Skipped on Windows: the OS itself case-folds, so even creating
        // both names winds up writing to the same canonical directory.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WriteModule("Engine/Source/Runtime/Foo", "Foo", ModuleTier.Engine);
        WriteModule("Engine/Source/Runtime/foo", "FooLower", ModuleTier.Engine);

        ModuleCatalog catalog = ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics);

        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGet("Foo", out _));
        Assert.True(catalog.TryGet("FooLower", out _));
        Assert.Empty(_diagnostics.ParseFailures);
    }

    [Fact]
    public void BuildCs_And_BuildToml_Both_Present_BuildCs_Wins()
    {
        // Per Contract Section 9.6, when a module ships both descriptors
        // the .Build.cs takes precedence.
        string moduleDir = Path.Combine(_scratchDir, "Engine", "Source", "Runtime", "DualDescriptor");
        Directory.CreateDirectory(moduleDir);

        // Write the TOML with a sentinel define so we can prove which
        // path executed.
        ModuleRules tomlRules = new()
        {
            Name = "DualDescriptor",
            Tier = ModuleTier.Engine,
            ModuleType = ModuleType.Runtime,
        };
        tomlRules.PublicDefinitions.Add("FROM_TOML=1");
        string tomlPath = Path.Combine(moduleDir, "DualDescriptor.Build.toml");
        File.WriteAllText(tomlPath, BuildTomlSerializer.Serialize(tomlRules), new UTF8Encoding(false));

        // Write the .Build.cs with its own sentinel define.
        WriteBuildCs(moduleDir, "DualDescriptor",
            tier: "Engine",
            extraCtorBody: "PublicDefinitions.Add(\"FROM_BUILDCS=1\");");

        string cacheDir = Path.Combine(_scratchDir, "Cache");
        Directory.CreateDirectory(cacheDir);
        TargetRules target = MakeTarget();

        ModuleCatalog catalog = ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics,
            target,
            buildCsCacheDirectory: cacheDir);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("DualDescriptor", out ModuleRecord rec));
        // The .Build.cs branch ran -- FROM_BUILDCS=1 present, FROM_TOML=1 absent.
        Assert.Contains("FROM_BUILDCS=1", rec.Rules.PublicDefinitions);
        Assert.DoesNotContain("FROM_TOML=1", rec.Rules.PublicDefinitions);
        // The descriptor path on the record is the .Build.cs, not the TOML.
        Assert.EndsWith(".Build.cs", rec.DescriptorPath);
    }

    private static TargetRules MakeTarget(bool fipsMode = false)
        => new()
        {
            Name = "TestTarget",
            TargetType = BuildTargetType.Editor,
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            Architecture = "x86_64",
            StationRole = StationRole.Engineer,
            FipsMode = fipsMode,
        };

    private static void WriteBuildCs(
        string moduleDir,
        string moduleName,
        string tier,
        string extraCtorBody)
    {
        string source = $$"""
            // Copyright Simgenics. All Rights Reserved.
            using Simgenics.XPact.XBT.Configuration;
            using Simgenics.XPact.XBT.Manifest;

            public sealed class {{moduleName}}Build : ModuleRules
            {
                public {{moduleName}}Build(TargetRules target)
                {
                    Name = "{{moduleName}}";
                    Tier = ModuleTier.{{tier}};
                    ModuleType = ModuleType.Runtime;
                    {{extraCtorBody}}
                }
            }
            """;
        File.WriteAllText(
            Path.Combine(moduleDir, moduleName + ".Build.cs"),
            source,
            new UTF8Encoding(false));
    }

    private void WriteModule(string relativePath, string moduleName, ModuleTier tier)
    {
        string fullDir = Path.Combine(_scratchDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(fullDir);
        WriteTomlDescriptor(fullDir, moduleName, tier);
    }

    private static void WriteTomlDescriptor(string dir, string name, ModuleTier tier)
    {
        ModuleRules rules = new()
        {
            Name = name,
            Tier = tier,
            ModuleType = ModuleType.Runtime,
        };
        string toml = BuildTomlSerializer.Serialize(rules);
        File.WriteAllText(Path.Combine(dir, name + ".Build.toml"), toml, new UTF8Encoding(false));
    }

    private sealed class RecordingDiagnostics : IDiscoveryDiagnostics
    {
        public List<(string path, string message)> ParseFailures { get; } = new();
        public List<(string path, string message)> PluginFailures { get; } = new();
        public List<string> RoslynPending { get; } = new();
        public List<(string root, string message)> DiscoveryFailures { get; } = new();

        public void ReportDiscoveryFailure(string root, string message)
            => DiscoveryFailures.Add((root, message));

        public void ReportPluginParseFailure(string descriptorPath, string message)
            => PluginFailures.Add((descriptorPath, message));

        public void ReportModuleParseFailure(string descriptorPath, string message)
            => ParseFailures.Add((descriptorPath, message));

        public void ReportRoslynFallbackPending(string descriptorPath)
            => RoslynPending.Add(descriptorPath);
    }
}
