// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using MetadataReferenceResolver = Simgenics.XPact.XIL2CPP.Frontend.MetadataReferenceResolver;

namespace Simgenics.XPact.XIL2CPP.Entry.Modes;

/// <summary>
/// The <c>refonly-compile</c> CLI mode. Emits a module's
/// <c>&lt;Module&gt;.refonly.dll</c> (a metadata-only reference assembly:
/// type + method signatures, no method bodies) so a downstream module's
/// transpile can resolve cross-module C# types through Roslyn against the
/// module's public surface. Per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 9.8 (the ReferenceCompileCSharpAction pipeline).
/// </summary>
/// <remarks>
/// <para>
/// <b>Subprocess of the XIL2CPP binary.</b> XBT's
/// <c>ReferenceCompileCSharpAction</c> (XActionType slot 14) invokes this
/// mode as a subprocess. Running the Roslyn metadata-only emit here keeps
/// Roslyn out of <c>XBT.ActionGraph</c> and reuses the Pass-1 front-end's
/// compilation builder (the same parse + bind path
/// <c>transpile-module</c> uses), so the reference assembly binds against
/// exactly the option set + reference set Pass 1 sees.
/// </para>
/// <para>
/// <b>What it produces.</b> The Roslyn reference-only emit
/// (<c>EmitOptions(metadataOnly: true, includePrivateMembers: false)</c> --
/// the exact equivalent of the compiler's <c>/refonly</c> switch) writes a
/// PE whose metadata tables carry every public/protected type + member
/// signature, marked with <c>System.Runtime.CompilerServices.ReferenceAssemblyAttribute</c>,
/// and whose method bodies are reduced to a trivial <c>throw null;</c> stub
/// (no real IL logic) -- Section 9.8: "type signatures + method signatures
/// but no method bodies." <c>includePrivateMembers: false</c> is what
/// distinguishes a true reference assembly from a plain metadata-only emit
/// (which retains private members and is not marked as a reference
/// assembly). The bytes are written atomically (temp-file-then-rename) to
/// the <c>-Out=</c> path, creating the <c>Reference/</c> directory first.
/// </para>
/// <para>
/// <b>BCL references.</b> Product code targets the curated
/// <c>XPact.CSharp.BCL</c> reference-only DLL (Section 3.2), which does not
/// exist yet at Phase 6.a. As a Phase-6.a bridge, this mode loads whatever
/// BCL reference assemblies are staged on disk under
/// <c>&lt;intermediateRoot&gt;/Bcl/*.dll</c> (the <see cref="BclSubdirectory"/>),
/// falling back to an empty BCL set when that directory is absent (matching
/// <c>transpile-module</c>'s empty-BCL behaviour when nothing is staged).
/// Without a BCL, any type declaration fails to bind <c>System.Object</c>
/// and the metadata-only emit fails; with a BCL staged, the emit produces a
/// signature-only reference assembly. The mode does NOT reference the
/// build-machine's TPA list / installed runtime (that would break the
/// X-IL2CPP-CSPATH-DET cross-machine determinism gate, Section 9.9); the BCL
/// comes from the explicitly-staged on-disk set only. A later sub-phase
/// wires the curated <c>XPact.CSharp.BCL</c> ref DLL set through here.
/// </para>
/// <para>
/// <b>Exit codes.</b> A successful emit returns
/// <see cref="ExitCodes.Success"/> (0). A malformed manifest or
/// module-not-in-manifest throws <c>ManifestMalformedException</c> which
/// <c>Program</c> maps to <see cref="ExitCodes.ManifestMalformed"/> (50). An
/// emit failure (Roslyn <c>EmitResult.Success == false</c>) returns
/// <see cref="ExitCodes.Xil2CppInternalFailure"/> (63) after reporting the
/// emit diagnostics. A CLI argument error returns
/// <see cref="ExitCodes.CliArgumentError"/> (10).
/// </para>
/// </remarks>
[XIL2CPPMode("refonly-compile")]
public sealed class RefonlyCompileMode : IToolMode
{
    /// <summary>
    /// Phase-6.a BCL staging subdirectory under the intermediate root
    /// (<c>&lt;intermediateRoot&gt;/Bcl/</c>). Every <c>*.dll</c> there is
    /// loaded as a BCL <see cref="MetadataReference"/> until the curated
    /// <c>XPact.CSharp.BCL</c> reference DLL exists. Absent in production
    /// today, so the mode runs with an empty BCL set unless something stages
    /// reference assemblies there.
    /// </summary>
    public const string BclSubdirectory = "Bcl";

    /// <inheritdoc />
    public string Name => "refonly-compile";

