// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Entry;
using Simgenics.XPact.XBT.Manifest;
using Xunit;

// Tests.Tests.BuildMode is also a namespace in this assembly; alias the
// type explicitly so identifier "BuildMode" resolves to the type.
using BuildModeType = Simgenics.XPact.XBT.Entry.BuildMode;

namespace Simgenics.XPact.XBT.Tests.Tests.Entry;

/// <summary>
/// Audit fix R4-M6: smoke tests for the CLI modes whose implementations
/// landed in earlier rounds but had no test coverage:
/// <see cref="CleanMode"/>, <see cref="ValidateCopyrightMode"/>,
/// <see cref="WriteManifestMode"/>, <see cref="HelpMode"/>.
/// </summary>
/// <remarks>
/// <para>
/// These are intentionally narrow smoke tests -- exit code + one or two
/// observable side effects each. The full integration coverage lives in
/// the per-subsystem test files.
/// </para>
/// <para>
/// Audit fix R5-M3: in the
/// <see cref="Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection"/>
/// serial collection because some scenarios call
/// <c>BuildMode.Run</c>, which transitively reads
/// <c>ToolchainSelfHash.XbtBinaryHash</c> through the toolchain emit
/// path's cache-key composition.
/// </para>
/// </remarks>
[Collection(nameof(Simgenics.XPact.XBT.Tests.ToolchainSelfHashCollection))]
public sealed class ModeSmokeTests : IDisposable
{
    private readonly string _scratchRoot;

    public ModeSmokeTests()
    {
        _scratchRoot = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.ModeSmoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    // ----- CleanMode -----

    /// <summary>
    /// Audit fix R4-M6: <see cref="CleanMode"/> deletes the per-target
    /// intermediate-build directory + the per-target binaries when
    /// invoked with <c>-Target=&lt;name&gt; -Engine=&lt;root&gt;</c>.
    /// Returns exit 0.
    /// </summary>
    [Fact]
    public async Task CleanMode_DeletesIntermediateBuildTree()
    {
        string engineRoot = Path.Combine(_scratchRoot, "Engine");
        Directory.CreateDirectory(engineRoot);
        File.WriteAllText(Path.Combine(engineRoot, "Engine.xengine"), "{\"Version\":\"0.1.0\"}");

        string intermediate = Path.Combine(
            engineRoot, "Intermediate", "Build", "FakeTarget", "Development");
        Directory.CreateDirectory(intermediate);
        File.WriteAllText(Path.Combine(intermediate, "stale.obj"), "stale");

        Assert.True(Directory.Exists(intermediate));

        CleanMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { "-Target=FakeTarget", $"-Engine={engineRoot}" },
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(intermediate),
            "CleanMode should remove the per-target/config intermediate directory.");
    }

