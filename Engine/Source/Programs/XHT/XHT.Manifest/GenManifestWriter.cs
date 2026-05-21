// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Manifest;

/// <summary>
/// One entry in the <c>[Inputs]</c> or <c>[Generated]</c> section of a
/// per-module <c>.gen.manifest</c> per <c>/Documents/XHT.html</c> Rev 8
/// Section 9.2.
/// </summary>
/// <param name="RelativePath">
/// Relative path to the file. Per XHT.html Section 9.2 (Xm4): MUST NOT
/// contain commas (the format reserves commas as list separators in some
/// sections); the writer rejects with diagnostic XHT005 before write.
/// </param>
/// <param name="ContentHash16">
/// 16-character lowercase hex prefix of the file's BLAKE3 content hash
/// per XHT.html Section 9.2 hash-format rule.
/// </param>
public sealed record GenManifestEntry(
    string RelativePath,
    string ContentHash16);

/// <summary>
/// One entry in the <c>[Diagnostics]</c> section of a per-module
/// <c>.gen.manifest</c>. Mirrors the MSBuild diagnostic surface per
/// XHT.html Section 12 so XBT can re-emit XHT diagnostics on its own
/// log without re-parsing.
/// </summary>
/// <param name="Severity">Either <c>"error"</c>, <c>"warning"</c>, or <c>"info"</c>.</param>
/// <param name="Code">Diagnostic code in the form <c>XHT&lt;NNN&gt;</c>.</param>
/// <param name="File">Source file path. Null when not file-anchored.</param>
/// <param name="Line">Source line, 1-based. Null when not applicable.</param>
/// <param name="Column">Source column, 1-based. Null when not applicable.</param>
/// <param name="Message">Human-readable diagnostic text.</param>
public sealed record GenManifestDiagnostic(
    string Severity,
    string Code,
    string? File,
    int? Line,
    int? Column,
    string Message);

/// <summary>
/// The XHT-produced per-module <c>.gen.manifest</c> record. XBT consumes
/// this file as XHT's opaque output surface per
/// <c>/Documents/XHT.html</c> Rev 8 Section 9.2 + Contract Section 7.1.
/// </summary>
/// <param name="XhtSchemaVersion">Schema version (currently <c>1</c>).</param>
/// <param name="ContractVersion">Contract version XHT was built against.</param>
/// <param name="ModuleName">The module this manifest belongs to.</param>
/// <param name="GeneratedAtUtcIso">
/// Informational UTC timestamp in ISO-8601 form. <strong>Not</strong> part
/// of any hash input per XHT.html Section 14.2; carried for human
/// readability. The schema also emits a <c>ProducedAtUtcDeterministic = 0</c>
/// line as the deterministic surface.
/// </param>
/// <param name="Inputs">Input source files that contributed to the module's emit.</param>
/// <param name="Generated">Generated output files XHT produced for the module.</param>
/// <param name="Diagnostics">Diagnostics emitted during the module's emit.</param>
public sealed record GenManifest(
    int XhtSchemaVersion,
    string ContractVersion,
    string ModuleName,
    string GeneratedAtUtcIso,
    ImmutableArray<GenManifestEntry> Inputs,
    ImmutableArray<GenManifestEntry> Generated,
    ImmutableArray<GenManifestDiagnostic> Diagnostics);

/// <summary>
/// Writer for the XHT-produced per-module <c>.gen.manifest</c> file per
/// <c>/Documents/XHT.html</c> Rev 8 Section 9.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism.</b> Per XHT.html Section 14: two clean XHT runs over
/// the same input MUST produce byte-identical output. The writer sorts
/// Inputs / Generated / Diagnostics deterministically
/// (<see cref="StringComparer.Ordinal"/>) and emits LF-only line endings.
/// The <c>GeneratedAtUtc</c> field is informational; the
/// <c>ProducedAtUtcDeterministic</c> field is always <c>0</c> for
/// byte-identical reproducibility.
/// </para>
/// <para>
/// <b>Validation.</b> The writer rejects entries whose
/// <see cref="GenManifestEntry.RelativePath"/> contains a comma (per
/// XHT.html Section 9.2 Mi4 / XHT005 rule) and entries whose
/// <see cref="GenManifestEntry.ContentHash16"/> is not exactly 16
/// lowercase hex characters. Violations throw
/// <see cref="ManifestMalformedException"/> before any bytes hit disk.
/// </para>
/// <para>
/// <b>Atomic write.</b> Writes go through
/// <see cref="AtomicFile.WriteAllText"/> so an external observer never
/// sees a torn output. Per XHT.html Section 8.5 + Section 14.
/// </para>
/// </remarks>
public static class GenManifestWriter
{
    /// <summary>The fixed schema version XHT currently emits.</summary>
    public const int CurrentSchemaVersion = 1;

    private const string SectionMetadata = "[Metadata]";
    private const string SectionInputs = "[Inputs]";
    private const string SectionGenerated = "[Generated]";
    private const string SectionDiagnostics = "[Diagnostics]";
    private const string SectionEnd = "[End]";

