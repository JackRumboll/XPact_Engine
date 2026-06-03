// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// The ordered set of <see cref="MetadataReference"/>s passed to the
/// Pass-1 <see cref="Microsoft.CodeAnalysis.CSharp.CSharpCompilation"/>,
/// plus a reference-to-owning-module provenance map and any diagnostics
/// raised while assembling the set (e.g. a missing dependency
/// <c>.refonly.dll</c>) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section
/// 9.8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering.</b> The references are ordered (a) BCL reference set first,
/// then (b) each dependency module's <c>.refonly.dll</c> in dependency
/// declaration order. The order is a reproducibility input (Section 9.9):
/// keeping it stable keeps the constructed compilation -- and thus its
/// diagnostics and any later emit -- byte-identical across machines.
/// </para>
/// <para>
/// <b>Provenance.</b> <see cref="OwningModuleOf"/> maps a dependency-module
/// reference back to the module name that produced it.
/// <see cref="RoslynDiagnosticTranslator"/> consults the inverse view
/// (<see cref="DependencyModuleNames"/>) to augment an unresolved-type
/// diagnostic with the candidate dependency module a missing type would
/// belong to. BCL references have no owning module (they map to null).
/// </para>
/// <para>
/// <b>Degradation.</b> When a dependency module's <c>.refonly.dll</c> is
/// absent (the not-yet-built reference-DLL pipeline, Section 9.8), the
/// builder records a clear diagnostic in <see cref="Diagnostics"/> and
/// omits that reference rather than crashing. Type resolution that needed
/// it then fails downstream with a CS0246 / CS0234 that the translator
/// augments with the missing-module hint.
/// </para>
/// </remarks>
public sealed class ReferenceSet
{
    private readonly Dictionary<MetadataReference, string?> _owningModule;
    private readonly HashSet<string> _dependencyModuleNames;

    /// <summary>
    /// Construct a reference set.
    /// </summary>
    /// <param name="references">The ordered metadata references. Must not be null.</param>
    /// <param name="owningModule">
    /// Map from each dependency-module reference to its owning module name.
    /// BCL references are absent from this map (their provenance is null).
    /// Must not be null.
    /// </param>
    /// <param name="dependencyModuleNames">
    /// The set of dependency-module names the resolver attempted to resolve
    /// (whether or not the <c>.refonly.dll</c> was present). Used by the
    /// diagnostic translator to enumerate candidate modules. Must not be null.
    /// </param>
    /// <param name="diagnostics">Diagnostics raised while assembling the set. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public ReferenceSet(
        ImmutableArray<MetadataReference> references,
        IReadOnlyDictionary<MetadataReference, string> owningModule,
        IReadOnlyCollection<string> dependencyModuleNames,
        IReadOnlyList<DiagnosticRecord> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(owningModule);
        ArgumentNullException.ThrowIfNull(dependencyModuleNames);
        ArgumentNullException.ThrowIfNull(diagnostics);

        References = references;
        Diagnostics = diagnostics;

        // ReferenceEqualityComparer: a MetadataReference's identity is
        // reference-equality (two CreateFromFile calls on the same path
        // produce distinct, non-equal references). The provenance map is
        // keyed on the exact instances placed in References.
        _owningModule = new Dictionary<MetadataReference, string?>(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<MetadataReference, string> kv in owningModule)
        {
            _owningModule[kv.Key] = kv.Value;
        }

        _dependencyModuleNames = new HashSet<string>(dependencyModuleNames, StringComparer.Ordinal);
    }

    /// <summary>The ordered metadata references (BCL first, dependency modules after).</summary>
    public ImmutableArray<MetadataReference> References { get; }

    /// <summary>
    /// Diagnostics raised while assembling the set (e.g. a missing
    /// dependency <c>.refonly.dll</c>). Empty when every reference resolved.
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> Diagnostics { get; }

    /// <summary>
    /// The dependency-module names the resolver attempted to resolve, in no
    /// particular order. Used by the diagnostic translator to name a
    /// candidate module for an unresolved type.
    /// </summary>
    public IReadOnlyCollection<string> DependencyModuleNames => _dependencyModuleNames;

    /// <summary>
    /// The owning module name for a reference, or null when the reference
    /// is a BCL reference (no owning XPact module) or is not in the set.
    /// </summary>
    /// <param name="reference">The reference to look up. Must not be null.</param>
    /// <returns>The owning module name, or null.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="reference"/> is null.</exception>
    public string? OwningModuleOf(MetadataReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return _owningModule.TryGetValue(reference, out string? module) ? module : null;
    }
}
