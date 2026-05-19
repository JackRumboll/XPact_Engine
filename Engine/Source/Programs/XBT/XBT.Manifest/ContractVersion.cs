// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XBT.Manifest;

/// <summary>
/// Contract-version constant per Toolchain Contract Rev 13 Section 10.2.
/// </summary>
/// <remarks>
/// <para>
/// The Contract specifies that <c>ContractVersion</c> be auto-derived
/// from a BLAKE3 hash of the contract surface (see Section 10.2 "Contract
/// auto-derivation" -- the hash inputs are: the manifest JSON schema
/// field set, the marker macro list, the body-macro suffix list, the
/// mangling-rule grammar, the <c>XGCRootSpan</c> struct field list, the
/// <c>XResult</c> struct field list, the exit-code table, and the
/// cross-tier dep matrix).
/// </para>
/// <para>
/// The auto-derivation pipeline is Phase 1.1 work -- it requires a
/// canonicalization pass over each of those surfaces, which lives in a
/// later XBT module not yet built. Phase 1 ships with a manual
/// placeholder; the derivation lands when the auto-derive pass arrives.
/// </para>
/// <para>
/// When the auto-derivation pass arrives it will overwrite
/// <see cref="Current"/> with the format <c>"v1" + first-ten-hex-of-hash
/// + "_" + semantic-tag</c> per the Contract. Symbol mangling
/// (XIL2CPP), reflection metadata (XHT), and ActionHistory cache keys
/// (XBT.ActionGraph) all carry this string and the auto-derivation
/// guarantees a Contract revision that touches any of the listed
/// surfaces automatically bumps the value.
/// </para>
/// </remarks>
public static class ContractVersion
{
    /// <summary>
    /// Current contract version. Phase 1 placeholder pinned to
    /// Toolchain Contract Rev 13's semantic tag.
    /// </summary>
    /// <remarks>
    /// TODO Phase 1.1: replace with auto-derived value per Toolchain
    /// Contract Section 10.2 "ContractVersion auto-derivation". The
    /// auto-derive pass canonicalizes the contract surface and BLAKE3s
    /// it; the result is baked into XBT.Manifest's constants at the
    /// XBT build step.
    /// </remarks>
    public const string Current = "v13-stub";
}
