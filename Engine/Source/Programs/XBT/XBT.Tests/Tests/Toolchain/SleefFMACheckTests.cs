// Copyright Simgenics. All Rights Reserved.

using System;
using System.Linq;
using Simgenics.XPact.XBT.Toolchain;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Toolchain;

/// <summary>
/// Unit tests for <see cref="SleefFMACheck.ScanDisassembly"/> -- the
/// regex-based forbidden-FMA-instruction detector for sim-path Sleef
/// builds.  Maps to XCore-4a Rev 3 Section 17.3 acceptance criterion
/// C-extra + Section 14 Step 11.5 "Regression test" sub-item.
/// </summary>
/// <remarks>
/// <para>
/// The tests exercise the scan kernel against synthetic
/// <c>llvm-objdump -d --no-show-raw-insn</c> fixtures so the parser
/// behaviour is locked independent of any specific Sleef build /
/// upstream tarball drop-in.  The actual integration against a built
/// Sleef archive happens at the Phase 1g cross-arch CI shard.
/// </para>
/// </remarks>
public sealed class SleefFMACheckTests
{
    // -----------------------------------------------------------------
    // Synthetic AArch64 disassembly fixture -- contains no forbidden
    // instructions.  Format mirrors `llvm-objdump -d
    // --no-show-raw-insn` output.
    // -----------------------------------------------------------------
    private const string AArch64CleanFixture =
        "Disassembly of section .text:\n" +
        "\n" +
        "0000000000400500 <Sleef_sinf_u35>:\n" +
        "  400500: stp     x29, x30, [sp, #-16]!\n" +
        "  400504: mov     x29, sp\n" +
        "  400508: fmul    s1, s0, s0\n" +
        "  40050c: fadd    s2, s0, s1\n" +
        "  400510: fsub    s3, s2, s0\n" +
        "  400514: fdiv    s4, s3, s1\n" +
        "  400518: ldp     x29, x30, [sp], #16\n" +
        "  40051c: ret\n";

    // -----------------------------------------------------------------
    // Synthetic AArch64 fixture WITH a forbidden fmla instruction.
    // -----------------------------------------------------------------
    private const string AArch64DirtyFixture_Fmla =
        "Disassembly of section .text:\n" +
        "\n" +
        "0000000000400500 <Sleef_sinf_u35>:\n" +
        "  400500: stp     x29, x30, [sp, #-16]!\n" +
        "  400504: mov     x29, sp\n" +
        "  400508: fmla    v0.4s, v1.4s, v2.4s\n" +
        "  40050c: ldp     x29, x30, [sp], #16\n" +
        "  400510: ret\n";

    // -----------------------------------------------------------------
    // Synthetic AArch64 fixture WITH a forbidden fmadd (scalar FMA).
    // -----------------------------------------------------------------
    private const string AArch64DirtyFixture_Fmadd =
        "0000000000400600 <Sleef_cosf_u35>:\n" +
        "  400600: fmul    s1, s0, s0\n" +
        "  400604: fmadd   s2, s0, s1, s3\n" +
        "  400608: ret\n";

    // -----------------------------------------------------------------
    // Synthetic AArch64 fixture WITH multiple forbidden hits.
    // -----------------------------------------------------------------
    private const string AArch64DirtyFixture_Multiple =
        "0000000000400700 <Sleef_powf_u10>:\n" +
        "  400700: fmla    v0.4s, v1.4s, v2.4s\n" +
        "  400704: fmls    v0.4s, v1.4s, v2.4s\n" +
        "  400708: fnmla   v0.4s, v1.4s, v2.4s\n" +
        "  40070c: vfma    v0.4s, v1.4s, v2.4s\n" +
        "  400710: ret\n";

    // -----------------------------------------------------------------
    // Synthetic x86_64 fixture -- contains no forbidden vfm* prefixes.
    // -----------------------------------------------------------------
    private const string X86_64CleanFixture =
        "Disassembly of section .text:\n" +
        "\n" +
        "0000000000401000 <Sleef_sinf_u35>:\n" +
        "  401000: push    rbp\n" +
        "  401001: mov     rbp, rsp\n" +
        "  401004: movss   xmm1, xmm0\n" +
        "  401009: mulss   xmm1, xmm0\n" +
        "  40100e: addss   xmm2, xmm1\n" +
        "  401013: pop     rbp\n" +
        "  401014: ret\n";

