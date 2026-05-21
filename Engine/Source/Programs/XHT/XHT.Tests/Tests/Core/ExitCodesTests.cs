// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests asserting the exit-code constants match Contract Section 13.1
/// + <c>/Documents/XHT.html</c> Rev 8 Section 1.3 exactly. If a future
/// edit changes any value here the CI/IDE-facing wire format breaks --
/// these tests are the lock.
/// </summary>
public class ExitCodesTests
{
    [Fact]
    public void Success_Is_Zero()
    {
        Assert.Equal(0, ExitCodes.Success);
    }

    [Fact]
    public void GenericFailure_Is_One()
    {
        Assert.Equal(1, ExitCodes.GenericFailure);
    }

    [Fact]
    public void CliArgumentError_Is_Ten()
    {
        Assert.Equal(10, ExitCodes.CliArgumentError);
    }

    [Fact]
    public void DescriptorParseFailure_Is_Thirty_AndDeclaredForCompleteness()
    {
        // Owned by XBT per Contract Section 13.1; XHT does not emit it
        // (Rev 3 X-CR1 remap). The constant is declared here so the
        // Contract table is readable in one place.
        Assert.Equal(30, ExitCodes.DescriptorParseFailure);
    }

    [Fact]
    public void ManifestMalformed_Is_Fifty()
    {
        Assert.Equal(50, ExitCodes.ManifestMalformed);
    }

    [Fact]
    public void XhtInternalFailure_Is_SixtyTwo()
    {
        Assert.Equal(62, ExitCodes.XhtInternalFailure);
    }

    [Fact]
    public void Cancelled_Is_OneThirty()
    {
        Assert.Equal(130, ExitCodes.Cancelled);
    }

    /// <summary>
    /// The codes XHT actually emits per XHT.html Section 1.3:
    /// {0, 1, 10, 50, 62, 130}. Code 30 is declared but never returned
    /// (XBT-owned). This test pins the emission surface.
    /// </summary>
    [Fact]
    public void XhtEmittedSet_MatchesSpec()
    {
        int[] emitted = { ExitCodes.Success, ExitCodes.GenericFailure, ExitCodes.CliArgumentError,
                          ExitCodes.ManifestMalformed, ExitCodes.XhtInternalFailure, ExitCodes.Cancelled };
        int[] expected = { 0, 1, 10, 50, 62, 130 };
        Assert.Equal(expected, emitted);
    }
}
