// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Frontend;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using XilMetadataReferenceResolver = Simgenics.XPact.XIL2CPP.Frontend.MetadataReferenceResolver;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend.Corpus;

/// <summary>
/// Drives a single corpus fixture through the real XIL2CPP Pass-1 parse +
/// bind path (<see cref="ModuleParser.ParseBytes"/> ->
/// <see cref="XilMetadataReferenceResolver.BuildFromReferences"/> ->
/// <see cref="CompilationBuilder.Build"/>) using the pinned .NET 8 BCL
/// reference set, exactly as <see cref="Pass1Driver"/> does for an on-disk
/// module. Centralised here so every corpus suite binds identically (the
/// determinism the X-IL2CPP-*-DET gates require).
/// </summary>
internal static class CorpusBinder
{
    /// <summary>The fixed module / assembly name every corpus fixture binds under.</summary>
    public const string ModuleName = "CorpusModule";

    /// <summary>
    /// The result of parsing + binding one fixture: the parsed file (syntax
    /// diagnostics) and the bound compilation.
    /// </summary>
    /// <param name="Parsed">The parsed file (carries lexer + parser diagnostics).</param>
    /// <param name="Compilation">The bound compilation.</param>
    public sealed record BindOutcome(
        ModuleParser.ParsedFile Parsed,
        CSharpCompilation Compilation);

    /// <summary>
    /// Parse + bind a fixture's source. Never throws for valid-or-invalid C#
    /// (parse + bind are total over arbitrary text); a throw here is itself
    /// the failure the banned-feature suite guards against.
    /// </summary>
    /// <param name="source">The fixture source. Must not be null.</param>
    /// <param name="path">The synthetic path to associate with the tree.</param>
    /// <returns>The parse + bind outcome.</returns>
    public static BindOutcome ParseAndBind(string source, string path = "/corpus/Feature.cs")
    {
        ArgumentNullException.ThrowIfNull(source);

        ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes(
            path,
            Encoding.UTF8.GetBytes(source),
            ParseOptionsFactory.Create());

        ReferenceSet refs = XilMetadataReferenceResolver.BuildFromReferences(
            FrontendTestHelpers.BclReferences(),
            Array.Empty<string>(),
            _ => null);

        CSharpCompilation compilation = CompilationBuilder.Build(
            ModuleName,
            new[] { parsed.Tree },
            refs);

        return new BindOutcome(parsed, compilation);
    }

    /// <summary>
    /// Collect the error-severity Roslyn diagnostics of a bound compilation
    /// (syntax + binder), in compilation order.
    /// </summary>
    /// <param name="compilation">The bound compilation. Must not be null.</param>
    /// <returns>The error diagnostics (empty when the bind is clean).</returns>
    public static IReadOnlyList<Diagnostic> BindErrors(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == RoslynSeverity.Error)
            .ToList();
    }
}
