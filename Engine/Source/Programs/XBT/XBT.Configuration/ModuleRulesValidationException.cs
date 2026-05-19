// Copyright Simgenics. All Rights Reserved.

using System;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// A <see cref="ModuleRules"/> instance failed validation. Examples: a
/// SimPath module declared <see cref="Manifest.PCHUsageMode.UseSharedPCHs"/>;
/// an AVX SimdLevel on a SimPath module; <see cref="ModuleRules.Name"/>
/// empty.
/// </summary>
/// <remarks>
/// Default exit code is <strong>30</strong>
/// (<c>RulesCompileFailed</c> per Toolchain Contract Rev 13 Section 13).
/// SimPath-with-AVX surface the banned-flag exit code 41
/// (<c>BannedApiOnSimPathTU</c>) so the catching site can swap codes
/// when context demands; the exception's default is the catch-all
/// descriptor-validation code.
/// </remarks>
public sealed class ModuleRulesValidationException : XBTException
{
    /// <summary>Name of the offending module, or null when not yet bound.</summary>
    public string? ModuleName { get; }

    /// <summary>
    /// Construct with the default exit code 30
    /// (<c>RulesCompileFailed</c>).
    /// </summary>
    public ModuleRulesValidationException(string message, string? moduleName = null)
        : base(message, exitCode: 30)
    {
        ModuleName = moduleName;
    }

    /// <summary>
    /// Construct with an explicit Toolchain Contract Section 13 exit
    /// code (e.g. 41 for a banned-flag SimPath constraint).
    /// </summary>
    public ModuleRulesValidationException(string message, int exitCode, string? moduleName = null)
        : base(message, exitCode)
    {
        ModuleName = moduleName;
    }
}
