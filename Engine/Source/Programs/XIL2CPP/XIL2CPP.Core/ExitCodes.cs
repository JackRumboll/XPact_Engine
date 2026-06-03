// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Core;

/// <summary>
/// Process exit codes XIL2CPP emits. Owned by the Toolchain Contract
/// Section 13 (cross-tool surface; codes 0-99 + 130 are the stable wire
/// format IDEs, CI tools, and build dashboards key on).
/// </summary>
/// <remarks>
/// <para>
/// Per <c>/Documents/XToolchainContract.html</c> Section 13.1 + 13.3,
/// XIL2CPP emits a strict subset of the contract's table:
/// <see cref="Success"/>, <see cref="GenericFailure"/>,
/// <see cref="CliArgumentError"/>, <see cref="ManifestMalformed"/>,
/// <see cref="Xil2CppInternalFailure"/> (the canonical XIL2CPP analysis /
/// emit failure code), and <see cref="Cancelled"/>. When XIL2CPP runs as
/// an XBT subprocess, XBT translates the child's 63 (or any failure) to
/// <see cref="Xil2CppSubprocessFailure"/> (61) for aggregation -- the
/// direct analog of the XHT 62 -&gt; 60 mapping.
/// </para>
/// <para>
/// <see cref="SimPathBannedApiOrManifestEnvelope"/> (41) is declared for
/// table completeness. Per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7
/// a manifest declaring an unsupported ABI envelope tag emits XIL2CPP140 at
/// exit 41; that path lands in Phase 6.b's manifest-envelope validation, so
/// Phase 6.a never returns it. The constant is named here so callers
/// comparing against Contract Section 13.1 have a single readable surface;
/// tests pin that Phase 6.a's emitted set excludes it.
/// </para>
/// </remarks>
public static class ExitCodes
{
    /// <summary>
    /// 0 -- Success. Owned by all tools per Contract Section 13.1.
    /// </summary>
    public const int Success = 0;

    /// <summary>
    /// 1 -- Generic failure (uncategorised). Owned by all tools per
    /// Contract Section 13.1. XIL2CPP emits this only as a last-resort
    /// fall-through when no more specific category applies.
    /// </summary>
    public const int GenericFailure = 1;

    /// <summary>
    /// 10 -- CLI argument error (mode or flag invalid). Owned by all
    /// tools per Contract Section 13.1. XIL2CPP emits this when
    /// <c>XIL2CPP.Entry</c>'s CLI parser rejects an unknown mode, an
    /// unknown flag, or a malformed flag value before the manifest has
    /// loaded.
    /// </summary>
    public const int CliArgumentError = 10;

    /// <summary>
    /// 41 -- SimPath banned-API check failure (Contract Section 4.4);
    /// reused by XIL2CPP for the manifest ABI-envelope-tag rejection
    /// (XIL2CPP140) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7.
    /// <strong>Phase 6.a does not emit this</strong> -- declared for
    /// Contract Section 13.1 readability; the envelope-validation path
    /// lands in Phase 6.b.
    /// </summary>
    public const int SimPathBannedApiOrManifestEnvelope = 41;

    /// <summary>
    /// 50 -- Manifest malformed / unreadable. Covers JSON schema
    /// violations, FlatBuffers verifier rejection, ContractVersion
    /// mismatches, both forms missing, and module-not-in-manifest lookup
    /// failure. Mirrors the XHT manifest-reader discipline. Per
    /// Contract Section 13.1 + <c>/Documents/XIL2CPP.html</c> Rev 4
    /// Section 9.7.
    /// </summary>
    public const int ManifestMalformed = 50;

    /// <summary>
    /// 61 -- XIL2CPP subprocess failure. The code XBT reports when the
    /// <c>xil2cpp</c> child process fails; XBT translates the child's 63
    /// (or any non-zero, non-categorised exit) to 61 for aggregation.
    /// Declared here for cross-tool readability; the XIL2CPP binary
    /// itself returns <see cref="Xil2CppInternalFailure"/> (63), not 61.
    /// Per Contract Section 13.1 + 13.3.
    /// </summary>
    public const int Xil2CppSubprocessFailure = 61;

    /// <summary>
    /// 63 -- XIL2CPP analysis (e.g., NoThrow proof) or emit failure.
    /// The canonical internal-failure code the XIL2CPP binary returns;
    /// XBT aggregates it to <see cref="Xil2CppSubprocessFailure"/> (61)
    /// when XIL2CPP runs as a subprocess, or surfaces 63 directly when
    /// XIL2CPP is invoked standalone (CI pre-flight, IDE). Per Contract
    /// Section 13.1 + 13.3.
    /// </summary>
    public const int Xil2CppInternalFailure = 63;

    /// <summary>
    /// 130 -- Cancelled by user (Ctrl-C). Matches POSIX SIGINT
    /// convention. Owned by all tools per Contract Section 13.1.
    /// </summary>
    public const int Cancelled = 130;
}
