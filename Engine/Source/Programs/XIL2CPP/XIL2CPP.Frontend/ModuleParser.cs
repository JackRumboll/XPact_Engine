// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Pass-1 parser: reads each C# source file as UTF-8
/// <see cref="SourceText"/> with the SHA-256 source-hash algorithm and
/// parses it to a <see cref="SyntaxTree"/> keyed by absolute path, per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 ("Pass 1 -- Roslyn
/// Parse + Bind"). The SourceText is created as embeddable (canBeEmbedded)
/// and with <see cref="SourceHashAlgorithm.Sha256"/> so the
/// reproducibility envelope (Section 9.9: deterministic build IDs /
/// portable PDB checksums) holds. Mirrors the XBT
/// <c>BuildCsCompiler.CompileFresh</c> SourceText construction discipline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Encoding.</b> Source files are read as raw bytes and decoded as
/// UTF-8. A leading UTF-8 BOM is tolerated (Roslyn's
/// <see cref="SourceText.From(byte[], int, Encoding, SourceHashAlgorithm, bool, bool)"/>
/// strips it). The encoding is pinned to UTF-8 (not the host default code
/// page) so the same file parses identically on Windows and Linux.
/// </para>
/// <para>
/// <b>Syntax diagnostics.</b> Each parsed tree's
/// <see cref="SyntaxTree.GetDiagnostics(System.Threading.CancellationToken)"/> set (lexer + parser
/// diagnostics -- e.g. CS1002 missing semicolon, CS1513 missing brace) is
/// surfaced on each <see cref="ParsedFile"/>. Semantic (binder) diagnostics
/// are NOT produced here; those come from the
/// <see cref="Microsoft.CodeAnalysis.CSharp.CSharpCompilation"/> in
/// <see cref="CompilationBuilder"/>.
/// </para>
/// </remarks>
public static class ModuleParser
{
    /// <summary>
    /// One parsed C# source file: its absolute path, the parsed
    /// <see cref="SyntaxTree"/>, and the file's syntax (lexer + parser)
    /// diagnostics.
    /// </summary>
    /// <param name="AbsolutePath">Absolute on-disk path the tree was parsed from.</param>
    /// <param name="Tree">The parsed Roslyn syntax tree (its <c>FilePath</c> equals <paramref name="AbsolutePath"/>).</param>
    /// <param name="SyntaxDiagnostics">Lexer + parser diagnostics for this file (empty when the file parses cleanly).</param>
    public sealed record ParsedFile(
        string AbsolutePath,
        SyntaxTree Tree,
        IReadOnlyList<Diagnostic> SyntaxDiagnostics);

    /// <summary>
    /// Read and parse a single C# source file from disk.
    /// </summary>
    /// <param name="absolutePath">Absolute path to the <c>.cs</c> file.</param>
    /// <param name="parseOptions">The deterministic parse options (from <see cref="ParseOptionsFactory"/>). Must not be null.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentException">If <paramref name="absolutePath"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="parseOptions"/> is null.</exception>
    /// <exception cref="IOException">If the file cannot be read (caller surfaces this as a Pass-1 read failure).</exception>
    public static ParsedFile ParseFile(string absolutePath, CSharpParseOptions parseOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentNullException.ThrowIfNull(parseOptions);

        byte[] bytes = File.ReadAllBytes(absolutePath);
        return ParseBytes(absolutePath, bytes, parseOptions);
    }

    /// <summary>
    /// Parse C# source from an in-memory byte buffer. Used by
    /// <see cref="ParseFile"/> and by tests that supply synthetic source
    /// without touching the filesystem.
    /// </summary>
    /// <param name="path">The path to associate with the tree (drives diagnostic <c>file</c>). Must not be null / empty / whitespace.</param>
    /// <param name="utf8Bytes">UTF-8 source bytes (a leading BOM is tolerated). Must not be null.</param>
    /// <param name="parseOptions">The deterministic parse options. Must not be null.</param>
    /// <returns>The parsed file.</returns>
    /// <exception cref="ArgumentException">If <paramref name="path"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="utf8Bytes"/> or <paramref name="parseOptions"/> is null.</exception>
    public static ParsedFile ParseBytes(string path, byte[] utf8Bytes, CSharpParseOptions parseOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(utf8Bytes);
        ArgumentNullException.ThrowIfNull(parseOptions);

        SourceText sourceText = SourceText.From(
            buffer: utf8Bytes,
            length: utf8Bytes.Length,
            encoding: Encoding.UTF8,
            checksumAlgorithm: SourceHashAlgorithm.Sha256,
            throwIfBinaryDetected: false,
            canBeEmbedded: true);

        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            text: sourceText,
            options: parseOptions,
            path: path);

        // GetDiagnostics() over the tree returns the lexer + parser
        // diagnostics. ImmutableArray -> a plain list snapshot so callers
        // own a concrete IReadOnlyList without re-walking the tree.
        List<Diagnostic> syntaxDiagnostics = new();
        foreach (Diagnostic d in tree.GetDiagnostics())
        {
            syntaxDiagnostics.Add(d);
        }

        return new ParsedFile(path, tree, syntaxDiagnostics);
    }

    /// <summary>
    /// Parse an ordered list of resolved sources. Files are parsed in the
    /// supplied order (which the caller has already canonicalised via
    /// <see cref="CSharpSourceSet.Resolve"/>); the returned list preserves
    /// that order so partial-class merging downstream is deterministic.
    /// </summary>
    /// <param name="sources">The ordinal-ordered resolved sources. Must not be null.</param>
    /// <param name="parseOptions">The deterministic parse options. Must not be null.</param>
    /// <returns>The parsed files, in input order.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="sources"/> or <paramref name="parseOptions"/> is null.</exception>
    /// <exception cref="IOException">If any file cannot be read (the path is carried on the exception's message; the driver maps it to a diagnostic).</exception>
    public static IReadOnlyList<ParsedFile> ParseAll(
        IReadOnlyList<CSharpSourceSet.ResolvedSource> sources,
        CSharpParseOptions parseOptions)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(parseOptions);

        List<ParsedFile> result = new(sources.Count);
        foreach (CSharpSourceSet.ResolvedSource source in sources)
        {
            result.Add(ParseFile(source.AbsolutePath, parseOptions));
        }

        return result;
    }
}
