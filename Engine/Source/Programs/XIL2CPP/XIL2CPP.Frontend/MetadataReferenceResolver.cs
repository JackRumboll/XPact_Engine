// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Manifest;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Assembles the ordered <see cref="ReferenceSet"/> for a Pass-1
/// compilation from (a) a curated BCL reference set and (b) each dependency
/// module's <c>M.refonly.dll</c> published under
/// <c>Intermediate/.../&lt;Module&gt;/Reference/</c> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8 (the
/// ReferenceCompileCSharpAction pipeline).
/// </summary>
/// <remarks>
/// <para>
/// <b>BCL source -- product vs test.</b> Product code targets the curated
/// <c>XPact.CSharp.BCL</c> reference-only DLL (Section 3.2). That DLL does
/// not exist yet at Phase 6.a; product callers therefore pass an explicit
/// list of BCL reference paths (resolved by the caller from the manifest /
/// SDK layout), and this resolver references them by path. The TEST suite
/// supplies the BCL via <c>Basic.Reference.Assemblies.Net80</c> -- a pinned
/// in-package .NET 8 reference set -- and passes those
/// <see cref="MetadataReference"/>s directly through
/// <see cref="BuildFromReferences"/>. Product code never depends on
/// <c>Basic.Reference.Assemblies</c> (that would break the
/// X-IL2CPP-CSPATH-DET cross-machine determinism gate, Section 9.9).
/// </para>
/// <para>
/// <b>Critical divergence from XBT.</b> XBT's <c>BuildCsCompiler</c>
/// references the host process's <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. This
/// resolver MUST NOT: the TPA list is the build machine's installed runtime
/// and varies across machines, breaking cross-machine determinism. References
/// are pinned explicitly instead (gate X-IL2CPP-CSPATH-DET).
/// </para>
/// <para>
/// <b>Degradation.</b> A dependency module whose <c>.refonly.dll</c> is
/// absent (the reference-DLL pipeline has not produced it yet) records a
/// warning in the result's <see cref="ReferenceSet.Diagnostics"/> and is
/// omitted from the reference list -- the resolver never throws on a missing
/// DLL. Cross-module type resolution that needed that module then fails
/// downstream with a Roslyn CS0246 / CS0234 that
/// <see cref="RoslynDiagnosticTranslator"/> augments with the candidate
/// module name (the module name is retained in
/// <see cref="ReferenceSet.DependencyModuleNames"/> regardless of whether
/// the DLL resolved).
/// </para>
/// </remarks>
public static class MetadataReferenceResolver
{
    /// <summary>
    /// The reference-DLL filename suffix per Section 9.8
    /// (<c>M.refonly.dll</c>).
    /// </summary>
    public const string RefOnlyDllSuffix = ".refonly.dll";

    /// <summary>
    /// The per-module reference-DLL subdirectory name per Section 9.8
    /// (<c>Intermediate/.../&lt;Module&gt;/Reference/</c>).
    /// </summary>
    public const string ReferenceSubdirectory = "Reference";

