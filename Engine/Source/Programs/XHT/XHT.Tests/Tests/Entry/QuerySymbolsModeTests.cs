// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry.Modes;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="QuerySymbolsMode"/>. The Phase 2 stub returns
/// exit code 24 per <see cref="QuerySymbolsMode.Phase2StubExitCode"/>.
/// </summary>
[Collection(nameof(QuerySymbolsModeTests))]
[CollectionDefinition(nameof(QuerySymbolsModeTests), DisableParallelization = true)]
public sealed class QuerySymbolsModeTests : IDisposable
{
    public QuerySymbolsModeTests()
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
    public async Task Execute_ReturnsPhase2StubExitCode_24()
    {
        QuerySymbolsMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(24, exit);
        Assert.Equal(QuerySymbolsMode.Phase2StubExitCode, exit);
    }

    [Fact]
    public async Task Execute_EmitsNotYetImplementedError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        QuerySymbolsMode mode = new();
        await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("Phase 2", sw.ToString(), StringComparison.Ordinal);
        Assert.Contains("query-symbols", sw.ToString(), StringComparison.Ordinal);
    }
}
