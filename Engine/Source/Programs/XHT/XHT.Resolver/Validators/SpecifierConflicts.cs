// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;
using Simgenics.XPact.XHT.Resolver.Phases;
using Simgenics.XPact.XHT.Tables;

namespace Simgenics.XPact.XHT.Resolver.Validators;

/// <summary>
/// Static helper detecting common specifier conflicts per
/// <c>/Documents/XHT.html</c> Rev 7 Section 6.2 (validator catalog) +
/// Section 7.4 (class-flag coherence). Mirrors UHT's
/// <c>UhtClass.ValidateClassFlags</c> + <c>UhtProperty.Validate</c>
/// precedent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage.</b> The detector covers the class-flag, function-flag,
/// and property-flag specifier coherence checks:
/// </para>
/// <list type="bullet">
///   <item><description><c>XHT111</c> -- Function: <c>Server + Client + NetMulticast</c> mutually exclusive.</description></item>
///   <item><description><c>XHT112</c> -- PropertyMember: <c>EditAnywhere + Transient</c> mutually exclusive.</description></item>
///   <item><description><c>XHT114</c> -- Class: <c>NoExport + Config=</c>.</description></item>
///   <item><description><c>XHT115</c> -- Class: <c>Intrinsic + XGENERATED_BODY()</c>.</description></item>
///   <item><description><c>XHT116</c> -- Class: <c>MinimalAPI + RequiredAPI</c>.</description></item>
///   <item><description><c>XHT117</c> -- Class: <c>NoExport + Blueprintable</c>.</description></item>
/// </list>
/// <para>
/// The detector is purely diagnostic; it does not mutate the AST. It
/// returns a list of <see cref="DiagnosticRecord"/> entries the caller
/// (typically <see cref="StepResolveFinal"/>) appends to the resolver
/// context's diagnostics list.
/// </para>
/// </remarks>
public static class SpecifierConflictDetector
{
    /// <summary>
    /// Inspect the supplied AST node for specifier conflicts. Returns
    /// an empty list when none are present.
    /// </summary>
    /// <param name="type">The AST node to inspect. Must not be null.</param>
    /// <param name="registry">The specifier registry (unused at Phase 1d but reserved for context-mismatch checks). Must not be null.</param>
    /// <returns>List of diagnostics; empty when the node passes all checks.</returns>
    public static IReadOnlyList<DiagnosticRecord> Detect(
        XhtTypeBase type,
        ISpecifierRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(registry);

        List<DiagnosticRecord> diagnostics = new();
        switch (type)
        {
            case XhtClass cls:
                DetectClassConflicts(cls, diagnostics);
                foreach (XhtFunction fn in cls.Functions)
                {
                    DetectFunctionConflicts(cls, fn, diagnostics);
                }
                foreach (XhtProperty p in cls.Properties)
                {
                    DetectPropertyConflicts(cls, p, diagnostics);
                }
                break;
            case XhtStruct st:
                foreach (XhtProperty p in st.Properties)
                {
                    DetectPropertyConflicts(st, p, diagnostics);
                }
                break;
            case XhtInterface iface:
                foreach (XhtFunction fn in iface.Functions)
                {
                    DetectFunctionConflicts(iface, fn, diagnostics);
                }
                break;
            default:
                break;
        }
        return diagnostics;
    }

