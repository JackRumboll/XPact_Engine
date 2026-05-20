// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using Simgenics.XPact.XBT.LiveCoding;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.LiveCoding;

/// <summary>
/// Verifies <see cref="PatchDllDiscovery"/> scans the LiveCoding
/// directory and computes the next per-(Target, Module)
/// <c>&lt;gen&gt;</c> counter correctly per
/// <c>/Documents/XBT.html</c> Rev 4 Section 16.7.
/// </summary>
public sealed class PatchDllDiscoveryTests : IDisposable
{
    private readonly string _scratchDir;

    public PatchDllDiscoveryTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.PatchDllDiscovery",
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
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// <see cref="PatchDllDiscovery.GetNextGeneration"/> returns 0 for
    /// an empty directory (fresh CI agent baseline per Section 16.7).
    /// </summary>
    [Fact]
    public void GetNextGeneration_EmptyDirectory_Returns0()
    {
        int gen = PatchDllDiscovery.GetNextGeneration(_scratchDir, "XScoring");
        Assert.Equal(0, gen);
    }

    /// <summary>
    /// A missing directory yields 0 (Phase 1 XBT doesn't create the
    /// LiveCoding directory; Phase 2 XLiveCoding creates it lazily on
    /// first patch write).
    /// </summary>
    [Fact]
    public void GetNextGeneration_MissingDirectory_Returns0()
    {
        string missingPath = Path.Combine(_scratchDir, "NeverCreated");
        int gen = PatchDllDiscovery.GetNextGeneration(missingPath, "XScoring");
        Assert.Equal(0, gen);
    }

    /// <summary>
    /// With existing patch DLLs at generations 0, 3, and 7, the next
    /// generation returned is <c>max(observed) + 1 = 8</c>. This
    /// covers the canonical "second invocation in the same physical
    /// directory" scenario from Section 16.7.
    /// </summary>
    [Fact]
    public void GetNextGeneration_WithGenerations_0_3_7_Returns8()
    {
        // Create the three patch DLLs.
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.0.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.3.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.7.dll"), "");

        int gen = PatchDllDiscovery.GetNextGeneration(_scratchDir, "XScoring");
        Assert.Equal(8, gen);
    }

    /// <summary>
    /// Files that do not match the pattern
    /// <c>&lt;moduleName&gt;.patch.&lt;gen&gt;.dll</c> are ignored:
    /// wrong module name, non-integer counter, wrong extension, missing
    /// <c>.patch.</c> infix.
    /// </summary>
    [Fact]
    public void GetNextGeneration_IgnoresNonMatchingFiles()
    {
        // Real patch DLLs for XScoring.
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.0.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.1.dll"), "");

        // Non-matching files we expect to be ignored.
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.abc.dll"), "");        // non-integer counter
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.99.so"), "");          // wrong extension
        File.WriteAllText(Path.Combine(_scratchDir, "XOtherModule.patch.50.dll"), "");     // wrong module
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.dll"), "");                  // missing .patch.
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.dll"), "");            // missing counter
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.-1.dll"), "");         // negative counter
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.0.dll.bak"), "");      // extra suffix

        int gen = PatchDllDiscovery.GetNextGeneration(_scratchDir, "XScoring");
        // max(0, 1) + 1 = 2 -- the unmatched files contribute nothing.
        Assert.Equal(2, gen);
    }

    /// <summary>
    /// Two modules in the same target use independent counters
    /// (per-(Target, Module) scope per Section 16.7).
    /// </summary>
    [Fact]
    public void GetNextGeneration_PerModuleCounters_AreIndependent()
    {
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.0.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.5.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XInventory.patch.0.dll"), "");

        Assert.Equal(6, PatchDllDiscovery.GetNextGeneration(_scratchDir, "XScoring"));
        Assert.Equal(1, PatchDllDiscovery.GetNextGeneration(_scratchDir, "XInventory"));
        Assert.Equal(0, PatchDllDiscovery.GetNextGeneration(_scratchDir, "XNotPresent"));
    }

    /// <summary>
    /// <see cref="PatchDllDiscovery.EnumeratePatchDlls"/> yields only
    /// matching files, deterministically ordered.
    /// </summary>
    [Fact]
    public void EnumeratePatchDlls_YieldsOnlyMatchingFiles_InOrdinalOrder()
    {
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.10.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.2.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.patch.0.dll"), "");
        // Non-matching.
        File.WriteAllText(Path.Combine(_scratchDir, "Other.patch.0.dll"), "");
        File.WriteAllText(Path.Combine(_scratchDir, "XScoring.dll"), "");

        string[] enumerated = PatchDllDiscovery.EnumeratePatchDlls(_scratchDir, "XScoring").ToArray();
        Assert.Equal(3, enumerated.Length);
        // Ordinal sort places "10" before "2" (string sort, not
        // numeric). This is the documented stable order.
        Assert.EndsWith("XScoring.patch.0.dll", enumerated[0]);
        Assert.EndsWith("XScoring.patch.10.dll", enumerated[1]);
        Assert.EndsWith("XScoring.patch.2.dll", enumerated[2]);
    }

    /// <summary>
    /// <see cref="PatchDllDiscovery.GetPatchDirectory"/> composes
    /// the canonical
    /// <c>Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/LiveCoding</c>
    /// path per Section 16.7.
    /// </summary>
    [Fact]
    public void GetPatchDirectory_ComposesCanonicalPath()
    {
        string path = PatchDllDiscovery.GetPatchDirectory(
            engineRoot: @"C:\repo\Engine",
            targetName: "EditorTarget",
            config: BuildConfiguration.Development);

        // The path is composed with Path.Combine; on Windows that's
        // backslashes. Just check the trailing segments are present.
        Assert.Contains("EditorTarget", path);
        Assert.Contains("Development", path);
        Assert.EndsWith("LiveCoding", path);
    }
}
