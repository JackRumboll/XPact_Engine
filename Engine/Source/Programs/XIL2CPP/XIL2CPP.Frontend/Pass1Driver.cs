// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Manifest;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Orchestrates XIL2CPP Pass 1 (Roslyn Parse + Bind) for one module per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: read the manifest ->
/// locate the module -> enumerate its C# sources in canonical order ->
/// parse each to a syntax tree -> resolve the reference set (BCL + per-
/// dependency-module reference DLLs) -> build the compilation -> collect
/// every diagnostic into a single ordered list.
/// </summary>
/// <remarks>
/// <para>
/// <b>No C++ emit.</b> This driver stops at the bound compilation. Passes 2
/// through 7 (normalization, semantic analysis, tier classification,
/// mangling, emit, write) are later sub-phases; Pass 1 is the substrate
/// they build on.
/// </para>
/// <para>
/// <b>BCL injection.</b> The driver does not itself decide where the BCL
/// reference assemblies come from -- product code passes the curated
/// <c>XPact.CSharp.BCL</c> reference DLLs; tests pass
/// <c>Basic.Reference.Assemblies.Net80</c>. Both flow in through
/// <see cref="Pass1Options.BclReferences"/>, keeping the
/// <c>Basic.Reference.Assemblies</c> dependency out of product code (gate
/// X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// <para>
/// <b>Diagnostic ordering.</b> The collected list is (1) reference-
/// resolution warnings (missing / unreadable <c>.refonly.dll</c>), then
/// (2) the translated Roslyn diagnostics in compilation order. Roslyn
/// orders <see cref="Compilation.GetDiagnostics(System.Threading.CancellationToken)"/>
/// deterministically (by tree, then by span), so two runs over identical
/// inputs produce an identical list -- the determinism the spec requires
/// (Section 9.9).
/// </para>
/// </remarks>
public static class Pass1Driver
{
    /// <summary>
    /// Inputs the driver needs beyond the manifest path + module name: the
    /// BCL reference set and the dependency reference-DLL search rule.
    /// </summary>
    /// <param name="BclReferences">
    /// The curated BCL reference set (product: <c>XPact.CSharp.BCL</c> ref
    /// DLLs; tests: <c>Basic.Reference.Assemblies.Net80</c>). Must not be
    /// null. Placed first in the compilation's reference list.
    /// </param>
    /// <param name="ReferenceRootForModule">
    /// Maps a dependency-module name to the absolute path of its reference
    /// directory (<c>Intermediate/.../&lt;Module&gt;/Reference/</c>), or null
    /// when unknown. Must not be null. See
    /// <see cref="MetadataReferenceResolver.ComposeReferenceRoot"/> for the
    /// canonical layout.
    /// </param>
    public sealed record Pass1Options(
        IReadOnlyList<MetadataReference> BclReferences,
        Func<string, string?> ReferenceRootForModule);

    /// <summary>
    /// Run Pass 1 for the module named <paramref name="moduleName"/> in the
    /// manifest at <paramref name="manifestPath"/>.
    /// </summary>
    /// <param name="manifestPath">Absolute path to XBT's <c>Manifest.json</c>. Must not be null / empty / whitespace.</param>
    /// <param name="moduleName">The module to transpile. Must not be null / empty / whitespace.</param>
    /// <param name="options">The BCL + dependency-reference inputs. Must not be null.</param>
    /// <returns>The Pass-1 result (compilation + diagnostics).</returns>
    /// <exception cref="ArgumentException">If <paramref name="manifestPath"/> or <paramref name="moduleName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="options"/> is null.</exception>
    /// <exception cref="ManifestMalformedException">If the manifest is malformed or the module is not present (caller maps to exit 50).</exception>
    public static Pass1Result Run(string manifestPath, string moduleName, Pass1Options options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(options);

        XbtManifest manifest = Xil2CppManifestReader.Read(manifestPath);
        // RequireModule throws ManifestMalformedException (exit 50) on a miss.
        XbtModule module = Xil2CppManifestReader.RequireModule(manifest, moduleName);

        return RunForModule(manifest, module, options);
    }

