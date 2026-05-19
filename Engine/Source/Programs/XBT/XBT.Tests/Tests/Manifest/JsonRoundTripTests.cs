// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.Text;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

// See ManifestFixtures.cs for the rationale: the inner namespace
// Simgenics.XPact.XBT.Tests.Tests.Manifest shadows the imported `Manifest`
// type name; the alias resolves the ambiguity for the binder.
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// JSON round-trip and determinism tests for the manifest DTOs per
/// Toolchain Contract Rev 13 Section 10.2 and <c>/Documents/XBT.html</c>
/// Section 8.4 (parse-safety budget) / Section 22.1 (the unit-test row
/// "Manifest serialisation round-trip").
/// </summary>
public sealed class JsonRoundTripTests
{
    [Fact]
    public void Manifest_RoundTrips_ThroughJson_PreservesAllFields()
    {
        ManifestRecord original = ManifestFixtures.RichExample();

        string json = ManifestJson.SerializeToJson(original);
        ManifestRecord decoded = ManifestJson.DeserializeFromJson(json);

        ManifestEquality.AssertEqual(original, decoded);
    }

    /// <summary>
    /// Determinism: serializing the same POCO twice produces byte-identical
    /// JSON. The Contract Rev 13 Section 2.1 reproducibility envelope
    /// requires byte-identical artefacts across builds; the manifest is
    /// one of those artefacts.
    /// </summary>
    [Fact]
    public void Manifest_Json_Is_Deterministic_Across_Serialisations()
    {
        ManifestRecord m = ManifestFixtures.RichExample();

        string first  = ManifestJson.SerializeToJson(m);
        string second = ManifestJson.SerializeToJson(m);

        Assert.Equal(first, second);
        Assert.Equal(
            Encoding.UTF8.GetBytes(first),
            Encoding.UTF8.GetBytes(second));
    }

    /// <summary>
    /// XBT.html Section 8.4 mandates <c>MaxDepth = 64</c> on every manifest
    /// JSON read. A 70-level-deep synthetic object must be rejected before
    /// the binder runs.
    /// </summary>
    [Fact]
    public void Manifest_Json_Rejects_DepthAboveSixtyFour()
    {
        // Build a 70-deep nested JSON object.
        StringBuilder open  = new();
        StringBuilder close = new();
        for (int i = 0; i < 70; i++)
        {
            open.Append("{\"x\":");
            close.Append('}');
        }
        // Inner sentinel "null" so the JSON is well-formed.
        string deepJson = open.ToString() + "null" + close.ToString();

        Assert.ThrowsAny<System.Exception>(() => ManifestJson.DeserializeFromJson(deepJson));
    }

    [Fact]
    public void Manifest_Json_Rejects_EmptyPayload()
    {
        Assert.Throws<ManifestMalformedException>(
            () => ManifestJson.DeserializeFromJson(System.ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Manifest_Json_AllModuleTiers_AreRepresentable()
    {
        // Construct one module per tier (Engine, Studio, Project) and one
        // module of ModuleType.Programs (the Rev 13 append) and verify they
        // all survive a round-trip.
        ManifestRecord m = ManifestFixtures.OnePerTier();

        string json = ManifestJson.SerializeToJson(m);
        ManifestRecord decoded = ManifestJson.DeserializeFromJson(json);

        ManifestEquality.AssertEqual(m, decoded);

        // Sanity: confirm Programs round-tripped specifically.
        bool foundPrograms = false;
        foreach (Module mod in decoded.Modules)
        {
            if (mod.ModuleType == ModuleType.Programs)
            {
                foundPrograms = true;
                break;
            }
        }
        Assert.True(foundPrograms, "ModuleType.Programs must round-trip through JSON.");
    }
}