    /// <summary>
    /// Atomically write <paramref name="m"/> to <paramref name="destPath"/>.
    /// </summary>
    /// <param name="m">The manifest to write. Must not be null.</param>
    /// <param name="destPath">Absolute path to the output file. Must not be null / empty / whitespace.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="m"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="destPath"/> is null / empty / whitespace.</exception>
    /// <exception cref="ManifestMalformedException">
    /// If any entry violates the path / hash constraints. Mapped to
    /// exit code 50 by XHT.Entry.
    /// </exception>
    public static void Write(GenManifest m, string destPath)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        string content = Render(m);
        AtomicFile.WriteAllText(destPath, content);
    }

    /// <summary>
    /// Render a <see cref="GenManifest"/> to its canonical text form
    /// without writing to disk. Useful for tests + diff tooling.
    /// </summary>
    /// <param name="m">The manifest to render. Must not be null.</param>
    /// <returns>The rendered text (LF line endings).</returns>
    /// <exception cref="ManifestMalformedException">
    /// If any entry violates the path / hash constraints.
    /// </exception>
    public static string Render(GenManifest m)
    {
        ArgumentNullException.ThrowIfNull(m);

        if (m.XhtSchemaVersion != CurrentSchemaVersion)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest.XhtSchemaVersion must be {CurrentSchemaVersion} (got {m.XhtSchemaVersion}).");
        }
        ValidateString(m.ContractVersion, nameof(m.ContractVersion));
        ValidateString(m.ModuleName, nameof(m.ModuleName));
        ValidateString(m.GeneratedAtUtcIso, nameof(m.GeneratedAtUtcIso));

        // Sort deterministically. Per XHT.html Section 14.1: every
        // collection emit uses sorted iteration. StringComparer.Ordinal
        // throughout; no culture-aware comparisons.
        ImmutableArray<GenManifestEntry> sortedInputs = SortEntries(m.Inputs);
        ImmutableArray<GenManifestEntry> sortedGenerated = SortEntries(m.Generated);
        ImmutableArray<GenManifestDiagnostic> sortedDiagnostics = SortDiagnostics(m.Diagnostics);

        StringBuilder sb = new(capacity: 1024);

        // Use LF-only line endings throughout per Section 14 byte-identical
        // output discipline; explicit "\n" rather than Environment.NewLine
        // (which is "\r\n" on Windows).
        AppendLine(sb, SectionMetadata);
        AppendKv(sb, "XhtSchemaVersion", m.XhtSchemaVersion.ToString(CultureInfo.InvariantCulture));
        AppendKv(sb, "ContractVersion", m.ContractVersion);
        AppendKv(sb, "ModuleName", m.ModuleName);
        AppendKv(sb, "ProducedAtUtcDeterministic", "0");
        AppendKv(sb, "GeneratedAtUtc", m.GeneratedAtUtcIso);
        AppendLine(sb, string.Empty);

        AppendLine(sb, SectionInputs);
        AppendLine(sb, "# Path,ContentHash16");
        foreach (GenManifestEntry e in sortedInputs)
        {
            ValidateEntry(e, sectionLabel: "Inputs");
            AppendLine(sb, $"{e.RelativePath},{e.ContentHash16}");
        }
        AppendLine(sb, string.Empty);

        AppendLine(sb, SectionGenerated);
        AppendLine(sb, "# Path,ContentHash16");
        foreach (GenManifestEntry e in sortedGenerated)
        {
            ValidateEntry(e, sectionLabel: "Generated");
            AppendLine(sb, $"{e.RelativePath},{e.ContentHash16}");
        }
        AppendLine(sb, string.Empty);

        AppendLine(sb, SectionDiagnostics);
        AppendLine(sb, "# Severity,Code,File,Line,Column,Message");
        foreach (GenManifestDiagnostic d in sortedDiagnostics)
        {
            ValidateDiagnostic(d);
            string fileField = d.File ?? string.Empty;
            string lineField = d.Line is int ln ? ln.ToString(CultureInfo.InvariantCulture) : string.Empty;
            string colField = d.Column is int co ? co.ToString(CultureInfo.InvariantCulture) : string.Empty;
            string escapedMsg = EscapeMessage(d.Message);
            AppendLine(sb, $"{d.Severity},{d.Code},{fileField},{lineField},{colField},{escapedMsg}");
        }
        AppendLine(sb, string.Empty);

        AppendLine(sb, SectionEnd);

        return sb.ToString();
    }

    private static ImmutableArray<GenManifestEntry> SortEntries(ImmutableArray<GenManifestEntry> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return ImmutableArray<GenManifestEntry>.Empty;
        }
        // Sort by RelativePath Ordinal; ties broken by ContentHash16 for
        // total determinism (in practice no two entries share both fields).
        List<GenManifestEntry> sorted = new(entries);
        sorted.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath);
            return cmp != 0
                ? cmp
                : StringComparer.Ordinal.Compare(a.ContentHash16, b.ContentHash16);
        });
        return sorted.ToImmutableArray();
    }

    private static ImmutableArray<GenManifestDiagnostic> SortDiagnostics(ImmutableArray<GenManifestDiagnostic> diags)
    {
        if (diags.IsDefaultOrEmpty)
        {
            return ImmutableArray<GenManifestDiagnostic>.Empty;
        }
        // Sort by (File, Line, Column, Code, Message) Ordinal. Per
        // XHT.html Section 14.1 every collection emit is sorted; we
        // pick a lexicographic key that lets a developer reading the
        // manifest find the diagnostic they're looking for by file
        // location.
        List<GenManifestDiagnostic> sorted = new(diags);
        sorted.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.File ?? string.Empty, b.File ?? string.Empty);
            if (cmp != 0) { return cmp; }
            cmp = (a.Line ?? 0).CompareTo(b.Line ?? 0);
            if (cmp != 0) { return cmp; }
            cmp = (a.Column ?? 0).CompareTo(b.Column ?? 0);
            if (cmp != 0) { return cmp; }
            cmp = StringComparer.Ordinal.Compare(a.Code, b.Code);
            if (cmp != 0) { return cmp; }
            return StringComparer.Ordinal.Compare(a.Message, b.Message);
        });
        return sorted.ToImmutableArray();
    }

    private static void ValidateEntry(GenManifestEntry entry, string sectionLabel)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.RelativePath))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest [{sectionLabel}] entry has empty RelativePath.");
        }
        // Per XHT.html Section 9.2 Mi4 / XHT005 rule: commas in paths
        // are reserved as the list separator.
        if (entry.RelativePath.Contains(','))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestCommaInPath,
                message: $"Source path '{entry.RelativePath}' contains comma; the .gen.manifest [{sectionLabel}] section format reserves commas as separators.");
        }
        // Newlines would break the line-oriented format.
        if (entry.RelativePath.Contains('\n') || entry.RelativePath.Contains('\r'))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest [{sectionLabel}] entry RelativePath '{entry.RelativePath}' contains newline.");
        }

        if (!IsValidHash16(entry.ContentHash16))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest [{sectionLabel}] entry for '{entry.RelativePath}' has invalid ContentHash16 '{entry.ContentHash16}' (must be 16 lowercase hex chars).");
        }
    }

    private static void ValidateDiagnostic(GenManifestDiagnostic d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrEmpty(d.Severity))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: "GenManifest [Diagnostics] entry has empty Severity.");
        }
        if (d.Severity != "error" && d.Severity != "warning" && d.Severity != "info")
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest [Diagnostics] entry has invalid Severity '{d.Severity}' (must be error/warning/info).");
        }
        if (string.IsNullOrEmpty(d.Code))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: "GenManifest [Diagnostics] entry has empty Code.");
        }
        if (d.Code.Contains(','))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest [Diagnostics] entry has Code '{d.Code}' containing comma.");
        }
        if (d.File is not null && d.File.Contains(','))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest [Diagnostics] entry File '{d.File}' contains comma.");
        }
        if (d.Message is null)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: "GenManifest [Diagnostics] entry Message is null.");
        }
    }

    private static bool IsValidHash16(string s)
    {
        if (s is null || s.Length != 16)
        {
            return false;
        }
        for (int i = 0; i < 16; i++)
        {
            char c = s[i];
            bool isDigit = c >= '0' && c <= '9';
            bool isLowerHex = c >= 'a' && c <= 'f';
            if (!isDigit && !isLowerHex)
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateString(string value, string context)
    {
        if (value is null)
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest.{context} is null (required).");
        }
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ManifestMalformedException(
                diagnosticCode: DiagnosticCodes.GenManifestStructural,
                message: $"GenManifest.{context} contains a newline character; metadata values must be single-line.");
        }
    }

    /// <summary>
    /// Escape characters that would break the line-oriented manifest
    /// format: carriage returns, newlines, embedded backslashes, AND
    /// commas (per M15 audit -- commas are the [Diagnostics] section
    /// field separator). The escape uses <c>"\\"</c> (double backslash)
    /// for literal backslash, <c>"\n"</c> / <c>"\r"</c> for newlines,
    /// and <c>"\,"</c> for embedded commas. The reader reverses every
    /// escape on read so byte-identical round-trip holds.
    /// </summary>
    private static string EscapeMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }
        if (!message.Contains('\n')
            && !message.Contains('\r')
            && !message.Contains('\\')
            && !message.Contains(','))
        {
            return message;
        }
        StringBuilder sb = new(message.Length + 8);
        foreach (char c in message)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case ',':  sb.Append("\\,"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string line)
    {
        sb.Append(line);
        sb.Append('\n');
    }

    private static void AppendKv(StringBuilder sb, string key, string value)
    {
        sb.Append(key);
        sb.Append(" = ");
        sb.Append(value);
        sb.Append('\n');
    }
}
