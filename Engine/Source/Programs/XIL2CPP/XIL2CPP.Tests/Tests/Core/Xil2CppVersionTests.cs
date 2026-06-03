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
        // and Simgenics.XPact.XHT.Core.XhtVersion.ContractVersion. The XBT
        // slot-14 amendment (ReferenceCompileCSharpAction) landed at
        // Phase 6.a and rotated the auto-derived structure hash from the
        // Rev 13.9 value; this literal is re-pinned in lockstep.
        Assert.Equal("13.10+bbcc0292b75e9a10", Xil2CppVersion.ContractVersion);
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
        Assert.Contains("Contract 13.10+bbcc0292b75e9a10", s);
        Assert.Contains("Runtime ", s);
        Assert.EndsWith("\n", s);
    }
}