    /// <summary>
    /// Build a <see cref="ReferenceSet"/> from already-resolved BCL
    /// references plus the on-disk reference-DLL search for each dependency
    /// module.
    /// </summary>
    /// <param name="bclReferences">
    /// The curated BCL reference set, already constructed by the caller
    /// (product: from the <c>XPact.CSharp.BCL</c> ref DLL paths; tests:
    /// from <c>Basic.Reference.Assemblies.Net80</c>). Must not be null.
    /// Placed first in the ordered reference list.
    /// </param>
    /// <param name="dependencyModuleNames">
    /// The dependency-module names (from
    /// <see cref="Xil2CppManifestReader.GetDependencyModuleNames(XbtModule)"/>),
    /// in declaration order. Must not be null.
    /// </param>
    /// <param name="referenceRootForModule">
    /// A callback mapping a dependency-module name to the absolute path of
    /// its reference directory
    /// (<c>Intermediate/.../&lt;Module&gt;/Reference/</c>). Must not be null;
    /// may return null for a module whose reference root is unknown, which
    /// is treated identically to an absent DLL.
    /// </param>
    /// <returns>The assembled reference set (never null).</returns>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    public static ReferenceSet BuildFromReferences(
        IReadOnlyList<MetadataReference> bclReferences,
        IReadOnlyList<string> dependencyModuleNames,
        Func<string, string?> referenceRootForModule)
    {
        ArgumentNullException.ThrowIfNull(bclReferences);
        ArgumentNullException.ThrowIfNull(dependencyModuleNames);
        ArgumentNullException.ThrowIfNull(referenceRootForModule);

        ImmutableArray<MetadataReference>.Builder references =
            ImmutableArray.CreateBuilder<MetadataReference>();
        Dictionary<MetadataReference, string> owningModule =
            new(ReferenceEqualityComparer.Instance);
        List<string> attemptedModules = new(dependencyModuleNames.Count);
        List<DiagnosticRecord> diagnostics = new();

        // (a) BCL references first, in caller-supplied order.
        foreach (MetadataReference bcl in bclReferences)
        {
            ArgumentNullException.ThrowIfNull(bcl);
            references.Add(bcl);
            // BCL references have no owning XPact module: deliberately not
            // added to the provenance map (OwningModuleOf returns null).
        }

        // (b) Each dependency module's M.refonly.dll, in declaration order.
        foreach (string moduleName in dependencyModuleNames)
        {
            ArgumentNullException.ThrowIfNull(moduleName);
            attemptedModules.Add(moduleName);

            string? referenceRoot = referenceRootForModule(moduleName);
            if (string.IsNullOrEmpty(referenceRoot))
            {
                diagnostics.Add(MissingRefOnlyWarning(moduleName, refOnlyPath: null));
                continue;
            }

            string refOnlyPath = Path.Combine(referenceRoot, moduleName + RefOnlyDllSuffix);
            if (!File.Exists(refOnlyPath))
            {
                diagnostics.Add(MissingRefOnlyWarning(moduleName, refOnlyPath));
                continue;
            }

            MetadataReference moduleReference;
            try
            {
                moduleReference = MetadataReference.CreateFromFile(refOnlyPath);
            }
            catch (IOException ex)
            {
                diagnostics.Add(UnreadableRefOnlyWarning(moduleName, refOnlyPath, ex.Message));
                continue;
            }
            catch (BadImageFormatException ex)
            {
                diagnostics.Add(UnreadableRefOnlyWarning(moduleName, refOnlyPath, ex.Message));
                continue;
            }

            references.Add(moduleReference);
            owningModule[moduleReference] = moduleName;
        }

        return new ReferenceSet(
            references.ToImmutable(),
            owningModule,
            attemptedModules,
            diagnostics);
    }

    /// <summary>
    /// Compose the absolute reference-directory path for a dependency
    /// module under an intermediate root, per the Section 9.8 layout
    /// <c>&lt;intermediateRoot&gt;/&lt;Module&gt;/Reference/</c>.
    /// </summary>
    /// <param name="intermediateRoot">The intermediate-output root for this build. Must not be null.</param>
    /// <param name="moduleName">The dependency module name. Must not be null / empty / whitespace.</param>
    /// <returns>The absolute reference-directory path.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="intermediateRoot"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> is null / empty / whitespace.</exception>
    public static string ComposeReferenceRoot(string intermediateRoot, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(intermediateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return Path.Combine(intermediateRoot, moduleName, ReferenceSubdirectory);
    }

    private static DiagnosticRecord MissingRefOnlyWarning(string moduleName, string? refOnlyPath)
    {
        string where = refOnlyPath is null
            ? "no reference directory is known for it"
            : string.Format(CultureInfo.InvariantCulture, "expected at '{0}'", refOnlyPath);

        // No dedicated Section-12 catalog code is allocated for an absent
        // individual reference DLL (XIL2CPP170 covers the harder case: the
        // ReferenceCompileCSharpAction not being registered at all, which is
        // an exit-63 hard fail, not a per-DLL degradation). The per-DLL
        // degradation surfaces un-anchored at the logger sentinel, matching
        // the catalog's discipline for reader-infrastructure conditions.
        return new DiagnosticRecord(
            DiagnosticSeverity.Warning,
            DiagnosticCodes.LoggerSentinel,
            string.Format(
                CultureInfo.InvariantCulture,
                "Dependency module '{0}' reference DLL ({0}{1}) is missing ({2}); "
                + "cross-module type resolution against this module will fail. "
                + "Ensure XBT's ReferenceCompileCSharpAction has produced the "
                + "reference DLL before transpiling (Section 9.8).",
                moduleName,
                RefOnlyDllSuffix,
                where),
            Module: moduleName);
    }

    private static DiagnosticRecord UnreadableRefOnlyWarning(string moduleName, string refOnlyPath, string detail)
    {
        return new DiagnosticRecord(
            DiagnosticSeverity.Warning,
            DiagnosticCodes.LoggerSentinel,
            string.Format(
                CultureInfo.InvariantCulture,
                "Dependency module '{0}' reference DLL at '{1}' could not be loaded as metadata: {2}. "
                + "Cross-module type resolution against this module will fail.",
                moduleName,
                refOnlyPath,
                detail),
            Module: moduleName);
    }
}
