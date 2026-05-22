// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Integration helper that drives <see cref="SleefFMACheck"/> over a
/// linked sim-path artefact. Called by the build runner immediately
/// after the toolchain produces a sim-path module's link output;
/// emits the failure diagnostic and the exit code when the artefact
/// carries a forbidden FMA instruction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1g wiring.</b> The SleefFMACheck disassembly kernel ships
/// in <see cref="SleefFMACheck"/>; this class layers the build-runner
/// integration on top so the linker action's success no longer ends
/// the sim-path determinism contract. After every sim-path link the
/// runner invokes <see cref="VerifyArtefact"/>, which:
/// </para>
/// <list type="number">
///   <item>Locates <c>llvm-objdump</c> in the toolchain's bin directory
///   (or via the <c>LLVM_OBJDUMP</c> environment variable as an
///   override).</item>
///   <item>Calls <see cref="SleefFMACheck.ScanLinkedArtefact"/> to
///   disassemble the artefact and search for the forbidden FMA
///   mnemonic set.</item>
///   <item>On hit: emits the formatted diagnostic to stderr and
///   throws <see cref="ToolchainBannedFlagException"/> (exit code 41 --
///   same as the parser-level banned-flag failure).</item>
///   <item>On clean: returns silently; the build proceeds.</item>
/// </list>
/// <para>
/// <b>Architecture choice (not <see cref="ActionGraph.IExternalAction"/>).</b>
/// The check is NOT a new action type because adding a new
/// <c>XActionType</c> slot would rotate the
/// <c>ActionHistory.CurrentVersion</c> hash and invalidate every
/// cache entry across the engine. Instead, the build runner invokes
/// this helper synchronously after each sim-path link succeeds; the
/// per-link overhead is small (~50 ms for a typical Sleef library
/// disassembly) and the determinism gate is uncompromised.
/// </para>
/// <para>
/// <b>Architecture target inference.</b> <see cref="SleefFMACheck"/>'s
/// <c>ScanDisassembly</c> takes an architecture string
/// ("AArch64" / "x86_64"); we derive it from <see cref="Platform"/>
/// per Master Plan Section 2:
/// </para>
/// <list type="bullet">
///   <item><c>Platform.Win64</c>   -- x86_64.</item>
///   <item><c>Platform.Linux</c>   -- x86_64.</item>
///   <item><c>Platform.Android</c> -- AArch64 (Quest 3 ARM64).</item>
/// </list>
/// </remarks>
public static class SleefFMACheckIntegration
{
    /// <summary>
    /// Environment variable name overriding the auto-detected
    /// <c>llvm-objdump</c> path. Tested before any auto-detection logic
    /// so CI shards can pin a specific LLVM version.
    /// </summary>
    public const string ObjdumpEnvVar = "LLVM_OBJDUMP";

    /// <summary>
    /// Verify the linked artefact is FMA-free; throw on hit.
    /// </summary>
    /// <param name="artefactPath">Absolute path to the linked binary.</param>
    /// <param name="platform">Target platform (drives the architecture string for the scan).</param>
    /// <exception cref="ToolchainBannedFlagException">
    /// Thrown when the disassembly scan finds a forbidden FMA
    /// instruction in the artefact (exit code 41).
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when llvm-objdump cannot be located OR when the artefact
    /// itself does not exist (caller bug).
    /// </exception>
    /// <exception cref="ToolchainProcessException">
    /// Thrown when llvm-objdump exits with non-zero status (e.g.,
    /// the artefact is unparseable / corrupted).
    /// </exception>
    public static void VerifyArtefact(string artefactPath, Platform platform)
    {
        ArgumentNullException.ThrowIfNull(artefactPath);

        string architecture = ArchitectureForPlatform(platform);
        string objdumpPath = LocateObjdump()
            ?? throw new FileNotFoundException(
                "SleefFMACheckIntegration.VerifyArtefact: llvm-objdump not "
                + "found via the LLVM_OBJDUMP environment variable or in any "
                + "well-known toolchain bin directory. Set LLVM_OBJDUMP to "
                + "the absolute path of llvm-objdump (LLVM 14+) so the "
                + "sim-path FMA scan can disassemble linked artefacts.",
                "llvm-objdump");

        SleefFMACheck.ScanResult result = SleefFMACheck.ScanLinkedArtefact(
            objdumpPath: objdumpPath,
            artefactPath: artefactPath,
            architecture: architecture);

        if (result.IsClean)
        {
            return;
        }

        // Forbidden hit: surface the diagnostic + throw with exit 41.
        // The diagnostic captures up to 20 hits so the developer gets a
        // human-readable surface without drowning in identical errors.
        Console.Error.Write(SleefFMACheck.FormatDiagnostic(result));
        throw new ToolchainBannedFlagException(
            $"SleefFMACheck: linked sim-path artefact '{artefactPath}' "
            + $"contains {result.ForbiddenHits.Count} forbidden FMA "
            + $"instruction(s). Per XCore-4a Rev 3 Section 17.3 C-extra, "
            + "the sim-path Sleef library must be FMA-free for cross-arch "
            + "bit-exactness. See diagnostic above.");
    }

