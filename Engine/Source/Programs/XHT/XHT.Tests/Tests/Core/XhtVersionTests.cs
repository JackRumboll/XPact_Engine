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
        // Per Contract Rev 13.10 (XIL2CPP Phase 6.a cross-module
        // reference-compile action surface addition). The structure
        // hash bbcc0292b75e9a10 rotated from the prior 381d8ef7a7770d9b
        // because the amendment promoted XBT action-graph slot 14 from
        // the reserved placeholder Reserved_Phase2_F to the named,
        // emit-eligible ReferenceCompileCSharpAction and inserted it
        // into ContractSurface.ActionTypes (ascending-ordinal position,
        // after Tier2WholeProgramPass slot 13, before
        // BuildPluginManifestAction slot 16).
        Assert.Equal("13.10+bbcc0292b75e9a10", XhtVersion.ContractVersion);
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
        Assert.Contains("Contract 13.10+bbcc0292b75e9a10", s);
        Assert.Contains("Runtime ", s);
        Assert.EndsWith("\n", s);
    }
}
