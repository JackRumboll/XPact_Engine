// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;

namespace Simgenics.XPact.XHT.Manifest;

/// <summary>
/// Reader for the XHT-produced per-module <c>.gen.manifest</c> file per
/// <c>/Documents/XHT.html</c> Rev 6 Section 9.2.
/// </summary>
/// <remarks>
/// <para>
/// XBT's Phase 2 consumer side is expected to parse the manifest via a
/// similar reader; this implementation is the round-trip companion to
/// <see cref="GenManifestWriter"/>.
/// </para>
/// <para>
/// <b>Validation.</b> The reader enforces:
/// <list type="bullet">
///   <item><description>Section order Metadata -&gt; Inputs -&gt; Generated -&gt; Diagnostics -&gt; End.</description></item>
///   <item><description>16-char lowercase hex hashes in Inputs / Generated rows.</description></item>
///   <item><description>No commas in path values (per XHT.html Section 9.2 XHT005 rule).</description></item>
///   <item><description>Severity in <c>{error, warning, info}</c>.</description></item>
///   <item><description>Required metadata keys (<c>XhtSchemaVersion</c>, <c>ContractVersion</c>, <c>ModuleName</c>, <c>GeneratedAtUtc</c>).</description></item>
/// </list>
/// Any violation throws <see cref="ManifestMalformedException"/>.
/// </para>
/// </remarks>
public static class GenManifestReader
{
    /// <summary>
    /// Read and parse the <c>.gen.manifest</c> at <paramref name="srcPath"/>.
    /// </summary>
    /// <param name="srcPath">Absolute path to the file. Must not be null / empty / whitespace.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ManifestMalformedException">If the manifest fails any structural or semantic check.</exception>
    public static GenManifest Read(string srcPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcPath);

        if (!File.Exists(srcPath))
        {
            throw new ManifestMalformedException($"GenManifest not found: {srcPath}");
        }

        string text;
        try
        {
            text = File.ReadAllText(srcPath, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new ManifestMalformedException(
                $"Failed to read GenManifest at {srcPath}: {ex.Message}", ex);
        }

        return Parse(text);
    }

    /// <summary>
    /// Parse the manifest text directly. Convenience for round-trip
    /// tests via <see cref="GenManifestWriter.Render"/>.
    /// </summary>
    /// <param name="text">The manifest text. Must not be null.</param>
    /// <returns>The parsed manifest.</returns>
    /// <exception cref="ManifestMalformedException">If the text fails any structural or semantic check.</exception>
    public static GenManifest Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Split on LF; tolerate optional CR in case a tool re-saved the
        // file with CRLF (the writer only ever emits LF, but be liberal).
        string[] rawLines = text.Split('\n');

        int schemaVersion = -1;
        string? contractVersion = null;
        string? moduleName = null;
        string? generatedAtUtcIso = null;
        string? producedAtUtc = null;

        ImmutableArray<GenManifestEntry>.Builder inputsBuilder = ImmutableArray.CreateBuilder<GenManifestEntry>();
        ImmutableArray<GenManifestEntry>.Builder generatedBuilder = ImmutableArray.CreateBuilder<GenManifestEntry>();
        ImmutableArray<GenManifestDiagnostic>.Builder diagBuilder = ImmutableArray.CreateBuilder<GenManifestDiagnostic>();

