// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Simgenics.XPact.XBT.Manifest;
using Xunit;
using ManifestRecord = Simgenics.XPact.XBT.Manifest.Manifest;

namespace Simgenics.XPact.XBT.Tests.Tests.Manifest;

/// <summary>
/// Targeted regression tests for the adversarial audit's critical
/// findings on the manifest surface (C1, C10).
/// </summary>
public sealed class AuditFixTests
{
    /// <summary>
    /// Audit fix C1: Architecture field round-trips through both
    /// JSON and FBS forms. The POCO's Architecture wins when set;
    /// the FBS overload parameter is only the fallback for callers
    /// that construct a manifest with an empty Architecture.
    /// </summary>
    [Fact]
    public void Architecture_RoundTripsThroughJson()
    {
        ManifestRecord m = NewMinimalManifest(architecture: "aarch64");
        string json = ManifestJson.SerializeToJson(m);
        ManifestRecord decoded = ManifestJson.DeserializeFromJson(json);
        Assert.Equal("aarch64", decoded.Target.Architecture);
    }

    [Fact]
    public void Architecture_RoundTripsThroughFbs()
    {
        ManifestRecord m = NewMinimalManifest(architecture: "aarch64");
        byte[] bytes = ManifestFbs.SerializeToFbs(m);
        ManifestRecord decoded = ManifestFbs.DeserializeFromFbs(
            bytes, FbsVerifierLimits.ContractDefaults);
        Assert.Equal("aarch64", decoded.Target.Architecture);
    }

    /// <summary>
    /// Audit fix C1: when the FBS Serialize overload receives an empty
    /// POCO Architecture, the legacy <c>architecture</c> parameter is
    /// the fallback. When the POCO is set, the parameter is ignored
    /// (the POCO wins).
    /// </summary>
    [Fact]
    public void Architecture_PocoOverridesFbsParameter()
    {
        ManifestRecord m = NewMinimalManifest(architecture: "aarch64");
        byte[] bytes = ManifestFbs.SerializeToFbs(m, architecture: "i_should_be_ignored");
        ManifestRecord decoded = ManifestFbs.DeserializeFromFbs(
            bytes, FbsVerifierLimits.ContractDefaults);
        Assert.Equal("aarch64", decoded.Target.Architecture);
    }

    /// <summary>
    /// Audit fix R4-M7: the symmetric case of
    /// <see cref="Architecture_PocoOverridesFbsParameter"/> -- when the
    /// POCO carries an empty Architecture, the FBS overload's
    /// <c>architecture</c> parameter is the fallback. The decoded
    /// payload's architecture must equal the parameter value.
    /// </summary>
    [Fact]
    public void Architecture_PocoEmpty_UsesFbsParameter()
    {
        ManifestRecord m = NewMinimalManifest(architecture: string.Empty);
        byte[] bytes = ManifestFbs.SerializeToFbs(m, architecture: "aarch64");
        ManifestRecord decoded = ManifestFbs.DeserializeFromFbs(
            bytes, FbsVerifierLimits.ContractDefaults);
        Assert.Equal("aarch64", decoded.Target.Architecture);
    }

    /// <summary>
    /// Audit fix C10: ABI envelope fields (GCRootABI, ExceptionABI,
    /// ManglingScheme) round-trip through JSON.
    /// </summary>
    [Fact]
    public void AbiEnvelope_RoundTripsThroughJson()
    {
        ManifestRecord m = NewMinimalManifest();
        string json = ManifestJson.SerializeToJson(m);
        ManifestRecord decoded = ManifestJson.DeserializeFromJson(json);
        Assert.Equal(ManifestFbs.DefaultGCRootABI, decoded.Target.GCRootABI);
        Assert.Equal(ManifestFbs.DefaultExceptionABI, decoded.Target.ExceptionABI);
        Assert.Equal(ManifestFbs.DefaultManglingScheme, decoded.Target.ManglingScheme);
    }

    /// <summary>
    /// Audit fix C10: ABI envelope fields round-trip through FBS.
    /// </summary>
    [Fact]
    public void AbiEnvelope_RoundTripsThroughFbs()
    {
        ManifestRecord m = NewMinimalManifest();
        byte[] bytes = ManifestFbs.SerializeToFbs(m);
        ManifestRecord decoded = ManifestFbs.DeserializeFromFbs(
            bytes, FbsVerifierLimits.ContractDefaults);
        Assert.Equal(ManifestFbs.DefaultGCRootABI, decoded.Target.GCRootABI);
        Assert.Equal(ManifestFbs.DefaultExceptionABI, decoded.Target.ExceptionABI);
        Assert.Equal(ManifestFbs.DefaultManglingScheme, decoded.Target.ManglingScheme);
    }

    /// <summary>
    /// Audit fix C10 Phase 1 locked values: confirm the constants match
    /// the locked policy. Changing these constants is a contract bump
    /// (changes ContractVersion via the surface hash).
    /// </summary>
    [Fact]
    public void AbiEnvelope_Phase1Defaults()
    {
        Assert.Equal("Span-based v1", ManifestFbs.DefaultGCRootABI);
        Assert.Equal("Tier1-Shim/Tier2-Direct", ManifestFbs.DefaultExceptionABI);
        Assert.Equal("Itanium-LengthPrefixed-v1", ManifestFbs.DefaultManglingScheme);
    }

    private static ManifestRecord NewMinimalManifest(string architecture = "x86_64")
    {
        TargetInfo target = new(
            Name: "Minimal",
            Type: BuildTargetType.Editor,
            Platform: Platform.Win64,
            Configuration: BuildConfiguration.Development,
            Architecture: architecture,
            GCRootABI: ManifestFbs.DefaultGCRootABI,
            ExceptionABI: ManifestFbs.DefaultExceptionABI,
            ManglingScheme: ManifestFbs.DefaultManglingScheme,
            FipsMode: false,
            SimPathConservativeRootsAllowed: false,
            SimdLevelDefault: SimdLevel.SSE42,
            StationRole: StationRole.None);

        return new ManifestRecord(
            ContractVersion: ContractVersion.Current,
            EngineVersion: "0.1.0",
            Target: target,
            RootLocalPath: "C:/repo",
            ExternalDependenciesFile: null,
            Modules: new List<Module>());
    }
}
