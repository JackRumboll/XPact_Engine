// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Discovery;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Discovery;

/// <summary>
/// Exercises <see cref="PluginCatalog"/> shadow-precedence resolution
/// per <c>/Documents/XBT.html</c> Rev 4 Section 17.2 + Rev 2 audit
/// finding #8: ALL observed descriptors of each name are recorded so
/// editing a shadowed descriptor invalidates the cache.
/// </summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _scratchDir;

    public PluginCatalogTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.PluginCatalog",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
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

    /// <summary>
    /// Construct three records of the same plugin name (one per tier)
    /// and verify that Project wins resolution while Engine and Studio
    /// land in the Shadowed list.
    /// </summary>
    [Fact]
    public void ThreeTiers_Same_PluginName_Project_Wins_AllRecorded()
    {
        PluginRecord engineRec = MakeRecord("HMIPanels", ModuleTier.Engine, version: "1.0.0", contentSuffix: "engine");
        PluginRecord studioRec = MakeRecord("HMIPanels", ModuleTier.Studio, version: "2.0.0", contentSuffix: "studio");
        PluginRecord projectRec = MakeRecord("HMIPanels", ModuleTier.Project, version: "3.0.0", contentSuffix: "project");

        PluginCatalog catalog = new(new[] { engineRec, studioRec, projectRec });

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("HMIPanels", out PluginEntry entry));
        Assert.Equal(ModuleTier.Project, entry.Resolved.Tier);
        Assert.Equal("3.0.0", entry.Resolved.Descriptor.Version);

        // Both other records present in Shadowed; Studio appears
        // before Engine (ordered closest-to-Project first).
        Assert.Equal(2, entry.Shadowed.Count);
        Assert.Equal(ModuleTier.Studio, entry.Shadowed[0].Tier);
        Assert.Equal(ModuleTier.Engine, entry.Shadowed[1].Tier);
    }

    /// <summary>
    /// Studio shadows Engine when Project is absent.
    /// </summary>
    [Fact]
    public void Studio_Shadows_Engine_When_Project_Absent()
    {
        PluginRecord engineRec = MakeRecord("HMI", ModuleTier.Engine, "1.0.0", "engine");
        PluginRecord studioRec = MakeRecord("HMI", ModuleTier.Studio, "2.0.0", "studio");

        PluginCatalog catalog = new(new[] { engineRec, studioRec });
        Assert.True(catalog.TryGet("HMI", out PluginEntry entry));
        Assert.Equal(ModuleTier.Studio, entry.Resolved.Tier);
        Assert.Single(entry.Shadowed);
        Assert.Equal(ModuleTier.Engine, entry.Shadowed[0].Tier);
    }

    /// <summary>
    /// Audit-finding regression test: editing the SHADOWED descriptor's
    /// content changes the catalog's overall fingerprint. We do not
    /// directly observe a "cache key" here -- the test asserts the
    /// content hash of each shadowed record is recorded in the entry
    /// (so that hash flows into the makefile cache key per XBT.html
    /// Section 15.1 step 2). Editing the shadowed descriptor changes
    /// its hash, which changes the catalog's hash, which invalidates
    /// the makefile.
    /// </summary>
    [Fact]
    public void Catalog_Records_ContentHash_Of_Each_Shadowed_Descriptor()
    {
        PluginRecord engineV1 = MakeRecord("HMI", ModuleTier.Engine, "1.0.0", "engine-v1");
        PluginRecord engineV2 = MakeRecord("HMI", ModuleTier.Engine, "1.0.0", "engine-v2");
        PluginRecord studioRec = MakeRecord("HMI", ModuleTier.Studio, "2.0.0", "studio");

        PluginCatalog catalogA = new(new[] { engineV1, studioRec });
        PluginCatalog catalogB = new(new[] { engineV2, studioRec });

        // Both catalogs resolve to the same Studio winner.
        Assert.Equal(ModuleTier.Studio, catalogA.Entries[0].Resolved.Tier);
        Assert.Equal(ModuleTier.Studio, catalogB.Entries[0].Resolved.Tier);

        // The shadowed Engine record's content hash differs between
        // the two catalogs -- the audit-finding fix's whole point.
        Assert.NotEqual(
            catalogA.Entries[0].Shadowed[0].ContentHash,
            catalogB.Entries[0].Shadowed[0].ContentHash);
    }

    /// <summary>
    /// A plugin that appears at one tier only has an empty Shadowed
    /// list; resolution returns that single record.
    /// </summary>
    [Fact]
    public void SingleTier_Plugin_Has_Empty_ShadowedList()
    {
        PluginRecord rec = MakeRecord("OnlyEngine", ModuleTier.Engine, "1.0.0", "x");
        PluginCatalog catalog = new(new[] { rec });

        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet("OnlyEngine", out PluginEntry entry));
        Assert.Equal(ModuleTier.Engine, entry.Resolved.Tier);
        Assert.Empty(entry.Shadowed);
    }

    /// <summary>
    /// Multiple distinct plugin names produce an alphabetically-sorted
    /// catalog (determinism).
    /// </summary>
    [Fact]
    public void Catalog_Sorts_Entries_Alphabetically()
    {
        PluginCatalog catalog = new(new[]
        {
            MakeRecord("ZebraPlugin", ModuleTier.Engine, "1.0", "z"),
            MakeRecord("AlphaPlugin", ModuleTier.Engine, "1.0", "a"),
            MakeRecord("MikePlugin", ModuleTier.Engine, "1.0", "m"),
        });

        string[] names = catalog.Entries.Select(e => e.Name).ToArray();
        Assert.Equal(new[] { "AlphaPlugin", "MikePlugin", "ZebraPlugin" }, names);
    }

    /// <summary>
    /// Build a synthetic <see cref="PluginRecord"/> by writing a tiny
    /// JSON descriptor to scratch and computing its content hash. The
    /// <paramref name="contentSuffix"/> parameter is concatenated to
    /// the JSON body so otherwise-equivalent records have distinct
    /// content hashes; it also disambiguates the directory so the
    /// process-wide <see cref="FileItem"/> cache cannot conflate
    /// two records that live under the same logical tier+name in
    /// different test scenarios.
    /// </summary>
    private PluginRecord MakeRecord(string name, ModuleTier tier, string version, string contentSuffix)
    {
        // Include the suffix in the directory path so two records of
        // the same name+tier live at distinct absolute paths and get
        // independent FileItem cache entries. The PluginCatalog
        // ordering only inspects (Tier, Path) so the test's intent is
        // preserved.
        string tierDir = Path.Combine(_scratchDir, tier.ToString(), $"{name}-{contentSuffix}");
        Directory.CreateDirectory(tierDir);
        string descriptorPath = Path.Combine(tierDir, name + ".xplugin");

        string json = $$"""
            {
              "Name": "{{name}}",
              "Version": "{{version}}",
              "Description": "fixture-{{contentSuffix}}"
            }
            """;
        File.WriteAllText(descriptorPath, json, new UTF8Encoding(false));

        PluginDescriptor desc = PluginDescriptorParser.ParseFile(descriptorPath);
        IoHash hash = FileItem.GetItemByPath(descriptorPath).ContentHash;
        return new PluginRecord(desc, tier, descriptorPath, hash);
    }
}
