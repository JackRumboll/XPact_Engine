// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Tests.Tests.Frontend;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Normalization;

/// <summary>
/// Shared helpers for the Pass-2 (normalization) + Pass-3 (analysis)
/// foundation tests: build a minimal in-memory <see cref="Pass1Result"/>
/// from one or more synthetic C# source strings without touching the
/// filesystem, so the framework tests exercise the annotation layer +
/// reflection drivers against real Roslyn trees + semantic models.
/// </summary>
internal static class NormalizationTestHelpers
{
    /// <summary>
    /// Build a Pass-1 result over the supplied source strings (each becomes
    /// one parsed file named <c>Source{index}.cs</c>), bound against the
    /// deterministic test BCL set. Optionally flags the module as sim-path.
    /// </summary>
    /// <param name="isSimPath">True to set <see cref="Pass1Result.IsSimPath"/>.</param>
    /// <param name="sources">The C# source strings, in order.</param>
    /// <returns>A bound Pass-1 result.</returns>
    public static Pass1Result BuildPass1(bool isSimPath, params string[] sources)
    {
        CSharpParseOptions parseOptions = ParseOptionsFactory.Create(new List<string>());

        List<ModuleParser.ParsedFile> parsedFiles = new(sources.Length);
        List<SyntaxTree> trees = new(sources.Length);
        for (int i = 0; i < sources.Length; i++)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(sources[i]);
            ModuleParser.ParsedFile parsed = ModuleParser.ParseBytes(
                $"Source{i}.cs", bytes, parseOptions);
            parsedFiles.Add(parsed);
            trees.Add(parsed.Tree);
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "TestModule",
            syntaxTrees: trees,
            references: FrontendTestHelpers.BclReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return new Pass1Result(
            "TestModule",
            parsedFiles,
            compilation,
            new List<DiagnosticRecord>(),
            isSimPath: isSimPath);
    }

    /// <summary>
    /// Build a non-sim-path Pass-1 result over the supplied sources.
    /// </summary>
    public static Pass1Result BuildPass1(params string[] sources)
        => BuildPass1(isSimPath: false, sources);
}
