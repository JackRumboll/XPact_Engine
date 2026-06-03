// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Builds the Pass-1 <see cref="CSharpCompilation"/> from the parsed syntax
/// trees and the resolved <see cref="ReferenceSet"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 ("Pass 1 -- Roslyn
/// Parse + Bind"). Mirrors the XBT <c>BuildCsCompiler</c>'s
/// <see cref="CSharpCompilation.Create(string, IEnumerable{SyntaxTree}, IEnumerable{MetadataReference}, CSharpCompilationOptions)"/>
/// precedent with XIL2CPP's deterministic option set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compilation options.</b>
/// <list type="bullet">
///   <item><description>
///     <see cref="OutputKind.DynamicallyLinkedLibrary"/> -- a module
///     compiles as a library (no entry point requirement).
///   </description></item>
///   <item><description>
///     <c>deterministic: true</c> -- the reproducibility envelope (Section
///     9.9 / gate X-IL2CPP-MANGLE-DET): the compilation binds identically
///     across runs.
///   </description></item>
///   <item><description>
///     <see cref="NullableContextOptions.Enable"/> -- XPact C# is written
///     nullable-enabled; binding under the same nullable context the source
///     was authored against keeps nullability-driven diagnostics correct.
///   </description></item>
///   <item><description>
///     <c>allowUnsafe: true</c> -- non-sim-path TUs may use <c>unsafe</c>
///     for non-XObject types (Section 4.1 / 5.18). The compilation must
///     accept it so Pass 1 binds rather than rejecting it as a parse error;
///     the sim-path <c>unsafe</c> ban (XIL2CPP092) is a later semantic pass.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>No platform / optimization pinning beyond determinism.</b> Pass 1 is
/// parse + bind only; it never emits a PE, so optimization level and target
/// platform do not affect its output. They are left at Roslyn defaults to
/// avoid an over-specified surface; the emit sub-phase owns those when it
/// lands.
/// </para>
/// </remarks>
public static class CompilationBuilder
{
    /// <summary>
    /// Construct the Pass-1 compilation.
    /// </summary>
    /// <param name="assemblyName">
    /// The compilation's assembly name -- conventionally the module name.
    /// Must not be null / empty / whitespace.
    /// </param>
    /// <param name="syntaxTrees">The parsed syntax trees, in canonical order. Must not be null.</param>
    /// <param name="referenceSet">The resolved reference set. Must not be null.</param>
    /// <returns>The constructed compilation.</returns>
    /// <exception cref="ArgumentException">If <paramref name="assemblyName"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="syntaxTrees"/> or <paramref name="referenceSet"/> is null.</exception>
    public static CSharpCompilation Build(
        string assemblyName,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        ReferenceSet referenceSet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ArgumentNullException.ThrowIfNull(referenceSet);

        CSharpCompilationOptions options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            deterministic: true,
            nullableContextOptions: NullableContextOptions.Enable,
            allowUnsafe: true);

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            referenceSet.References,
            options);
    }
}
