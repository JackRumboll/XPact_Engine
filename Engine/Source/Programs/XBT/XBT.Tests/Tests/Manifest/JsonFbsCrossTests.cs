// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.Manifest;
using Xunit;

// See ManifestFixtures.cs for the rationale: the inner namespace
// Simgenics.XPact.XBT.Tests.Tests.Manifest shadows the imported `Manifest`
// type name; the alias resolves the ambiguity for the binder.
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// Cross-format consistency: a manifest serialised to JSON, deserialised, then
/// serialised to FBS, then deserialised must equal the original POCO. This
/// guards the lockstep guarantee of Toolchain Contract Rev 13 Section 10.2:
/// JSON and FBS are emitted from the same in-memory POCO so any field that
/// survives one path must survive the other.
/// </summary>
public sealed class JsonFbsCrossTests
{
    [Fact]
    public void Poco_JsonRound_FbsRound_PreservesEquality()
    {
        ManifestRecord original = ManifestFixtures.RichExample();

        // JSON round trip.
        string   json     = ManifestJson.SerializeToJson(original);
        ManifestRecord viaJson  = ManifestJson.DeserializeFromJson(json);

        // FBS round trip of the post-JSON POCO. If JSON dropped or altered
        // anything, the final FBS-decoded POCO will diverge from the original.
        byte[]   fbsBytes = ManifestFbs.SerializeToFbs(viaJson);
        ManifestRecord viaFbs   = ManifestFbs.DeserializeFromFbs(fbsBytes, FbsVerifierLimits.ContractDefaults);

        ManifestEquality.AssertEqual(original, viaFbs);
    }

    [Fact]
    public void Poco_FbsRound_JsonRound_PreservesEquality()
    {
        ManifestRecord original = ManifestFixtures.RichExample();

        byte[]   fbsBytes = ManifestFbs.SerializeToFbs(original);
        ManifestRecord viaFbs   = ManifestFbs.DeserializeFromFbs(fbsBytes, FbsVerifierLimits.ContractDefaults);

        string   json     = ManifestJson.SerializeToJson(viaFbs);
        ManifestRecord viaJson  = ManifestJson.DeserializeFromJson(json);

        ManifestEquality.AssertEqual(original, viaJson);
    }
}
