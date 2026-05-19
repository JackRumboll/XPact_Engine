// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Cached MSVC environment record. Discovers the active MSVC installation
/// via <c>vswhere.exe</c> on first access; subsequent reads return the
/// cached value. Mirrors UE's <c>VCEnvironment</c> in shape.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 7.2. Discovery is best-
/// effort: when <c>vswhere</c> is absent (e.g. CI agents without VS
/// Build Tools installed), <see cref="TryDiscover"/> returns
/// <see cref="DiscoveryResult.NotFound"/>. The toolchain reports a
/// build-blocking diagnostic at that point; no fallback to ad-hoc
/// path scanning is provided.
/// </para>
/// </remarks>
public sealed class VCEnvironment
{
    /// <summary>Absolute path to the Visual Studio installation root.</summary>
    public string VSInstallDir { get; }

    /// <summary>Absolute path to <c>cl.exe</c>.</summary>
    public string CompilerPath { get; }

    /// <summary>Absolute path to <c>link.exe</c>.</summary>
    public string LinkerPath { get; }

    /// <summary>Absolute path to <c>lib.exe</c>.</summary>
    public string LibraryManagerPath { get; }

    /// <summary>Absolute path to <c>rc.exe</c>.</summary>
    public string ResourceCompilerPath { get; }

    /// <summary>Include paths the compiler searches for system + MSVC headers.</summary>
    public IReadOnlyList<string> IncludePaths { get; }

    /// <summary>Library paths the linker searches for system + MSVC libraries.</summary>
    public IReadOnlyList<string> LibraryPaths { get; }

    /// <summary>Windows SDK version string (e.g. <c>"10.0.22621.0"</c>).</summary>
    public string WindowsSdkVersion { get; }

    /// <summary>Compiler version string (e.g. <c>"14.40.33807"</c>).</summary>
    public string CompilerVersion { get; }

    private VCEnvironment(
        string vsInstallDir,
        string compilerPath,
        string linkerPath,
        string libraryManagerPath,
        string resourceCompilerPath,
        IReadOnlyList<string> includePaths,
        IReadOnlyList<string> libraryPaths,
        string windowsSdkVersion,
        string compilerVersion)
    {
        VSInstallDir = vsInstallDir;
        CompilerPath = compilerPath;
        LinkerPath = linkerPath;
        LibraryManagerPath = libraryManagerPath;
        ResourceCompilerPath = resourceCompilerPath;
        IncludePaths = includePaths;
        LibraryPaths = libraryPaths;
        WindowsSdkVersion = windowsSdkVersion;
        CompilerVersion = compilerVersion;
    }

    /// <summary>Outcome of a discovery attempt.</summary>
    public enum DiscoveryResult
    {
        /// <summary>An MSVC installation was found and the environment is populated.</summary>
        Found,

        /// <summary>No MSVC installation was found; the toolchain cannot run.</summary>
        NotFound,
    }

