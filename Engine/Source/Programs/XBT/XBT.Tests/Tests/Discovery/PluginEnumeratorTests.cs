// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Discovery;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Discovery;

/// <summary>
/// Exercises <see cref="PluginEnumerator"/> directory-level pruning.
/// The same hazard <see cref="ModuleEnumeratorTests"/> covers for
/// <c>*.Build.toml</c> applies to <c>*.xplugin</c>: a test fixture
/// that ships a plugin descriptor and gets copied into a project's
/// build output (<c>bin/</c>, <c>obj/</c>) would surface as a phantom
/// plugin during the production scan.
/// </summary>
public sealed class PluginEnumeratorTests : IDisposable
{
    private readonly string _scratchDir;
    private readonly RecordingDiagnostics _diagnostics;

    public PluginEnumeratorTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.PluginEnumerator",
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
    public void Enumerate_Finds_Plugin_Under_Engine_Plugins()
    {
        // Baseline: a real Engine-tier plugin is discoverable.
        WritePlugin("Engine/Plugins/HMIPanels", "HMIPanels");

        PluginCatalog catalog = PluginEnumerator.Enumerate(
            engineRoot: Path.Combine(_scratchDir, "Engine"),
            studioRoot: null,
            projectRoots: null,
            diagnostics: _diagnostics);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("HMIPanels", out PluginEntry entry));
        Assert.Empty(_diagnostics.PluginFailures);
    }

    [Fact]
    public void Enumerate_Skips_BinSubtree_Descendants()
    {
        // A plugin descriptor that ends up inside a bin/ subtree (e.g.
        // a test project that copies a .xplugin to its build output) must
        // NOT surface in the production catalog.
        WritePlugin("Engine/Plugins/RealPlugin", "RealPlugin");
        WritePlugin("Engine/Plugins/SomeTool/bin/Debug/net8.0/Fixtures/GhostPlugin", "GhostPlugin");

        PluginCatalog catalog = PluginEnumerator.Enumerate(
            engineRoot: Path.Combine(_scratchDir, "Engine"),
            studioRoot: null,
            projectRoots: null,
            diagnostics: _diagnostics);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("RealPlugin", out _));
        Assert.False(catalog.TryGet("GhostPlugin", out _));
        Assert.Empty(_diagnostics.PluginFailures);
    }

    [Fact]
    public void Enumerate_Skips_ObjSubtree_Descendants()
    {
        WritePlugin("Engine/Plugins/RealPlugin", "RealPlugin");
        WritePlugin("Engine/Plugins/SomeTool/obj/Debug/Stale/GhostPlugin", "GhostPlugin");

        PluginCatalog catalog = PluginEnumerator.Enumerate(
            engineRoot: Path.Combine(_scratchDir, "Engine"),
            studioRoot: null,
            projectRoots: null,
            diagnostics: _diagnostics);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("RealPlugin", out _));
        Assert.False(catalog.TryGet("GhostPlugin", out _));
        Assert.Empty(_diagnostics.PluginFailures);
    }

    [Fact]
    public void Enumerate_Skips_DotIdeaSubtree_Descendants()
    {
        WritePlugin("Engine/Plugins/RealPlugin", "RealPlugin");
        WritePlugin("Engine/Plugins/.idea/scratch/GhostPlugin", "GhostPlugin");

        PluginCatalog catalog = PluginEnumerator.Enumerate(
            engineRoot: Path.Combine(_scratchDir, "Engine"),
            studioRoot: null,
            projectRoots: null,
            diagnostics: _diagnostics);

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("RealPlugin", out _));
        Assert.False(catalog.TryGet("GhostPlugin", out _));
        Assert.Empty(_diagnostics.PluginFailures);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private void WritePlugin(string relativePath, string pluginName)
    {
        string fullDir = Path.Combine(
            _scratchDir,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(fullDir);
        string json = $$"""
            {
              "Name": "{{pluginName}}",
              "Version": "1.0.0"
            }
            """;
        File.WriteAllText(
            Path.Combine(fullDir, pluginName + ".xplugin"),
            json,
            new UTF8Encoding(false));
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
