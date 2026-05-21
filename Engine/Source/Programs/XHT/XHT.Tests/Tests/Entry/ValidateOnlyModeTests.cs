// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Entry.Modes;
using Simgenics.XPact.XHT.Manifest;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Entry;

/// <summary>
/// Tests for <see cref="ValidateOnlyMode"/>. Per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.1.
/// </summary>
[Collection(nameof(ValidateOnlyModeTests))]
[CollectionDefinition(nameof(ValidateOnlyModeTests), DisableParallelization = true)]
public sealed class ValidateOnlyModeTests : IDisposable
{
    private readonly string _tempDir;

    public ValidateOnlyModeTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-Validate-" + Guid.NewGuid().ToString("N"));
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
    public async Task Execute_ValidInputs_ReturnsSuccess()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "XScoring");

        ValidateOnlyMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exit);
    }

    [Fact]
    public async Task Execute_ModuleNotInManifest_ThrowsManifestMalformed()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "Existing");

        ValidateOnlyMode mode = new();
        await Assert.ThrowsAsync<ManifestMalformedException>(async () =>
        {
            await mode.ExecuteAsync(new[]
            {
                $"-Manifest={manifestPath}",
                "-Module=NotPresent",
            }, CancellationToken.None);
        });
    }
}
