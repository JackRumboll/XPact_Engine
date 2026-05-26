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
/// against. The value <c>"13.8+d9514fb853e7dcc2"</c> is locked at
/// Contract Rev 13.8 (the XCore-4b Phase 4b.7 Stage B addendum that
/// freezes the reflection-type byte layouts via the
/// <c>XPACT_*_LAYOUT_TAG</c> macro family). Hash history:
/// <c>b04ae3cc84cdd9f3</c> covered Rev 13.2 / 13.3 / 13.4 / 13.5 /
/// 13.6 / 13.7 (all wording-only after the Round-5a rotation);
/// Rev 13.8 rotates the hash to <c>d9514fb853e7dcc2</c> because the
/// addendum adds the 15 <c>AbiLayoutTags</c> entries + the 14
/// <c>AbiTypeSizes</c> entries to <c>ContractSurface</c>
/// (see <c>XBT.Manifest/ContractSurface.cs</c>).
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
    /// Locked at Contract Rev 13.8 (XCore-4b Phase 4b.7 Stage B
    /// addendum); the hash <c>d9514fb853e7dcc2</c> reflects the
    /// addendum's contribution of the <c>AbiLayoutTags</c> +
    /// <c>AbiTypeSizes</c> tables to the canonical contract surface
    /// (see <c>XBT.Manifest/ContractSurface.cs</c>).
    /// </summary>
    public const string ContractVersion = "13.8+d9514fb853e7dcc2";

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
