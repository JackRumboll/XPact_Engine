// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Loader for <c>.Build.expr</c> sidecar files. Per Toolchain Contract
/// Rev 13 Section 9.6 and <c>/Documents/XBT.html</c> Rev 4 Section 3.3,
/// a <c>.Build.expr</c> file sits alongside a <c>.Build.toml</c> and
/// supplies named Starlark expressions referenced from the TOML via the
/// <c>@expr:&lt;identifier&gt;</c> sentinel.
/// </summary>
/// <remarks>
/// <para>
/// <b>File format.</b> The <c>.Build.expr</c> is itself a TOML file
/// where every top-level value is a string holding a Starlark
/// expression source. Identifier names must match
/// <c>^[A-Za-z_][A-Za-z0-9_]*$</c>. Example:
/// </para>
/// <code>
/// # IndustrialEquipment/IndustrialEquipment.Build.expr
/// choose_simd        = "target.simd_level if target.simd_level != 'Default' else 'AVX2'"
/// enable_replication = "target.station_role == 'Engineer'"
/// windows_define     = "['X_PLATFORM_WIN=1'] if target.platform == 'Win64' else []"
/// </code>
/// <para>
/// <b>Strict-parse.</b> Any non-string value, any identifier failing
/// the regex check, and any TOML syntax error fails the load with a
/// <see cref="DescriptorParseException"/> (exit code 30 per
/// <c>/Documents/XBT.html</c> Section 3.5).
/// </para>
/// <para>
/// <b>Read-only.</b> The returned dictionary is read-only -- callers
/// must not assume they can mutate it. The keys preserve TOML
/// declaration order via the underlying <see cref="TomlTable"/>.
/// </para>
/// </remarks>
public static class BuildExprFile
{
    /// <summary>
    /// Standard <c>.Build.expr</c> filename suffix.
    /// </summary>
    public const string BuildExprSuffix = ".Build.expr";

    /// <summary>
    /// Identifier regex applied to every top-level key in the
    /// <c>.Build.expr</c> file. Mirrors the Starlark grammar's
    /// <c>identifier ::= [A-Za-z_][A-Za-z0-9_]*</c> production (Contract
    /// Section 9.6 BNF).
    /// </summary>
    public static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Load the <c>.Build.expr</c> at <paramref name="path"/>. Returns a
    /// read-only dictionary mapping identifier name to Starlark
    /// expression source.
    /// </summary>
    /// <param name="path">Absolute path of the file.</param>
    /// <exception cref="DescriptorParseException">
    /// Thrown on TOML syntax error, non-string value, or identifier
    /// that does not match <see cref="IdentifierPattern"/>. Exit
    /// code 30.
    /// </exception>
    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new DescriptorParseException(
                $"Could not read .Build.expr at {path}: {ex.Message}",
                filePath: path);
        }

        return Parse(text, path);
    }

    /// <summary>
    /// Parse a <c>.Build.expr</c> source string. Exposed for tests that
    /// don't want to touch disk. Identical semantics to
    /// <see cref="Load(string)"/> minus the file read.
    /// </summary>
    /// <param name="text">The TOML source text.</param>
    /// <param name="sourcePath">
    /// Optional source path used in diagnostics (Tomlyn embeds this in
    /// every <c>SourceSpan</c>).
    /// </param>
    public static IReadOnlyDictionary<string, string> Parse(string text, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        DocumentSyntax doc = Toml.Parse(text, sourcePath ?? string.Empty);
        if (doc.HasErrors)
        {
            DiagnosticMessage first = doc.Diagnostics.First(d => d.Kind == DiagnosticMessageKind.Error);
            throw new DescriptorParseException(
                $"TOML syntax error in .Build.expr: {first.Message}",
                sourcePath,
                line: first.Span.Start.Line + 1,
                column: first.Span.Start.Column + 1);
        }

        TomlTable model = doc.ToModel();

        // Empty file (or whitespace-only) -> empty dictionary.
        if (model.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> kv in model)
        {
            // Identifier validation.
            if (!IdentifierPattern.IsMatch(kv.Key))
            {
                throw new DescriptorParseException(
                    $"Invalid expression name '{kv.Key}' in .Build.expr. " +
                    "Identifiers must match the regex ^[A-Za-z_][A-Za-z0-9_]*$ " +
                    "(letters, digits, underscores; first char a letter or underscore).",
                    filePath: sourcePath);
            }

            // Every value must be a string.
            if (kv.Value is not string expression)
            {
                throw new DescriptorParseException(
                    $"Expression '{kv.Key}' in .Build.expr must be a string " +
                    $"(got {DescribeType(kv.Value)}). Every .Build.expr value is " +
                    "a Starlark expression source string.",
                    filePath: sourcePath);
            }

            // Duplicate keys are already a TOML syntax error -- Tomlyn
            // rejects them at parse time. The dictionary insert here is
            // therefore guaranteed to succeed.
            result.Add(kv.Key, expression);
        }
        return result;
    }

    private static string DescribeType(object? value) => value switch
    {
        null => "null",
        string => "string",
        bool => "boolean",
        long or int => "integer",
        double or float => "float",
        TomlArray => "array",
        TomlTable => "table",
        _ => value!.GetType().Name,
    };
}
