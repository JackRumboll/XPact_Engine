// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// CI disassembly scan over the linked Sleef object archive / shared
/// object, verifying that NO forbidden FMA instructions slipped past
/// the determinism flag set. Maps to XCore-4a Rev 3 Section 17.3
/// acceptance criterion <strong>C-extra</strong> + Section 14 Step
/// 11.5 sub-item "Regression test".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed.</b> Per XCore-4a Rev 3 fix M3, NEON's FMA
/// instructions on AArch64 cannot be disabled at the ISA level even
/// with <c>-ffp-contract=off</c> + <c>-mno-fma</c>; the toolchain
/// will happily emit <c>fmla</c> if it judges that path optimal.  On
/// x86_64, <c>-mno-fma</c> + <c>/fp:precise</c> should prevent the
/// <c>vfmadd*</c> family, but a regression in the SIMD-kernel
/// selection logic (the upstream Sleef tarball ships SIMD variants
/// that we explicitly exclude; if a future maintainer accidentally
/// re-enables them, FMA leaks back in) would silently break sim-path
/// bit-exactness.
/// </para>
/// <para>
/// The scan reads the assembled object/archive with
/// <c>llvm-objdump -d</c> and greps the disassembled output for the
/// forbidden instruction set.  A single match fails the build with
/// exit code 41 (the same code as
/// <see cref="XToolChain"/>'s ban-flag violation; both flow through
/// <see cref="ToolchainBannedFlagException"/>) so the error surface
/// is uniform across the determinism contract.
/// </para>
/// <para>
/// <b>Phase 1e landing.</b> The scan tool ships in this Phase 1e
/// commit; the actual integration into the LinkModuleAction post-
/// build pass is a Phase 1g CI gate.  The unit tests in
/// <c>/Engine/Source/Runtime/XCore/Tests/Math/Sleef/</c> exercise the
/// scan in isolation against synthetic objdump fixtures so the
/// regex/parser code path is unit-tested independent of the upstream
/// Sleef tarball drop-in.
/// </para>
/// <para>
/// <b>Cross-platform tool selection.</b> <c>llvm-objdump</c> ships in
/// every supported toolchain bundle (LLVM 14+ for the Windows clang
/// path; Android NDK r26+; the system clang on Linux).  The scan
/// invokes <c>llvm-objdump -d --no-show-raw-insn</c> which produces
/// per-line assembly mnemonics in a stable form that the regex below
/// matches.  We do not use <c>objdump</c> (the GNU binutils variant)
/// because its mnemonic format diverges between Linux distributions
/// (e.g., the AT&amp;T-syntax FMA names differ from the Intel-syntax
/// names; <c>llvm-objdump</c> emits both consistently).
/// </para>
/// </remarks>
public static class SleefFMACheck
{
    /// <summary>
    /// AArch64 forbidden-instruction mnemonics per XCore-4a Rev 3 fix
    /// M3 + Section 17.3 C-extra.  The list covers every scalar /
    /// SIMD fused-multiply-add variant the AArch64 toolchain might
    /// emit; <c>fmla</c> is the SIMD variant, <c>fmadd</c> is the
    /// scalar variant, the <c>fn*</c> spellings are the negated
    /// counterparts.
    /// </summary>
    /// <remarks>
    /// The mnemonic match is performed at <strong>token</strong>
    /// granularity (whitespace-bounded), not substring, because
    /// substring would false-positive on legitimate non-FMA mnemonics
    /// that happen to contain "fma" as a prefix (none exist today on
    /// AArch64, but the discipline is to be exact).
    /// </remarks>
    public static readonly IReadOnlyList<string> ForbiddenAArch64Mnemonics = new[]
    {
        // Scalar FMA family.
        "fmadd",
        "fmsub",
        "fnmadd",
        "fnmsub",
        // SIMD vector FMA family.
        "fmla",
        "fmls",
        "fnmla",
        "fnmls",
        // ARMv7-A legacy spelling (some toolchains emit this on
        // AArch64 in 32-bit-compat mode; defence-in-depth).
        "vfma",
        "vfms",
        "vfnma",
        "vfnms",
    };

