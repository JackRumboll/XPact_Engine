// Copyright Simgenics. All Rights Reserved.

using System;
using System.Text.Json.Serialization;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// One structured diagnostic record. Serialised as a single-line JSON
/// object onto the streaming JSON channel (per <c>/Documents/XBT.html</c>
/// Rev 4 Section 21.2) and rendered as a human-readable line on stderr.
/// </summary>
/// <remarks>
/// <para>
/// Field naming mirrors the example in Section 21.2 verbatim:
/// <c>{"action":"compile","module":"XScoring","level":"error",...}</c>.
/// The <see cref="System.Text.Json.JsonSerializer"/> configuration on
/// <see cref="Logger"/> applies camelCase to property names and to the
/// <see cref="DiagnosticLevel"/> enum.
/// </para>
/// <para>
/// Optional fields use <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/>
/// so a sparse record (e.g. just message+level) produces a small JSON
/// object rather than a wide object littered with explicit nulls. IDE
/// consumers parse this stream with strict JSON; omitted fields mean
/// "not applicable for this record".
/// </para>
/// <para>
/// The <see cref="ExitCode"/> is the Toolchain Contract Rev 13 Section 13
/// numeric (<c>0</c>, <c>10</c>, <c>21</c>, ...). Only error-level records
/// carry a non-null exit code; the <see cref="Logger"/> public surface
/// enforces this on <see cref="Logger.Emit"/>.
/// </para>
/// </remarks>
public sealed record DiagnosticRecord
{
    /// <summary>UTC time at which the diagnostic was emitted.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity bucket. Maps to the JSON channel <c>level</c> field.</summary>
    public required DiagnosticLevel Level { get; init; }

    /// <summary>Human-readable diagnostic text. Required.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// Action verb the diagnostic relates to: <c>"compile"</c>, <c>"link"</c>,
    /// <c>"manifest"</c>, <c>"validate"</c>, <c>"discover"</c>, <c>"hook"</c>,
    /// etc. Optional.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>Owning module's name (e.g. <c>"XCore"</c>). Optional.</summary>
    public string? Module { get; init; }

    /// <summary>
    /// Source file path. When the path lives under <c>/Engine/</c>,
    /// <c>/Studio/</c>, or <c>/Projects/</c> the writer canonicalises it
    /// to a repo-relative form before emit; otherwise the absolute path
    /// is recorded. Path separator is normalised to forward-slash for
    /// cross-platform reproducibility. Optional.
    /// </summary>
    public string? File { get; init; }

    /// <summary>Source line. Optional; 1-based.</summary>
    public int? Line { get; init; }

    /// <summary>Source column. Optional; 1-based.</summary>
    public int? Column { get; init; }

    /// <summary>
    /// Tier the diagnostic relates to: <c>"Engine"</c>, <c>"Studio"</c>,
    /// or <c>"Project"</c>. Optional.
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// True if the diagnostic originates from a sim-path translation unit
    /// per <c>/Documents/XToolchainContract.html</c> Section 4. Optional.
    /// </summary>
    [JsonPropertyName("simpath")]
    public bool? SimPath { get; init; }

    /// <summary>
    /// Toolchain Contract Rev 13 Section 13 exit code attached to error
    /// records. Must be null on non-error records;
    /// <see cref="Logger.Emit(DiagnosticRecord)"/> enforces.
    /// </summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// Diagnostic severity. Camel-cased on the JSON channel
/// (<c>"debug" / "info" / "warning" / "error"</c>) via the
/// <see cref="JsonStringEnumConverter"/> configured on the channel
/// writer.
/// </summary>
public enum DiagnosticLevel
{
    /// <summary>Verbose tracing.</summary>
    Debug,

    /// <summary>Informational progress messages.</summary>
    Info,

    /// <summary>Recoverable issue that should be visible.</summary>
    Warning,

    /// <summary>Build-blocking failure. Carries an exit code.</summary>
    Error,
}
