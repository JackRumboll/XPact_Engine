// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Configuration;

/// <summary>
/// Verifies <see cref="EngineVersionValidator"/> against the rules in
/// <c>/Documents/XBT.html</c> Rev 4 Section 12 + Toolchain Contract
/// Rev 13 Section 9.4. Maps every test to the exit code matrix of
/// Contract Section 13.
/// </summary>
public sealed class EngineVersionValidatorTests : IDisposable
{
    private readonly string _scratchDir;

    public EngineVersionValidatorTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.EngineVersionValidator",
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
    /// <see cref="SemanticVersion.Parse"/> accepts well-formed semver
    /// triples. Throws on garbage input.
    /// </summary>
    [Fact]
    public void SemanticVersion_ParsesWellFormed_RejectsMalformed()
    {
        Assert.Equal(new SemanticVersion(13, 0, 0), SemanticVersion.Parse("13.0.0"));
        Assert.Equal(new SemanticVersion(0, 1, 5), SemanticVersion.Parse("0.1.5"));
        Assert.Equal(new SemanticVersion(2, 7, 9), SemanticVersion.Parse("2.7.9"));

        Assert.Throws<FormatException>(() => SemanticVersion.Parse(""));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("1.0"));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("1.0.0.0"));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("v1.0.0"));
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("garbage"));
    }

    /// <summary>
    /// Pre-1.0 semver follows the same major/minor compatibility rule
    /// as 1.x.y per Contract Section 9.6 footnote.
    /// </summary>
    [Fact]
    public void SemanticVersion_PreOne_FollowsSameCompatibilityRule()
    {
        SemanticVersion engineA = SemanticVersion.Parse("0.1.0");
        SemanticVersion projectA = SemanticVersion.Parse("0.1.0");
        Assert.True(engineA.IsCompatibleWith(projectA), "0.1.0 must satisfy 0.1.0");

        SemanticVersion engineB = SemanticVersion.Parse("0.1.5");
        SemanticVersion projectB = SemanticVersion.Parse("0.1.0");
        Assert.True(engineB.IsCompatibleWith(projectB), "0.1.5 must satisfy 0.1.0 (engine.minor >= project.minor)");

        SemanticVersion engineC = SemanticVersion.Parse("0.2.0");
        SemanticVersion projectC = SemanticVersion.Parse("0.1.0");
        // Project pinned to 0.1, engine is 0.2 -- engine.minor (2) >= project.minor (1) so compatible.
        Assert.True(engineC.IsCompatibleWith(projectC), "0.2.0 must satisfy 0.1.0 (minor monotone up)");

        SemanticVersion engineD = SemanticVersion.Parse("0.1.0");
        SemanticVersion projectD = SemanticVersion.Parse("0.2.0");
        // Project pinned to 0.2, engine is 0.1 -- engine.minor (1) < project.minor (2). NOT compatible.
        Assert.False(engineD.IsCompatibleWith(projectD), "0.1.0 must NOT satisfy 0.2.0 (engine.minor < project.minor)");

        SemanticVersion engineE = SemanticVersion.Parse("1.0.0");
        SemanticVersion projectE = SemanticVersion.Parse("0.1.0");
        // Different major.
        Assert.False(engineE.IsCompatibleWith(projectE), "1.0.0 must NOT satisfy 0.1.0 (major mismatch)");
    }

    /// <summary>
    /// Project declares EngineVersion=13.0.0, engine is 13.0.5: compatible.
    /// </summary>
    [Fact]
    public void ValidateProject_CompatibleVersion_DoesNotThrow()
    {
        SemanticVersion engine = SemanticVersion.Parse("13.0.5");
        ProjectDescriptor project = new()
        {
            Name = "TestProject",
            EngineVersion = "13.0.0",
        };

        EngineVersionValidator.ValidateProject(project, engine);
    }

    /// <summary>
    /// Project declares EngineVersion=12.5.0, engine is 13.0.0: throws
    /// EngineVersionMismatchException with exit 23 + diagnostic naming
    /// project + actual + expected.
    /// </summary>
    [Fact]
    public void ValidateProject_IncompatibleVersion_ThrowsExit23()
    {
        SemanticVersion engine = SemanticVersion.Parse("13.0.0");
        ProjectDescriptor project = new()
        {
            Name = "Mining-Training",
            EngineVersion = "12.5.0",
        };

        EngineVersionMismatchException ex = Assert.Throws<EngineVersionMismatchException>(
            () => EngineVersionValidator.ValidateProject(project, engine));
        Assert.Equal(23, ex.ExitCode);
        Assert.Contains("Mining-Training", ex.Message);
        Assert.Contains("13.0.0", ex.Message);
        Assert.Contains("12.5.0", ex.Message);
    }

    /// <summary>
    /// Plugin in-range: validator does not throw.
    /// </summary>
    [Fact]
    public void ValidatePlugins_InRange_DoesNotThrow()
    {
        SemanticVersion engine = SemanticVersion.Parse("13.0.0");
        PluginDescriptor plugin = new()
        {
            Name = "IndustrialEquipment",
            Version = "2.3.0",
            MinEngineVersion = "12.0.0",
            MaxEngineVersion = "13.5.0",
        };

        EngineVersionValidator.ValidatePlugins(new[] { plugin }, engine);
    }

    /// <summary>
    /// Plugin out-of-range: throws exit 23, diagnostic names plugin +
    /// its range + engine version.
    /// </summary>
    [Fact]
    public void ValidatePlugins_OutOfRange_ThrowsExit23()
    {
        SemanticVersion engine = SemanticVersion.Parse("0.1.0");
        PluginDescriptor plugin = new()
        {
            Name = "IndustrialEquipment",
            Version = "2.4.0",
            MinEngineVersion = "0.2.0",
            MaxEngineVersion = "0.4.0",
        };

        EngineVersionMismatchException ex = Assert.Throws<EngineVersionMismatchException>(
            () => EngineVersionValidator.ValidatePlugins(new[] { plugin }, engine));
        Assert.Equal(23, ex.ExitCode);
        Assert.Contains("IndustrialEquipment", ex.Message);
        Assert.Contains("0.2.0", ex.Message);
        Assert.Contains("0.4.0", ex.Message);
        Assert.Contains("0.1.0", ex.Message);
    }

    /// <summary>
    /// <see cref="ProjectDescriptor.EngineVersion"/> = null emits a
    /// warning, not a failure (back-compat per Phase 1.3 spec).
    /// </summary>
    [Fact]
    public void ValidateProject_NullEngineVersion_WarnsButDoesNotThrow()
    {
        SemanticVersion engine = SemanticVersion.Parse("0.1.0");
        ProjectDescriptor project = new()
        {
            Name = "EmptyProject",
            EngineVersion = null,
        };

        // No exception is thrown.
        EngineVersionValidator.ValidateProject(project, engine);
    }

    /// <summary>
    /// <see cref="EngineVersionValidator.DiscoverEngineVersion"/> reads
    /// Engine.xengine and extracts the version field.
    /// </summary>
    [Fact]
    public void DiscoverEngineVersion_ReadsEngineXengineFromDisk()
    {
        string engineRoot = Path.Combine(_scratchDir, "Engine");
        Directory.CreateDirectory(engineRoot);
        string descriptorPath = Path.Combine(engineRoot, "Engine.xengine");
        File.WriteAllText(descriptorPath, """
            {
              "EngineVersion": "0.1.0",
              "Copyright": "Copyright Simgenics. All Rights Reserved."
            }
            """);

        SemanticVersion version = EngineVersionValidator.DiscoverEngineVersion(engineRoot);
        Assert.Equal(new SemanticVersion(0, 1, 0), version);
    }

    /// <summary>
    /// <see cref="EngineVersionValidator.DiscoverEngineVersion"/> throws
    /// exit 23 when Engine.xengine is missing.
    /// </summary>
    [Fact]
    public void DiscoverEngineVersion_MissingFile_ThrowsExit23()
    {
        string engineRoot = Path.Combine(_scratchDir, "EmptyEngine");
        Directory.CreateDirectory(engineRoot);
        // Deliberately do not create Engine.xengine.

        EngineVersionMismatchException ex = Assert.Throws<EngineVersionMismatchException>(
            () => EngineVersionValidator.DiscoverEngineVersion(engineRoot));
        Assert.Equal(23, ex.ExitCode);
        Assert.Contains("Engine.xengine", ex.Message);
    }
}
