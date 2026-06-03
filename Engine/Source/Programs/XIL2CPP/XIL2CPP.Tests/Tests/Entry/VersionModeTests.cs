// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Entry.Modes;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="VersionMode"/>. Per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 15.
/// </summary>
[Collection(nameof(VersionModeTests))]
[CollectionDefinition(nameof(VersionModeTests), DisableParallelization = true)]
public sealed class VersionModeTests : IDisposable
{
    public VersionModeTests()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    [Fact]
    public async Task Execute_ReturnsSuccess()
    {
        VersionMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Execute_OutputsContainsXil2CppAndSemverAndContract()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        VersionMode mode = new();
        await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        string output = sw.ToString();
        Assert.Contains("XIL2CPP", output, StringComparison.Ordinal);
        Assert.Contains(Xil2CppVersion.Semver, output, StringComparison.Ordinal);
        Assert.Contains(Xil2CppVersion.ContractVersion, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_CancelledToken_ReturnsCancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        VersionMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), cts.Token);

        Assert.Equal(ExitCodes.Cancelled, exit);
    }
}
