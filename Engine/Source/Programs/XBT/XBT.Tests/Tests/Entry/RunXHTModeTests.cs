// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Entry;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Entry;

/// <summary>
/// Coverage for <see cref="RunXHTMode"/>'s CLI surface + subprocess
/// invocation discipline per XBT.html Rev 10 Section 1.1 + Section 9.1.
/// </summary>
public sealed class RunXHTModeTests : IDisposable
{
    private readonly string _scratchDir;

    public RunXHTModeTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.RunXHTMode",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Missing <c>-Manifest=</c> raises exit 10 (CLI arg error).
    /// </summary>
    [Fact]
    public async Task Missing_Manifest_Returns_10()
    {
        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { "-Module=X", $"-Out={_scratchDir}" },
            CancellationToken.None);
        Assert.Equal(10, exit);
    }

    /// <summary>
    /// Missing <c>-Module=</c> raises exit 10.
    /// </summary>
    [Fact]
    public async Task Missing_Module_Returns_10()
    {
        string manifest = Path.Combine(_scratchDir, "Manifest.json");
        File.WriteAllText(manifest, "{}");

        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { $"-Manifest={manifest}", $"-Out={_scratchDir}" },
            CancellationToken.None);
        Assert.Equal(10, exit);
    }

    /// <summary>
    /// Missing <c>-Out=</c> raises exit 10.
    /// </summary>
    [Fact]
    public async Task Missing_Out_Returns_10()
    {
        string manifest = Path.Combine(_scratchDir, "Manifest.json");
        File.WriteAllText(manifest, "{}");

        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { $"-Manifest={manifest}", "-Module=X" },
            CancellationToken.None);
        Assert.Equal(10, exit);
    }

    /// <summary>
    /// Unknown flag raises exit 10.
    /// </summary>
    [Fact]
    public async Task Unknown_Flag_Returns_10()
    {
        string manifest = Path.Combine(_scratchDir, "Manifest.json");
        File.WriteAllText(manifest, "{}");

        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] {
                $"-Manifest={manifest}",
                "-Module=X",
                $"-Out={_scratchDir}",
                "-NoSuchFlag=true",
            },
            CancellationToken.None);
        Assert.Equal(10, exit);
    }

    /// <summary>
    /// XHT executable not found (manifest exists, but no XHT binary in
    /// the expected layout) raises exit 24 (closest-fit
    /// <c>ToolNotFound</c> per Contract Section 13).
    /// </summary>
    [Fact]
    public async Task XhtExe_NotFound_Returns_24()
    {
        // Create a stand-alone manifest path with no Binaries directory
        // adjacent. ResolveXhtExecutable will fail to locate xht.
        string manifest = Path.Combine(_scratchDir, "Manifest.json");
        File.WriteAllText(manifest, "{}");

        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] {
                $"-Manifest={manifest}",
                "-Module=XScoring",
                $"-Out={_scratchDir}",
                // Pass an explicit -XhtExe= that does not exist to force
                // the "tool not found" diagnostic regardless of host
                // engine root.
                $"-XhtExe={Path.Combine(_scratchDir, "definitely-not-xht.exe")}",
            },
            CancellationToken.None);
        Assert.Equal(RunXHTMode.ToolNotFoundExitCode, exit);
    }

    /// <summary>
    /// Missing manifest file (file path does not exist on disk) raises
    /// exit 10.
    /// </summary>
    [Fact]
    public async Task Manifest_FileDoesNotExist_Returns_10()
    {
        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] {
                $"-Manifest={Path.Combine(_scratchDir, "does-not-exist.json")}",
                "-Module=XScoring",
                $"-Out={_scratchDir}",
            },
            CancellationToken.None);
        Assert.Equal(10, exit);
    }

    /// <summary>
    /// Mode name is the canonical <c>"run-xht"</c>.
    /// </summary>
    [Fact]
    public void Mode_Name_IsRunXht()
    {
        Assert.Equal("run-xht", RunXHTMode.Name);
        Assert.False(string.IsNullOrEmpty(RunXHTMode.Description));
    }
}
