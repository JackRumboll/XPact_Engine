// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Simgenics.XPact.XIL2CPP.Core;

/// <summary>
/// Diagnostic severity for a <see cref="DiagnosticRecord"/>. Lower-cased on
/// the JSON channel via <see cref="DiagnosticRecord.WriteJson"/>.
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
/// object onto the streaming JSON channel per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12, mirroring the XHT /
/// XBT diagnostic record shape so XBT can aggregate channels from XBT,
/// XHT, and XIL2CPP into a single stream and log readers can filter by the
/// <c>tool</c> field.
/// </summary>
/// <param name="Severity">Severity bucket.</param>
/// <param name="Code">Per-diagnostic code in the form <c>XIL2CPP&lt;NNN&gt;</c> (decimal; see <see cref="DiagnosticCodes"/>).</param>
/// <param name="Message">Human-readable diagnostic text.</param>
/// <param name="File">Source file path. Null when the diagnostic is not anchored to a file.</param>
/// <param name="Line">Source line, 1-based. Null when not applicable.</param>
/// <param name="Column">Source column, 1-based. Null when not applicable.</param>
/// <param name="Module">Owning module name. Null when not applicable.</param>
/// <param name="Context">Optional free-form key/value enrichment (e.g., the candidate dependency module for an unresolved cross-module type). Null when not used.</param>
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
    /// Tool attribution for the JSON record. Always <c>"XIL2CPP"</c> on
    /// records this assembly emits; mirrors XHT's <c>"tool":"XHT"</c>
    /// attribution.
    /// </summary>
    public string Tool => "XIL2CPP";

    /// <summary>
    /// Write this record as a single-line JSON object to
    /// <paramref name="writer"/>. The caller flushes the stream; this
    /// method only writes and emits the trailing newline.
    /// </summary>
    /// <param name="writer">Destination stream writer. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void WriteJson(StreamWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Build the JSON record with a stable field order for diffing.
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

        byte[] bytes = ms.ToArray();
        string jsonLine = System.Text.Encoding.UTF8.GetString(bytes);
        writer.Write(jsonLine);
        writer.Write('\n');
    }

    /// <summary>
    /// Render this record in MSBuild diagnostic format per
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12:
    /// <c>&lt;file&gt;(&lt;line&gt;,&lt;column&gt;): &lt;severity&gt; XIL2CPP&lt;NNN&gt;: &lt;message&gt;</c>.
    /// Records without a file fall back to a leading-severity form
    /// (<c>&lt;severity&gt; XIL2CPP&lt;NNN&gt;: &lt;message&gt;</c>).
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
