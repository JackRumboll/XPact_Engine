// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XIL2CPP.Core;

/// <summary>
/// Centralised catalog of the <c>XIL2CPP&lt;NNN&gt;</c> diagnostic codes
/// XIL2CPP emits, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 12.
/// Pulling code strings into named constants means a typo (e.g.,
/// <c>"XLI2CPP001"</c> transposing I and L) fails to compile rather than
/// manifesting at runtime, matching the XHT.Core.DiagnosticCodes
/// discipline.
/// </summary>
/// <remarks>
/// <para>
/// The full Section 12 catalog spans XIL2CPP001-XIL2CPP187 and is added
/// incrementally as each sub-phase that emits a code lands -- this Phase
/// 6.a milestone anchors only the infrastructure + Pass-1 codes. Later
/// sub-phases (6.b semantic analysis, 6.c tier classification, 6.e+ emit)
/// extend this catalog as their diagnostics come online; the constants
/// must always match the numeric allocations in Section 12.
/// </para>
/// <para>
/// Band layout (per XIL2CPP.html Section 12):
/// <list type="bullet">
///   <item><description><b>XIL2CPP000</b> -- logger sentinel (info / warning / error lines without a catalog anchor); the <c>000-009</c> band header is "Locked Commitment 3 violations" but the <c>000</c> slot itself is unallocated, so it serves as the un-anchored sentinel exactly as XHT000 does for XHT.</description></item>
///   <item><description><b>XIL2CPP020-021</b> -- language-version / target-framework gates (Pass 1 surfaces these as it inspects the manifest LangVersion / .NET version).</description></item>
///   <item><description><b>XIL2CPP140-146</b> -- manifest / ABI-envelope / schema-version validation (manifest reader band).</description></item>
///   <item><description><b>XIL2CPP170</b> -- ReferenceCompileCSharpAction not registered in the XBT action graph (cross-module type-resolution prerequisite).</description></item>
///   <item><description><b>XIL2CPP900</b> -- ICE band: internal compiler error surfaced through an un-anchored exception path (above the Section 12 catalog's 187 ceiling; matches the XHT900 convention).</description></item>
/// </list>
/// </para>
/// </remarks>
public static class DiagnosticCodes
{
    // -----------------------------------------------------------------
    // Logger / infrastructure.
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP000 -- logger sentinel for info / warning / error lines without a catalog anchor.</summary>
    public const string LoggerSentinel = "XIL2CPP000";

    // -----------------------------------------------------------------
    // Language version / target framework gates (XIL2CPP020-029).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP020 -- Module declares LangVersion higher than MVP-supported C# 12. Section 2.2.</summary>
    public const string LangVersionTooHigh = "XIL2CPP020";

    /// <summary>XIL2CPP021 -- Module targets a .NET version higher than MVP-supported .NET 8. Section 2.2.</summary>
    public const string TargetFrameworkTooHigh = "XIL2CPP021";

    // -----------------------------------------------------------------
    // Manifest / ABI-envelope / schema validation (XIL2CPP140-149).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP140 -- Manifest declares an unknown ABI envelope tag value. Section 9.7. (Emitted starting Phase 6.b envelope validation.)</summary>
    public const string ManifestUnknownAbiEnvelopeTag = "XIL2CPP140";

    /// <summary>XIL2CPP141 -- Manifest schema version is outside XIL2CPP's supported set. Section 9.7.</summary>
    public const string ManifestUnsupportedSchemaVersion = "XIL2CPP141";

    /// <summary>XIL2CPP146 -- XHT-emitted FProperty descriptor schema version mismatches XIL2CPP's supported value. Section 9.7. (Emitted starting Phase 6.c reflection consumption.)</summary>
    public const string XhtFPropertySchemaMismatch = "XIL2CPP146";

    // -----------------------------------------------------------------
    // Cross-module reference pipeline (XIL2CPP170).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP170 -- ReferenceCompileCSharpAction not registered in the XBT action graph; the XBT slot-14 amendment is missing, so cross-module Roslyn type resolution cannot proceed. Section 9.8 (hard-fail, exit 63).</summary>
    public const string ReferenceCompileActionMissing = "XIL2CPP170";

    // -----------------------------------------------------------------
    // ICE band (XIL2CPP900).
    // -----------------------------------------------------------------

    /// <summary>XIL2CPP900 -- internal compiler error: an XIL2CPP-side bug surfaced through an un-anchored exception path. See <c>XIL2CPP.Entry.Program</c>'s catch surface.</summary>
    public const string InternalCompilerError = "XIL2CPP900";
}
