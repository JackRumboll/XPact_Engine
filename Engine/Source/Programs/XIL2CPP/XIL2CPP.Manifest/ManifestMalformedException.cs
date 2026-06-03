// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Manifest;

/// <summary>
/// Thrown when an XBT manifest payload fails validation. XIL2CPP.Entry
/// maps this to exit code <see cref="ExitCodes.ManifestMalformed"/> (50)
/// per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.7. Mirrors the
/// XHT.Manifest <c>ManifestMalformedException</c> discipline (the standalone
/// tools maintain parallel reader stacks; neither links the other's
/// manifest library at runtime).
/// </summary>
/// <remarks>
/// <para>
/// Covers JSON schema violations, FlatBuffers verifier rejection,
/// <c>ContractVersion</c> mismatches, both manifest forms missing, and
/// module-not-in-manifest lookup failure.
/// </para>
/// <para>
/// <see cref="DiagnosticCode"/> carries the specific
/// <c>XIL2CPP&lt;NNN&gt;</c> diagnostic code from
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12 when the throw site is
/// anchored to a catalog entry (e.g. <c>"XIL2CPP141"</c> for the manifest
/// schema-version gate per Section 9.7). It is <c>null</c> for the
/// reader-infrastructure failures (manifest-not-found, hardened-reader
/// limit rejection, ContractVersion mismatch, module-not-in-manifest) that
/// the Section 12 catalog does not allocate a dedicated code for &#8212; the
/// XIL2CPP catalog's low band (<c>000-009</c>) is reserved for Locked
/// Commitment 3 violations, so unlike XHT (which anchors XHT001-004 to
/// these reader-infra cases) XIL2CPP surfaces them un-anchored at exit 50.
/// </para>
/// <para>
/// The no-code constructors are <c>internal</c> so production callers
/// cannot construct an un-anchored exception unintentionally; the test
/// assembly (via <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
/// on <c>XIL2CPP.Manifest.csproj</c>) and the reader's own
/// infrastructure-failure throw sites are the only producers. This keeps
/// the XIL2CPP900 ICE branch in <c>XIL2CPP.Entry.Program</c> reachable for
/// regression-test coverage without exposing the un-anchored form to
/// future plugin assemblies. Mirrors Round 7 R6-XH1 on the XHT side.
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
    /// The catalog-anchored XIL2CPP diagnostic code per
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12, or <c>null</c> when
    /// the throw site is not anchored to a single catalog entry (the
    /// reader-infrastructure failures listed in the type remarks).
    /// Example anchored value: <c>"XIL2CPP141"</c> (manifest schema-version
    /// gate).
    /// </summary>
    public string? DiagnosticCode { get; }

    /// <summary>
    /// Construct with a diagnostic message and no catalog-anchored
    /// diagnostic code. This constructor is <c>internal</c> so production
    /// code outside the reader cannot construct an un-anchored exception.
    /// The XIL2CPP900 ICE branch in <c>XIL2CPP.Entry.Program</c> exists as
    /// a defence against a future code-side bug; the test assembly uses
    /// this constructor (via <c>InternalsVisibleTo</c>) to validate that
    /// the branch still emits the expected stderr text.
    /// </summary>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    internal ManifestMalformedException(string message)
        : base(message)
    {
        DiagnosticCode = null;
    }

    /// <summary>
    /// Construct with a diagnostic message and an inner exception
    /// (typically the JSON parser's <c>JsonException</c>). This constructor
    /// is <c>internal</c> for the same lockdown rationale as the no-inner
    /// overload.
    /// </summary>
    /// <param name="message">Diagnostic message describing the malformation.</param>
    /// <param name="inner">The underlying exception that triggered this one.</param>
    internal ManifestMalformedException(string message, Exception inner)
        : base(message, inner)
    {
        DiagnosticCode = null;
    }

    /// <summary>
    /// Construct with a catalog-anchored diagnostic code and a message.
    /// </summary>
    /// <param name="diagnosticCode">
    /// The <c>XIL2CPP&lt;NNN&gt;</c> code from
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12 (e.g.
    /// <c>"XIL2CPP141"</c> for the manifest schema-version gate). Must not
    /// be null / empty / whitespace.
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
    /// The <c>XIL2CPP&lt;NNN&gt;</c> code from
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12. Must not be null /
    /// empty / whitespace.
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
