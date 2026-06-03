// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using XilSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Translates Roslyn <see cref="RoslynDiagnostic"/>s into XIL2CPP
/// <see cref="DiagnosticRecord"/>s per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2: "Type resolution failures ... produce standard Roslyn
/// diagnostics with file:line:col location and XPact-augmented context
/// (which module is being transpiled, which dependency module the
/// unresolved reference would belong to)."
/// </summary>
/// <remarks>
/// <para>
/// <b>Location.</b> Roslyn reports 0-based line / column; the translator
/// converts to the 1-based MSBuild convention via
/// <see cref="SourceSpan.Point(string, int, int)"/>. A diagnostic with no
/// source location (a compilation-level diagnostic) maps to a file-less
/// record.
/// </para>
/// <para>
/// <b>Code.</b> The record's <see cref="DiagnosticRecord.Code"/> carries
/// the Roslyn diagnostic id (e.g. <c>CS0246</c>) verbatim, not an
/// XIL2CPP&lt;NNN&gt; catalog code: these are the C# compiler's own
/// diagnostics surfaced for the operator, distinct from the transpiler's
/// catalog (which a later semantic pass emits). Carrying the CS id keeps
/// the diagnostic actionable and lets IDEs map it to the well-known C#
/// error.
/// </para>
/// <para>
/// <b>Unresolved-type augmentation.</b> For the unresolved-name family
/// (<c>CS0246</c> type-or-namespace not found, <c>CS0234</c> type-or-
/// namespace does not exist in namespace, <c>CS0103</c> name does not exist
/// in the current context) the translator appends a hint naming the
/// candidate dependency module the missing type would belong to, drawn from
/// the <see cref="ReferenceSet"/>'s dependency-module set, and records the
/// candidate list in the record's <see cref="DiagnosticRecord.Context"/>.
/// This turns a bare "type not found" into "type not found; it may live in
/// dependency module X whose reference DLL is missing."
/// </para>
/// </remarks>
public static class RoslynDiagnosticTranslator
{
    /// <summary>Roslyn id: type or namespace name could not be found.</summary>
    public const string CsTypeOrNamespaceNotFound = "CS0246";

    /// <summary>Roslyn id: type or namespace does not exist in the namespace.</summary>
    public const string CsTypeNotInNamespace = "CS0234";

    /// <summary>Roslyn id: name does not exist in the current context.</summary>
    public const string CsNameDoesNotExist = "CS0103";

    /// <summary>Context key carrying the comma-joined candidate dependency modules.</summary>
    public const string ContextKeyCandidateModules = "candidateModules";

    /// <summary>
    /// Translate a single Roslyn diagnostic.
    /// </summary>
    /// <param name="diagnostic">The Roslyn diagnostic. Must not be null.</param>
    /// <param name="moduleName">The module being transpiled (diagnostic <c>module</c> context). May be null.</param>
    /// <param name="references">
    /// The reference set, consulted for the dependency-module provenance hint
    /// on unresolved-type diagnostics. May be null (no augmentation).
    /// </param>
    /// <returns>The translated XIL2CPP diagnostic record.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="diagnostic"/> is null.</exception>
    public static DiagnosticRecord Translate(
        RoslynDiagnostic diagnostic,
        string? moduleName,
        ReferenceSet? references)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        XilSeverity severity = MapSeverity(diagnostic.Severity);
        string code = diagnostic.Id;
        string message = diagnostic.GetMessage(CultureInfo.InvariantCulture);

        // Location: convert Roslyn's 0-based line/col to 1-based.
        FileLinePositionSpan lineSpan = diagnostic.Location.GetLineSpan();
        bool hasLocation = diagnostic.Location.IsInSource && !string.IsNullOrEmpty(lineSpan.Path);

        IReadOnlyDictionary<string, string>? context = null;
        if (IsUnresolvedNameDiagnostic(code) && references is not null)
        {
            IReadOnlyList<string> candidates = CandidateModules(references);
            if (candidates.Count > 0)
            {
                message = AugmentUnresolvedMessage(message, candidates);
                context = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ContextKeyCandidateModules] = string.Join(", ", candidates),
                };
            }
        }

        if (!hasLocation)
        {
            return new DiagnosticRecord(
                severity,
                code,
                message,
                File: null,
                Line: null,
                Column: null,
                Module: moduleName,
                Context: context);
        }

        int line = lineSpan.StartLinePosition.Line + 1;
        int column = lineSpan.StartLinePosition.Character + 1;
        SourceSpan span = SourceSpan.Point(lineSpan.Path, line, column);

        return new DiagnosticRecord(
            severity,
            code,
            message,
            File: span.File,
            Line: span.StartLine,
            Column: span.StartColumn,
            Module: moduleName,
            Context: context);
    }

    /// <summary>
    /// Translate a sequence of Roslyn diagnostics, in input order.
    /// </summary>
    /// <param name="diagnostics">The Roslyn diagnostics. Must not be null.</param>
    /// <param name="moduleName">The module being transpiled. May be null.</param>
    /// <param name="references">The reference set for provenance hints. May be null.</param>
    /// <returns>The translated records, in input order.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="diagnostics"/> is null.</exception>
    public static IReadOnlyList<DiagnosticRecord> TranslateAll(
        IEnumerable<RoslynDiagnostic> diagnostics,
        string? moduleName,
        ReferenceSet? references)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        List<DiagnosticRecord> result = new();
        foreach (RoslynDiagnostic d in diagnostics)
        {
            result.Add(Translate(d, moduleName, references));
        }

        return result;
    }

    /// <summary>
    /// True iff <paramref name="code"/> is one of the unresolved-name
    /// diagnostics that gets the candidate-module augmentation.
    /// </summary>
    /// <param name="code">The Roslyn diagnostic id.</param>
    /// <returns>True for CS0246 / CS0234 / CS0103.</returns>
    public static bool IsUnresolvedNameDiagnostic(string code)
        => code is CsTypeOrNamespaceNotFound or CsTypeNotInNamespace or CsNameDoesNotExist;

    private static IReadOnlyList<string> CandidateModules(ReferenceSet references)
    {
        // The candidate set is every dependency module the resolver tried
        // to resolve (whether or not its reference DLL was present). A
        // missing reference DLL is exactly the case where an unresolved type
        // would have come from that module, so listing all dependencies --
        // ordinal-sorted for deterministic message text -- is the
        // actionable hint.
        List<string> sorted = references.DependencyModuleNames.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static string AugmentUnresolvedMessage(string baseMessage, IReadOnlyList<string> candidates)
    {
        string joined = string.Join(", ", candidates);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} XIL2CPP hint: the unresolved name may live in a dependency module ({1}); "
            + "if so, confirm that module's reference DLL ({2}) was produced by XBT's "
            + "ReferenceCompileCSharpAction (Section 9.8).",
            baseMessage,
            joined,
            MetadataReferenceResolver.RefOnlyDllSuffix);
    }

    private static XilSeverity MapSeverity(RoslynSeverity severity) => severity switch
    {
        RoslynSeverity.Error => XilSeverity.Error,
        RoslynSeverity.Warning => XilSeverity.Warning,
        RoslynSeverity.Info => XilSeverity.Info,
        // Hidden (IDE-only) diagnostics map to Info; XIL2CPP has no hidden bucket.
        RoslynSeverity.Hidden => XilSeverity.Info,
        _ => XilSeverity.Info,
    };
}
