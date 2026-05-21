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
/// Smoke tests for <see cref="DumpAstMode"/>. Phase 1b stub: returns
/// success after emitting an informational log line. Real AST JSON
/// emission ships in Phase 1d+.
/// </summary>
[Collection(nameof(DumpAstModeTests))]
[CollectionDefinition(nameof(DumpAstModeTests), DisableParallelization = true)]
public sealed class DumpAstModeTests : IDisposable
{
    public DumpAstModeTests()
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
        DumpAstMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Execute_EmitsPhase1bNotImplementedMessage()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        DumpAstMode mode = new();
        await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("Phase 1b stub", sw.ToString(), StringComparison.Ordinal);
    }
}