        // Parser state machine. Expect sections in strict order.
        ParseState state = ParseState.Start;
        bool sawEnd = false;

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue; // blank line; skip
            }
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue; // comment line; skip
            }

            // Section header transitions.
            switch (line)
            {
                case "[Metadata]":
                    EnsureTransition(state, ParseState.Start, line);
                    state = ParseState.Metadata;
                    continue;
                case "[Inputs]":
                    EnsureTransition(state, ParseState.Metadata, line);
                    state = ParseState.Inputs;
                    continue;
                case "[Generated]":
                    EnsureTransition(state, ParseState.Inputs, line);
                    state = ParseState.Generated;
                    continue;
                case "[Diagnostics]":
                    EnsureTransition(state, ParseState.Generated, line);
                    state = ParseState.Diagnostics;
                    continue;
                case "[End]":
                    EnsureTransition(state, ParseState.Diagnostics, line);
                    state = ParseState.Done;
                    sawEnd = true;
                    continue;
            }

            switch (state)
            {
                case ParseState.Metadata:
                {
                    (string k, string v) = SplitKv(line, sectionLabel: "Metadata");
                    switch (k)
                    {
                        case "XhtSchemaVersion":
                            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out schemaVersion))
                            {
                                throw new ManifestMalformedException(
                                    $"GenManifest [Metadata] XhtSchemaVersion not an integer: '{v}'.");
                            }
                            break;
                        case "ContractVersion":
                            contractVersion = v;
                            break;
                        case "ModuleName":
                            moduleName = v;
                            break;
                        case "GeneratedAtUtc":
                            generatedAtUtcIso = v;
                            break;
                        case "ProducedAtUtcDeterministic":
                            producedAtUtc = v;
                            break;
                        default:
                            // Forward-compat: unknown keys are tolerated
                            // so a Phase 2 schema addition doesn't break
                            // older readers (matches Section 9.2 schema-
                            // versioning posture).
                            break;
                    }
                    break;
                }
                case ParseState.Inputs:
                {
                    GenManifestEntry e = ParseEntry(line, sectionLabel: "Inputs");
                    inputsBuilder.Add(e);
                    break;
                }
                case ParseState.Generated:
                {
                    GenManifestEntry e = ParseEntry(line, sectionLabel: "Generated");
                    generatedBuilder.Add(e);
                    break;
                }
                case ParseState.Diagnostics:
                {
                    GenManifestDiagnostic d = ParseDiagnostic(line);
                    diagBuilder.Add(d);
                    break;
                }
                case ParseState.Start:
                    throw new ManifestMalformedException(
                        $"GenManifest: content before [Metadata] section: '{line}'");
                case ParseState.Done:
                    throw new ManifestMalformedException(
                        $"GenManifest: content after [End] section: '{line}'");
            }
        }

        if (!sawEnd)
        {
            throw new ManifestMalformedException("GenManifest: missing [End] section marker.");
        }
        if (schemaVersion < 0)
        {
            throw new ManifestMalformedException(
                "GenManifest [Metadata] is missing required key 'XhtSchemaVersion'.");
        }
        if (contractVersion is null)
        {
            throw new ManifestMalformedException(
                "GenManifest [Metadata] is missing required key 'ContractVersion'.");
        }
        if (moduleName is null)
        {
            throw new ManifestMalformedException(
                "GenManifest [Metadata] is missing required key 'ModuleName'.");
        }
        if (generatedAtUtcIso is null)
        {
            throw new ManifestMalformedException(
                "GenManifest [Metadata] is missing required key 'GeneratedAtUtc'.");
        }
        // ProducedAtUtcDeterministic is required per the writer schema;
        // verify it's the literal "0".
        if (producedAtUtc != "0")
        {
            throw new ManifestMalformedException(
                $"GenManifest [Metadata] ProducedAtUtcDeterministic must be '0' (got '{producedAtUtc ?? "<missing>"}').");
        }

        return new GenManifest(
            XhtSchemaVersion: schemaVersion,
            ContractVersion: contractVersion,
            ModuleName: moduleName,
            GeneratedAtUtcIso: generatedAtUtcIso,
            Inputs: inputsBuilder.ToImmutable(),
            Generated: generatedBuilder.ToImmutable(),
            Diagnostics: diagBuilder.ToImmutable());
    }

    private static void EnsureTransition(ParseState current, ParseState expected, string sectionLine)
    {
        if (current != expected)
        {
            throw new ManifestMalformedException(
                $"GenManifest section order violated: saw '{sectionLine}' in state {current} (expected previous state {expected}).");
        }
    }

    private static (string key, string value) SplitKv(string line, string sectionLabel)
    {
        // Format: "Key = Value" (whitespace tolerated around =).
        int eq = line.IndexOf('=');
        if (eq < 0)
        {
            throw new ManifestMalformedException(
                $"GenManifest [{sectionLabel}] line not in 'Key = Value' form: '{line}'");
        }
        string key = line.AsSpan(0, eq).Trim().ToString();
        string value = line.AsSpan(eq + 1).Trim().ToString();
        if (key.Length == 0)
        {
            throw new ManifestMalformedException(
                $"GenManifest [{sectionLabel}] line has empty key: '{line}'");
        }
        return (key, value);
    }

    private static GenManifestEntry ParseEntry(string line, string sectionLabel)
    {
        // Format: "Path,Hash16".
        int comma = line.IndexOf(',');
        if (comma < 0)
        {
            throw new ManifestMalformedException(
                $"GenManifest [{sectionLabel}] line missing comma separator: '{line}'");
        }
        string path = line.AsSpan(0, comma).ToString();
        string hash = line.AsSpan(comma + 1).ToString();

        if (path.Length == 0)
        {
            throw new ManifestMalformedException(
                $"GenManifest [{sectionLabel}] entry has empty Path: '{line}'");
        }

        // Defence in depth: validate via the writer's rules. The writer
        // already rejects commas/newlines/bad hashes before write, but a
        // hand-edited manifest may have drift; surface it.
        if (path.Contains(','))
        {
            throw new ManifestMalformedException(
                $"XHT005: Source path '{path}' contains comma in [{sectionLabel}] section.");
        }
        if (hash.Length != 16)
        {
            throw new ManifestMalformedException(
                $"GenManifest [{sectionLabel}] entry for '{path}' has hash '{hash}' (must be 16 chars).");
        }
        for (int i = 0; i < 16; i++)
        {
            char c = hash[i];
            bool isDigit = c >= '0' && c <= '9';
            bool isLowerHex = c >= 'a' && c <= 'f';
            if (!isDigit && !isLowerHex)
            {
                throw new ManifestMalformedException(
                    $"GenManifest [{sectionLabel}] entry for '{path}' has invalid hex char '{c}' in hash '{hash}'.");
            }
        }

        return new GenManifestEntry(path, hash);
    }

    private static GenManifestDiagnostic ParseDiagnostic(string line)
    {
        // Format: "Severity,Code,File,Line,Column,Message".
        // Split into at most 6 fields; the message itself may contain
        // escape-encoded newlines but not raw commas (the writer
        // currently does not escape commas in the message, so we keep
        // the same split discipline here).
        string[] parts = line.Split(',', 6);
        if (parts.Length < 6)
        {
            throw new ManifestMalformedException(
                $"GenManifest [Diagnostics] line has fewer than 6 comma-separated fields: '{line}'");
        }
        string severity = parts[0];
        string code = parts[1];
        string fileField = parts[2];
        string lineField = parts[3];
        string colField = parts[4];
        string message = UnescapeMessage(parts[5]);

        if (severity != "error" && severity != "warning" && severity != "info")
        {
            throw new ManifestMalformedException(
                $"GenManifest [Diagnostics] entry has invalid Severity '{severity}'.");
        }
        if (code.Length == 0)
        {
            throw new ManifestMalformedException(
                $"GenManifest [Diagnostics] entry has empty Code: '{line}'");
        }

        string? filePath = fileField.Length == 0 ? null : fileField;
        int? lineNum = ParseOptInt(lineField, "Line");
        int? colNum = ParseOptInt(colField, "Column");

        return new GenManifestDiagnostic(
            Severity: severity,
            Code: code,
            File: filePath,
            Line: lineNum,
            Column: colNum,
            Message: message);
    }

    private static int? ParseOptInt(string s, string fieldName)
    {
        if (s.Length == 0)
        {
            return null;
        }
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
        {
            throw new ManifestMalformedException(
                $"GenManifest [Diagnostics] entry has non-integer {fieldName} '{s}'.");
        }
        return v;
    }

    private static string UnescapeMessage(string s)
    {
        if (s.Length == 0 || !s.Contains('\\'))
        {
            return s;
        }
        StringBuilder sb = new(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                switch (next)
                {
                    case 'n': sb.Append('\n'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                    // M15 audit: '\,' reverses the writer's
                    // EscapeMessage handling of literal commas in
                    // diagnostic messages.
                    case ',': sb.Append(','); i++; continue;
                    default: sb.Append(c); continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private enum ParseState
    {
        Start,
        Metadata,
        Inputs,
        Generated,
        Diagnostics,
        Done,
    }
}
