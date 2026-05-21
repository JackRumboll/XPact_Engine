// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Static version surface for XHT. The strings here are the human-readable
/// version banner XHT prints under <c>xht.exe version</c> per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.1 (CLI surface).
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
/// against. The value <c>"13.2+b04ae3cc84cdd9f3"</c> is locked at
/// <c>/Documents/XHT.html</c> Rev 5 Section 0 (matches Contract Rev 13.6
/// + Addendum Revision 5; the structure hash <c>b04ae3cc84cdd9f3</c>
/// is unchanged across Rev 13.2 / 13.3 / 13.4 / 13.5 / 13.6).
/// </para>
/// </remarks>
public static class XhtVersion
{
    /// <summary>
    /// XHT's semantic version string per
    /// <c>/Documents/XHT.html</c> Rev 5 Section 1.1.
    /// Phase 1b: hard-coded; Phase 1c+: derived from git-tag injection
    /// via <c>Directory.Build.targets</c>.
    /// </summary>
    public const string Semver = "0.1.0+phase1b";

    /// <summary>
    /// The Contract version string XHT reads + writes manifests against.
    /// Locked at <c>/Documents/XHT.html</c> Rev 5 Section 0 and at
    /// Contract Rev 13.6.
    /// </summary>
    public const string ContractVersion = "13.2+b04ae3cc84cdd9f3";

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