    /// <summary>
    /// Map an XPact <see cref="Platform"/> to the architecture string
    /// <see cref="SleefFMACheck.ScanDisassembly"/> expects.
    /// </summary>
    /// <remarks>
    /// Per Master Plan Section 2: Win64 / Linux target x86_64; Android
    /// targets ARM64 (Quest 3 / Snapdragon XR2 Gen 2). The mapping is
    /// fixed -- there is no per-target architecture override.
    /// </remarks>
    public static string ArchitectureForPlatform(Platform platform) => platform switch
    {
        Platform.Win64 => "x86_64",
        Platform.Linux => "x86_64",
        Platform.Android => "AArch64",
        _ => throw new ArgumentOutOfRangeException(
            nameof(platform),
            platform,
            "SleefFMACheckIntegration.ArchitectureForPlatform: unrecognised "
            + "platform (expected Win64 / Linux / Android per Master Plan "
            + "Section 2).")
    };

    /// <summary>
    /// Attempt to locate <c>llvm-objdump</c>. Returns null if no
    /// candidate exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution order:
    /// </para>
    /// <list type="number">
    ///   <item><c>$LLVM_OBJDUMP</c> environment variable (CI override).</item>
    ///   <item><c>$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/&lt;host&gt;/bin/llvm-objdump</c></item>
    ///   <item>Standard <c>PATH</c> search via <c>where</c> / <c>which</c>
    ///   semantics (the runtime's process-start fallback).</item>
    /// </list>
    /// <para>
    /// The function does NOT throw on miss; it returns null so the
    /// caller can produce a single uniform error.
    /// </para>
    /// </remarks>
    public static string? LocateObjdump()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(ObjdumpEnvVar);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        // Fall through: check well-known toolchain bin paths.
        string exeSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string fileName = "llvm-objdump" + exeSuffix;

        // NDK fallback: ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/<host>/bin
        string? ndkRoot = Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT");
        if (!string.IsNullOrEmpty(ndkRoot))
        {
            // Host triple is OS-specific; iterate common forms.
            string[] hostDirs = OperatingSystem.IsWindows()
                ? new[] { "windows-x86_64" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "darwin-x86_64", "darwin-arm64" }
                    : new[] { "linux-x86_64" };
            foreach (string host in hostDirs)
            {
                string candidate = Path.Combine(
                    ndkRoot, "toolchains", "llvm", "prebuilt", host, "bin", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // System PATH fallback. We can't rely on `where` here without
        // spawning a subprocess; instead, scan PATH directly.
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            char sep = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (string dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Skip invalid PATH entries.
                }
            }
        }

        return null;
    }
}

// Note: ToolchainBannedFlagException is defined in XToolChain.cs and
// shared across the toolchain layer; SleefFMACheckIntegration consumes
// the existing definition rather than re-declaring it. The exit code
// (41) and message-only ctor are the load-bearing surface.