    /// <summary>
    /// Probe for an MSVC installation on the host. Returns
    /// <see cref="DiscoveryResult.Found"/> with a populated
    /// <paramref name="environment"/> on success;
    /// <see cref="DiscoveryResult.NotFound"/> with a null
    /// <paramref name="environment"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe order is:
    /// </para>
    /// <list type="number">
    ///   <item>The <c>$env:VS_INSTALLDIR</c> environment variable, if set.</item>
    ///   <item><c>vswhere.exe</c> in the standard installer path
    ///   (<c>%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\</c>).</item>
    /// </list>
    /// <para>
    /// When MSVC is found, the helper does not validate version
    /// requirements; the caller compares
    /// <see cref="CompilerVersion"/> against
    /// <see cref="XMSVCToolChain"/>'s minimum-version policy and emits a
    /// diagnostic if the version is too old.
    /// </para>
    /// </remarks>
    public static DiscoveryResult TryDiscover(out VCEnvironment? environment)
    {
        environment = null;

        // 1. Explicit env-var override.
        string? envOverride = Environment.GetEnvironmentVariable("VS_INSTALLDIR");
        if (!string.IsNullOrEmpty(envOverride) && Directory.Exists(envOverride))
        {
            environment = ProbeFromInstallDir(envOverride);
            if (environment is not null)
            {
                return DiscoveryResult.Found;
            }
        }

        // 2. vswhere.exe in the standard path.
        string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrEmpty(programFilesX86))
        {
            return DiscoveryResult.NotFound;
        }
        string vswhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return DiscoveryResult.NotFound;
        }

        // vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64
        //         -property installationPath
        string? installDir = RunVswhereForInstallPath(vswhere);
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
        {
            return DiscoveryResult.NotFound;
        }

        environment = ProbeFromInstallDir(installDir);
        return environment is not null ? DiscoveryResult.Found : DiscoveryResult.NotFound;
    }

    /// <summary>
    /// Test hook: construct a synthetic VCEnvironment for a per-test
    /// fixture. The real <see cref="TryDiscover"/> spawns vswhere which is
    /// machine-dependent; tests substitute a synthetic env so they run
    /// the same on every machine.
    /// </summary>
    internal static VCEnvironment ForTesting(
        string vsInstallDir,
        string compilerPath,
        string linkerPath,
        string? libraryManagerPath = null,
        string? resourceCompilerPath = null,
        IReadOnlyList<string>? includePaths = null,
        IReadOnlyList<string>? libraryPaths = null,
        string? windowsSdkVersion = null,
        string? compilerVersion = null)
    {
        return new VCEnvironment(
            vsInstallDir,
            compilerPath,
            linkerPath,
            libraryManagerPath ?? "",
            resourceCompilerPath ?? "",
            includePaths ?? Array.Empty<string>(),
            libraryPaths ?? Array.Empty<string>(),
            windowsSdkVersion ?? "10.0.22621.0",
            compilerVersion ?? "14.40.0.0");
    }

    private static VCEnvironment? ProbeFromInstallDir(string installDir)
    {
        // Standard MSVC layout:
        //   <installDir>/VC/Tools/MSVC/<ver>/bin/HostX64/x64/cl.exe
        string msvcRoot = Path.Combine(installDir, "VC", "Tools", "MSVC");
        if (!Directory.Exists(msvcRoot))
        {
            return null;
        }

        // Pick the highest-numbered version directory.
        string[] versionDirs = Directory.GetDirectories(msvcRoot);
        if (versionDirs.Length == 0)
        {
            return null;
        }

        Array.Sort(versionDirs, StringComparer.Ordinal);
        Array.Reverse(versionDirs);
        string versionDir = versionDirs[0];
        string version = Path.GetFileName(versionDir);

        string compilerPath = Path.Combine(versionDir, "bin", "HostX64", "x64", "cl.exe");
        string linkerPath = Path.Combine(versionDir, "bin", "HostX64", "x64", "link.exe");
        string libPath = Path.Combine(versionDir, "bin", "HostX64", "x64", "lib.exe");
        if (!File.Exists(compilerPath))
        {
            return null;
        }

        List<string> includePaths = new()
        {
            Path.Combine(versionDir, "include"),
        };
        List<string> libraryPaths = new()
        {
            Path.Combine(versionDir, "lib", "x64"),
        };

        return new VCEnvironment(
            installDir,
            compilerPath,
            linkerPath,
            File.Exists(libPath) ? libPath : "",
            "",                       // rc.exe lives in the Windows SDK; resolved later
            includePaths,
            libraryPaths,
            "10.0.22621.0",           // Windows SDK; later improvement: read registry
            version);
    }

    private static string? RunVswhereForInstallPath(string vswhereExe)
    {
        ProcessStartInfo psi = new()
        {
            FileName = vswhereExe,
            Arguments = "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }
            return stdout.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
