// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Entry;

/// <summary>
/// Thrown on malformed CLI input (an unknown / malformed flag value
/// rejected before mode dispatch, or a per-mode argument-parse failure).
/// The entry-point catch surface maps it to
/// <see cref="ExitCodes.CliArgumentError"/> (10). Mirrors XHT.Entry's
/// <c>CliArgumentException</c> discipline.
/// </summary>
/// <remarks>
/// <para>
/// In XHT this type lives alongside the per-mode option parser; XIL2CPP
/// Phase 6.a has no parse-module mode yet, so it is its own helper here
/// (the pre-pass <c>-JsonFd=</c> validation in <see cref="Program"/> is
/// the sole current throw site). Later sub-phases reuse it from the
/// transpile-mode option parser when that mode lands.
/// </para>
/// </remarks>
internal sealed class CliArgumentException : Exception
{
    /// <summary>Construct the exception with a diagnostic message.</summary>
    /// <param name="message">Human-readable diagnostic text.</param>
    public CliArgumentException(string message) : base(message) { }
}
