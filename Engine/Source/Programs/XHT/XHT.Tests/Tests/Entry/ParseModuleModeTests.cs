// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry;
using Simgenics.XPact.XHT.Entry.Modes;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="ParseModuleMode"/>. Per <c>/Documents/XHT.html</c>
/// Rev 5 Section 1.1.
/// </summary>
[Collection(nameof(ParseModuleModeTests))]
[CollectionDefinition(nameof(ParseModuleModeTests), DisableParallelization = true)]
public sealed class ParseModuleModeTests : IDisposable
{
    private readonly string _tempDir;

    public ParseModuleModeTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-ParseMode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
    }

    public void Dispose()
    {
        Logger.DisableJsonChannel();
        Logger.ResetCounters();
        Logger.__SetStderrForTesting(null);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public async Task Execute_ValidManifestAndModule_ReturnsSuccess_AndWritesTokensBin()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "XScoring");
        string outDir = Path.Combine(_tempDir, "Generated");

        ParseModuleMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exit);

        string tokensPath = Path.Combine(outDir, "XScoring.tokens.bin");
        Assert.True(File.Exists(tokensPath), $"Expected tokens.bin at {tokensPath}");

        // The Phase 1b placeholder writes exactly 4 bytes.
        byte[] bytes = File.ReadAllBytes(tokensPath);
        Assert.Equal(4, bytes.Length);
    }

    [Fact]
    public async Task Execute_ModuleNotInManifest_ThrowsManifestMalformed()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "OtherModule");
        string outDir = Path.Combine(_tempDir, "Generated");

        ParseModuleMode mode = new();
        await Assert.ThrowsAsync<ManifestMalformedException>(async () =>
        {
            await mode.ExecuteAsync(new[]
            {
                $"-Manifest={manifestPath}",
                "-Module=NotPresent",
                $"-Out={outDir}",
            }, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Execute_MissingManifestArg_ReturnsCliArgumentError()
    {
        ParseModuleMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            "-Module=XScoring",
            $"-Out={_tempDir}",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Execute_MissingModuleArg_ReturnsCliArgumentError()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir);
        ParseModuleMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            $"-Out={_tempDir}",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Execute_MissingOutArg_ReturnsCliArgumentError()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir);
        ParseModuleMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.CliArgumentError, exit);
    }

    [Fact]
    public async Task Execute_NoMutexWaitFlag_AcceptedInPhase1b()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir);
        string outDir = Path.Combine(_tempDir, "Generated");

        ParseModuleMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
            "-NoMutexWait",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exit);
    }
}
