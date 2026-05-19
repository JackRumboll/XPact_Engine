// Copyright Simgenics. All Rights Reserved.

using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// A <c>.Build.toml</c>, <c>.Build.expr</c>, or <c>.xplugin</c> file
/// could not be parsed (syntax error, unknown top-level key, bad enum
/// value, conflicting fields, etc).
/// </summary>
/// <remarks>
/// <para>
/// Default exit code is <strong>50</strong>
/// (<c>ManifestMalformed</c> per Toolchain Contract Rev 13 Section 13)
/// for plugin descriptors; module / target descriptors and
/// <c>.Build.expr</c> failures use exit code <strong>30</strong>
/// (<c>RulesCompileFailed</c>) per <c>/Documents/XBT.html</c>
/// Section 3.5. Callers select the appropriate code at throw time.
/// </para>
/// <para>
/// The <see cref="FilePath"/>, <see cref="Line"/>, <see cref="Column"/>
/// fields populate the standard MSBuild-format diagnostic prefix
/// emitted by the catching site.
/// </para>
/// </remarks>
public sealed class DescriptorParseException : XBTException
{
    /// <summary>Absolute path of the file that failed to parse.</summary>
    public string? FilePath { get; }

    /// <summary>1-based source line of the error, or null if unavailable.</summary>
    public int? Line { get; }

    /// <summary>1-based source column of the error, or null if unavailable.</summary>
    public int? Column { get; }

    /// <summary>
    /// Construct with the default exit code 30
    /// (<c>RulesCompileFailed</c>; matches the descriptor / expression
    /// failure path).
    /// </summary>
    public DescriptorParseException(
        string message,
        string? filePath = null,
        int? line = null,
        int? column = null)
        : base(message, exitCode: 30)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
    }

    /// <summary>
    /// Construct with an explicit exit code (e.g. 50 for plugin
    /// descriptors).
    /// </summary>
    public DescriptorParseException(
        string message,
        int exitCode,
        string? filePath = null,
        int? line = null,
        int? column = null)
        : base(message, exitCode)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
    }
}
