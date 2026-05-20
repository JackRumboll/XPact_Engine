// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Cached MSVC environment record. Discovers the active MSVC installation
/// via <c>vswhere.exe</c> and the Windows SDK via the registry on first
/// access; subsequent reads return the cached value. Mirrors UE's
/// <c>VCEnvironment</c> in shape.
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
/// <para>
/// Phase 1.4a extension: the discovery probe now also locates the
/// Windows 10/11 SDK (headers + import libs) via the
/// <c>HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots</c> registry
/// key (value <c>KitsRoot10</c>) and the highest-numbered subdirectory
/// under <c>&lt;root&gt;/Include/</c>. The chosen SDK version may be
/// overridden via the <c>XPACT_WINSDK_VERSION</c> environment variable.
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

    /// <summary>
    /// Include paths the MSVC compiler itself ships (CRT, ATL, MFC, the
    /// MSVC headers). Used as the first half of <see cref="IncludePaths"/>.
    /// </summary>
    public IReadOnlyList<string> MsvcIncludePaths { get; }

    /// <summary>
    /// Library paths the MSVC compiler itself ships (libcmt, libcpmt,
    /// vcruntime, msvcrt, the import-library half of the MSVC redist).
    /// Used as the first half of <see cref="LibraryPaths"/>.
    /// </summary>
    public IReadOnlyList<string> MsvcLibraryPaths { get; }

    /// <summary>
    /// Absolute path to the Windows 10/11 SDK root (typically
    /// <c>C:\Program Files (x86)\Windows Kits\10</c>). Empty for a
    /// synthetic test environment that has not declared an SDK.
    /// </summary>
    public string WindowsSdkRoot { get; }

    /// <summary>
    /// Windows SDK version string (e.g. <c>"10.0.26100.0"</c>). Used to
    /// resolve include + library subdirectories under
    /// <see cref="WindowsSdkRoot"/>.
    /// </summary>
    public string WindowsSdkVersion { get; }

    /// <summary>
    /// Include paths under <c>&lt;sdkRoot&gt;/Include/&lt;sdkVersion&gt;/</c>:
    /// <c>um</c> (user-mode Win32), <c>shared</c> (kernel + user shared),
    /// <c>ucrt</c> (the universal C runtime headers), and <c>winrt</c>
    /// (Windows Runtime). Listed in the order MSVC's <c>cl.exe</c>
    /// expects.
    /// </summary>
    public IReadOnlyList<string> SdkIncludePaths { get; }

    /// <summary>
    /// Library paths under <c>&lt;sdkRoot&gt;/Lib/&lt;sdkVersion&gt;/</c>:
    /// <c>um/x64</c> (user-mode import libs) and <c>ucrt/x64</c> (UCRT
    /// import libs). Always in the order linker would consume them.
    /// </summary>
    public IReadOnlyList<string> SdkLibraryPaths { get; }

    /// <summary>Composite include paths: MSVC paths first, then SDK paths.</summary>
    public IReadOnlyList<string> IncludePaths { get; }

    /// <summary>Composite library paths: MSVC paths first, then SDK paths.</summary>
    public IReadOnlyList<string> LibraryPaths { get; }

    /// <summary>Compiler version string (e.g. <c>"14.40.33807"</c>).</summary>
    public string CompilerVersion { get; }

    private VCEnvironment(
        string vsInstallDir,
        string compilerPath,
        string linkerPath,
        string libraryManagerPath,
        string resourceCompilerPath,
        IReadOnlyList<string> msvcIncludePaths,
        IReadOnlyList<string> msvcLibraryPaths,
        string windowsSdkRoot,
        string windowsSdkVersion,
        IReadOnlyList<string> sdkIncludePaths,
        IReadOnlyList<string> sdkLibraryPaths,
        string compilerVersion)
    {
        VSInstallDir = vsInstallDir;
        CompilerPath = compilerPath;
        LinkerPath = linkerPath;
        LibraryManagerPath = libraryManagerPath;
        ResourceCompilerPath = resourceCompilerPath;
        MsvcIncludePaths = msvcIncludePaths;
        MsvcLibraryPaths = msvcLibraryPaths;
        WindowsSdkRoot = windowsSdkRoot;
        WindowsSdkVersion = windowsSdkVersion;
        SdkIncludePaths = sdkIncludePaths;
        SdkLibraryPaths = sdkLibraryPaths;
        CompilerVersion = compilerVersion;

        // Composite paths: MSVC first, then SDK. Build once at construction
        // so callers and the determinism test get identical instances on
        // repeat reads.
        List<string> includes = new(msvcIncludePaths.Count + sdkIncludePaths.Count);
        includes.AddRange(msvcIncludePaths);
        includes.AddRange(sdkIncludePaths);
        IncludePaths = includes;

        List<string> libs = new(msvcLibraryPaths.Count + sdkLibraryPaths.Count);
        libs.AddRange(msvcLibraryPaths);
        libs.AddRange(sdkLibraryPaths);
        LibraryPaths = libs;
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
    /// When MSVC is found, the helper additionally probes the Windows
    /// SDK (via the registry) and populates
    /// <see cref="WindowsSdkRoot"/>, <see cref="WindowsSdkVersion"/>,
    /// <see cref="SdkIncludePaths"/>, and <see cref="SdkLibraryPaths"/>.
    /// A missing SDK throws <see cref="VCEnvironmentNotFoundException"/>.
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
    /// fixture. The real <see cref="TryDiscover"/> spawns vswhere and
    /// touches the registry, both of which are machine-dependent; tests
    /// substitute a synthetic env so they run the same on every machine.
    /// </summary>
    /// <param name="sdkRoot">
    /// Synthetic Windows SDK root. When non-null and non-empty, the
    /// factory probes <c>&lt;sdkRoot&gt;/Include/</c> for a version
    /// subdirectory (the highest one wins, or <paramref name="sdkVersion"/>
    /// if provided). When the directory does not exist or contains no
    /// version subdirectories, <see cref="VCEnvironmentNotFoundException"/>
    /// is thrown so tests exercise the missing-SDK path.
    /// </param>
    /// <param name="sdkVersion">
    /// Explicit SDK version to pick. When null or empty, the highest
    /// version under <paramref name="sdkRoot"/>/Include is chosen.
    /// </param>
    internal static VCEnvironment ForTesting(
        string vsInstallDir,
        string compilerPath,
        string linkerPath,
        string? libraryManagerPath = null,
        string? resourceCompilerPath = null,
        IReadOnlyList<string>? msvcIncludePaths = null,
        IReadOnlyList<string>? msvcLibraryPaths = null,
        string? sdkRoot = null,
        string? sdkVersion = null,
        string? compilerVersion = null)
    {
        IReadOnlyList<string> sdkIncludes = Array.Empty<string>();
        IReadOnlyList<string> sdkLibs = Array.Empty<string>();
        string resolvedSdkRoot = "";
        string resolvedSdkVersion = "";

        if (!string.IsNullOrEmpty(sdkRoot))
        {
            (resolvedSdkRoot, resolvedSdkVersion, sdkIncludes, sdkLibs) =
                ResolveSdkPaths(sdkRoot, sdkVersion);
        }
        else if (!string.IsNullOrEmpty(sdkVersion))
        {
            // No root supplied but a version: synthetic path-only fixture
            // (tests for cache-key invalidation use this).
            resolvedSdkVersion = sdkVersion!;
        }

        return new VCEnvironment(
            vsInstallDir,
            compilerPath,
            linkerPath,
            libraryManagerPath ?? "",
            resourceCompilerPath ?? "",
            msvcIncludePaths ?? Array.Empty<string>(),
            msvcLibraryPaths ?? Array.Empty<string>(),
            resolvedSdkRoot,
            resolvedSdkVersion,
            sdkIncludes,
            sdkLibs,
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

        List<string> msvcIncludePaths = new()
        {
            Path.Combine(versionDir, "include"),
        };
        List<string> msvcLibraryPaths = new()
        {
            Path.Combine(versionDir, "lib", "x64"),
        };

        // Discover the Windows SDK. Throws VCEnvironmentNotFoundException
        // when the SDK is absent -- that is a fatal-on-Windows condition
        // and the message instructs the operator to install the
        // Win10/11 SDK.
        (string sdkRoot, string sdkVersion, IReadOnlyList<string> sdkIncludes, IReadOnlyList<string> sdkLibs)
            = DiscoverWindowsSdk();

        // Resolve rc.exe under the SDK root for the chosen SDK version.
        // rc.exe lives at <sdkRoot>/bin/<sdkVersion>/x64/rc.exe.
        string rcPath = "";
        if (!string.IsNullOrEmpty(sdkRoot))
        {
            string candidateRc = Path.Combine(sdkRoot, "bin", sdkVersion, "x64", "rc.exe");
            if (File.Exists(candidateRc))
            {
                rcPath = candidateRc;
            }
        }

        return new VCEnvironment(
            installDir,
            compilerPath,
            linkerPath,
            File.Exists(libPath) ? libPath : "",
            rcPath,
            msvcIncludePaths,
            msvcLibraryPaths,
            sdkRoot,
            sdkVersion,
            sdkIncludes,
            sdkLibs,
            version);
    }

    /// <summary>
    /// Resolve the Windows SDK root + version + include + library paths
    /// from the registry, with override support via the
    /// <c>XPACT_WINSDK_VERSION</c> environment variable.
    /// </summary>
    /// <remarks>
    /// Reads <c>HKLM\SOFTWARE\Microsoft\Windows Kits\Installed Roots</c>'s
    /// <c>KitsRoot10</c> value. Enumerates the
    /// <c>&lt;root&gt;/Include/</c> subdirectories and picks the highest
    /// version (or the env-var override, if set). Builds the standard
    /// include + library subdirectory list. Throws
    /// <see cref="VCEnvironmentNotFoundException"/> when the registry
    /// key or any include subdirectory is missing.
    /// </remarks>
    private static (string Root, string Version, IReadOnlyList<string> Includes, IReadOnlyList<string> Libs)
        DiscoverWindowsSdk()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new VCEnvironmentNotFoundException(
                "Windows SDK discovery is only supported on Windows hosts. " +
                "MSVC / Win64 builds require Windows; use the Clang toolchain " +
                "for Linux / Android.");
        }

        string? root = ReadKitsRoot10FromRegistry();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            throw new VCEnvironmentNotFoundException(
                "Windows SDK not found. The registry value HKLM\\SOFTWARE\\Microsoft\\" +
                "Windows Kits\\Installed Roots\\KitsRoot10 is missing or points to a " +
                "non-existent directory. Install the Windows 10/11 SDK via " +
                "https://developer.microsoft.com/windows/downloads/windows-sdk/ " +
                "or the Visual Studio Installer 'Desktop development with C++' workload.");
        }

        // ResolveSdkPaths consults XPACT_WINSDK_VERSION internally if no
        // explicit override is provided; pass null here to let the env
        // var path drive in the production discovery flow.
        return ResolveSdkPaths(root, versionOverride: null);
    }

    /// <summary>
    /// Given an SDK root (real or synthetic) and an optional explicit
    /// version, resolve to (root, version, includes, libs). The version
    /// preference order is:
    /// <list type="number">
    ///   <item><paramref name="versionOverride"/> if non-null + non-empty.</item>
    ///   <item>The <c>XPACT_WINSDK_VERSION</c> environment variable, if set.</item>
    ///   <item>The highest version subdirectory found under
    ///   <paramref name="root"/>/Include.</item>
    /// </list>
    /// Throws <see cref="VCEnvironmentNotFoundException"/> if no version
    /// is resolvable.
    /// </summary>
    private static (string Root, string Version, IReadOnlyList<string> Includes, IReadOnlyList<string> Libs)
        ResolveSdkPaths(string root, string? versionOverride)
    {
        string includeRoot = Path.Combine(root, "Include");
        if (!Directory.Exists(includeRoot))
        {
            throw new VCEnvironmentNotFoundException(
                $"Windows SDK root '{root}' does not contain an Include/ subdirectory. " +
                "Install the Windows 10/11 SDK via " +
                "https://developer.microsoft.com/windows/downloads/windows-sdk/.");
        }

        // Enumerate version subdirectories. A valid SDK version subdirectory
        // contains the canonical 'um' folder; anything else (e.g. wdf, .nuget)
        // is filtered.
        List<string> versionDirs = new();
        foreach (string dir in Directory.GetDirectories(includeRoot))
        {
            string umCandidate = Path.Combine(dir, "um");
            if (Directory.Exists(umCandidate))
            {
                versionDirs.Add(Path.GetFileName(dir));
            }
        }

        if (versionDirs.Count == 0)
        {
            throw new VCEnvironmentNotFoundException(
                $"Windows SDK root '{root}' has an Include/ directory but no version " +
                "subdirectories containing a 'um' folder. Install the Windows 10/11 " +
                "SDK via the Visual Studio Installer (workload " +
                "'Desktop development with C++').");
        }

        // The effective override is the explicit param if non-empty,
        // otherwise the env var. ResolveSdkPaths is the central choke
        // point so the env var works for both the discovery path (which
        // doesn't pass an explicit version) and the test path (which
        // wants to exercise the env var too).
        string? effectiveOverride = !string.IsNullOrEmpty(versionOverride)
            ? versionOverride
            : Environment.GetEnvironmentVariable("XPACT_WINSDK_VERSION");

        // Choose: explicit/env-var > highest.
        string chosen;
        if (!string.IsNullOrEmpty(effectiveOverride) && versionDirs.Contains(effectiveOverride))
        {
            chosen = effectiveOverride!;
        }
        else if (!string.IsNullOrEmpty(effectiveOverride))
        {
            throw new VCEnvironmentNotFoundException(
                $"Requested Windows SDK version '{effectiveOverride}' is not installed. " +
                $"Available versions under '{includeRoot}': " +
                string.Join(", ", versionDirs) + ".");
        }
        else
        {
            // Sort ordinal so 10.0.26100.0 sorts after 10.0.22621.0 (which
            // is the desired ordering for numeric-with-dots version strings;
            // ordinal is sufficient because the dot-separated components
            // are zero-padded to the same width within each SDK release
            // family). Reverse to get highest-first.
            versionDirs.Sort(StringComparer.Ordinal);
            versionDirs.Reverse();
            chosen = versionDirs[0];
        }

        // Build include + library paths. Order is the same one the
        // Visual Studio "x64 Native Tools Command Prompt" uses, so any
        // diagnostic the compiler emits about a missing header mentions
        // the directories in the same order.
        List<string> includes = new()
        {
            Path.Combine(includeRoot, chosen, "um"),
            Path.Combine(includeRoot, chosen, "shared"),
            Path.Combine(includeRoot, chosen, "ucrt"),
            Path.Combine(includeRoot, chosen, "winrt"),
        };
        List<string> libs = new()
        {
            Path.Combine(root, "Lib", chosen, "um", "x64"),
            Path.Combine(root, "Lib", chosen, "ucrt", "x64"),
        };

        return (root, chosen, includes, libs);
    }

    /// <summary>
    /// Read the Windows SDK install root from the registry. Returns null
    /// on any failure (key absent, access denied, registry corrupt, etc.).
    /// The trailing backslash is stripped so callers can <see cref="Path.Combine(string, string)"/>
    /// without doubling separators.
    /// </summary>
    private static string? ReadKitsRoot10FromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // The KitsRoot10 value lives in the 32-bit registry view on
            // 64-bit Windows hosts. Use the explicit 32-bit view so we
            // hit the same key as the Microsoft installers regardless of
            // whether the calling process is 32- or 64-bit.
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry32);
            using RegistryKey? subKey = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Kits\Installed Roots",
                writable: false);
            if (subKey is null)
            {
                return null;
            }
            object? value = subKey.GetValue("KitsRoot10");
            string? root = value as string;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            // Registry access can throw on locked-down machines; treat as
            // "not found" rather than propagating.
            return null;
        }
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

/// <summary>
/// Thrown by <see cref="VCEnvironment"/> when a discovery probe fails
/// in a way that cannot be recovered (e.g. the Windows SDK registry key
/// is missing, or the requested SDK version is not installed). Maps to
/// Toolchain Contract Rev 13 Section 13 exit code 25
/// (Toolchain discovery range; new code reserved for Phase 1.4a alongside
/// the existing 23 = engine/toolchain version mismatch).
/// </summary>
public sealed class VCEnvironmentNotFoundException : XBTException
{
    /// <summary>Exit code reserved for Win SDK discovery failure.</summary>
    public const int ExitCodeWinSdkNotFound = 25;

    /// <summary>Construct an SDK-not-found exception with the given message.</summary>
    public VCEnvironmentNotFoundException(string message)
        : base(message, exitCode: ExitCodeWinSdkNotFound)
    {
    }

    /// <summary>Construct an SDK-not-found exception with an inner cause.</summary>
    public VCEnvironmentNotFoundException(string message, Exception inner)
        : base(message, exitCode: ExitCodeWinSdkNotFound, inner)
    {
    }
}
