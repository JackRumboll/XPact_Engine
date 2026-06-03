// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Per-<see cref="SyntaxTree"/> <see cref="SemanticModel"/> cache per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2: "The semantic model is
/// built lazily per syntax-tree." A semantic model is constructed on first
/// request for a given tree and reused for every subsequent request, so a
/// depth-first walk that asks for binding info on many nodes in the same
/// file does not pay the model-construction cost repeatedly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why lazy + cached.</b> <see cref="Compilation.GetSemanticModel(SyntaxTree, bool)"/>
/// is
/// not free; constructing one model per node would be wasteful, and
/// constructing models eagerly for trees a pass never visits would waste
/// work on a large module. Caching one model per tree -- created on demand
/// -- matches the spec's lazy-per-tree note and the Roslyn-recommended
/// usage.
/// </para>
/// <para>
/// <b>Thread safety.</b> The cache guards its dictionary with a lock so two
/// threads requesting the model for different trees do not race the backing
/// store. Roslyn semantic models are themselves safe for concurrent
/// read-only use once obtained; the lock only protects the cache insertion.
/// </para>
/// <para>
/// <b>Lifetime.</b> One instance is owned by a single
/// <see cref="Pass1Result"/> and lives as long as that result's
/// <see cref="Compilation"/>. It must not outlive the compilation it was
/// built against (the models reference it). Per Section 3.4 the Roslyn
/// workspace is not cached across <c>transpile-module</c> invocations, so a
/// fresh cache is created per Pass-1 run.
/// </para>
/// </remarks>
public sealed class LazySemanticModel
{
    private readonly Compilation _compilation;
    private readonly Dictionary<SyntaxTree, SemanticModel> _cache = new();
    private readonly object _gate = new();

    /// <summary>
    /// Construct a lazy semantic-model cache over a compilation.
    /// </summary>
    /// <param name="compilation">The compilation whose trees' models are cached. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="compilation"/> is null.</exception>
    public LazySemanticModel(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
    }

    /// <summary>
    /// Get (constructing on first request, then caching) the semantic model
    /// for <paramref name="tree"/>. The tree must belong to the compilation
    /// this cache was built over.
    /// </summary>
    /// <param name="tree">The syntax tree. Must not be null and must be in the compilation.</param>
    /// <returns>The cached semantic model for the tree.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="tree"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="tree"/> is not part of the compilation.</exception>
    public SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        lock (_gate)
        {
            if (_cache.TryGetValue(tree, out SemanticModel? cached))
            {
                return cached;
            }

            // Throws ArgumentException if the tree is not in the
            // compilation -- the correct contract; callers must only ask
            // for trees that participate in this compilation.
            SemanticModel model = _compilation.GetSemanticModel(tree, ignoreAccessibility: false);
            _cache[tree] = model;
            return model;
        }
    }
}