    // -----------------------------------------------------------------
    // Synthetic x86_64 fixture WITH forbidden vfmadd213ps.
    // -----------------------------------------------------------------
    private const string X86_64DirtyFixture_Vfmadd =
        "0000000000401000 <Sleef_sinf_u35>:\n" +
        "  401000: push    rbp\n" +
        "  401001: vfmadd213ps  xmm0, xmm1, xmm2\n" +
        "  401007: pop     rbp\n" +
        "  401008: ret\n";

    // -----------------------------------------------------------------
    // Synthetic x86_64 fixture with vfnmsub (negated FMA).
    // -----------------------------------------------------------------
    private const string X86_64DirtyFixture_Vfnmsub =
        "0000000000401100 <Sleef_cosf_u35>:\n" +
        "  401100: vfnmsub132pd  ymm0, ymm1, ymm2\n" +
        "  401106: ret\n";

    [Fact]
    public void ScanDisassembly_CleanAArch64_ReturnsZeroHits()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64CleanFixture, "AArch64", "test.a");

        Assert.True(result.IsClean);
        Assert.Empty(result.ForbiddenHits);
        Assert.Equal("AArch64", result.Architecture);
        Assert.Equal("test.a", result.Path);
    }

    [Fact]
    public void ScanDisassembly_AArch64_Fmla_IsDetected()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64DirtyFixture_Fmla, "AArch64", "test.a");

        Assert.False(result.IsClean);
        Assert.Single(result.ForbiddenHits);
        SleefFMACheck.ForbiddenHit hit = result.ForbiddenHits[0];
        Assert.Equal("fmla", hit.Mnemonic);
        Assert.Contains("fmla", hit.SourceLine);
    }

    [Fact]
    public void ScanDisassembly_AArch64_Fmadd_IsDetected()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64DirtyFixture_Fmadd, "AArch64", "test.a");

        Assert.False(result.IsClean);
        Assert.Single(result.ForbiddenHits);
        Assert.Equal("fmadd", result.ForbiddenHits[0].Mnemonic);
    }

    [Fact]
    public void ScanDisassembly_AArch64_MultipleHits_AllDetected()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64DirtyFixture_Multiple, "AArch64", "test.a");

        Assert.False(result.IsClean);
        Assert.Equal(4, result.ForbiddenHits.Count);
        Assert.Equal(new[] { "fmla", "fmls", "fnmla", "vfma" },
            result.ForbiddenHits.Select(h => h.Mnemonic).ToArray());
    }

    [Fact]
    public void ScanDisassembly_CleanX86_64_ReturnsZeroHits()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(X86_64CleanFixture, "x86_64", "test.a");

        Assert.True(result.IsClean);
        Assert.Empty(result.ForbiddenHits);
    }

    [Fact]
    public void ScanDisassembly_X86_64_Vfmadd213ps_IsDetected()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(X86_64DirtyFixture_Vfmadd, "x86_64", "test.a");

        Assert.False(result.IsClean);
        Assert.Single(result.ForbiddenHits);
        Assert.Equal("vfmadd213ps", result.ForbiddenHits[0].Mnemonic);
    }

    [Fact]
    public void ScanDisassembly_X86_64_Vfnmsub132pd_IsDetected()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(X86_64DirtyFixture_Vfnmsub, "x86_64", "test.a");

        Assert.False(result.IsClean);
        Assert.Single(result.ForbiddenHits);
        Assert.Equal("vfnmsub132pd", result.ForbiddenHits[0].Mnemonic);
    }

    [Fact]
    public void ScanDisassembly_AArch64_DoesNotMatchOnX86_64Mnemonics()
    {
        // An x86_64 fixture should NOT flag when scanned as AArch64
        // because the x86 vfmadd prefix is x86-only.
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(X86_64DirtyFixture_Vfmadd, "AArch64", "test.a");

        Assert.True(result.IsClean);
    }

    [Fact]
    public void ScanDisassembly_X86_64_DoesNotMatchOnAArch64Mnemonics()
    {
        // An AArch64 fixture should NOT flag when scanned as x86_64
        // because the AArch64 fmla mnemonic doesn't start with vfm*.
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64DirtyFixture_Fmla, "x86_64", "test.a");

        Assert.True(result.IsClean);
    }

    [Fact]
    public void ScanDisassembly_UnknownArchitecture_Throws()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SleefFMACheck.ScanDisassembly("", "PowerPC64", "test.a"));

        Assert.Contains("PowerPC64", ex.Message);
        Assert.Contains("AArch64", ex.Message);
        Assert.Contains("x86_64", ex.Message);
    }

    [Fact]
    public void ScanDisassembly_EmptyInput_ReturnsClean()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly("", "AArch64", "empty.a");

        Assert.True(result.IsClean);
        Assert.Empty(result.ForbiddenHits);
    }

    [Fact]
    public void ScanDisassembly_AArch64_IgnoresLabelsContainingFmaSubstrings()
    {
        // Label like "<fmaddSimulator>:" must NOT trigger a hit because
        // the mnemonic matcher requires the token to be in the mnemonic
        // position (after the address and colon), not anywhere on the
        // line.  Synthetic test verifies the discipline.
        const string Fixture =
            "0000000000400800 <fmaddSimulator>:\n" +
            "  400800: fmul    s1, s0, s0\n" +
            "  400804: ret\n";

        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(Fixture, "AArch64", "test.a");

        Assert.True(result.IsClean);
    }

    [Fact]
    public void FormatDiagnostic_CleanResult_ReportsPass()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64CleanFixture, "AArch64", "test.a");

        string diag = SleefFMACheck.FormatDiagnostic(result);
        Assert.Contains("PASS", diag);
        Assert.Contains("AArch64", diag);
        Assert.Contains("test.a", diag);
    }

    [Fact]
    public void FormatDiagnostic_DirtyResult_ReportsFailAndHits()
    {
        SleefFMACheck.ScanResult result =
            SleefFMACheck.ScanDisassembly(AArch64DirtyFixture_Multiple, "AArch64", "test.a");

        string diag = SleefFMACheck.FormatDiagnostic(result);
        Assert.Contains("FAIL", diag);
        Assert.Contains("fmla", diag);
        Assert.Contains("fmls", diag);
        Assert.Contains("count = 4", diag);
    }

    [Fact]
    public void ForbiddenMnemonicLists_CoverRev3M3MandatedSets()
    {
        // Per XCore-4a Rev 3 fix M3 / Section 17.3 C-extra, the AArch64
        // forbidden list MUST include {fmla, fmls, fnmla, fnmls, vfma,
        // vfnma} at minimum.
        Assert.Contains("fmla",  SleefFMACheck.ForbiddenAArch64Mnemonics);
        Assert.Contains("fmls",  SleefFMACheck.ForbiddenAArch64Mnemonics);
        Assert.Contains("fnmla", SleefFMACheck.ForbiddenAArch64Mnemonics);
        Assert.Contains("fnmls", SleefFMACheck.ForbiddenAArch64Mnemonics);
        Assert.Contains("vfma",  SleefFMACheck.ForbiddenAArch64Mnemonics);
        Assert.Contains("vfnma", SleefFMACheck.ForbiddenAArch64Mnemonics);

        // x86_64 list must cover {vfmadd*, vfmsub*, vfnmadd*, vfnmsub*}.
        Assert.Contains("vfmadd",  SleefFMACheck.ForbiddenX86_64MnemonicPrefixes);
        Assert.Contains("vfmsub",  SleefFMACheck.ForbiddenX86_64MnemonicPrefixes);
        Assert.Contains("vfnmadd", SleefFMACheck.ForbiddenX86_64MnemonicPrefixes);
        Assert.Contains("vfnmsub", SleefFMACheck.ForbiddenX86_64MnemonicPrefixes);
    }

    [Fact]
    public void ForbiddenInstructionExitCode_Is41()
    {
        // Toolchain Contract Rev 13 Section 13 exit code 41
        // (BannedApiOnSimPathTU family).  A SleefFMACheck hit IS a
        // SimPath-determinism-envelope failure; it shares the same
        // exit code as the broader ban-flag detection per the
        // ToolchainBannedFlagException class.
        Assert.Equal(41, SleefFMACheck.ForbiddenInstructionExitCode);
    }
}
