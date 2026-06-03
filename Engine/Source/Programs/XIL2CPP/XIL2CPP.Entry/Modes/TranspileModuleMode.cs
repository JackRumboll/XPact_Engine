// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;
using MetadataReferenceResolver = Simgenics.XPact.XIL2CPP.Frontend.MetadataReferenceResolver;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Entry.Modes;

/// <summary>
/// The <c>transpile-module</c> CLI mode. Phase 6.b runs the full
/// Pass 1 -&gt; Pass 2 -&gt; Pass 3 analysis pipeline (Roslyn parse + bind,
/// AST normalization, semantic analysis) for one module and emits the
/// collected diagnostics per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2 + 15.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass 1-3 -- still no C++ emit.</b> This sub-phase wires the front-end
/// substrate (CSharpSyntaxTree parse + CSharpCompilation bind + per-tree
/// semantic models) through Pass 2 (reflection-discovered AST normalizers)
/// and Pass 3 (reflection-discovered semantic analyzers). Passes 4 through 7
/// (tier classification, mangling, C++ emit, output write) are later
/// sub-phases. The mode's <see cref="Description"/> says so honestly; it
/// writes no <c>.cs.cpp</c> / <c>.cs.h</c> yet (that is Phase 6.e).
/// </para>
/// <para>
/// <b>Pipeline gating.</b> Pass 2 + Pass 3 only run when Pass 1 reported no
/// error-severity diagnostics: a parse / bind error means the trees are not
/// trustworthy substrate for the normalizers + analyzers, so the mode stops
/// at Pass 1 and surfaces those errors (exit 63). When Pass 1 is clean, the
/// mode runs Pass 2 (<see cref="Pass2Driver.Run(Pass1Result)"/>) then Pass 3
/// (<see cref="Pass3Driver.Run(NormalizedUnit)"/>) and emits every Pass-2 +
/// Pass-3 diagnostic.
/// </para>
/// <para>
/// <b>BCL references.</b> Product code targets the curated
/// <c>XPact.CSharp.BCL</c> reference-only DLL (Section 3.2), which does not
/// exist yet at Phase 6.b, so this mode supplies an empty BCL reference set
/// and surfaces an informational note. Binding against an empty BCL surfaces
/// every BCL-type reference as a Roslyn CS0246 -- expected at this scaffold
/// stage; the pipeline wiring (manifest -&gt; parse -&gt; bind -&gt;
/// normalize -&gt; analyze -&gt; diagnostics) is what this mode delivers. A
/// later sub-phase wires the curated BCL ref DLL set through here.
/// </para>
/// <para>
/// <b>Exit codes</b> (computed honestly from the union of Pass-1, Pass-2,
/// and Pass-3 diagnostics, per Contract Section 13.1):
/// <list type="bullet">
///   <item><description>
///     No error-severity diagnostic anywhere -&gt;
///     <see cref="ExitCodes.Success"/> (0).
///   </description></item>
///   <item><description>
///     At least one error whose code is a sim-path banned-API code
///     (the <c>XIL2CPP040-049 / 055 / 057 / 058 / 059 / 064</c> band) -&gt;
///     <see cref="ExitCodes.SimPathBannedApiOrManifestEnvelope"/> (41), the
///     sim-path banned-API check-failure code.
///   </description></item>
///   <item><description>
///     Any other error (a Pass-1 syntax / binder error, or any non-sim-path
///     analysis error such as <c>XIL2CPP001</c> / <c>XIL2CPP030</c>) -&gt;
///     <see cref="ExitCodes.Xil2CppInternalFailure"/> (63), the canonical
///     XIL2CPP analysis-failure code.
///   </description></item>
/// </list>
/// A malformed manifest or module-not-in-manifest throws
/// <c>ManifestMalformedException</c> which <c>Program</c> maps to exit 50; a
/// CLI argument error returns 10. The sim-path band is checked FIRST so a
/// module that mixes a banned-API error with another error still surfaces
/// the more specific 41.
/// </para>
/// </remarks>
[XIL2CPPMode("transpile-module")]
public sealed class TranspileModuleMode : IToolMode
{
    /// <summary>
    /// Test-only override for the curated BCL reference set. Production code
    /// leaves this null, so Pass 1 runs with an empty BCL set (the curated
    /// XPact.CSharp.BCL ref DLL is a later sub-phase). XIL2CPP.Tests installs
    /// a pinned in-package .NET 8 reference set here (via the internal hook)
    /// so the end-to-end CLI tests can bind a module whose sources reference
    /// BCL types -- exercising the full Pass 1-3 pipeline through the real
    /// mode. Mirrors the Logger's <c>__SetStderrForTesting</c> seam.
    /// </summary>
    private static IReadOnlyList<MetadataReference>? s_bclReferencesOverride;

    /// <inheritdoc />
    public string Name => "transpile-module";

    /// <inheritdoc />
    public string Description =>
        "Run the Pass 1-3 analysis pipeline (parse + bind + normalize + analyze) for one "
        + "module and report diagnostics. Phase 6.b: no C++ emit yet (that is Phase 6.e).";

