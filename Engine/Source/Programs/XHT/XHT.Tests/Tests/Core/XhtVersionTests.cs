// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="XhtVersion"/>. The Contract pin is the key
/// invariant: a mismatch would mean XHT reads manifests it cannot
/// safely consume.
/// </summary>
public class XhtVersionTests
{
    [Fact]
    public void ContractVersion_MatchesXhtHtmlSection0Pin()
    {
        // Per Contract Rev 13.9 (XCoreXObject Phase 5.a' Contract
        // micro-bump prerequisite). The structure hash
        // 381d8ef7a7770d9b rotated from the prior d9514fb853e7dcc2
        // because the micro-bump updated 3 existing reflection-type
        // tag contents (FStruct v4 -> v5, FScriptStruct v4 -> v5,
        // FClass v4 -> v6) + 3 existing AbiTypeSizes rows (FStruct
        // 112 -> 120, FClass 224 -> 240, FScriptStruct 128 -> 136)
        // and added 8 new XObject-side layout tags + 7 new
        // AbiTypeSizes rows to ContractSurface.
        Assert.Equal("13.9+381d8ef7a7770d9b", XhtVersion.ContractVersion);
    }

    [Fact]
    public void Semver_HasPhase1bSuffix()
    {
        Assert.Equal("0.1.0+phase1b", XhtVersion.Semver);
    }

    [Fact]
    public void DotNetVersion_IsNonEmpty()
    {
        // RuntimeInformation.FrameworkDescription is always populated;
        // the value depends on the host but always has content.
        Assert.False(string.IsNullOrWhiteSpace(XhtVersion.DotNetVersion));
    }

    [Fact]
    public void GetVersionString_IncludesAllThreeLines()
    {
        string s = XhtVersion.GetVersionString();
        Assert.Contains("XHT 0.1.0+phase1b", s);
        Assert.Contains("Contract 13.9+381d8ef7a7770d9b", s);
        Assert.Contains("Runtime ", s);
        Assert.EndsWith("\n", s);
    }
}
