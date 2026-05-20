// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Toolchain;

/// <summary>
/// Verifies the Phase 1.4a Windows SDK discovery + composition logic in
/// <see cref="VCEnvironment"/>: registry probe, version override, composite
/// path ordering, and the determinism contract for the IncludePaths /
/// LibraryPaths getters.
/// </summary>
/// <remarks>
/// <para>
/// Synthetic-fixture tests build a fake SDK directory tree under a temp
/// scratch directory and pass its root to
/// <see cref="VCEnvironment.ForTesting(string, string, string, string?, string?, System.Collections.Generic.IReadOnlyList{string}?, System.Collections.Generic.IReadOnlyList{string}?, string?, string?, string?)"/>.
/// This lets the discovery code run the same on machines without a
/// Windows SDK installed (e.g. CI Linux agents).
/// </para>
/// <para>
/// The "real registry" test is gated on
/// <see cref="RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform)"/>
/// and a runtime registry probe; on Linux / CI without an SDK it skips
/// silently with a clear warning (per the smoke-test pattern).
/// </para>
/// </remarks>
public sealed class VCEnvironmentSdkTests : IDisposable
{
    private readonly string _scratchDir;

    public VCEnvironmentSdkTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            "XBT.Tests.VCEnvSdk",
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
            // Best-effort cleanup; ignore on Windows file-locking races.
        }
    }

    /// <summary>
    /// On Windows hosts with a Windows SDK installed,
    /// <see cref="VCEnvironment.TryDiscover"/> finds it and returns a
    /// fully-populated environment whose include / library lists carry
    /// the four canonical SDK include directories + two canonical SDK
    /// library directories. On non-Windows or SDK-absent hosts the
    /// test skips with a clear warning (matching the smoke-test gate).
    /// </summary>
    [Fact]
    public void Discover_OnWindowsWithSdkInstalled_PopulatesSdkPaths()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Simgenics.XPact.XBT.Core.Logger.Warning(
                "VCEnvironmentSdkTests.Discover_OnWindowsWithSdkInstalled_PopulatesSdkPaths: " +
                "skipped because host is not Windows.");
            return;
        }

        VCEnvironment.DiscoveryResult result = VCEnvironment.TryDiscover(out VCEnvironment? env);
        if (result == VCEnvironment.DiscoveryResult.NotFound)
        {
            Simgenics.XPact.XBT.Core.Logger.Warning(
                "VCEnvironmentSdkTests.Discover_OnWindowsWithSdkInstalled_PopulatesSdkPaths: " +
                "skipped because no MSVC toolchain was discovered on this host.");
            return;
        }

        Assert.NotNull(env);
        Assert.False(string.IsNullOrEmpty(env!.WindowsSdkRoot),
            "WindowsSdkRoot must be set when discovery succeeds.");
        Assert.False(string.IsNullOrEmpty(env.WindowsSdkVersion),
            "WindowsSdkVersion must be set when discovery succeeds.");
        // The four canonical Win10/11 SDK include subdirectories.
        Assert.Equal(4, env.SdkIncludePaths.Count);
        Assert.Contains(env.SdkIncludePaths, p => p.EndsWith("um", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(env.SdkIncludePaths, p => p.EndsWith("shared", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(env.SdkIncludePaths, p => p.EndsWith("ucrt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(env.SdkIncludePaths, p => p.EndsWith("winrt", StringComparison.OrdinalIgnoreCase));

        // Library paths: um/x64 and ucrt/x64 (two entries on x64 hosts).
        Assert.Equal(2, env.SdkLibraryPaths.Count);
        Assert.All(env.SdkLibraryPaths, p =>
            Assert.EndsWith("x64", p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// With two synthetic SDK versions present, ForTesting picks the
    /// highest. Ordinal version-string sort places 10.0.26100.0 after
    /// 10.0.22621.0 for Win11 SDKs.
    /// </summary>
    [Fact]
    public void SyntheticSdk_PicksHighestVersion()
    {
        CreateSyntheticSdk(_scratchDir, "10.0.22621.0");
        CreateSyntheticSdk(_scratchDir, "10.0.26100.0");

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: _scratchDir,
            compilerPath: Path.Combine(_scratchDir, "cl.exe"),
            linkerPath: Path.Combine(_scratchDir, "link.exe"),
            sdkRoot: _scratchDir);

        Assert.Equal("10.0.26100.0", env.WindowsSdkVersion);
        Assert.All(env.SdkIncludePaths, p =>
            Assert.Contains("10.0.26100.0", p, StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>XPACT_WINSDK_VERSION</c> env var overrides "pick highest"
    /// when the requested version is installed. Phase 1.4a contract:
    /// override beats auto-pick, missing override beats absent SDK
    /// version with a clear error.
    /// </summary>
    [Fact]
    public void EnvVarOverride_SelectsRequestedVersion()
    {
        CreateSyntheticSdk(_scratchDir, "10.0.22621.0");
        CreateSyntheticSdk(_scratchDir, "10.0.26100.0");

        string? saved = Environment.GetEnvironmentVariable("XPACT_WINSDK_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("XPACT_WINSDK_VERSION", "10.0.22621.0");

            VCEnvironment env = VCEnvironment.ForTesting(
                vsInstallDir: _scratchDir,
                compilerPath: Path.Combine(_scratchDir, "cl.exe"),
                linkerPath: Path.Combine(_scratchDir, "link.exe"),
                sdkRoot: _scratchDir);

            Assert.Equal("10.0.22621.0", env.WindowsSdkVersion);
            Assert.All(env.SdkIncludePaths, p =>
                Assert.Contains("10.0.22621.0", p, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XPACT_WINSDK_VERSION", saved);
        }
    }

    /// <summary>
    /// A synthetic SDK root with no version subdirectories throws
    /// <see cref="VCEnvironmentNotFoundException"/> with the canonical
    /// exit code 25.
    /// </summary>
    [Fact]
    public void MissingSdk_Throws_VCEnvironmentNotFoundException()
    {
        // Create an Include/ directory but no version subdirectories.
        Directory.CreateDirectory(Path.Combine(_scratchDir, "Include"));

        VCEnvironmentNotFoundException ex = Assert.Throws<VCEnvironmentNotFoundException>(
            () => VCEnvironment.ForTesting(
                vsInstallDir: _scratchDir,
                compilerPath: Path.Combine(_scratchDir, "cl.exe"),
                linkerPath: Path.Combine(_scratchDir, "link.exe"),
                sdkRoot: _scratchDir));
        Assert.Equal(VCEnvironmentNotFoundException.ExitCodeWinSdkNotFound, ex.ExitCode);
        Assert.Equal(25, ex.ExitCode);   // Pin the contract code.
        Assert.Contains("Windows", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Composite <see cref="VCEnvironment.IncludePaths"/> lists the MSVC
    /// paths BEFORE the SDK paths. Ordering is load-bearing: a header
    /// shadowed by both MSVC and SDK is resolved against the MSVC copy
    /// first (matching the standard cl.exe behaviour from the x64
    /// Native Tools Command Prompt).
    /// </summary>
    [Fact]
    public void IncludePaths_Composite_MsvcBeforeSdk()
    {
        CreateSyntheticSdk(_scratchDir, "10.0.26100.0");

        string fakeMsvcInc = Path.Combine(_scratchDir, "FakeMSVC", "include");
        Directory.CreateDirectory(fakeMsvcInc);

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: _scratchDir,
            compilerPath: Path.Combine(_scratchDir, "cl.exe"),
            linkerPath: Path.Combine(_scratchDir, "link.exe"),
            msvcIncludePaths: new[] { fakeMsvcInc },
            sdkRoot: _scratchDir);

        Assert.Equal(5, env.IncludePaths.Count);   // 1 MSVC + 4 SDK.
        // MSVC entry comes first.
        Assert.Equal(fakeMsvcInc, env.IncludePaths[0]);
        // SDK entries follow in canonical order: um, shared, ucrt, winrt.
        Assert.EndsWith("um", env.IncludePaths[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("shared", env.IncludePaths[2], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ucrt", env.IncludePaths[3], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("winrt", env.IncludePaths[4], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Composite <see cref="VCEnvironment.LibraryPaths"/> lists the MSVC
    /// paths BEFORE the SDK paths. Same load-bearing reason as
    /// IncludePaths: a tie between MSVC and Win SDK import libs resolves
    /// to the MSVC copy.
    /// </summary>
    [Fact]
    public void LibraryPaths_Composite_MsvcBeforeSdk()
    {
        CreateSyntheticSdk(_scratchDir, "10.0.26100.0");

        string fakeMsvcLib = Path.Combine(_scratchDir, "FakeMSVC", "lib", "x64");
        Directory.CreateDirectory(fakeMsvcLib);

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: _scratchDir,
            compilerPath: Path.Combine(_scratchDir, "cl.exe"),
            linkerPath: Path.Combine(_scratchDir, "link.exe"),
            msvcLibraryPaths: new[] { fakeMsvcLib },
            sdkRoot: _scratchDir);

        Assert.Equal(3, env.LibraryPaths.Count);   // 1 MSVC + 2 SDK.
        Assert.Equal(fakeMsvcLib, env.LibraryPaths[0]);
        // SDK entries: um/x64 then ucrt/x64.
        Assert.Contains("um", env.LibraryPaths[1]);
        Assert.Contains("ucrt", env.LibraryPaths[2]);
    }

    /// <summary>
    /// Determinism: two reads of <see cref="VCEnvironment.IncludePaths"/>
    /// + <see cref="VCEnvironment.LibraryPaths"/> on the same VCEnvironment
    /// instance return the same list (same items, same order). Otherwise
    /// the cache-key components would differ between consecutive build
    /// passes, breaking the reproducibility envelope.
    /// </summary>
    [Fact]
    public void IncludeAndLibrary_Getters_AreStableAcrossReads()
    {
        CreateSyntheticSdk(_scratchDir, "10.0.26100.0");

        VCEnvironment env = VCEnvironment.ForTesting(
            vsInstallDir: _scratchDir,
            compilerPath: Path.Combine(_scratchDir, "cl.exe"),
            linkerPath: Path.Combine(_scratchDir, "link.exe"),
            sdkRoot: _scratchDir);

        var includesA = env.IncludePaths.ToArray();
        var includesB = env.IncludePaths.ToArray();
        var libsA = env.LibraryPaths.ToArray();
        var libsB = env.LibraryPaths.ToArray();

        // Same content, same order. The list reference itself need not
        // be the same instance (the contract is content stability across
        // reads); both lists must contain the same items in the same
        // positions.
        Assert.Equal(includesA, includesB);
        Assert.Equal(libsA, libsB);
        // Sanity: not empty (would be vacuously equal).
        Assert.NotEmpty(includesA);
        Assert.NotEmpty(libsA);
    }

    /// <summary>
    /// An unknown env-var override (e.g. user typo'd
    /// <c>10.0.99999.0</c>) is a fatal error -- ForTesting throws
    /// <see cref="VCEnvironmentNotFoundException"/> rather than silently
    /// falling back to "highest installed". The message lists the
    /// versions that ARE installed so the operator can correct it.
    /// </summary>
    [Fact]
    public void EnvVarOverride_UnknownVersion_ThrowsWithDiagnostic()
    {
        CreateSyntheticSdk(_scratchDir, "10.0.26100.0");

        string? saved = Environment.GetEnvironmentVariable("XPACT_WINSDK_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("XPACT_WINSDK_VERSION", "10.0.99999.0");

            VCEnvironmentNotFoundException ex = Assert.Throws<VCEnvironmentNotFoundException>(
                () => VCEnvironment.ForTesting(
                    vsInstallDir: _scratchDir,
                    compilerPath: Path.Combine(_scratchDir, "cl.exe"),
                    linkerPath: Path.Combine(_scratchDir, "link.exe"),
                    sdkRoot: _scratchDir));
            Assert.Contains("10.0.99999.0", ex.Message);
            Assert.Contains("10.0.26100.0", ex.Message);   // Lists installed versions.
        }
        finally
        {
            Environment.SetEnvironmentVariable("XPACT_WINSDK_VERSION", saved);
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Create a minimal synthetic SDK tree under <paramref name="root"/>
    /// containing <c>Include/&lt;version&gt;/{um,shared,ucrt,winrt}</c>
    /// and <c>Lib/&lt;version&gt;/{um,ucrt}/x64</c>. The 'um' folder is
    /// the marker the discovery code uses to identify a valid SDK
    /// version subdirectory.
    /// </summary>
    private static void CreateSyntheticSdk(string root, string version)
    {
        foreach (string sub in new[] { "um", "shared", "ucrt", "winrt" })
        {
            Directory.CreateDirectory(Path.Combine(root, "Include", version, sub));
        }
        foreach (string sub in new[] { "um", "ucrt" })
        {
            Directory.CreateDirectory(Path.Combine(root, "Lib", version, sub, "x64"));
        }
    }
}
