// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;
using System.IO;
using Simgenics.XPact.XBT.Discovery;

namespace Simgenics.XPact.XBT.Entry;

/// <summary>
/// UE-style include-path resolution for the <c>shared_pch_header_file</c>
/// field. The grouping pass in
/// <see cref="Simgenics.XPact.XBT.Entry.BuildMode"/> uses this helper to
/// canonicalise a bare header name (e.g.
/// <c>"EngineCommon.h"</c>) by searching every module's
/// <see cref="Configuration.ModuleRules.PublicIncludePaths"/> for the
/// first existing on-disk match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bare-name form exists.</b> The Toolchain Contract Rev 13
/// Section 2.1 reproducibility envelope rejects <c>..</c> path-traversal
/// references and absolute paths in descriptor fields. Two modules in
/// different directories therefore cannot reference the same physical
/// shared-PCH header via relative paths -- the only ways to share a
/// header on disk would be to copy it (defeats determinism) or to
/// climb above each module's <c>BaseDirectory</c> (rejected by
/// validation). UE solves this by allowing a logical include-path
/// reference: a module declares
/// <c>shared_pch_header_file = "EngineCommon.h"</c> and the resolver
/// finds the file in some module's <c>PublicIncludePaths</c>. The host
/// of the shared header need not declare a shared PCH itself -- it
/// merely exposes the file via its public include path.
/// </para>
/// <para>
/// <b>First-match semantics.</b> If two modules expose the same header
/// name in their <c>PublicIncludePaths</c>, the resolver picks the
/// first match in iteration order. Iteration order is the input list's
/// natural order; <see cref="ModuleCatalog"/> sorts modules
/// alphabetically by <see cref="Configuration.ModuleRules.Name"/>, so
/// the choice is deterministic across runs. Ambiguity is NOT a parse
/// error -- the alphabetical-first match is taken silently, mirroring
/// UE's <c>UEBuildModule.FindIncludeFile</c> behaviour.
/// </para>
/// </remarks>
internal static class SharedPchResolver
{
    /// <summary>
    /// Search every supplied module's <see cref="Configuration.ModuleRules.PublicIncludePaths"/>
    /// for a file with the given bare name. Returns the canonical
    /// absolute path of the first existing on-disk match, or null if
    /// no module exposes a file by that name.
    /// </summary>
    /// <param name="headerName">
    /// Bare header file name (e.g. <c>"EngineCommon.h"</c>). Must not
    /// contain a path separator; bare-name validation is the caller's
    /// responsibility.
    /// </param>
    /// <param name="allModules">
    /// Every module in the build's target-modules list. Iteration
    /// order is honoured for first-match semantics (see
    /// remarks on the type).
    /// </param>
    /// <returns>
    /// Canonical absolute path of the resolved header, or null when
    /// no module's <c>PublicIncludePaths</c> contains the file.
    /// </returns>
    public static string? FindIncludeFile(
        string headerName,
        IReadOnlyList<ModuleRecord> allModules)
    {
        foreach (ModuleRecord rec in allModules)
        {
            string moduleDir = Path.GetDirectoryName(rec.DescriptorPath)!;
            foreach (string includePath in rec.Rules.PublicIncludePaths)
            {
                string candidate = Path.GetFullPath(
                    Path.Combine(moduleDir, includePath, headerName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
