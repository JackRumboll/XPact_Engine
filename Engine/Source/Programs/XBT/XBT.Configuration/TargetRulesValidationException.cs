// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// A <see cref="TargetRules"/> instance failed validation. Examples:
/// a Server target with a non-<see cref="Manifest.StationRole.None"/>
/// station role; a Test configuration with FIPS mode enabled.
/// </summary>
/// <remarks>
/// Default exit code is <strong>20</strong>
/// (<c>ConfigurationError</c> per Toolchain Contract Rev 13 Section 13).
/// </remarks>
public sealed class TargetRulesValidationException : XBTException
{
    /// <summary>Name of the offending target, or null when not yet bound.</summary>
    public string? TargetName { get; }

    /// <summary>
    /// Construct with the default exit code 20
    /// (<c>ConfigurationError</c>).
    /// </summary>
    public TargetRulesValidationException(string message, string? targetName = null)
        : base(message, exitCode: 20)
    {
        TargetName = targetName;
    }
}