    /// <summary>
    /// x86_64 forbidden-instruction mnemonics.  The <c>vfm*</c> family
    /// covers every Intel AVX-FMA encoding (vfmadd132ps, vfmadd213ps,
    /// vfmadd231ps; double-precision and scalar variants similarly).
    /// We match the <strong>prefix</strong> <c>vfm</c> followed by
    /// <c>add</c>/<c>sub</c>/<c>nmadd</c>/<c>nmsub</c> at any
    /// xmm/ymm/zmm width.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenX86_64MnemonicPrefixes = new[]
    {
        "vfmadd",   // covers vfmadd132ps, vfmadd213pd, vfmadd231ps, etc.
        "vfmsub",
        "vfnmadd",
        "vfnmsub",
        // AMD XOP-encoded FMA4 family (pre-FMA3 era; not part of any
        // modern x86_64 toolchain emission but defence-in-depth).
        "vfmaddsub",
        "vfmsubadd",
    };

    /// <summary>Exit code emitted when a forbidden instruction is found.</summary>
    public const int ForbiddenInstructionExitCode = 41;

    /// <summary>
    /// Per-line disassembly format from <c>llvm-objdump -d
    /// --no-show-raw-insn</c>.  Example line:
    /// <code>
    ///   400560: fmla v0.4s, v1.4s, v2.4s
    /// </code>
    /// The mnemonic is the first whitespace-delimited token after the
    /// hex address + colon.  The regex extracts it; the address
    /// portion is optional so we tolerate slight format variants
    /// across <c>llvm-objdump</c> versions.
    /// </summary>
    private static readonly Regex MnemonicPattern = new(
        @"^\s*(?:[0-9a-fA-F]+:\s+)?(?<mnemonic>[a-z][a-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Result of a single disassembly scan.  <see cref="ForbiddenHits"/>
    /// enumerates every offending instruction found; the build fails
    /// when the list is non-empty.
    /// </summary>
    public sealed record ScanResult
    {
        /// <summary>The architecture the object was disassembled as.</summary>
        public required string Architecture { get; init; }

        /// <summary>The path to the scanned object/archive.</summary>
        public required string Path { get; init; }

        /// <summary>
        /// Every forbidden instruction match.  Each hit records the
        /// disassembly line number (1-based), the offending mnemonic,
        /// and the full source line for the build diagnostic.
        /// </summary>
        public required IReadOnlyList<ForbiddenHit> ForbiddenHits { get; init; }

        /// <summary>True iff the scan found NO forbidden instructions.</summary>
        public bool IsClean => ForbiddenHits.Count == 0;
    }

    /// <summary>One forbidden-instruction match.</summary>
    public sealed record ForbiddenHit
    {
        /// <summary>Disassembly line number (1-based).</summary>
        public required int LineNumber { get; init; }

        /// <summary>The offending mnemonic token.</summary>
        public required string Mnemonic { get; init; }

        /// <summary>The full disassembly source line.</summary>
        public required string SourceLine { get; init; }
    }

    /// <summary>
    /// Scan a previously-captured disassembly listing (the output of
    /// <c>llvm-objdump -d --no-show-raw-insn</c>) for forbidden FMA
    /// instructions.  This is the unit-testable kernel of the
    /// post-link scan -- callers that have already captured the
    /// disassembly (e.g., unit tests with synthetic fixtures) invoke
    /// this method directly.  The
    /// <see cref="ScanLinkedArtefact"/> method composes this with the
    /// <c>llvm-objdump</c> invocation.
    /// </summary>
    /// <param name="disassemblyText">Captured llvm-objdump stdout.</param>
    /// <param name="architecture">"AArch64" or "x86_64". The forbidden mnemonic set is selected by this.</param>
    /// <param name="path">The path to attach to the diagnostic on hit; informational only.</param>
    /// <returns>The scan result; the build proceeds iff <see cref="ScanResult.IsClean"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="architecture"/> is not "AArch64" or "x86_64".</exception>
    public static ScanResult ScanDisassembly(
        string disassemblyText,
        string architecture,
        string path)
    {
        ArgumentNullException.ThrowIfNull(disassemblyText);
        ArgumentNullException.ThrowIfNull(architecture);
        ArgumentNullException.ThrowIfNull(path);

        IReadOnlyList<string> exactMnemonics;
        IReadOnlyList<string> prefixMnemonics;
        switch (architecture)
        {
            case "AArch64":
                exactMnemonics = ForbiddenAArch64Mnemonics;
                prefixMnemonics = Array.Empty<string>();
                break;
            case "x86_64":
                exactMnemonics = Array.Empty<string>();
                prefixMnemonics = ForbiddenX86_64MnemonicPrefixes;
                break;
            default:
                throw new ArgumentException(
                    $"SleefFMACheck.ScanDisassembly: unrecognised architecture '{architecture}'. "
                    + "Valid values are 'AArch64' or 'x86_64' per XCore-4a Section 17.3 C-extra.",
                    nameof(architecture));
        }

        List<ForbiddenHit> hits = new();
        // Newline-split. Either \n or \r\n; ReadOnlySpan<char>.Split
        // would be more efficient but the API surface uses string[]
        // for clarity; the scan cost is dominated by IO not parsing.
        string[] lines = disassemblyText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            Match m = MnemonicPattern.Match(line);
            if (!m.Success)
            {
                continue;
            }
            string mnemonic = m.Groups["mnemonic"].Value;

            // Exact-match path (AArch64).
            foreach (string forbidden in exactMnemonics)
            {
                if (string.Equals(mnemonic, forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new ForbiddenHit
                    {
                        LineNumber = i + 1,
                        Mnemonic = mnemonic,
                        SourceLine = line,
                    });
                    goto nextLine;
                }
            }

            // Prefix-match path (x86_64; the FMA family has 132/213/231
            // suffixes + ps/pd/ss/sd width markers that all share the
            // common prefix).
            foreach (string forbiddenPrefix in prefixMnemonics)
            {
                if (mnemonic.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new ForbiddenHit
                    {
                        LineNumber = i + 1,
                        Mnemonic = mnemonic,
                        SourceLine = line,
                    });
                    goto nextLine;
                }
            }
            nextLine:;
        }