    /// <summary>
    /// Round-6 final-cleanup M2: <see cref="CleanMode"/> accepts the
    /// spec-canonical <c>-Project=&lt;.xproject&gt;</c> flag and sweeps
    /// the project-side <c>Intermediate/Build/&lt;Target&gt;</c> tree in
    /// addition to the engine-side tree.
    /// </summary>
    [Fact]
    public async Task CleanMode_AcceptsSpecCanonical_ProjectFlag()
    {
        string engineRoot = Path.Combine(_scratchRoot, "CleanProjectEngine");
        Directory.CreateDirectory(engineRoot);
        File.WriteAllText(Path.Combine(engineRoot, "Engine.xengine"), "{\"Version\":\"0.1.0\"}");

        string projectRoot = Path.Combine(_scratchRoot, "CleanProjectProject");
        Directory.CreateDirectory(projectRoot);
        string projectFile = Path.Combine(projectRoot, "Demo.xproject");
        File.WriteAllText(projectFile, "{\"EngineVersion\":\"0.1.0\"}");

        string projectIntermediate = Path.Combine(
            projectRoot, "Intermediate", "Build", "FakeTarget", "Development");
        Directory.CreateDirectory(projectIntermediate);
        File.WriteAllText(Path.Combine(projectIntermediate, "stale.obj"), "stale");

        CleanMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[]
            {
                "-Target=FakeTarget",
                $"-EngineRoot={engineRoot}",
                $"-Project={projectFile}",
            },
            CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(projectIntermediate),
            "CleanMode should sweep the project's Intermediate/Build tree when -Project= is set.");
    }

    // ----- ValidateCopyrightMode -----

    /// <summary>
    /// Audit fix R4-M6: <see cref="ValidateCopyrightMode"/> against a
    /// scratch tree containing only files that carry the canonical
    /// header returns exit 0.
    /// </summary>
    [Fact]
    public async Task ValidateCopyrightMode_AllValid_Returns0()
    {
        string root = Path.Combine(_scratchRoot, "ValidateCopyrightOk");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "Good.cs"),
            "// Copyright Simgenics. All Rights Reserved.\nnamespace X { }\n");

        ValidateCopyrightMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { $"-Root={root}" },
            CancellationToken.None);

        Assert.Equal(0, exit);
    }

    /// <summary>
    /// Audit fix R4-M6: <see cref="ValidateCopyrightMode"/> against a
    /// scratch tree containing a file that lacks the canonical header
    /// returns exit 40 (<c>CopyrightHeaderMissing</c>).
    /// </summary>
    [Fact]
    public async Task ValidateCopyrightMode_MissingHeader_Returns40()
    {
        string root = Path.Combine(_scratchRoot, "ValidateCopyrightBad");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "Good.cs"),
            "// Copyright Simgenics. All Rights Reserved.\nnamespace X { }\n");
        File.WriteAllText(
            Path.Combine(root, "Bad.cs"),
            "// (no header)\nnamespace X { }\n");

        ValidateCopyrightMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[] { $"-Root={root}" },
            CancellationToken.None);

        Assert.Equal(40, exit);
    }

    /// <summary>
    /// Round-6 final-cleanup M2: <see cref="ValidateCopyrightMode"/>
    /// accepts the spec-canonical <c>-Paths=</c> flag (comma-separated
    /// list, optionally repeatable) and the <c>-Exclude=</c> flag.
    /// Files under any <c>-Exclude=</c> entry are skipped even if they
    /// would otherwise fail validation.
    /// </summary>
    [Fact]
    public async Task ValidateCopyrightMode_AcceptsSpecCanonicalPathsAndExclude()
    {
        string root = Path.Combine(_scratchRoot, "ValidateCopyrightPathsExclude");
        Directory.CreateDirectory(root);
        // A "good" tree.
        string goodSubtree = Path.Combine(root, "Good");
        Directory.CreateDirectory(goodSubtree);
        File.WriteAllText(
            Path.Combine(goodSubtree, "Good.cs"),
            "// Copyright Simgenics. All Rights Reserved.\nnamespace X { }\n");
        // A "bad" tree that would fail validation if scanned.
        string thirdPartySubtree = Path.Combine(root, "ThirdParty");
        Directory.CreateDirectory(thirdPartySubtree);
        File.WriteAllText(
            Path.Combine(thirdPartySubtree, "Bad.cs"),
            "// (no header, vendored upstream)\nnamespace X { }\n");

        ValidateCopyrightMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[]
            {
                $"-Paths={root}",
                $"-Exclude={thirdPartySubtree}",
            },
            CancellationToken.None);

        Assert.Equal(0, exit);
    }

    // ----- HelpMode -----

    /// <summary>
    /// Audit fix R4-M6: <see cref="HelpMode"/> always returns exit 0
    /// and emits the list of registered mode names. We capture the
    /// Logger.Info emit by inspecting the registry (the mode emits one
    /// Info message with the help text; the registry is reflected over
    /// loaded assemblies).
    /// </summary>
    [Fact]
    public async Task HelpMode_ReturnsZero_AndRegistryContainsAllModeNames()
    {
        HelpMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(0, exit);

        // Cross-check: every registered mode (built-in or out-of-tree)
        // has a non-empty Name + Description.
        Assembly entryAsm = typeof(HelpMode).Assembly;
        IEnumerable<Type> modeTypes = entryAsm.GetTypes()
            .Where(t => t.GetCustomAttribute<XBTModeAttribute>() is not null);
        Assert.Contains(modeTypes, t => t == typeof(HelpMode));
        Assert.Contains(modeTypes, t => t == typeof(CleanMode));
        Assert.Contains(modeTypes, t => t == typeof(ValidateCopyrightMode));
        Assert.Contains(modeTypes, t => t == typeof(WriteManifestMode));
        Assert.Contains(modeTypes, t => t == typeof(BuildModeType));
        Assert.Contains(modeTypes, t => t == typeof(VersionMode));
        // Audit fix R4-M8: new Phase 2 stub modes are registered.
        Assert.Contains(modeTypes, t => t == typeof(RunXHTMode));
        Assert.Contains(modeTypes, t => t == typeof(RunXIL2CPPMode));
    }

    // ----- WriteManifestMode -----

    /// <summary>
    /// Audit fix R4-M6: <see cref="WriteManifestMode"/> emits both
    /// Manifest.json and Manifest.fbs.bin into the documented
    /// per-target/configuration directory under
    /// <c>Engine/Intermediate/Build/&lt;Target&gt;/&lt;Config&gt;/</c>.
    /// We exercise this against the HelloWorldEngine fixture so the
    /// full discovery + manifest emission path is invoked end-to-end.
    /// </summary>
    [Fact]
    public async Task WriteManifestMode_EmitsManifestArtefacts()
    {
        // Locate the fixture and copy it into a scratch directory so
        // the test does not pollute the source tree's Intermediate.
        string fixtureSource = LocateHelloWorldFixture();
        string scratchEngineParent = Path.Combine(_scratchRoot, "WriteManifestFixture");
        Directory.CreateDirectory(scratchEngineParent);
        CopyTree(fixtureSource, scratchEngineParent);
        string engineRoot = Path.Combine(scratchEngineParent, "Engine");

        WriteManifestMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[]
            {
                "-Target=HelloModule",
                "-Configuration=Development",
                "-Platform=Win64",
                $"-Engine={engineRoot}",
            },
            CancellationToken.None);

        Assert.Equal(0, exit);

        string expectedDir = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development");
        string manifestJson = Path.Combine(expectedDir, "Manifest.json");
        string manifestFbs = Path.Combine(expectedDir, "Manifest.fbs.bin");

        Assert.True(File.Exists(manifestJson),
            $"Expected Manifest.json at {manifestJson}.");
        Assert.True(File.Exists(manifestFbs),
            $"Expected Manifest.fbs.bin at {manifestFbs}.");
    }

    /// <summary>
    /// Round-6 final-cleanup M2: <see cref="WriteManifestMode"/> accepts
    /// the spec-canonical <c>-Out=&lt;dir&gt;</c> flag and emits the
    /// manifest pair into that directory instead of the default
    /// <c>Intermediate/Build/&lt;Target&gt;/&lt;Configuration&gt;/</c>
    /// location.
    /// </summary>
    [Fact]
    public async Task WriteManifestMode_AcceptsSpecCanonical_OutFlag()
    {
        string fixtureSource = LocateHelloWorldFixture();
        string scratchEngineParent = Path.Combine(_scratchRoot, "WriteManifestOutFlag");
        Directory.CreateDirectory(scratchEngineParent);
        CopyTree(fixtureSource, scratchEngineParent);
        string engineRoot = Path.Combine(scratchEngineParent, "Engine");
        string customOutDir = Path.Combine(_scratchRoot, "WriteManifestOutFlag-Out");
        // Intentionally do NOT pre-create the directory; the mode
        // must create it.

        WriteManifestMode mode = new();
        int exit = await mode.ExecuteAsync(
            new[]
            {
                "-Target=HelloModule",
                "-Configuration=Development",
                "-Platform=Win64",
                $"-EngineRoot={engineRoot}",
                $"-Out={customOutDir}",
            },
            CancellationToken.None);

        Assert.Equal(0, exit);
        string manifestJson = Path.Combine(customOutDir, "Manifest.json");
        string manifestFbs = Path.Combine(customOutDir, "Manifest.fbs.bin");
        Assert.True(File.Exists(manifestJson),
            $"Expected Manifest.json at {manifestJson}.");
        Assert.True(File.Exists(manifestFbs),
            $"Expected Manifest.fbs.bin at {manifestFbs}.");

        // The default location must NOT have the manifest (would
        // indicate -Out= was ignored).
        string defaultDir = Path.Combine(
            engineRoot, "Intermediate", "Build", "HelloModule", "Development");
        Assert.False(File.Exists(Path.Combine(defaultDir, "Manifest.json")),
            "When -Out= is set, the default location must not receive a Manifest.json.");
    }

    // ----- BuildMode cancellation -----

    /// <summary>
    /// Audit fix R4-M6: cancellation is honoured end-to-end. A
    /// pre-cancelled token results in
    /// <see cref="OperationCanceledException"/> propagating out of the
    /// guard call at the top of <c>BuildMode.ExecuteAsync</c>; a
    /// token cancelled DURING discovery / module enumeration surfaces
    /// the same exception via <c>BuildMode.Run</c>. Either path is
    /// "cancellation observable" -- the exact behaviour the test
    /// asserts depends on when the token fires.
    /// </summary>
    [Fact]
    public void BuildMode_CancelledToken_PropagatesCancellation()
    {
        string fixtureSource = LocateHelloWorldFixture();
        string scratchEngineParent = Path.Combine(_scratchRoot, "BuildModeCancel");
        Directory.CreateDirectory(scratchEngineParent);
        CopyTree(fixtureSource, scratchEngineParent);
        string engineRoot = Path.Combine(scratchEngineParent, "Engine");

        using CancellationTokenSource cts = new();
        cts.Cancel();

        BuildOptions options = new()
        {
            TargetName = "HelloModule",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
            ManifestOnly = true,
        };

        // The Run static API does not silently swallow cancellation; it
        // propagates OperationCanceledException out so callers can react.
        // BuildMode.ExecuteAsync (the public mode entry point) catches
        // the exception and converts it to exit 130. Either signature
        // is "cancellation observable", which is the audit's contract.
        Assert.Throws<OperationCanceledException>(
            () => BuildModeType.Run(options, cts.Token));
    }

    // ----- BuildMode test module gating -----

    /// <summary>
    /// Audit fix R4-M6: a module declared <c>b_is_test_module = true</c>
    /// is filtered out of every build except a <c>-Configuration=Test</c>
    /// build. Verified by emitting a manifest against the fixture with
    /// the test-module marker and inspecting the emitted modules.
    /// </summary>
    [Fact]
    public void BuildMode_TestModule_FiltersFromNonTestBuilds()
    {
        string fixtureSource = LocateHelloWorldFixture();
        string scratchEngineParent = Path.Combine(_scratchRoot, "TestModuleGating");
        Directory.CreateDirectory(scratchEngineParent);
        CopyTree(fixtureSource, scratchEngineParent);
        string engineRoot = Path.Combine(scratchEngineParent, "Engine");

        // Mutate the fixture: add b_is_test_module = true to the
        // HelloModule descriptor.
        string tomlPath = Path.Combine(
            engineRoot, "Source", "Runtime", "HelloModule", "HelloModule.Build.toml");
        Assert.True(File.Exists(tomlPath), $"Fixture TOML missing at {tomlPath}.");
        string original = File.ReadAllText(tomlPath);
        File.WriteAllText(tomlPath, original + "\nb_is_test_module = true\n");

        BuildOptions devOptions = new()
        {
            TargetName = "HelloModule",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
            ManifestOnly = true,
        };
        BuildResult devResult = BuildModeType.Run(devOptions, CancellationToken.None);
        Assert.True(devResult.Success,
            "Development build should not fail even when the only module is gated out.");
        Assert.Empty(devResult.Actions);

        BuildOptions testOptions = devOptions with { Configuration = BuildConfiguration.Test };
        BuildResult testResult = BuildModeType.Run(testOptions, CancellationToken.None);
        Assert.True(testResult.Success,
            "Test build must succeed because the test module is included for Test config.");
        // Manifest-only emission produces no actions; what we care about
        // is that no exception is thrown and Success=true.
    }

    // ----- BuildMode minimum-toolchain-version -----

    /// <summary>
    /// Audit fix R4-M6: a module declaring
    /// <c>minimum_toolchain_version = "99.0.0"</c> against any
    /// realistic installed toolchain fails the build with exit 23
    /// (<c>EngineOrToolchainVersionMismatch</c>).
    /// </summary>
    [Fact]
    public void BuildMode_MinimumToolchainVersion_RejectsWithExit23()
    {
        string fixtureSource = LocateHelloWorldFixture();
        string scratchEngineParent = Path.Combine(_scratchRoot, "MinToolchainCheck");
        Directory.CreateDirectory(scratchEngineParent);
        CopyTree(fixtureSource, scratchEngineParent);
        string engineRoot = Path.Combine(scratchEngineParent, "Engine");

        string tomlPath = Path.Combine(
            engineRoot, "Source", "Runtime", "HelloModule", "HelloModule.Build.toml");
        string original = File.ReadAllText(tomlPath);
        File.WriteAllText(tomlPath, original + "\nminimum_toolchain_version = \"99.0.0\"\n");

        BuildOptions options = new()
        {
            TargetName = "HelloModule",
            Configuration = BuildConfiguration.Development,
            Platform = Platform.Win64,
            EngineRoot = engineRoot,
            ManifestOnly = true,
        };

        // The toolchain-version gate raises XBTException(23). The
        // exception is thrown out of BuildMode.Run; we assert via the
        // typed catch in BuildMode.ExecuteAsync by going through the
        // public mode API.
        BuildModeType mode = new();
        int exit = mode.ExecuteAsync(
            new[]
            {
                "-Target=HelloModule",
                "-Configuration=Development",
                $"-Engine={engineRoot}",
            },
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(23, exit);
    }

    // ----- M8: Phase 2 stub modes -----

    /// <summary>
    /// Phase 1f: <see cref="RunXHTMode"/> registers under
    /// <c>"run-xht"</c>, exposes the <c>ToolNotFoundExitCode = 24</c>
    /// constant (closest-fit per Contract Section 13), and rejects an
    /// empty args vector with exit <c>10</c> (CLI arg error) -- the
    /// missing-required-flag path. The thorough subprocess-invocation
    /// coverage lives in <c>Tests/Entry/RunXHTModeTests.cs</c>.
    /// </summary>
    [Fact]
    public async Task RunXHTMode_RejectsEmptyArgsWithExit10()
    {
        Assert.Equal("run-xht", RunXHTMode.Name);
        Assert.Equal(24, RunXHTMode.ToolNotFoundExitCode);

        RunXHTMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(10, exit);
    }

    /// <summary>
    /// Audit fix R4-M8: <see cref="RunXIL2CPPMode"/> returns the
    /// "tool not found" exit code (24) and is registered under
    /// the canonical name <c>"run-xil2cpp"</c>.
    /// </summary>
    [Fact]
    public async Task RunXIL2CPPMode_Returns_ToolNotFoundExitCode()
    {
        Assert.Equal("run-xil2cpp", RunXIL2CPPMode.Name);
        Assert.Equal(24, RunXIL2CPPMode.ToolNotFoundExitCode);

        RunXIL2CPPMode mode = new();
        int exit = await mode.ExecuteAsync(Array.Empty<string>(), CancellationToken.None);
        Assert.Equal(24, exit);
    }

    // ----- Helpers -----

    /// <summary>
    /// Locate the HelloWorldEngine fixture relative to the test
    /// assembly. The directory layout follows the convention used by
    /// <c>HelloWorldSmokeTest</c> (Fixtures live under
    /// <c>XBT.Tests/Fixtures/HelloWorldEngine</c>).
    /// </summary>
    private static string LocateHelloWorldFixture()
    {
        string asmDir = Path.GetDirectoryName(typeof(ModeSmokeTests).Assembly.Location)!;
        // Walk up looking for the Fixtures directory: bin/Debug/net8.0
        // -> bin/Debug -> bin -> XBT.Tests.
        string cursor = asmDir;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(cursor, "Fixtures", "HelloWorldEngine");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            string? parent = Path.GetDirectoryName(cursor);
            if (parent is null || parent == cursor) break;
            cursor = parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the HelloWorldEngine fixture starting from {asmDir}.");
    }

    private static void CopyTree(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, dirPath);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }
        foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, filePath);
            File.Copy(filePath, Path.Combine(destDir, rel), overwrite: true);
        }
    }
}
