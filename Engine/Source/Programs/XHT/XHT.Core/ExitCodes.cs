// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Process exit codes XHT emits. Owned by the Toolchain Contract Section 13
/// (cross-tool surface; codes 0-99 + 130 are the stable wire format that
/// IDEs, CI tools, and build dashboards key on).
/// </summary>
/// <remarks>
/// <para>
/// Per <c>/Documents/XHT.html</c> Rev 6 Section 1.3, XHT only emits a
/// strict subset of the contract's table: <see cref="Success"/>,
/// <see cref="GenericFailure"/>, <see cref="CliArgumentError"/>,
/// <see cref="ManifestMalformed"/>, <see cref="XhtInternalFailure"/>, and
/// <see cref="Cancelled"/>. Codes XHT does not emit (20-24, 30, 40, 41, 60,
/// 61, 70-71, 80-81, 90-99) are owned by XBT, XIL2CPP, or XLiveCoding per
/// the contract's per-tool emit discipline (Contract Section 13.3).
/// </para>
/// <para>
/// <see cref="DescriptorParseFailure"/> (30) is declared here for
/// completeness so callers comparing against the Contract Section 13.1
/// table have a named constant -- but XHT itself MUST NOT emit it. The
/// Rev 3 audit (X-CR1) remapped XHT's "module not in manifest" diagnostic
/// from 30 to 50 because 30 is XBT's <c>RulesCompileFailed</c> per
/// Contract Section 13.1. Including the constant here keeps the surface
/// readable; tests verify XHT never returns it.
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
    /// Contract Section 13.1. XHT emits this only as a last-resort
    /// fall-through when no more specific category applies.
    /// </summary>
    public const int GenericFailure = 1;

    /// <summary>
    /// 10 -- CLI argument error (mode or flag invalid). Owned by all
    /// tools per Contract Section 13.1. XHT emits this when
    /// <c>XHT.Entry</c>'s CLI parser rejects an unknown mode, an unknown
    /// flag, or a malformed flag value before <c>Main</c> has loaded the
    /// manifest. Per <c>/Documents/XHT.html</c> Rev 6 Section 1.3.
    /// </summary>
    public const int CliArgumentError = 10;

    /// <summary>
    /// 30 -- Descriptor / RulesAssembly compile failure. Owned by XBT
    /// per Contract Section 13.1. <strong>XHT MUST NOT emit this code.</strong>
    /// Declared here for table completeness; the Rev 3 audit (X-CR1)
    /// remapped XHT's "module not in manifest" diagnostic from 30 to 50
    /// because 30 belongs to XBT exclusively. Per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 1.3.
    /// </summary>
    public const int DescriptorParseFailure = 30;

    /// <summary>
    /// 50 -- Manifest malformed / unreadable. Covers JSON schema
    /// violations, FlatBuffers verifier rejection, ContractVersion
    /// mismatches, both forms missing, and module-not-in-manifest lookup
    /// failure (Rev 3 X-CR1 remap from 30). Per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 1.3 + Contract Section 13.1.
    /// </summary>
    public const int ManifestMalformed = 50;

    /// <summary>
    /// 62 -- XHT parse or emit internal failure. Owned by XHT per
    /// Contract Section 13.1. When XHT is invoked as a subprocess by XBT,
    /// XBT aggregates 62 into the surface exit 60
    /// (<c>XhtSubprocessFailure</c>); when XHT exits 62 standalone (CI
    /// pre-flight, IDE), the caller sees 62 directly. Per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 1.3.
    /// </summary>
    public const int XhtInternalFailure = 62;

    /// <summary>
    /// 130 -- Cancelled by user (Ctrl-C). Matches POSIX SIGINT convention.
    /// Owned by all tools per Contract Section 13.1. Per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 1.3.
    /// </summary>
    public const int Cancelled = 130;
}
