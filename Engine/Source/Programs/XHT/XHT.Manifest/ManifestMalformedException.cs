// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Manifest;

/// <summary>
/// Thrown when a manifest payload (XBT input or XHT-produced
/// <c>.gen.manifest</c>) fails validation. XHT.Entry maps this to exit
/// code <see cref="ExitCodes.ManifestMalformed"/> (50) per
/// <c>/Documents/XHT.html</c> Rev 5 Section 1.3.
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
/// </remarks>
public sealed class ManifestMalformedException : Exception
{
    /// <summary>
    /// The Contract Section 13 exit code corresponding to this exception.
    /// Always <see cref="ExitCodes.ManifestMalformed"/> (50).
    /// </summary>
    public int ExitCode { get; } = ExitCodes.ManifestMalformed;

    /// <summary>
    /// Construct with a diagnostic message.
    /// </summary>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    public ManifestMalformedException(string message)
        : base(message)
    {
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
    }
}
