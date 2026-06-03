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
/// Tests for <see cref="HelpMode"/>. Per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 15.
/// </summary>
[Collection(nameof(HelpModeTests))]
[CollectionDefinition(nameof(HelpModeTests), DisableParallelization = true)]
public sealed class HelpModeTests : IDisposable
{
    public HelpModeTests()
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
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        HelpMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Execute_OutputsHeaderAndUsageBanner()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        HelpMode mode = new();
        await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        string output = sw.ToString();
        Assert.Contains("XIL2CPP (XPact IL-to-C++ transpiler)", output, StringComparison.Ordinal);
        Assert.Contains("Usage: xil2cpp", output, StringComparison.Ordinal);
        Assert.Contains("/Documents/XIL2CPP.html", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ListsEveryRegisteredMode()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        HelpMode mode = new();
        await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        string output = sw.ToString();
        Assert.Contains("help", output, StringComparison.Ordinal);
        Assert.Contains("version", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_CancelledToken_ReturnsCancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        HelpMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), cts.Token);

        Assert.Equal(ExitCodes.Cancelled, exit);
    }
}
