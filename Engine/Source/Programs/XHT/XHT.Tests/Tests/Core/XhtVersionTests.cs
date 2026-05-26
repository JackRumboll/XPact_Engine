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
        // Per Contract Rev 13.8 (XCore-4b Phase 4b.7 Stage B addendum).
        // The structure hash d9514fb853e7dcc2 rotated from the prior
        // b04ae3cc84cdd9f3 because the addendum added the AbiLayoutTags
        // + AbiTypeSizes tables to ContractSurface.
        Assert.Equal("13.8+d9514fb853e7dcc2", XhtVersion.ContractVersion);
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
        Assert.Contains("Contract 13.8+d9514fb853e7dcc2", s);
        Assert.Contains("Runtime ", s);
        Assert.EndsWith("\n", s);
    }
}
