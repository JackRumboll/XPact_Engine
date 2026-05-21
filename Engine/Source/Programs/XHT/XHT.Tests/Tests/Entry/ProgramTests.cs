// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="Program"/>'s mode dispatch + exit-code path per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.3.
/// </summary>
/// <remarks>
/// Tests invoke <see cref="Program.Main"/> directly; the Logger's stderr
/// override captures the diagnostic text so we can assert message
/// content. Logger state is process-global so the collection serialises
/// these tests against the parallel LoggerTests collection.
/// </remarks>
[Collection(nameof(ProgramTests))]
[CollectionDefinition(nameof(ProgramTests), DisableParallelization = true)]
public sealed class ProgramTests : IDisposable
{
    public ProgramTests()
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
    public async Task Main_NoArgs_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(Array.Empty<string>());

        Assert.Equal(ExitCodes.Success, exit);
        // Help should mention the version banner.
        Assert.Contains("XHT (XPact Header Tool)", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_HelpShortcut_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "--help" });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Modes:", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_DashHShortcut_RoutesToHelp_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "-h" });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Modes:", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_VersionShortcut_RoutesToVersion_ReturnsZero()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "--version" });

        Assert.Equal(ExitCodes.Success, exit);
        // The version banner mentions XHT + the semver string.
        Assert.Contains("XHT ", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_UnknownMode_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "no-such-mode" });

        Assert.Equal(ExitCodes.CliArgumentError, exit);
        Assert.Contains("Unknown mode", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_HelpMode_ListsAllRegisteredModes()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "help" });

        Assert.Equal(ExitCodes.Success, exit);
        string output = sw.ToString();
        Assert.Contains("parse-module", output, StringComparison.Ordinal);
        Assert.Contains("emit-module", output, StringComparison.Ordinal);
        Assert.Contains("validate-only", output, StringComparison.Ordinal);
        Assert.Contains("dump-ast", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_InvalidJsonFdFlag_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "-JsonFd=not-a-number", "help" });

        Assert.Equal(ExitCodes.CliArgumentError, exit);
        Assert.Contains("JsonFd", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_ParseModuleMissingManifest_ReturnsCliArgumentError()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        int exit = await Program.Main(new[] { "parse-module", "-Module=X", "-Out=." });

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Main_ParseModuleNonexistentManifest_ReturnsManifestMalformed()
    {
        using StringWriter sw = new();
        Logger.__SetStderrForTesting(sw);

        string fakePath = Path.Combine(Path.GetTempPath(), $"no-such-manifest-{Guid.NewGuid():N}.json");
        int exit = await Program.Main(new[]
        {
            "parse-module",
            $"-Manifest={fakePath}",
            "-Module=X",
            "-Out=.",
        });

        Assert.Equal(ExitCodes.ManifestMalformed, exit);
    }
}
