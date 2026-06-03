// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Simgenics.XPact.XIL2CPP.Tiering;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Tiering;

/// <summary>
/// Tests for the <see cref="TierTable"/> deterministic JSON serializer +
/// parser (scenario 8) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 /
/// 3.3: serialize twice -&gt; identical bytes; parse -&gt; equal table; the
/// emitted JSON carries the documented schema envelope and is sorted by
/// stable id.
/// </summary>
public sealed class TierTableJsonTests
{
    private static TierTable SampleTable()
    {
        // Deliberately UNSORTED input order so the ctor's sort is exercised.
        List<TierClassification> classifications = new()
        {
            new TierClassification(
                new StableId("M.Z.Last()"), "M.Z.Last()", FunctionTier.Tier1, "exported"),
            new TierClassification(
                new StableId("M.A.First()"), "M.A.First()", FunctionTier.Tier2, string.Empty),
            new TierClassification(
                new StableId("M.M.Middle(int)"), "M.M.Middle(int)", FunctionTier.Tier1,
                "throws in body"),
        };
        return new TierTable("MyModule", classifications);
    }

    [Fact]
    public void Ctor_SortsClassifications_ByStableId_Ordinal()
    {
        TierTable table = SampleTable();

        List<string> ids = table.Classifications.Select(c => c.Id.Value).ToList();
        List<string> expected = ids.OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal(expected, ids);
        // Concretely: First, Middle, Last by ordinal of the full display.
        Assert.Equal("M.A.First()", ids[0]);
    }

    [Fact]
    public void Serialize_Twice_ProducesIdenticalBytes()
    {
        TierTable table = SampleTable();

        byte[] first = table.SerializeToUtf8Bytes();
        byte[] second = table.SerializeToUtf8Bytes();

        Assert.Equal(first, second);

        // The string overload agrees with the byte overload (no BOM).
        Assert.Equal(Encoding.UTF8.GetString(first), table.Serialize());
    }

    [Fact]
    public void Serialize_NoUtf8Bom()
    {
        byte[] bytes = SampleTable().SerializeToUtf8Bytes();

        // A UTF-8 BOM would be EF BB BF; the writer must not emit one (the
        // file is read by deterministic byte comparison downstream).
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        // First byte is the opening brace.
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void Serialize_CarriesSchemaEnvelope()
    {
        string json = SampleTable().Serialize();

        Assert.Contains("\"$schema\"", json, StringComparison.Ordinal);
        Assert.Contains(TierTable.SchemaUri, json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"module\": \"MyModule\"", json, StringComparison.Ordinal);
        Assert.Contains("\"functions\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tier\": \"Tier1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tier\": \"Tier2\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RoundTrips_EqualTable()
    {
        TierTable original = SampleTable();

        TierTable parsed = TierTable.Parse(original.Serialize());

        Assert.Equal(original.ModuleName, parsed.ModuleName);
        Assert.Equal(original.Classifications.Count, parsed.Classifications.Count);
        for (int i = 0; i < original.Classifications.Count; i++)
        {
            // TierClassification is a record: structural value equality.
            Assert.Equal(original.Classifications[i], parsed.Classifications[i]);
        }
    }

    [Fact]
    public void Parse_ThenSerialize_IsByteIdentical_ToOriginalSerialize()
    {
        TierTable original = SampleTable();
        string firstJson = original.Serialize();

        TierTable parsed = TierTable.Parse(firstJson);
        string secondJson = parsed.Serialize();

        Assert.Equal(firstJson, secondJson);
    }

    [Fact]
    public void Parse_EmptyFunctions_RoundTrips()
    {
        TierTable empty = new("EmptyModule", Array.Empty<TierClassification>());

        TierTable parsed = TierTable.Parse(empty.Serialize());

        Assert.Equal("EmptyModule", parsed.ModuleName);
        Assert.Empty(parsed.Classifications);
    }

    [Fact]
    public void Parse_Tier2_AbsentReasonKey_ParsesAsEmptyReason()
    {
        // A hand-written table that omits the (empty) reason for a Tier-2
        // entry still parses, with the reason defaulting to empty.
        const string json = """
            {
              "$schema": "https://xpact.dev/schemas/TierTable.partial.v1.json",
              "schemaVersion": 1,
              "module": "Hand",
              "functions": [
                {
                  "stableId": "M.C.F()",
                  "functionDisplay": "M.C.F()",
                  "tier": "Tier2"
                }
              ]
            }
            """;

        TierTable parsed = TierTable.Parse(json);

        TierClassification c = Assert.Single(parsed.Classifications);
        Assert.Equal(FunctionTier.Tier2, c.Tier);
        Assert.Equal(string.Empty, c.DemotionReason);
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => TierTable.Parse("{ not json"));
    }

    [Fact]
    public void Parse_MissingModule_ThrowsFormatException()
    {
        const string json = """
            { "schemaVersion": 1, "functions": [] }
            """;
        Assert.Throws<FormatException>(() => TierTable.Parse(json));
    }

    [Fact]
    public void Parse_UnknownTierToken_ThrowsFormatException()
    {
        const string json = """
            {
              "module": "M",
              "functions": [
                { "stableId": "x", "functionDisplay": "x", "tier": "Tier3", "reason": "" }
              ]
            }
            """;
        Assert.Throws<FormatException>(() => TierTable.Parse(json));
    }

    [Fact]
    public void Pipeline_Output_RoundTrips_ByteStable()
    {
        // End-to-end: a real Pass 1-4 table serializes deterministically and
        // round-trips byte-stable.
        const string source = """
            namespace M;
            public class Surface
            {
                public int Add(int a, int b) => a + b;
                private int Helper() => 1;
            }
            """;

        TierTable table = TieringTestHelpers.Classify(source);

        string firstJson = table.Serialize();
        string secondJson = table.Serialize();
        Assert.Equal(firstJson, secondJson);

        TierTable parsed = TierTable.Parse(firstJson);
        Assert.Equal(firstJson, parsed.Serialize());
        Assert.Equal(table.ModuleName, parsed.ModuleName);
        Assert.Equal(table.Classifications.Count, parsed.Classifications.Count);
    }
}
