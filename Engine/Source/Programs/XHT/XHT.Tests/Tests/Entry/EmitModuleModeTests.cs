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
/// Tests for <see cref="EmitModuleMode"/>. Per <c>/Documents/XHT.html</c>
/// Rev 5 Section 1.1; verifies the load-bearing <c>.gen.manifest</c>
/// emit path works end-to-end.
/// </summary>
[Collection(nameof(EmitModuleModeTests))]
[CollectionDefinition(nameof(EmitModuleModeTests), DisableParallelization = true)]
public sealed class EmitModuleModeTests : IDisposable
{
    private readonly string _tempDir;

    public EmitModuleModeTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "XHT.Tests-EmitMode-" + Guid.NewGuid().ToString("N"));
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
    public async Task Execute_ValidManifestAndModule_ReturnsSuccess_AndWritesGenManifest()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "XScoring");
        string outDir = Path.Combine(_tempDir, "Generated");

        EmitModuleMode mode = new();
        int exit = await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
        }, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, exit);

        string genManifestPath = Path.Combine(outDir, "XScoring.gen.manifest");
        Assert.True(File.Exists(genManifestPath), $"Expected gen.manifest at {genManifestPath}");
    }

    [Fact]
    public async Task Execute_ValidManifestAndModule_WrittenManifestHasModuleNamePopulated()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "XScoring");
        string outDir = Path.Combine(_tempDir, "Generated");

        EmitModuleMode mode = new();
        await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
        }, CancellationToken.None);

        string genManifestPath = Path.Combine(outDir, "XScoring.gen.manifest");
        GenManifest m = GenManifestReader.Read(genManifestPath);

        Assert.Equal("XScoring", m.ModuleName);
        Assert.Equal(XhtVersion.ContractVersion, m.ContractVersion);
        Assert.Equal(GenManifestWriter.CurrentSchemaVersion, m.XhtSchemaVersion);
    }

    [Fact]
    public async Task Execute_ValidManifestAndModule_PopulatesInputsFromSourceFiles()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "XScoring");
        string outDir = Path.Combine(_tempDir, "Generated");

        EmitModuleMode mode = new();
        await mode.ExecuteAsync(new[]
        {
            $"-Manifest={manifestPath}",
            "-Module=XScoring",
            $"-Out={outDir}",
        }, CancellationToken.None);

        string genManifestPath = Path.Combine(outDir, "XScoring.gen.manifest");
        GenManifest m = GenManifestReader.Read(genManifestPath);

        // The test manifest carries one .h + one .cs source file; the
        // emit-module stub populates both into [Inputs] with the
        // placeholder hash.
        Assert.Equal(2, m.Inputs.Length);
    }

    [Fact]
    public async Task Execute_ModuleNotInManifest_ThrowsManifestMalformed()
    {
        string manifestPath = TestManifestBuilder.WriteOneModuleManifest(_tempDir, "Existing");
        string outDir = Path.Combine(_tempDir, "Generated");

        EmitModuleMode mode = new();
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
}