    private static void DetectClassConflicts(XhtClass cls, List<DiagnosticRecord> diagnostics)
    {
        bool intrinsic = PhaseHelpers.HasSpecifier(cls.Specifiers, "Intrinsic");
        bool minimalApi = PhaseHelpers.HasSpecifier(cls.Specifiers, "MinimalAPI");
        bool requiredApi = !string.IsNullOrEmpty(cls.RequiredAPIMacroName);
        bool noExport = PhaseHelpers.HasSpecifier(cls.Specifiers, "NoExport");
        bool blueprintable = PhaseHelpers.HasSpecifier(cls.Specifiers, "Blueprintable");
        Specifier? config = PhaseHelpers.FindSpecifier(cls.Specifiers, "Config");

        // XHT115: Intrinsic + XGENERATED_BODY().
        if (intrinsic && cls.HasGeneratedBody)
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.IntrinsicWithGeneratedBody,
                $"Class '{cls.FullyQualifiedName}' is Intrinsic but also declares XGENERATED_BODY(); Intrinsic classes are runtime-registered and must not carry the body macro.",
                cls.Span));
        }

        // XHT116: MinimalAPI + RequiredAPI.
        if (minimalApi && requiredApi)
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.MinimalApiWithRequiredApi,
                $"Class '{cls.FullyQualifiedName}' declares MinimalAPI together with the <Module>_API export macro (RequiredAPI); the two are contradictory.",
                cls.Span));
        }

        // XHT117: NoExport + Blueprintable.
        if (noExport && blueprintable)
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.NoExportWithBlueprintable,
                $"Class '{cls.FullyQualifiedName}' is NoExport but also Blueprintable; Blueprintable requires the body-macro dispatch surface that NoExport elides.",
                cls.Span));
        }

        // XHT114: NoExport + Config=.
        if (noExport && config is not null)
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.ConfigConflictsWithNoExport,
                $"Class '{cls.FullyQualifiedName}' is NoExport but also declares Config=; NoExport elides the config accessor that runtime config-load requires.",
                cls.Span));
        }
    }

    private static void DetectFunctionConflicts(XhtTypeBase container, XhtFunction fn, List<DiagnosticRecord> diagnostics)
    {
        // XHT111: Server + Client + NetMulticast must be at-most-one.
        bool server = PhaseHelpers.HasSpecifier(fn.Specifiers, "Server");
        bool client = PhaseHelpers.HasSpecifier(fn.Specifiers, "Client");
        bool multicast = PhaseHelpers.HasSpecifier(fn.Specifiers, "NetMulticast");

        int count = 0;
        if (server) count++;
        if (client) count++;
        if (multicast) count++;

        if (count > 1)
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.FunctionSpecifierConflict,
                $"Function '{container.FullyQualifiedName}.{fn.Name}' carries more than one of Server / Client / NetMulticast; exactly one is allowed.",
                fn.Span));
        }
    }

    private static void DetectPropertyConflicts(XhtTypeBase container, XhtProperty p, List<DiagnosticRecord> diagnostics)
    {
        // XHT112: EditAnywhere + Transient mutually exclusive.
        bool editAnywhere = PhaseHelpers.HasSpecifier(p.Specifiers, "EditAnywhere");
        bool transient = PhaseHelpers.HasSpecifier(p.Specifiers, "Transient");

        if (editAnywhere && transient)
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.PropertySpecifierConflict,
                $"Property '{container.FullyQualifiedName}.{p.Name}' declares both EditAnywhere and Transient; EditAnywhere implies persistence which Transient denies.",
                p.Span));
        }

        // Replicated on a struct member is an error per Section 6.2.
        if (container is XhtStruct && PhaseHelpers.HasSpecifier(p.Specifiers, "Replicated"))
        {
            diagnostics.Add(MakeError(
                DiagnosticCodes.PropertySpecifierConflict,
                $"Property '{container.FullyQualifiedName}.{p.Name}' declares Replicated on a struct; replication is class-only.",
                p.Span));
        }
    }

    private static DiagnosticRecord MakeError(string code, string message, SourceSpan span)
    {
        return new DiagnosticRecord(
            Severity: DiagnosticSeverity.Error,
            Code: code,
            Message: message,
            File: string.IsNullOrEmpty(span.SourceFilePath) ? null : span.SourceFilePath,
            Line: span.Line == 0 ? null : span.Line,
            Column: span.Column == 0 ? null : span.Column);
    }
}
