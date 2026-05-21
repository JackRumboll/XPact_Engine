// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;

namespace Simgenics.XPact.XBT.ActionGraph.Actions;

/// <summary>
/// XBT-side mirror of XHT.Emitter's filename derivation helpers per
/// <c>/Documents/XHT.html</c> Rev 8 Section 9.3.1 +
/// <c>/Documents/XToolchainContract.html</c> Rev 13.6 Section 10.3.
/// XBT pre-discovers XHT's output filenames so the action graph can be
/// wired up before XHT runs; XHT subsequently emits exactly those names
/// (sentinel-convergence rule).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why mirror, not reference.</b> XBT consumes XHT's outputs opaquely
/// (XHT.html Section 9 + XBT.html Section 9; "XBT does not parse .gen.h
/// or .gen.cpp"). Adding a ProjectReference from XBT.ActionGraph to
/// XHT.Emitter (or vice versa) would couple the two tools at the
/// build-tool layer; the contract is explicit that XBT invokes XHT as a
/// subprocess and reads its output manifest as the join surface. The
/// naming logic is mirrored on both sides; a cross-tool naming-contract
/// test in XHT.Tests verifies byte-identical output for the same inputs
/// so the mirroring cannot drift silently.
/// </para>
/// <para>
/// <b>Naming rules</b> (kept in lockstep with XHT.Emitter):
/// </para>
/// <list type="bullet">
///   <item><c>{HeaderStem}.gen.h</c> per header (mirrors
///   <c>XHT.Emitter.HeaderEmitter.DeriveGenHeaderFileName</c>).</item>
///   <item><c>{HeaderStem}.gen.cpp</c> per header (mirrors
///   <c>XHT.Emitter.SourceEmitter.DeriveGenSourceFileName</c>).</item>
///   <item><c>{Base}.init.gen.cpp</c> per module (mirrors
///   <c>XHT.Emitter.ModuleInitEmitter.DeriveModuleInitFileName</c>).</item>
///   <item><c>{Base}.gen.manifest</c> per module (mirrors
///   <c>XHT.Emitter.ModuleEmitter.DeriveGenManifestFileName</c>).</item>
/// </list>
/// <para>
/// <c>{Base}</c> is the module's <c>GeneratedCPPFilenameBase</c> when set,
/// falling back to the module <c>Name</c>. <c>{HeaderStem}</c> is the
/// header's filename without extension; per the contract, header paths
/// like <c>Public/XValve.h</c> reduce to <c>XValve</c>. Normalisation
/// replaces backslashes with forward slashes so Windows + Linux paths
/// produce identical stems.
/// </para>
/// </remarks>
public static class XhtOutputNaming
{
    /// <summary>
    /// Derive the per-header <c>.gen.h</c> filename. Mirrors
    /// <c>XHT.Emitter.HeaderEmitter.DeriveGenHeaderFileName</c> verbatim:
    /// normalise backslashes to forward slashes, take the filename, strip
    /// the extension, append <c>.gen.h</c>.
    /// </summary>
    /// <param name="sourceRelativePath">
    /// Source-relative (or absolute) path of the header (e.g.
    /// <c>"Public/XValve.h"</c> or <c>"X:/.../XValve.h"</c>). Only the
    /// final filename component is used.
    /// </param>
    /// <returns>Just the filename (e.g. <c>"XValve.gen.h"</c>), no directory.</returns>
    public static string GenHeaderFileName(string sourceRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        string normalized = sourceRelativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem + ".gen.h";
    }

    /// <summary>
    /// Derive the per-header <c>.gen.cpp</c> filename. Mirrors
    /// <c>XHT.Emitter.SourceEmitter.DeriveGenSourceFileName</c> verbatim.
    /// </summary>
    /// <param name="sourceRelativePath">
    /// Source-relative (or absolute) path of the header.
    /// </param>
    /// <returns>Just the filename (e.g. <c>"XValve.gen.cpp"</c>), no directory.</returns>
    public static string GenSourceFileName(string sourceRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        string normalized = sourceRelativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem + ".gen.cpp";
    }

    /// <summary>
    /// Derive the per-module <c>.init.gen.cpp</c> aggregator filename.
    /// Mirrors <c>XHT.Emitter.ModuleInitEmitter.DeriveModuleInitFileName</c>:
    /// uses <paramref name="generatedCppFilenameBase"/> when non-empty,
    /// falls back to <paramref name="moduleName"/>.
    /// </summary>
    /// <param name="moduleName">The module's <c>Name</c> from the manifest.</param>
    /// <param name="generatedCppFilenameBase">
    /// The module's <c>GeneratedCPPFilenameBase</c> from the manifest. Pass
    /// null or empty to use the module name.
    /// </param>
    /// <returns>Just the filename (e.g. <c>"XScoring.init.gen.cpp"</c>), no directory.</returns>
    public static string ModuleInitFileName(string moduleName, string? generatedCppFilenameBase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        string baseName = string.IsNullOrEmpty(generatedCppFilenameBase)
            ? moduleName
            : generatedCppFilenameBase;
        return baseName + ".init.gen.cpp";
    }

    /// <summary>
    /// Derive the per-module <c>.gen.manifest</c> filename. Mirrors
    /// <c>XHT.Emitter.ModuleEmitter.DeriveGenManifestFileName</c>: uses
    /// <paramref name="generatedCppFilenameBase"/> when non-empty, falls
    /// back to <paramref name="moduleName"/>.
    /// </summary>
    /// <param name="moduleName">The module's <c>Name</c> from the manifest.</param>
    /// <param name="generatedCppFilenameBase">
    /// The module's <c>GeneratedCPPFilenameBase</c> from the manifest. Pass
    /// null or empty to use the module name.
    /// </param>
    /// <returns>Just the filename (e.g. <c>"XScoring.gen.manifest"</c>), no directory.</returns>
    public static string GenManifestFileName(string moduleName, string? generatedCppFilenameBase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        string baseName = string.IsNullOrEmpty(generatedCppFilenameBase)
            ? moduleName
            : generatedCppFilenameBase;
        return baseName + ".gen.manifest";
    }

    /// <summary>
    /// Filename of the per-module token-AST cache produced by
    /// <see cref="ParseHeadersAction"/> per XBT.html Section 9.4: one
    /// <c>{Module}.tokens.bin</c> blob carrying the module's parsed token
    /// stream. The output is opaque to XBT; the file exists so the action
    /// graph can express the parse -&gt; emit ordering edge.
    /// </summary>
    /// <param name="moduleName">The module's <c>Name</c> from the manifest.</param>
    /// <returns>Just the filename (e.g. <c>"XScoring.tokens.bin"</c>), no directory.</returns>
    public static string TokensBinFileName(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return moduleName + ".tokens.bin";
    }
}
