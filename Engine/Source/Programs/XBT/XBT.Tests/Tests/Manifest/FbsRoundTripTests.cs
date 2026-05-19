// Copyright Simgenics. All Rights Reserved.

using System;
using FlatSharp;            // extension methods: ISerializer<T>.Parse(byte[]) etc.
using Simgenics.XPact.XBT.Manifest;
using Xunit;

// See ManifestFixtures.cs for the rationale: the inner namespace
// Simgenics.XPact.XBT.Tests.Tests.Manifest shadows the imported `Manifest`
// type name; the alias resolves the ambiguity for the binder.
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// FlatBuffers round-trip and verifier-rejection tests per Toolchain Contract
/// Rev 13 Section 10.2 and <c>/Documents/XBT.html</c> Sections 8.4 / 8.5 /
/// 22.1.
/// </summary>
public sealed class FbsRoundTripTests
{
    [Fact]
    public void Manifest_RoundTrips_ThroughFbs_PreservesAllFields()
    {
        ManifestRecord original = ManifestFixtures.RichExample();

        byte[] bytes = ManifestFbs.SerializeToFbs(original);
        ManifestRecord decoded = ManifestFbs.DeserializeFromFbs(bytes, FbsVerifierLimits.ContractDefaults);

        // FBS round-trip preserves all POCO-mapped fields. The Architecture
        // field is FBS-only (the POCO has no Architecture); it is set on
        // write and ignored on read (FromFbs does not surface it back into
        // the POCO).
        ManifestEquality.AssertEqual(original, decoded);
    }

    [Fact]
    public void FbsBuffer_StartsWithXmftFileIdentifier()
    {
        ManifestRecord m = ManifestFixtures.RichExample();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);

        // file_identifier is at offset 4..8 per the FlatBuffers wire format
        // (the first 4 bytes are the uoffset to the root table).
        Assert.True(bytes.Length >= 8, $"Buffer too short ({bytes.Length} bytes).");

        ReadOnlySpan<byte> idSpan = bytes.AsSpan(4, 4);
        ReadOnlySpan<byte> expected = "XMFT"u8;
        Assert.True(idSpan.SequenceEqual(expected),
            $"file_identifier mismatch: expected \"XMFT\", got \"{System.Text.Encoding.ASCII.GetString(idSpan)}\".");
    }

    [Fact]
    public void Verifier_Rejects_WrongFileIdentifier()
    {
        ManifestRecord m = ManifestFixtures.RichExample();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);

        // Corrupt the file_identifier by zeroing its bytes.
        bytes[4] = 0;
        bytes[5] = 0;
        bytes[6] = 0;
        bytes[7] = 0;

        ManifestException ex = Assert.Throws<ManifestException>(
            () => ManifestFbs.DeserializeFromFbs(bytes, FbsVerifierLimits.ContractDefaults));
        Assert.Contains("file_identifier", ex.Message);
    }

    [Fact]
    public void Verifier_Rejects_TruncatedBuffer()
    {
        ManifestRecord m = ManifestFixtures.RichExample();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);

        // Drop the last 4 bytes -- inside the table somewhere.
        byte[] truncated = new byte[bytes.Length - 4];
        Array.Copy(bytes, truncated, truncated.Length);

        Assert.Throws<ManifestException>(
            () => ManifestFbs.DeserializeFromFbs(truncated, FbsVerifierLimits.ContractDefaults));
    }

    [Fact]
    public void Verifier_Rejects_TooSmallBuffer()
    {
        Assert.Throws<ManifestException>(
            () => ManifestFbs.DeserializeFromFbs(new byte[3], FbsVerifierLimits.ContractDefaults));
    }

    [Fact]
    public void Verifier_Rejects_OversizeBuffer()
    {
        // Use a custom limits record with an artificially tiny budget so we
        // can hit the MaxBytes branch without allocating 100+ MB of data.
        FbsVerifierLimits tinyLimits = new() { MaxBytes = 16 };

        ManifestRecord m = ManifestFixtures.RichExample();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);

        ManifestException ex = Assert.Throws<ManifestException>(
            () => ManifestFbs.DeserializeFromFbs(bytes, tinyLimits));
        Assert.Contains("MaxBytes", ex.Message);
    }

    [Fact]
    public void Verifier_Rejects_OversizeStringField()
    {
        // Same buffer; clamp MaxStringLength to a tiny budget so the
        // rich-example's perfectly-valid 50-ish-char paths trip the limit.
        FbsVerifierLimits tinyStrings = new() { MaxStringLength = 4 };

        ManifestRecord m = ManifestFixtures.RichExample();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);

        Assert.Throws<ManifestException>(
            () => ManifestFbs.DeserializeFromFbs(bytes, tinyStrings));
    }

    [Fact]
    public void Verifier_AcceptsValidBufferWithContractDefaults()
    {
        // Sanity: the rich example deserializes cleanly under contract
        // defaults. If this test fails the test suite has a defect; this
        // is the positive-control row that anchors the negative tests above.
        ManifestRecord m = ManifestFixtures.RichExample();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);

        ManifestRecord decoded = ManifestFbs.DeserializeFromFbs(bytes, FbsVerifierLimits.ContractDefaults);
        Assert.NotNull(decoded);
    }

    [Fact]
    public void DynamicModuleNames_AreMarked_IsDynamic_InBinary()
    {
        // SerializeToFbs accepts an optional set of module names that the
        // writer marks is_dynamic=true. The deserialize path drops the
        // flag back onto the FbsModuleDep -- the POCO ModuleDep does not
        // carry IsDynamic in Phase 1 -- but we can verify the writer wrote
        // the flag by inspecting the FlatBuffers-generated reader directly.
        ManifestRecord m = ManifestFixtures.RichExample();
        var dynamicNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "XSerialization",
        };

        byte[] bytes = ManifestFbs.SerializeToFbs(m, dynamicModuleNames: dynamicNames);

        // Direct read with the FlatSharp-generated Serializer so the IsDynamic
        // bit can be observed before lossy conversion back to POCO.
        var rootSerializer = global::XPact.Build.Manifest.Manifest.Serializer
            .WithSettings(s => s.UseGreedyDeserialization()
                                 .WithObjectDepthLimit((short)64));
        global::XPact.Build.Manifest.Manifest root = rootSerializer.Parse(bytes);

        bool foundDynamicDep = false;
        foreach (var mod in root.Modules!)
        {
            foreach (var dep in mod.ModuleDependencies!)
            {
                if (dep.Name == "XSerialization" && dep.IsDynamic)
                {
                    foundDynamicDep = true;
                }
            }
        }
        Assert.True(foundDynamicDep, "is_dynamic flag must be set for the named module dependency.");
    }
}
