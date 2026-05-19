// Copyright Simgenics. All Rights Reserved.

using System;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Common base type for every XBT-thrown exception. Every other typed
/// exception in <c>XBT.Configuration</c>, <c>XBT.Discovery</c>,
/// <c>XBT.ActionGraph</c>, and <c>XBT.Toolchain</c> derives from this so
/// the top-level mode handler in <c>XBT.Entry</c> can catch a single base
/// type and translate to a contract exit code without losing the
/// originating subsystem's diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="ExitCode"/> property carries the Toolchain Contract
/// Rev 13 Section 13 numeric code that the catching site should report.
/// A subclass that does not have a single canonical exit code may leave
/// the property at its default (<c>1 = GenericFailure</c>); the
/// catching site should then translate via the more specific runtime
/// information it has.
/// </para>
/// <para>
/// Exception construction is intentionally minimal -- no logging side
/// effects, no JSON-channel emission. Logging is the responsibility of
/// the catching site (which has the full context to populate
/// <see cref="DiagnosticRecord"/> fields like <c>Action</c>,
/// <c>Module</c>, etc.). Throwing the exception is purely a control-flow
/// mechanism.
/// </para>
/// </remarks>
public class XBTException : Exception
{
    /// <summary>
    /// Toolchain Contract Rev 13 Section 13 exit code that should be
    /// reported when this exception escapes the top-level mode handler.
    /// Defaults to <c>1 = GenericFailure</c>; subclasses set their own
    /// canonical value.
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Construct an XBT exception with a generic exit code.
    /// </summary>
    /// <param name="message">Human-readable diagnostic text.</param>
    public XBTException(string message)
        : base(message)
    {
        ExitCode = 1;
    }

    /// <summary>
    /// Construct an XBT exception with an explicit exit code.
    /// </summary>
    /// <param name="message">Human-readable diagnostic text.</param>
    /// <param name="exitCode">Toolchain Contract Section 13 exit code.</param>
    public XBTException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>
    /// Construct an XBT exception with an inner cause and an explicit
    /// exit code.
    /// </summary>
    public XBTException(string message, int exitCode, Exception inner)
        : base(message, inner)
    {
        ExitCode = exitCode;
    }
}
