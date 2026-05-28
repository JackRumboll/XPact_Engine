// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Static version surface for XHT. The strings here are the human-readable
/// version banner XHT prints under <c>xht.exe version</c> per
/// <c>/Documents/XHT.html</c> Rev 8 Section 1.1 (CLI surface).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1b commitment.</b> <see cref="Semver"/> is hard-coded to
/// <c>"0.1.0+phase1b"</c> at this scaffold milestone. Phase 1c+ derives
/// the version from a git-tag injection in
/// <c>Directory.Build.targets</c> mirroring XBT's discipline
/// (XBT.html Section 1.1).
/// </para>
/// <para>
/// <b>ContractVersion pin.</b> <see cref="ContractVersion"/> is the
/// auto-derived Contract identifier XHT reads + writes manifests
/// against. Locked at Contract Rev 13.9 (the XCoreXObject Phase 5.a'
/// Contract prerequisite that micro-bumps the Stage B addendum: 3
/// existing reflection-type tags update content + version int
/// (<c>XPACT_FSTRUCT_LAYOUT_TAG</c> v4 -&gt; v5,
/// <c>XPACT_FSCRIPTSTRUCT_LAYOUT_TAG</c> v4 -&gt; v5,
/// <c>XPACT_FCLASS_LAYOUT_TAG</c> v4 -&gt; v6 with v5 skipped per
/// FIX-N-R2-1); 8 new XObject-side tags join the addendum
/// (XPACT_XOBJECT_LAYOUT_TAG, XPACT_XGC_CARDTABLE_LAYOUT_TAG,
/// XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG, XPACT_XOBJECTKEY_LAYOUT_TAG,
/// XPACT_XWEAKPTR_LAYOUT_TAG, XPACT_XPTR_LAYOUT_TAG,
/// XPACT_XOBJECT_LIFECYCLE_TABLE_TAG,
/// XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG); 7 new AbiTypeSizes rows for
/// the same types; FStruct +8 -&gt; 120, FClass +16 -&gt; 240,
/// FScriptStruct +8 -&gt; 136 cascade.
/// </para>
/// <para>
/// Hash history: <c>b04ae3cc84cdd9f3</c> covered Rev 13.2 / 13.3 /
/// 13.4 / 13.5 / 13.6 / 13.7 (all wording-only after the Round-5a
/// rotation); <c>d9514fb853e7dcc2</c> covered Rev 13.8 (Stage B
/// addendum adding the 15 AbiLayoutTags + 14 AbiTypeSizes entries
/// to ContractSurface); Rev 13.9 rotates the hash to a new value
/// derived deterministically by canonicalization (the live value is
/// logged by
/// <c>ContractVersionTests.ContractVersion_Current_LogsTheValueForDocAlignment</c>
/// per the established Rev 13.7 / Rev 13.8 cadence).
/// </para>
/// <para>
/// <b>Schema-mismatch detection.</b> This constant is the
/// compile-time pin <c>XbtManifestReader.ValidateContractVersion</c>
/// compares against when reading an XBT-emitted manifest. A
/// disagreement fires diagnostic <c>XHT002 -- ContractVersion mismatch</c>
/// at exit code 50 per Section 23.2; XHT must never silently consume a
/// manifest produced against a different contract surface.
/// </para>
/// </remarks>
public static class XhtVersion
{
    /// <summary>
    /// XHT's semantic version string per
    /// <c>/Documents/XHT.html</c> Rev 8 Section 1.1.
    /// Phase 1b: hard-coded; Phase 1c+: derived from git-tag injection
    /// via <c>Directory.Build.targets</c>.
    /// </summary>
    public const string Semver = "0.1.0+phase1b";

    /// <summary>
    /// The Contract version string XHT reads + writes manifests against.
    /// Locked at Contract Rev 13.9 (XCoreXObject Phase 5.a' Contract
    /// micro-bump prerequisite per XCoreXObject Rev 4 §11.2 / §11.3);
    /// the hash <c>381d8ef7a7770d9b</c> reflects the addendum's
    /// contribution of the 3 updated existing reflection-type tag
    /// contents + 3 updated existing AbiTypeSizes rows (FStruct 112
    /// -&gt; 120, FClass 224 -&gt; 240, FScriptStruct 128 -&gt; 136)
    /// + 8 new XObject-side layout tags + 7 new AbiTypeSizes rows on
    /// top of the Rev 13.8 baseline, via
    /// <c>Simgenics.XPact.XBT.Manifest.ContractVersion.ComputeStructureHash</c>
    /// canonicalization (see <c>XBT.Manifest/ContractSurface.cs</c>).
    /// </summary>
    /// <remarks>
    /// Rev 13.9 hash derived by running
    /// <c>ContractVersionTests.ContractVersion_Current_LogsTheValueForDocAlignment</c>
    /// after the Phase 5.a' Contract surface delta landed. The
    /// canonicalization algorithm is unchanged from Rev 13.8 (no
    /// <c>ContractVersion.cs</c> code edit was required); the
    /// additional canonical bytes from the 11 layout-tag additions
    /// (3 updated + 8 new) + 10 type-size additions (3 updated + 7
    /// new) rotated the BLAKE3 digest deterministically. Do NOT
    /// hand-rotate; further surface changes must be re-derived the
    /// same way.
    /// </remarks>
    public const string ContractVersion = "13.9+381d8ef7a7770d9b";

    /// <summary>
    /// The .NET runtime XHT is currently running on. Surfaced for
    /// diagnostic enrichment in the version banner and JSON channel
    /// startup record.
    /// </summary>
    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// Compose the multi-line <c>xht version</c> output. Used by
    /// <c>XHT.Entry</c>'s <c>version</c> mode handler.
    /// </summary>
    /// <returns>A multi-line string ending with a trailing newline.</returns>
    public static string GetVersionString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"XHT {Semver}\nContract {ContractVersion}\nRuntime {DotNetVersion}\n");
    }
}
