// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XIL2CPP.Core;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Core;

/// <summary>
/// Tests asserting the exit-code constants match Contract Section 13.1
/// exactly. If a future edit changes any value here the CI / IDE-facing
/// wire format breaks -- these tests are the lock.
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
    public void SimPathBannedApiOrManifestEnvelope_Is_FortyOne_AndDeclaredForCompleteness()
    {
        // Contract code 41 (SimPath banned-API check); XIL2CPP reuses it
        // for the manifest ABI-envelope-tag rejection (XIL2CPP140) per
        // XIL2CPP.html Section 9.7. Phase 6.a does not emit it; declared
        // for Contract Section 13.1 readability.
        Assert.Equal(41, ExitCodes.SimPathBannedApiOrManifestEnvelope);
    }

    [Fact]
    public void ManifestMalformed_Is_Fifty()
    {
        Assert.Equal(50, ExitCodes.ManifestMalformed);
    }

    [Fact]
    public void Xil2CppSubprocessFailure_Is_SixtyOne()
    {
        Assert.Equal(61, ExitCodes.Xil2CppSubprocessFailure);
    }

    [Fact]
    public void Xil2CppInternalFailure_Is_SixtyThree()
    {
        Assert.Equal(63, ExitCodes.Xil2CppInternalFailure);
    }

    [Fact]
    public void Cancelled_Is_OneThirty()
    {
        Assert.Equal(130, ExitCodes.Cancelled);
    }

    /// <summary>
    /// The codes the XIL2CPP binary actually returns: {0, 1, 10, 50, 63,
    /// 130}. Code 61 is the XBT-side aggregation code (XBT translates the
    /// child's 63 to 61); code 41 is declared but not emitted in Phase 6.a.
    /// This test pins the binary's emission surface.
    /// </summary>
    [Fact]
    public void Xil2CppBinaryEmittedSet_MatchesSpec()
    {
        int[] emitted = { ExitCodes.Success, ExitCodes.GenericFailure, ExitCodes.CliArgumentError,
                          ExitCodes.ManifestMalformed, ExitCodes.Xil2CppInternalFailure, ExitCodes.Cancelled };
        int[] expected = { 0, 1, 10, 50, 63, 130 };
        Assert.Equal(expected, emitted);
    }
}
