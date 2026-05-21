// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Diagnostic severity for an <see cref="DiagnosticRecord"/>. Camel-cased
/// on the JSON channel via <see cref="DiagnosticRecord.WriteJson"/>.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational progress message.</summary>
    Info,

    /// <summary>Recoverable issue; increments the warning counter.</summary>
    Warning,

    /// <summary>Build-blocking failure; increments the error counter.</summary>
    Error,
}

/// <summary>
/// One structured diagnostic record. Serialised as a single-line JSON
/// object onto the streaming JSON channel per <c>/Documents/XHT.html</c>
/// Rev 6 Section 1.4 + Section 12.4.
/// </summary>
/// <remarks>
/// <para>
/// Field naming mirrors the example in Section 12.4 verbatim:
/// <c>{"tool":"XHT","severity":"error","code":"XHT070","file":"...","line":42,"column":13,"message":"..."}</c>.
/// The writer always emits the <c>tool</c> field as the literal
/// <c>"XHT"</c> so log readers can filter by tool when XBT aggregates
/// channels from XBT, XHT, and XIL2CPP into a single stream.
/// </para>
/// <para>
/// Optional fields (<see cref="File"/>, <see cref="Line"/>,
/// <see cref="Column"/>, <see cref="Module"/>) are omitted from the JSON
/// when null. The <see cref="Context"/> dictionary, when non-null and
/// non-empty, is emitted as a nested object for free-form key/value
/// enrichment (used by token-range carry-through per XHT.html
/// Section 12.2).
/// </para>
/// </remarks>
/// <param name="Severity">Severity bucket.</param>
/// <param name="Code">Per-diagnostic code in the form <c>XHT&lt;NNN&gt;</c> (decimal; XHT001-XHT999 per XHT.html Section 12.3).</param>
/// <param name="Message">Human-readable diagnostic text.</param>
/// <param name="File">Source file path. Null when the diagnostic is not anchored to a file.</param>
/// <param name="Line">Source line, 1-based. Null when not applicable.</param>
/// <param name="Column">Source column, 1-based. Null when not applicable.</param>
/// <param name="Module">Owning module name. Null when not applicable.</param>
/// <param name="Context">Optional free-form key/value enrichment. Null when not used.</param>
public sealed record DiagnosticRecord(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File = null,
    int? Line = null,
    int? Column = null,
    string? Module = null,
    IReadOnlyDictionary<string, string>? Context = null)
{
    /// <summary>
    /// Tool attribution for the JSON record. Always <c>"XHT"</c> on
    /// records this assembly emits; mirrors XBT's <c>"tool":"XBT"</c>
    /// attribution.
    /// </summary>
    public string Tool => "XHT";

    /// <summary>
    /// Write this record as a single-line JSON object to
    /// <paramref name="writer"/>. The writer's stream is flushed by the
    /// caller; this method only writes and emits the trailing newline.
    /// </summary>
    /// <param name="writer">Destination stream writer. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void WriteJson(StreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Build the JSON record manually so the field order is stable
        // for diffing (System.Text.Json's source generator path would
        // also work but adds a generator step we don't need for one
        // small record type).
        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            w.WriteStartObject();
            w.WriteString("tool", Tool);
            w.WriteString("severity", SeverityToString(Severity));
            w.WriteString("code", Code);
            if (File is not null) { w.WriteString("file", File); }
            if (Line is int line) { w.WriteNumber("line", line); }
            if (Column is int col) { w.WriteNumber("column", col); }
            w.WriteString("message", Message);
            if (Module is not null) { w.WriteString("module", Module); }
            if (Context is { Count: > 0 } ctx)
            {
                w.WriteStartObject("context");
                foreach (KeyValuePair<string, string> kv in ctx)
                {
                    w.WriteString(kv.Key, kv.Value);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        // One line per record per XHT.html Section 1.4.
        byte[] bytes = ms.ToArray();
        string jsonLine = System.Text.Encoding.UTF8.GetString(bytes);
        writer.Write(jsonLine);
        writer.Write('\n');
    }

    /// <summary>
    /// Render this record in MSBuild diagnostic format per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 12.1:
    /// <c>&lt;file&gt;(&lt;line&gt;,&lt;column&gt;): &lt;severity&gt; XHT&lt;NNN&gt;: &lt;message&gt;</c>.
    /// Records without a file fall back to a leading-severity form
    /// (<c>&lt;severity&gt; XHT&lt;NNN&gt;: &lt;message&gt;</c>).
    /// </summary>
    public string FormatMsBuild()
    {
        string severity = SeverityToString(Severity);
        if (File is null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{severity} {Code}: {Message}");
        }

        string location = Line is int line
            ? Column is int col
                ? string.Create(CultureInfo.InvariantCulture, $"{File}({line},{col})")
                : string.Create(CultureInfo.InvariantCulture, $"{File}({line})")
            : File;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{location}: {severity} {Code}: {Message}");
    }

    private static string SeverityToString(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info => "info",
        _ => "info",
    };
}