    /// <summary>
    /// Install (or clear, with null) the test-only BCL reference set the mode
    /// binds against. Exposed to XIL2CPP.Tests via InternalsVisibleTo so the
    /// end-to-end CLI tests can supply a deterministic .NET 8 reference set
    /// without the curated XPact.CSharp.BCL ref DLL (which is a later
    /// sub-phase). Production never calls this; the default empty BCL stands.
    /// </summary>
    /// <param name="references">The references to bind against, or null to restore the empty default.</param>
    internal static void __SetBclReferencesForTesting(IReadOnlyList<MetadataReference>? references)
    {
        s_bclReferencesOverride = references;
    }

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ModuleModeOptions opts;
        try
        {
            // Pass 1 writes no outputs yet, so -Out= is not required.
            opts = ModuleModeOptions.Parse(args, requireOutput: false);
        }
        catch (CliArgumentException ex)
        {
            Logger.Error($"error {DiagnosticCodes.LoggerSentinel}: {ex.Message}");
            return Task.FromResult(ExitCodes.CliArgumentError);
        }

        return Task.FromResult(Run(opts, ct));
    }

    private static int Run(ModuleModeOptions opts, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }

        // Phase 6.b: the curated XPact.CSharp.BCL reference DLL is not yet
        // available, so Pass 1 runs with an empty BCL set. Surface the note
        // so the operator understands why BCL-type references will not
        // resolve. A later sub-phase wires the curated BCL ref DLLs here.
        Logger.Info(
            "info {0}: transpile-module (Phase 6.b Pass 1-3): the curated XPact.CSharp.BCL "
            + "reference set is not wired yet; BCL-type references will not resolve. "
            + "This mode parses + binds + normalizes + analyzes and reports diagnostics; "
            + "no C++ is emitted.",
            DiagnosticCodes.LoggerSentinel);

        IReadOnlyList<MetadataReference> bclReferences =
            s_bclReferencesOverride ?? Array.Empty<MetadataReference>();

        // Dependency reference DLLs live under
        // <repo>/Intermediate/Build/XIL2CPP/<Module>/Reference/. Resolve the
        // intermediate root next to the manifest so the search is anchored to
        // this build's layout (Section 9.8). Absent DLLs degrade with a
        // warning rather than crashing.
        string? intermediateRoot = ResolveIntermediateRoot(opts.ManifestPath);
        Func<string, string?> referenceRootForModule = moduleName =>
            intermediateRoot is null
                ? null
                : MetadataReferenceResolver.ComposeReferenceRoot(intermediateRoot, moduleName);

        Pass1Driver.Pass1Options pass1Options = new(bclReferences, referenceRootForModule);

        // Pass1Driver.Run throws ManifestMalformedException on a malformed
        // manifest or a module-not-in-manifest miss; Program.cs catches it
        // and maps to exit 50. Any other unexpected exception bubbles to
        // Program's XIL2CPP900 ICE branch (exit 63).
        Pass1Result result = Pass1Driver.Run(opts.ManifestPath, opts.ModuleName, pass1Options);

        ct.ThrowIfCancellationRequested();

        // Emit every collected Pass-1 diagnostic on the logger's channels.
        foreach (DiagnosticRecord diagnostic in result.Diagnostics)
        {
            Logger.EmitDiagnostic(diagnostic);
        }

        // Pass 2 + Pass 3 only run when Pass 1 is clean of ERROR-severity
        // diagnostics: a parse / bind error means the trees + semantic
        // models are not trustworthy substrate for the normalizers +
        // analyzers, so we stop at Pass 1 and surface those errors. (Pass-1
        // warnings / infos do not block the downstream passes.)
        if (result.HasErrors)
        {
            Logger.Info(
                "info {0}: transpile-module {1}: parsed {2} file(s); Pass 1 reported errors -- "
                + "skipping Pass 2 (normalize) + Pass 3 (analyze).",
                DiagnosticCodes.LoggerSentinel,
                result.ModuleName,
                result.ParsedFiles.Count);

            // A Pass-1 syntax / binder error is never a sim-path banned-API
            // code, so the honest exit is the canonical internal-failure 63.
            return ExitCodes.Xil2CppInternalFailure;
        }

        // Pass 2: reflection-discover + run every normalizer; emit Pass-2
        // diagnostics.
        NormalizedUnit unit = Pass2Driver.Run(result);
        ct.ThrowIfCancellationRequested();
        foreach (DiagnosticRecord diagnostic in unit.Diagnostics)
        {
            Logger.EmitDiagnostic(diagnostic);
        }

        // Pass 3: reflection-discover + run every analyzer; emit Pass-3
        // diagnostics.
        Pass3Result p3 = Pass3Driver.Run(unit);
        ct.ThrowIfCancellationRequested();
        foreach (DiagnosticRecord diagnostic in p3.Diagnostics)
        {
            Logger.EmitDiagnostic(diagnostic);
        }

        int exitCode = ComputeExitCode(unit.Diagnostics, p3.Diagnostics);

        Logger.Info(
            "info {0}: transpile-module {1}: parsed {2} file(s); {3}.",
            DiagnosticCodes.LoggerSentinel,
            result.ModuleName,
            result.ParsedFiles.Count,
            exitCode == ExitCodes.Success
                ? "Pass 1-3 clean"
                : "Pass 2/3 reported errors");

        return exitCode;
    }

    /// <summary>
    /// Compute the exit code HONESTLY from the union of the Pass-2 and
    /// Pass-3 diagnostics (Pass-1 errors are handled before this is reached):
    /// <list type="bullet">
    ///   <item><description>
    ///     no Error-severity diagnostic -&gt; <see cref="ExitCodes.Success"/> (0);
    ///   </description></item>
    ///   <item><description>
    ///     at least one Error whose code is a sim-path banned-API code -&gt;
    ///     <see cref="ExitCodes.SimPathBannedApiOrManifestEnvelope"/> (41);
    ///   </description></item>
    ///   <item><description>
    ///     any other Error -&gt;
    ///     <see cref="ExitCodes.Xil2CppInternalFailure"/> (63).
    ///   </description></item>
    /// </list>
    /// The sim-path band is checked first so the more specific 41 wins when a
    /// module mixes a banned-API error with another error.
    /// </summary>
    private static int ComputeExitCode(
        IReadOnlyList<DiagnosticRecord> pass2,
        IReadOnlyList<DiagnosticRecord> pass3)
    {
        bool anyError = false;
        bool anySimPathBannedApiError = false;

        foreach (IReadOnlyList<DiagnosticRecord> stream in new[] { pass2, pass3 })
        {
            foreach (DiagnosticRecord d in stream)
            {
                if (d.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }
                anyError = true;
                if (IsSimPathBannedApiCode(d.Code))
                {
                    anySimPathBannedApiError = true;
                }
            }
        }

        if (!anyError)
        {
            return ExitCodes.Success;
        }
        if (anySimPathBannedApiError)
        {
            return ExitCodes.SimPathBannedApiOrManifestEnvelope;
        }
        return ExitCodes.Xil2CppInternalFailure;
    }

    /// <summary>
    /// True iff <paramref name="code"/> is a sim-path banned-API diagnostic
    /// code -- the <c>XIL2CPP040-049 / 055 / 057 / 058 / 059 / 064</c> band
    /// owned by <c>SimPathBannedApiAnalyzer</c> per
    /// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 7.4 / 7.5 / 7.8. A
    /// sim-path banned-API check failure maps to exit 41 per Contract
    /// Section 13.1.
    /// </summary>
    private static bool IsSimPathBannedApiCode(string code) => code switch
    {
        DiagnosticCodes.SimPathBannedApiCall                  // XIL2CPP040
            or DiagnosticCodes.NonDeterministicIterationOrder // XIL2CPP041
            or DiagnosticCodes.SimPathStringIndexNonDeterministic // XIL2CPP042
            or DiagnosticCodes.SimPathLockBanned              // XIL2CPP043
            or DiagnosticCodes.SimPathAsyncAwaitBanned        // XIL2CPP044
            or DiagnosticCodes.SimPathReadsSerialNumber       // XIL2CPP045
            or DiagnosticCodes.SimPathHashesXObjectKey        // XIL2CPP046
            or DiagnosticCodes.SimPathIteratesFXObjectArrayOrdered // XIL2CPP047
            or DiagnosticCodes.SimPathTaskBanned              // XIL2CPP048
            or DiagnosticCodes.SimPathRandomBanned            // XIL2CPP049
            or DiagnosticCodes.SimPathInterlockedBanned       // XIL2CPP055
            or DiagnosticCodes.SimPathThreadStaticBanned      // XIL2CPP057
            or DiagnosticCodes.SimPathLocaleDependentToStringParse // XIL2CPP058
            or DiagnosticCodes.SimPathFNameFromNonLiteral     // XIL2CPP059
            or DiagnosticCodes.SimPathForeachOverIEnumerableBanned // XIL2CPP064
            => true,
        _ => false,
    };

    /// <summary>
    /// Resolve the intermediate-output root that holds per-module reference
    /// DLLs, by walking up from the manifest's directory looking for an
    /// ancestor with an <c>Engine/</c> sibling (the canonical repo layout
    /// signature, matching XBT's
    /// <c>BuildCsCompiler.ResolveCacheDirectory</c> discipline). Returns null
    /// when no such ancestor is found; the caller then treats every
    /// dependency reference DLL as absent.
    /// </summary>
    private static string? ResolveIntermediateRoot(string manifestPath)
    {
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        }
        catch (ArgumentException)
        {
            return null;
        }

        DirectoryInfo? cursor = dir is null ? null : new DirectoryInfo(dir);
        while (cursor is not null)
        {
            if (Directory.Exists(Path.Combine(cursor.FullName, "Engine")))
            {
                return Path.Combine(cursor.FullName, "Intermediate", "Build", "XIL2CPP");
            }
            cursor = cursor.Parent;
        }

        return null;
    }
}
