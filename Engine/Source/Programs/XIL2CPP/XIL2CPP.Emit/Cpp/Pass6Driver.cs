// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XIL2CPP.Emit.Cpp.Body;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// XIL2CPP Pass 6 (C++ emit) driver per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 3.2: iterate the module's parsed files in canonical order, emit each
/// one through the <see cref="FileEmitter"/>, and collect the per-file
/// <see cref="EmitResult"/> set the <see cref="Output.Pass7Writer"/> consumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical order = determinism.</b> The files are visited in
/// <see cref="Frontend.Pass1Result.ParsedFiles"/> order (the deterministic
/// tree order Pass 1 established); the body-lowering rule set is discovered once
/// (<see cref="BodyLoweringRuleRegistry.Discover"/>) and shared across every
/// file. Two runs over the same unit produce a byte-identical result list (gate
/// X-IL2CPP-CSPATH-DET).
/// </para>
/// <para>
/// <b>One EmitResult per source file.</b> A file with no emittable type still
/// produces a result (an empty-but-well-formed header / source pair), so the
/// per-file output identity is stable and the Pass-7 writer always lands a
/// <c>.cs.h</c> + <c>.cs.cpp</c> for every input <c>.cs</c>.
/// </para>
/// </remarks>
public sealed class Pass6Driver
{
    /// <summary>
    /// The source-relative path stem the module-level container partial-spec
    /// unit is keyed under. It yields the synthetic output pair
    /// <c>&lt;Module&gt;.ContainerSpecs.cs.h</c> /
    /// <c>&lt;Module&gt;.ContainerSpecs.cs.cpp</c> (Section 5.7 module-level
    /// container emit), which holds the deduplicated container partial
    /// specializations for every XGC-aware closed instantiation in the module.
    /// </summary>
    public const string ContainerSpecsStem = ".ContainerSpecs.cs";

    private readonly FileEmitter _fileEmitter = new();
    private readonly ContainerPartialSpecEmitter _containerSpecEmitter = new();

    /// <summary>
    /// Run Pass 6 over <paramref name="context"/>: emit every parsed file in the
    /// module, in canonical order, returning the collected results.
    /// </summary>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <returns>One <see cref="EmitResult"/> per source file, in canonical file order.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="context"/> is null.</exception>
    public IReadOnlyList<EmitResult> Run(EmitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        BodyLoweringRuleRegistry registry = BodyLoweringRuleRegistry.Discover();

        IReadOnlyList<ModuleParser.ParsedFile> files = context.Unit.Pass1.ParsedFiles;
        string sourceRoot = CommonSourceRoot(files);

        List<EmitResult> results = new();
        foreach (ModuleParser.ParsedFile file in files)
        {
            string relative = RelativeSourcePath(file.AbsolutePath, sourceRoot);
            results.Add(_fileEmitter.EmitFile(file, relative, context, registry));
        }

        // Module-level container partial specializations (WU-6F / Section 5.7):
        // one deduplicated C++ partial spec per XGC-aware closed container
        // instantiation in the module, landing in a synthetic module-level
        // .cs.cpp. Appended AFTER the per-file results so the per-file output
        // identity (one EmitResult per source file) is unchanged; emitted only
        // when the module actually has an XGC-aware container site, so a
        // container-free module produces exactly the per-file results.
        EmitResult? containerSpecs = EmitContainerSpecs(context);
        if (containerSpecs is not null)
        {
            results.Add(containerSpecs);
        }

        return results;
    }

    /// <summary>
    /// Emit the module-level container partial-spec unit, or null when the
    /// module has no XGC-aware container sites (so a container-free module
    /// produces no synthetic unit). The unit's source carries the deduplicated
    /// partial specializations the <see cref="ContainerPartialSpecEmitter"/>
    /// produces; its header is intentionally empty (the specs are TU-local
    /// definitions emitted into the source).
    /// </summary>
    private EmitResult? EmitContainerSpecs(EmitContext context)
    {
        if (context.Pass3.GetAll<Analysis.ContainerSite>().Count == 0)
        {
            return null;
        }

        CppWriter w = new();
        w.AppendLine(FileEmitter.CopyrightBanner);
        w.AppendLine(FileEmitter.GeneratorBanner);
        w.AppendComment(
            "Container partial specializations for module " + context.ModuleName + ".");
        w.AppendLine();
        w.AppendLine("#include \"XCoreXObject/XObject.h\"");
        w.AppendLine("#include \"XCoreXObject/Internal/XGCWriteBarrier.h\"");
        w.AppendLine("#include \"XReflectionRuntime.h\"");
        w.AppendLine();

        _containerSpecEmitter.Emit(context, w);

        return new EmitResult(
            context.ModuleName + ContainerSpecsStem,
            HeaderContent: string.Empty,
            SourceContent: w.Build());
    }

    /// <summary>
    /// The longest common directory prefix of every parsed file's path, used as
    /// the module source root the per-file output stems are relativised against.
    /// Empty when there are no files or no shared prefix (each file then keys on
    /// its bare name). Deterministic: it is a pure function of the (already
    /// deterministic) file-path set.
    /// </summary>
    private static string CommonSourceRoot(IReadOnlyList<ModuleParser.ParsedFile> files)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        string Normalize(string p) => p.Replace('\\', '/');

        string[] firstParts = DirectorySegments(Normalize(files[0].AbsolutePath));
        int common = firstParts.Length;
        for (int i = 1; i < files.Count; i++)
        {
            string[] parts = DirectorySegments(Normalize(files[i].AbsolutePath));
            int max = Math.Min(common, parts.Length);
            int match = 0;
            while (match < max && string.Equals(firstParts[match], parts[match], StringComparison.Ordinal))
            {
                match++;
            }
            common = match;
        }

        if (common == 0)
        {
            return string.Empty;
        }
        return string.Join('/', firstParts, 0, common);
    }

    /// <summary>The directory segments of a path (the file name dropped).</summary>
    private static string[] DirectorySegments(string forwardSlashedPath)
    {
        int lastSlash = forwardSlashedPath.LastIndexOf('/');
        string dir = lastSlash < 0 ? string.Empty : forwardSlashedPath[..lastSlash];
        return dir.Length == 0
            ? Array.Empty<string>()
            : dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The forward-slashed path of <paramref name="absolutePath"/> relative to
    /// <paramref name="sourceRoot"/>; falls back to the bare file name when the
    /// path is not under the root (so the stem is never a rooted path that would
    /// defeat the Pass-7 writer's <c>Path.Combine</c>).
    /// </summary>
    private static string RelativeSourcePath(string absolutePath, string sourceRoot)
    {
        string normalized = absolutePath.Replace('\\', '/');
        if (sourceRoot.Length > 0)
        {
            string prefix = sourceRoot.EndsWith('/') ? sourceRoot : sourceRoot + "/";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return normalized[prefix.Length..];
            }
        }

        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[(slash + 1)..];
    }
}