    /// <inheritdoc />
    public string Description =>
        "Emit a module's <Module>.refonly.dll (a metadata-only reference assembly: type + "
        + "method signatures, no method bodies) for cross-module Roslyn type resolution. "
        + "Invoked as a subprocess by XBT's ReferenceCompileCSharpAction (Section 9.8).";

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ModuleModeOptions opts;
        try
        {
            // The reference DLL is written to -Out=, so it is required.
            opts = ModuleModeOptions.Parse(args, requireOutput: true);
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

        // Dependency reference DLLs live under
        // <repo>/Intermediate/Build/XIL2CPP/<Module>/Reference/. Resolve the
        // intermediate root next to the manifest so the search is anchored to
        // this build's layout (Section 9.8). Absent DLLs degrade with a
        // warning rather than crashing (the reference-set resolver records
        // them in the Pass-1 diagnostics).
        string? intermediateRoot = ResolveIntermediateRoot(opts.ManifestPath);

        // Phase 6.a bridge: the curated XPact.CSharp.BCL reference DLL is not
        // wired yet, so load whatever BCL reference assemblies are staged on
        // disk under <intermediateRoot>/Bcl/. Absent -> empty BCL set (any
        // type declaration then fails to bind System.Object and the
        // metadata-only emit fails). The mode never reaches into the
        // build-machine's TPA list (X-IL2CPP-CSPATH-DET).
        IReadOnlyList<MetadataReference> bclReferences = LoadStagedBclReferences(intermediateRoot);
        Logger.Info(
            "info {0}: refonly-compile (Phase 6.a): loaded {1} staged BCL reference assembly(ies) "
            + "from the '{2}' staging directory. Output is signature-only (no method bodies).",
            DiagnosticCodes.LoggerSentinel,
            bclReferences.Count,
            BclSubdirectory);

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

        // Surface every Pass-1 diagnostic (reference-resolution warnings +
        // translated Roslyn syntax / binder diagnostics) so the operator sees
        // the same picture transpile-module shows. These do NOT gate the
        // refonly emit by themselves: Roslyn's metadataOnly emit reports its
        // own EmitResult.Success, which is what the exit code keys on. (A
        // signature-only emit can succeed even when bodies reference
        // unresolved BCL types, which is exactly the Phase 6.a empty-BCL
        // situation.)
        foreach (DiagnosticRecord diagnostic in result.Diagnostics)
        {
            Logger.EmitDiagnostic(diagnostic);
        }

        // Emit the metadata-only reference assembly into an in-memory buffer
        // first, then write it atomically to -Out so a failed/partial emit
        // never leaves a half-written .refonly.dll on disk for the next
        // build to mistake for a cache hit.
        using MemoryStream peStream = new();
        EmitResult emit = result.Compilation.Emit(
            peStream,
            options: new EmitOptions(metadataOnly: true, includePrivateMembers: false));

        if (!emit.Success)
        {
            Logger.Error(
                "error {0}: refonly-compile {1}: metadata-only emit failed with {2} error diagnostic(s).",
                DiagnosticCodes.LoggerSentinel,
                result.ModuleName,
                CountEmitErrors(emit));
            foreach (Diagnostic d in emit.Diagnostics)
            {
                if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                {
                    Logger.Error("error {0}: {1}", DiagnosticCodes.LoggerSentinel, d.GetMessage(CultureInfo.InvariantCulture));
                }
            }
            return ExitCodes.Xil2CppInternalFailure;
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            WriteAtomic(opts.OutputDir, peStream.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error(
                "error {0}: refonly-compile {1}: failed to write reference DLL to '{2}': {3}.",
                DiagnosticCodes.LoggerSentinel,
                result.ModuleName,
                opts.OutputDir,
                ex.Message);
            return ExitCodes.Xil2CppInternalFailure;
        }

        Logger.Info(
            "info {0}: refonly-compile {1}: wrote metadata-only reference assembly to '{2}'.",
            DiagnosticCodes.LoggerSentinel,
            result.ModuleName,
            opts.OutputDir);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Load every <c>*.dll</c> staged under
    /// <c>&lt;intermediateRoot&gt;/Bcl/</c> as a BCL
    /// <see cref="MetadataReference"/>, ordinal-sorted by filename for a
    /// deterministic reference order. Returns an empty list when the
    /// intermediate root is unknown or the staging directory is absent (the
    /// Phase-6.a no-curated-BCL situation). A DLL that fails to load as
    /// metadata is skipped with a warning rather than aborting the compile.
    /// </summary>
    private static IReadOnlyList<MetadataReference> LoadStagedBclReferences(string? intermediateRoot)
    {
        if (string.IsNullOrEmpty(intermediateRoot))
        {
            return Array.Empty<MetadataReference>();
        }

        string bclDir = Path.Combine(intermediateRoot, BclSubdirectory);
        if (!Directory.Exists(bclDir))
        {
            return Array.Empty<MetadataReference>();
        }

        string[] dllPaths = Directory.GetFiles(bclDir, "*.dll", SearchOption.TopDirectoryOnly);
        Array.Sort(dllPaths, StringComparer.Ordinal);

        List<MetadataReference> references = new(dllPaths.Length);
        foreach (string dllPath in dllPaths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(dllPath));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                Logger.Warning(
                    "warning {0}: refonly-compile: skipping staged BCL reference '{1}' "
                    + "(could not load as metadata: {2}).",
                    DiagnosticCodes.LoggerSentinel,
                    dllPath,
                    ex.Message);
            }
        }

        return references;
    }

    private static int CountEmitErrors(EmitResult emit)
    {
        int count = 0;
        foreach (Diagnostic d in emit.Diagnostics)
        {
            if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Atomically write <paramref name="bytes"/> to
    /// <paramref name="outputPath"/>: create the parent directory (the
    /// Section-9.8 <c>Reference/</c> dir), write to a sibling temp file, then
    /// rename over the target. The rename is atomic on a single volume, so a
    /// reader never observes a partially-written reference DLL.
    /// </summary>
    private static void WriteAtomic(string outputPath, byte[] bytes)
    {
        string fullOutput = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = fullOutput + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            // File.Move with overwrite is atomic on a single volume on both
            // Windows and Linux for the rename step.
            File.Move(tempPath, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the temp file is uniquely named so
                    // a leak does not corrupt the output.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Resolve the intermediate-output root that holds per-module reference
    /// DLLs, by walking up from the manifest's directory looking for an
    /// ancestor with an <c>Engine/</c> sibling (the canonical repo layout
    /// signature). Returns null when no such ancestor is found; the caller
    /// then treats every dependency reference DLL as absent. Mirrors
    /// <see cref="TranspileModuleMode"/>'s discipline so both modes resolve
    /// dependency reference DLLs identically.
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