    /// <summary>
    /// Run Pass 1 for an already-resolved manifest + module. Used by
    /// <see cref="Run"/> and by tests that construct a synthetic manifest in
    /// memory.
    /// </summary>
    /// <param name="manifest">The parsed manifest. Must not be null.</param>
    /// <param name="module">The module to transpile (must belong to the manifest). Must not be null.</param>
    /// <param name="options">The BCL + dependency-reference inputs. Must not be null.</param>
    /// <returns>The Pass-1 result.</returns>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static Pass1Result RunForModule(XbtManifest manifest, XbtModule module, Pass1Options options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(options);

        List<DiagnosticRecord> diagnostics = new();

        // 1. Ordered C# sources (ordinal-sorted, absolute paths).
        IReadOnlyList<CSharpSourceSet.ResolvedSource> sources =
            CSharpSourceSet.Resolve(manifest, module);

        // 2. Parse options from the module's defines + conditional symbols.
        List<string> preprocessorSymbols = new(module.PublicDefines);
        if (module.ConditionalSymbols is not null)
        {
            preprocessorSymbols.AddRange(module.ConditionalSymbols);
        }
        CSharpParseOptions parseOptions = ParseOptionsFactory.Create(preprocessorSymbols);

        // 3. Parse each source. A read failure surfaces as an error
        // diagnostic and that file is omitted (the rest still parse).
        List<ModuleParser.ParsedFile> parsedFiles = new(sources.Count);
        List<SyntaxTree> trees = new(sources.Count);
        foreach (CSharpSourceSet.ResolvedSource source in sources)
        {
            ModuleParser.ParsedFile parsed;
            try
            {
                parsed = ModuleParser.ParseFile(source.AbsolutePath, parseOptions);
            }
            catch (IOException ex)
            {
                diagnostics.Add(SourceReadFailure(module.Name, source.AbsolutePath, ex.Message));
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                diagnostics.Add(SourceReadFailure(module.Name, source.AbsolutePath, ex.Message));
                continue;
            }

            parsedFiles.Add(parsed);
            trees.Add(parsed.Tree);
        }

        // 4. Resolve the reference set (BCL + dependency reference DLLs).
        IReadOnlyList<string> dependencyNames =
            Xil2CppManifestReader.GetDependencyModuleNames(module);
        ReferenceSet referenceSet = MetadataReferenceResolver.BuildFromReferences(
            options.BclReferences,
            dependencyNames,
            options.ReferenceRootForModule);

        // Reference-resolution diagnostics come first in the collected list.
        diagnostics.AddRange(referenceSet.Diagnostics);

        // 5. Build the compilation.
        CSharpCompilation compilation =
            CompilationBuilder.Build(module.Name, trees, referenceSet);

        // 6. Collect + translate the Roslyn diagnostics (syntax + binder).
        // Compilation.GetDiagnostics() returns both in deterministic order.
        IReadOnlyList<DiagnosticRecord> roslyn = RoslynDiagnosticTranslator.TranslateAll(
            compilation.GetDiagnostics(),
            module.Name,
            referenceSet);
        diagnostics.AddRange(roslyn);

        return new Pass1Result(module.Name, parsedFiles, compilation, diagnostics);
    }

    private static DiagnosticRecord SourceReadFailure(string moduleName, string path, string detail)
    {
        return new DiagnosticRecord(
            DiagnosticSeverity.Error,
            DiagnosticCodes.LoggerSentinel,
            string.Format(
                CultureInfo.InvariantCulture,
                "Failed to read C# source '{0}' for module '{1}': {2}.",
                path,
                moduleName,
                detail),
            File: path,
            Module: moduleName);
    }
}
