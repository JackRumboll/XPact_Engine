// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Core;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// The product of XIL2CPP Pass 1 (Roslyn Parse + Bind) for one module per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: the module name, the
/// parsed files, the constructed <see cref="CSharpCompilation"/>, a lazy
/// per-tree semantic-model accessor, the collected diagnostics, and the
/// has-errors flag the later passes (and the CLI mode) gate on.
/// </summary>
/// <remarks>
/// <para>
/// This is the substrate later sub-phases build on: Pass 2 (AST
/// normalization) walks <see cref="ParsedFiles"/>'s trees;
/// Pass 3 (semantic analysis) requests binding info through
/// <see cref="GetSemanticModel"/>. The <see cref="Compilation"/> and its
/// semantic models must not outlive this result (Section 3.4: no
/// cross-invocation workspace reuse).
/// </para>
/// </remarks>
public sealed class Pass1Result
{
    private readonly LazySemanticModel _semanticModels;

    /// <summary>
    /// Construct a Pass-1 result.
    /// </summary>
    /// <param name="moduleName">The module that was parsed + bound. Must not be null / empty / whitespace.</param>
    /// <param name="parsedFiles">The parsed C# files, in canonical order. Must not be null.</param>
    /// <param name="compilation">The constructed compilation. Must not be null.</param>
    /// <param name="diagnostics">
    /// All Pass-1 diagnostics (reference-resolution warnings + translated
    /// Roslyn syntax + binder diagnostics), in collection order. Must not be
    /// null.
    /// </param>
    /// <param name="isSimPath">
    /// True iff the module is a SimPath-determinism module (the manifest
    /// <c>sim_path</c> flag). Pass 3's sim-path banned-API + non-determinism
    /// analyzers gate on this per <c>/Documents/XIL2CPP.html</c> Rev 4
    /// Section 3.2 + 7.4. Defaults to false so existing Pass-1 call sites and
    /// tests that do not carry the flag keep their non-sim-path behaviour.
    /// </param>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="parsedFiles"/>, <paramref name="compilation"/>, or <paramref name="diagnostics"/> is null.</exception>
    public Pass1Result(
        string moduleName,
        IReadOnlyList<ModuleParser.ParsedFile> parsedFiles,
        CSharpCompilation compilation,
        IReadOnlyList<DiagnosticRecord> diagnostics,
        bool isSimPath = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(parsedFiles);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ModuleName = moduleName;
        ParsedFiles = parsedFiles;
        Compilation = compilation;
        Diagnostics = diagnostics;
        IsSimPath = isSimPath;
        _semanticModels = new LazySemanticModel(compilation);

        bool hasErrors = false;
        foreach (DiagnosticRecord d in diagnostics)
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                hasErrors = true;
                break;
            }
        }
        HasErrors = hasErrors;
    }

    /// <summary>The module that was parsed + bound.</summary>
    public string ModuleName { get; }

    /// <summary>The parsed C# files, in canonical (ordinal) order.</summary>
    public IReadOnlyList<ModuleParser.ParsedFile> ParsedFiles { get; }

    /// <summary>The Pass-1 compilation (parse trees + references + options).</summary>
    public CSharpCompilation Compilation { get; }

    /// <summary>
    /// All Pass-1 diagnostics, in collection order: reference-resolution
    /// warnings first, then the translated Roslyn syntax + binder
    /// diagnostics in compilation order.
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }

    /// <summary>
    /// True iff any diagnostic in <see cref="Diagnostics"/> is
    /// error-severity. The CLI mode gates its exit code on this.
    /// </summary>
    public bool HasErrors { get; }

    /// <summary>
    /// True iff the module is a SimPath-determinism module (the manifest
    /// <c>sim_path</c> flag). Pass 3's sim-path banned-API + non-determinism
    /// analyzers gate on this per <c>/Documents/XIL2CPP.html</c> Rev 4
    /// Section 3.2 + 7.4 (e.g., <c>XIL2CPP040</c> / <c>XIL2CPP044</c>).
    /// </summary>
    public bool IsSimPath { get; }

    /// <summary>
    /// Get (lazily, cached) the semantic model for one of the parsed trees.
    /// The tree must belong to <see cref="Compilation"/>.
    /// </summary>
    /// <param name="tree">A syntax tree from <see cref="ParsedFiles"/>. Must not be null.</param>
    /// <returns>The cached semantic model.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="tree"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="tree"/> is not part of the compilation.</exception>
    public SemanticModel GetSemanticModel(SyntaxTree tree) => _semanticModels.GetSemanticModel(tree);
}
