// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using MetadataReferenceResolver = Simgenics.XPact.XIL2CPP.Frontend.MetadataReferenceResolver;

namespace Simgenics.XPact.XIL2CPP.Entry.Modes;

/// <summary>
/// The <c>transpile-module</c> CLI mode. Phase 6.a runs the Roslyn Pass-1
/// front-end (parse + bind) for one module and emits the collected
/// diagnostics per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 + 15.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass 1 only -- no C++ emit.</b> This sub-phase ships the front-end
/// substrate (CSharpSyntaxTree parse + CSharpCompilation bind + per-tree
/// semantic models). Passes 2 through 7 (AST normalization, semantic
/// analysis, tier classification, mangling, C++ emit, output write) are
/// later sub-phases. The mode's <see cref="Description"/> says so honestly;
/// it writes no <c>.cs.cpp</c> / <c>.cs.h</c> yet.
/// </para>
/// <para>
/// <b>BCL references.</b> Product code targets the curated
/// <c>XPact.CSharp.BCL</c> reference-only DLL (Section 3.2), which does not
/// exist yet at Phase 6.a, so this mode supplies an empty BCL reference set
/// and surfaces an informational note. Binding against an empty BCL surfaces
/// every BCL-type reference as a Roslyn CS0246 -- expected at this scaffold
/// stage; the pipeline wiring (manifest -&gt; parse -&gt; bind -&gt;
/// diagnostics) is what Phase 6.a delivers. A later sub-phase wires the
/// curated BCL ref DLL set through here.
/// </para>
/// <para>
/// <b>Exit codes.</b> A clean parse/bind (no error-severity diagnostics)
/// returns <see cref="ExitCodes.Success"/> (0). Error-severity Pass-1
/// diagnostics (syntax / binder errors) return
/// <see cref="ExitCodes.Xil2CppInternalFailure"/> (63), the canonical
/// XIL2CPP analysis-failure code. A malformed manifest or
/// module-not-in-manifest throws <c>ManifestMalformedException</c> which
/// <c>Program</c> maps to exit 50; a CLI argument error returns 10.
/// </para>
/// </remarks>
[XIL2CPPMode("transpile-module")]
public sealed class TranspileModuleMode : IToolMode
{
    /// <inheritdoc />
    public string Name => "transpile-module";

    /// <inheritdoc />
    public string Description =>
        "Run the Roslyn Pass-1 front-end (parse + bind) for one module and report diagnostics. "
        + "Phase 6.a: Pass 1 only -- no C++ emit yet.";

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

        // Phase 6.a: the curated XPact.CSharp.BCL reference DLL is not yet
        // available, so Pass 1 runs with an empty BCL set. Surface the note
        // so the operator understands why BCL-type references will not
        // resolve. A later sub-phase wires the curated BCL ref DLLs here.
        Logger.Info(
            "info {0}: transpile-module (Phase 6.a Pass 1): the curated XPact.CSharp.BCL "
            + "reference set is not wired yet; BCL-type references will not resolve. "
            + "This mode parses + binds and reports diagnostics; no C++ is emitted.",
            DiagnosticCodes.LoggerSentinel);

        IReadOnlyList<MetadataReference> bclReferences = Array.Empty<MetadataReference>();

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

        // Emit every collected diagnostic on the logger's channels.
        foreach (DiagnosticRecord diagnostic in result.Diagnostics)
        {
            Logger.EmitDiagnostic(diagnostic);
        }

        Logger.Info(
            "info {0}: transpile-module {1}: parsed {2} file(s); {3}.",
            DiagnosticCodes.LoggerSentinel,
            result.ModuleName,
            result.ParsedFiles.Count,
            result.HasErrors ? "Pass 1 reported errors" : "Pass 1 clean");

        return result.HasErrors
            ? ExitCodes.Xil2CppInternalFailure
            : ExitCodes.Success;
    }

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
