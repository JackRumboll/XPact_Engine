// Copyright Simgenics. All Rights Reserved.

using System.Globalization;
using System.Runtime.InteropServices;

namespace Simgenics.XPact.XIL2CPP.Core;

/// <summary>
/// Static version surface for XIL2CPP. The strings here are the
/// human-readable version banner XIL2CPP prints under
/// <c>xil2cpp version</c> per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 15 (CLI surface), mirroring the XHT <c>XhtVersion</c> pattern.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a commitment.</b> <see cref="Semver"/> is hard-coded to
/// <c>"0.1.0+phase6a"</c> at this scaffold milestone. A later sub-phase
/// derives the version from a git-tag injection in
/// <c>Directory.Build.targets</c> mirroring XHT / XBT discipline.
/// </para>
/// <para>
/// <b>ContractVersion pin.</b> <see cref="ContractVersion"/> is the
/// auto-derived Contract identifier XIL2CPP reads manifests against. It
/// MUST equal <c>Simgenics.XPact.XBT.Manifest.ContractVersion.Current</c>
/// and the XHT pin (<c>XhtVersion.ContractVersion</c>); a disagreement
/// fires the manifest-reader's ContractVersion mismatch diagnostic at
/// exit code 50 (Section 9.7), because XIL2CPP must never silently consume
/// a manifest produced against a different contract surface.
/// </para>
/// <para>
/// The value tracks the live Contract surface. The XBT slot-14
/// amendment (<c>ReferenceCompileCSharpAction</c>) landed at Phase 6.a
/// and rotated the auto-derived structure hash from the Rev 13.9 value;
/// this constant is re-pinned in lockstep with the XBT + XHT pins to the
/// re-derived Rev 13.10 value (the live value is logged by
/// <c>ContractVersionTests.ContractVersion_Current_LogsTheValueForDocAlignment</c>
/// per the established Rev-bump cadence). Do NOT hand-rotate.
/// </para>
/// </remarks>
public static class Xil2CppVersion
{
    /// <summary>
    /// XIL2CPP's semantic version string. Phase 6.a: hard-coded; a later
    /// sub-phase derives it from git-tag injection via
    /// <c>Directory.Build.targets</c>.
    /// </summary>
    public const string Semver = "0.1.0+phase6a";

    /// <summary>
    /// The Contract version string XIL2CPP reads manifests against.
    /// Locked to the live Contract surface (currently Rev 13.10, hash
    /// <c>bbcc0292b75e9a10</c>, matching the XBT + XHT pins). Re-pinned in
    /// lockstep whenever the auto-derived ContractSurface structure hash
    /// rotates -- see the remarks on <see cref="Xil2CppVersion"/>.
    /// </summary>
    public const string ContractVersion = "13.10+bbcc0292b75e9a10";

    /// <summary>
    /// The .NET runtime XIL2CPP is currently running on. Surfaced for
    /// diagnostic enrichment in the version banner and JSON channel
    /// startup record.
    /// </summary>
    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// Compose the multi-line <c>xil2cpp version</c> output. Used by
    /// <c>XIL2CPP.Entry</c>'s <c>version</c> mode handler.
    /// </summary>
    /// <returns>A multi-line string ending with a trailing newline.</returns>
    public static string GetVersionString()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"XIL2CPP {Semver}\nContract {ContractVersion}\nRuntime {DotNetVersion}\n");
    }
}