        return new ScanResult
        {
            Architecture = architecture,
            Path = path,
            ForbiddenHits = hits,
        };
    }

    /// <summary>
    /// Run the disassembly scan against a linked artefact (object
    /// archive or shared object).  Invokes <c>llvm-objdump -d
    /// --no-show-raw-insn</c> on the artefact and forwards the output
    /// to <see cref="ScanDisassembly"/>.
    /// </summary>
    /// <param name="objdumpPath">Absolute path to <c>llvm-objdump</c>.</param>
    /// <param name="artefactPath">Absolute path to the linked .a / .so / .lib / .obj.</param>
    /// <param name="architecture">"AArch64" or "x86_64".</param>
    /// <returns>The scan result.</returns>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="objdumpPath"/> or <paramref name="artefactPath"/> does not exist.</exception>
    /// <remarks>
    /// <para>
    /// The method launches <c>llvm-objdump</c> as a child process and
    /// captures its stdout.  Failure modes:
    /// </para>
    /// <list type="bullet">
    ///   <item>The objdump binary does not exist -> <see cref="FileNotFoundException"/>.</item>
    ///   <item>The artefact does not exist -> <see cref="FileNotFoundException"/>.</item>
    ///   <item>objdump exits non-zero -> <see cref="ToolchainProcessException"/>.</item>
    /// </list>
    /// <para>
    /// The scan is read-only with respect to the linked artefact;
    /// objdump never modifies its input.  The scan is safe to run
    /// concurrently with other build actions reading the same
    /// artefact.
    /// </para>
    /// </remarks>
    public static ScanResult ScanLinkedArtefact(
        string objdumpPath,
        string artefactPath,
        string architecture)
    {
        ArgumentNullException.ThrowIfNull(objdumpPath);
        ArgumentNullException.ThrowIfNull(artefactPath);
        ArgumentNullException.ThrowIfNull(architecture);

        if (!File.Exists(objdumpPath))
        {
            throw new FileNotFoundException(
                $"SleefFMACheck.ScanLinkedArtefact: llvm-objdump binary not found at '{objdumpPath}'. "
                + "The disassembly scan requires LLVM 14+ which ships in every supported toolchain "
                + "bundle (Windows clang, Android NDK r26+, Linux system clang).",
                objdumpPath);
        }
        if (!File.Exists(artefactPath))
        {
            throw new FileNotFoundException(
                $"SleefFMACheck.ScanLinkedArtefact: linked artefact not found at '{artefactPath}'.",
                artefactPath);
        }

        ProcessStartInfo psi = new()
        {
            FileName = objdumpPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("--no-show-raw-insn");
        psi.ArgumentList.Add(artefactPath);

        using Process proc = new() { StartInfo = psi };
        StringBuilder stdoutBuilder = new(capacity: 4 * 1024 * 1024);
        StringBuilder stderrBuilder = new();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdoutBuilder.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderrBuilder.AppendLine(e.Data);
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new ToolchainProcessException(
                $"SleefFMACheck.ScanLinkedArtefact: llvm-objdump exited with code {proc.ExitCode} "
                + $"while disassembling '{artefactPath}'.  stderr:\n{stderrBuilder}");
        }

        return ScanDisassembly(stdoutBuilder.ToString(), architecture, artefactPath);
    }

    /// <summary>
    /// Format a <see cref="ScanResult"/> as a human-readable build
    /// diagnostic.  Used by the build pipeline when emitting the
    /// failure record to the developer's terminal.
    /// </summary>
    public static string FormatDiagnostic(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsClean)
        {
            return $"SleefFMACheck: PASS ({result.Architecture} '{result.Path}')";
        }

        StringBuilder sb = new();
        sb.AppendLine(
            $"SleefFMACheck: FAIL ({result.Architecture} '{result.Path}')");
        sb.AppendLine(
            $"  Per XCore-4a Rev 3 Section 17.3 C-extra: sim-path linked Sleef artefact "
            + $"must contain NO forbidden FMA instructions.");
        sb.AppendLine(
            $"  Forbidden hits (count = {result.ForbiddenHits.Count}):");
        // Cap the diagnostic to the first 20 hits so the build log
        // doesn't drown the developer in a million identical errors
        // if the entire library has FMAs.
        int show = Math.Min(20, result.ForbiddenHits.Count);
        for (int i = 0; i < show; i++)
        {
            ForbiddenHit h = result.ForbiddenHits[i];
            sb.AppendLine($"    line {h.LineNumber}: [{h.Mnemonic}] {h.SourceLine}");
        }
        if (result.ForbiddenHits.Count > show)
        {
            sb.AppendLine($"    ... and {result.ForbiddenHits.Count - show} more.");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Exit-coded exception thrown when <c>llvm-objdump</c> fails during a
/// SleefFMACheck scan.  The exit code propagates to the build runner
/// via the standard XBT exit-code-bearing exception protocol per
/// Toolchain Contract Section 13.  Maps to the same exit code (41) as
/// the broader SimPath-determinism-violation family because a failure
/// to verify FMA-absence on the linked Sleef artefact IS a determinism-
/// envelope failure (the build cannot prove the artefact is FMA-free).
/// </summary>
public sealed class ToolchainProcessException : XBTException
{
    /// <summary>The exit code for SimPath-determinism failures (41).</summary>
    public const int DefaultExitCode = 41;

    /// <summary>Construct a toolchain-process exception with the given message.</summary>
    public ToolchainProcessException(string message)
        : base(message, exitCode: DefaultExitCode)
    {
    }

    /// <summary>Construct a toolchain-process exception with an inner cause.</summary>
    public ToolchainProcessException(string message, Exception inner)
        : base(message, exitCode: DefaultExitCode, inner: inner)
    {
    }
}
