// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry;
using Simgenics.XPact.XHT.Entry.Modes;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="HelpMode"/>. Per <c>/Documents/XHT.html</c>
/// Rev 8 Section 1.1.
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
        Assert.Contains("XHT (XPact Header Tool)", output, StringComparison.Ordinal);
        Assert.Contains("Usage: xht", output, StringComparison.Ordinal);
        Assert.Contains("/Documents/XHT.html", output, StringComparison.Ordinal);
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
        Assert.Contains("parse-module", output, StringComparison.Ordinal);
        Assert.Contains("emit-module", output, StringComparison.Ordinal);
        Assert.Contains("validate-only", output, StringComparison.Ordinal);
        Assert.Contains("dump-ast", output, StringComparison.Ordinal);
        Assert.Contains("query-symbols", output, StringComparison.Ordinal);
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
