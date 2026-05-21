// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Manifest;

/// <summary>
/// Thrown when a manifest payload (XBT input or XHT-produced
/// <c>.gen.manifest</c>) fails validation. XHT.Entry maps this to exit
/// code <see cref="ExitCodes.ManifestMalformed"/> (50) per
/// <c>/Documents/XHT.html</c> Rev 6 Section 1.3.
/// </summary>
/// <remarks>
/// <para>
/// Covers JSON schema violations, FlatBuffers verifier rejection,
/// <c>ContractVersion</c> mismatches, both manifest forms missing,
/// module-not-in-manifest lookup failure (Rev 3 X-CR1 remap from 30),
/// and the <c>.gen.manifest</c> structural checks in
/// <see cref="GenManifestReader"/> (comma-in-path, hash-shape, section
/// ordering).
/// </para>
/// <para>
/// <see cref="DiagnosticCode"/> carries the specific XHT&lt;NNN&gt;
/// diagnostic code from <c>/Documents/XHT.html</c> Rev 6 Section 12.3
/// when the throw site is anchored to a catalog entry (e.g.
/// <c>"XHT002"</c> for <c>ContractVersion</c> mismatch per Section 23.2).
/// Throw sites that are not catalog-anchored may pass <c>null</c>; the
/// catch site in <c>XHT.Entry.Program</c> uses the carried code when
/// present and falls back to its generic manifest-malformed
/// diagnostic surface otherwise.
/// </para>
/// </remarks>
public sealed class ManifestMalformedException : Exception
{
    /// <summary>
    /// The Contract Section 13 exit code corresponding to this exception.
    /// Always <see cref="ExitCodes.ManifestMalformed"/> (50).
    /// </summary>
    public int ExitCode { get; } = ExitCodes.ManifestMalformed;

    /// <summary>
    /// The catalog-anchored XHT diagnostic code per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 12.3, or <c>null</c> when
    /// the throw site is not anchored to a single catalog entry. Examples:
    /// <c>"XHT001"</c> (manifest not found), <c>"XHT002"</c>
    /// (ContractVersion mismatch), <c>"XHT003"</c> (verifier limits),
    /// <c>"XHT004"</c> (module not in manifest).
    /// </summary>
    public string? DiagnosticCode { get; }

    /// <summary>
    /// Construct with a diagnostic message and no catalog-anchored
    /// diagnostic code.
    /// </summary>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    public ManifestMalformedException(string message)
        : base(message)
    {
        DiagnosticCode = null;
    }

    /// <summary>
    /// Construct with a diagnostic message and an inner exception
    /// (typically the JSON parser's <c>JsonException</c>).
    /// </summary>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    /// <param name="inner">The underlying exception that triggered this one.</param>
    public ManifestMalformedException(string message, Exception inner)
        : base(message, inner)
    {
        DiagnosticCode = null;
    }

    /// <summary>
    /// Construct with a catalog-anchored diagnostic code and a message.
    /// </summary>
    /// <param name="diagnosticCode">
    /// The XHT&lt;NNN&gt; code from <c>/Documents/XHT.html</c> Rev 6
    /// Section 12.3 (e.g. <c>"XHT002"</c> for ContractVersion mismatch).
    /// Must not be null / empty / whitespace.
    /// </param>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    /// <exception cref="ArgumentException">
    /// If <paramref name="diagnosticCode"/> is null / empty / whitespace.
    /// </exception>
    public ManifestMalformedException(string diagnosticCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>
    /// Construct with a catalog-anchored diagnostic code, a message, and an
    /// inner exception.
    /// </summary>
    /// <param name="diagnosticCode">
    /// The XHT&lt;NNN&gt; code from <c>/Documents/XHT.html</c> Rev 6
    /// Section 12.3. Must not be null / empty / whitespace.
    /// </param>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    /// <param name="inner">The underlying exception that triggered this one.</param>
    /// <exception cref="ArgumentException">
    /// If <paramref name="diagnosticCode"/> is null / empty / whitespace.
    /// </exception>
    public ManifestMalformedException(string diagnosticCode, string message, Exception inner)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }
}
