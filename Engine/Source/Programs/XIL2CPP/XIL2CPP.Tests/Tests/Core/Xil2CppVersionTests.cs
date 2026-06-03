// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Core;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="Xil2CppVersion"/>. The Contract pin is the key
/// invariant: a mismatch would mean XIL2CPP reads manifests it cannot
/// safely consume. The pin MUST equal the XBT + XHT ContractVersion pins.
/// </summary>
public class Xil2CppVersionTests
{
    [Fact]
    public void ContractVersion_MatchesLiveContractSurfacePin()
    {
        // Must equal Simgenics.XPact.XBT.Manifest.ContractVersion.Current
        // and Simgenics.XPact.XHT.Core.XhtVersion.ContractVersion. When the
        // XBT slot-14 amendment (ReferenceCompileCSharpAction) lands it
        // rotates the auto-derived structure hash; this literal is re-pinned
        // in lockstep at that point.
        Assert.Equal("13.9+381d8ef7a7770d9b", Xil2CppVersion.ContractVersion);
    }

    [Fact]
    public void Semver_HasPhase6aSuffix()
    {
        Assert.Equal("0.1.0+phase6a", Xil2CppVersion.Semver);
    }

    [Fact]
    public void DotNetVersion_IsNonEmpty()
    {
        // RuntimeInformation.FrameworkDescription is always populated; the
        // value depends on the host but always has content.
        Assert.False(string.IsNullOrWhiteSpace(Xil2CppVersion.DotNetVersion));
    }

    [Fact]
    public void GetVersionString_IncludesAllThreeLines()
    {
        string s = Xil2CppVersion.GetVersionString();
        Assert.Contains("XIL2CPP 0.1.0+phase6a", s);
        Assert.Contains("Contract 13.9+381d8ef7a7770d9b", s);
        Assert.Contains("Runtime ", s);
        Assert.EndsWith("\n", s);
    }
}
