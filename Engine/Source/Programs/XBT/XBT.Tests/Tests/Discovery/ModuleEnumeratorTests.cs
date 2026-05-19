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

        ModuleEnumerator.Enumerate(
            new[] { Path.Combine(_scratchDir, "Engine", "Source") },
            _diagnostics);

        Assert.Single(_diagnostics.RoslynPending);
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
