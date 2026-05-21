// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph.Actions;

/// <summary>
/// XHT pass-2 action: emits the per-header <c>.gen.h</c> + <c>.gen.cpp</c>,
/// the per-module <c>.init.gen.cpp</c> aggregator, and the per-module
/// <c>.gen.manifest</c> text manifest XBT consumes. Maps to
/// <see cref="XActionType.EmitReflectionAction"/> per
/// <c>/Documents/XBT.html</c> Rev 10 Section 9.4 +
/// <c>/Documents/XHT.html</c> Rev 5 Sections 8 + 9 +
/// <c>/Documents/XToolchainContract.html</c> Rev 13.6 Section 10.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subprocess shape.</b> Invokes <c>xht.exe emit-module</c>:
/// </para>
/// <code>
/// xht.exe emit-module
///     -Manifest=&lt;path-to-XBT-Manifest.json&gt;
///     -Module=&lt;ModuleName&gt;
///     -Out=&lt;output-directory&gt;
/// </code>
/// <para>
/// <b>Pre-discovered outputs</b> (XHT.html Section 9.3.1: XBT seeds the
/// action graph with these expected outputs before XHT runs; the
/// sentinel-convergence rule guarantees XHT's actual output set matches
/// for every header in the input list):
/// </para>
/// <list type="bullet">
///   <item>Per header H: <c>&lt;Out&gt;/&lt;HeaderStem&gt;.gen.h</c> + <c>&lt;Out&gt;/&lt;HeaderStem&gt;.gen.cpp</c></item>
///   <item>Per module: <c>&lt;Out&gt;/&lt;Base&gt;.init.gen.cpp</c></item>
///   <item>Per module: <c>&lt;Out&gt;/&lt;Base&gt;.gen.manifest</c></item>
/// </list>
/// <para>
/// where <c>Base</c> = <c>GeneratedCPPFilenameBase</c> (from the manifest)
/// or the module name when the base is unset, and <c>HeaderStem</c> is
/// the header's filename without extension. Filenames are derived through
/// <see cref="XhtOutputNaming"/> -- the XBT-side mirror of XHT.Emitter's
/// naming helpers, cross-tool-verified byte-identical in the XHT.Tests
/// contract test.
/// </para>
/// <para>
/// <b>Prerequisites.</b> The full <c>.h</c> + <c>.cs</c> input set, the
/// XBT manifest, the XHT executable, and -- separately -- the upstream
/// <see cref="ParseHeadersAction"/>'s
/// <c>&lt;Module&gt;.tokens.bin</c> output. The two-action split lets
/// module B's parse run in parallel with module A's emit; the link-time
/// ordering edge is established by including the tokens.bin in this
/// action's prerequisite list (caller responsibility -- typically
/// <c>EmitReflectionAction.PrerequisiteItems</c> includes the
/// ParseHeadersAction's ProducedItems through BuildMode's emission
/// wiring).
/// </para>
/// <para>
/// <b>Determinism.</b> <see cref="CommandVersion"/> is stable across
/// reconstruction with the same inputs: folds in the action type ordinal,
/// the XHT executable path, the command arguments, the module name + base
/// name, the manifest path, the output directory, and every reflection
/// header relative path (sorted ordinal). Content-hash invalidation flows
/// through prerequisite recording in <see cref="ParallelExecutor"/>; the
/// command-version hash does not need to embed content.
/// </para>
/// </remarks>
public sealed class EmitReflectionAction : ActionBase
{
    private readonly string _moduleName;
    private readonly string _xhtExecutablePath;
    private readonly string _manifestJsonPath;
    private readonly string _outputDirectory;
    private readonly string? _generatedCppFilenameBase;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<FileItem> _prerequisiteItems;
    private readonly IReadOnlyList<FileItem> _producedItems;
    private readonly IReadOnlyList<string> _commandArguments;
    private readonly IReadOnlyList<string> _reflectionHeaderRelativePaths;
    private readonly FileItem? _dependencyListFile;
    private readonly string? _tier;
    private readonly bool _simPath;
    private readonly BuildConfiguration _configuration;
    private readonly Platform _platform;

    /// <inheritdoc/>
    public override XActionType ActionType => XActionType.EmitReflectionAction;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> PrerequisiteItems => _prerequisiteItems;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> ProducedItems => _producedItems;

    /// <inheritdoc/>
    public override string CommandPath => _xhtExecutablePath;

    /// <inheritdoc/>
    public override IReadOnlyList<string> CommandArguments => _commandArguments;

    /// <inheritdoc/>
    /// <remarks>
    /// Audit fix R7-C2: see <see cref="ParseHeadersAction.WorkingDirectory"/>
    /// for the rationale. Captured at construction; never re-read.
    /// </remarks>
    public override string WorkingDirectory => _workingDirectory;

    /// <inheritdoc/>
    public override string CommandDescription => "XHT.Emit";

    /// <inheritdoc/>
    public override string StatusDescription => _moduleName;

    /// <inheritdoc/>
    public override bool CanExecuteRemotely => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Emit cost includes preprocessor, resolver, and per-header IO; the
    /// per-module step is heavier than a parse but still bounded by a
    /// typical compile action's weight.
    /// </remarks>
    public override double Weight => 1.0;

    /// <inheritdoc/>
    public override string? Module => _moduleName;

    /// <inheritdoc/>
    /// <remarks>Audit fix R7-C2: see <see cref="ParseHeadersAction.Tier"/>.</remarks>
    public override string? Tier => _tier;

    /// <inheritdoc/>
    /// <remarks>Audit fix R7-C2: see <see cref="ParseHeadersAction.SimPath"/>.</remarks>
    public override bool SimPath => _simPath;

    /// <inheritdoc/>
    /// <remarks>Audit fix R7-C2: see <see cref="ParseHeadersAction.Configuration"/>.</remarks>
    public override BuildConfiguration Configuration => _configuration;

    /// <inheritdoc/>
    /// <remarks>Audit fix R7-C2: see <see cref="ParseHeadersAction.Platform"/>.</remarks>
    public override Platform Platform => _platform;

    /// <inheritdoc/>
    public override FileItem? DependencyListFile => _dependencyListFile;

    /// <summary>The module this action emits reflection scaffolding for.</summary>
    public string ModuleName => _moduleName;

    /// <summary>Absolute path to the XHT executable.</summary>
    public string XhtExecutablePath => _xhtExecutablePath;

    /// <summary>Absolute path to XBT's Manifest.json.</summary>
    public string ManifestJsonPath => _manifestJsonPath;

    /// <summary>Absolute path to the per-module output directory.</summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>
    /// The module's <c>GeneratedCPPFilenameBase</c> from the manifest, or
    /// null when the base was unset (in which case the module name is
    /// used as the base for the <c>.init.gen.cpp</c> + <c>.gen.manifest</c>
    /// filenames).
    /// </summary>
    public string? GeneratedCppFilenameBase => _generatedCppFilenameBase;

    /// <summary>
    /// The reflection header relative paths that contributed to the
    /// pre-discovered produced-items list. Used by tests and tooling to
    /// inspect what XBT expected XHT to emit before the subprocess ran.
    /// </summary>
    public IReadOnlyList<string> ReflectionHeaderRelativePaths => _reflectionHeaderRelativePaths;

    /// <summary>Construct an emit-reflection action for one module.</summary>
    /// <param name="moduleName">
    /// The module's <c>Name</c> from the manifest. XHT receives this via
    /// <c>-Module=</c>.
    /// </param>
    /// <param name="xhtExecutablePath">
    /// Absolute path to <c>xht.exe</c> (Win64) or <c>xht</c>
    /// (Linux/macOS).
    /// </param>
    /// <param name="manifestJsonPath">
    /// Absolute path to XBT's emitted <c>Manifest.json</c>.
    /// </param>
    /// <param name="outputDirectory">
    /// Absolute path to the per-module intermediate directory where XHT
    /// will write its outputs.
    /// </param>
    /// <param name="reflectionInputs">
    /// The full <c>.h</c> + <c>.cs</c> input set: every source XHT reads
    /// at emit time. Sorted ordinal by the constructor.
    /// </param>
    /// <param name="reflectionHeaderRelativePaths">
    /// Relative (or absolute) paths of every header XBT expects XHT to
    /// emit a <c>.gen.h</c> + <c>.gen.cpp</c> pair for. The constructor
    /// derives the pre-discovered output filenames from these via
    /// <see cref="XhtOutputNaming"/>. Pass an empty list for a module
    /// with no <c>.h</c> files (still emits <c>.init.gen.cpp</c> +
    /// <c>.gen.manifest</c> sentinel pair).
    /// </param>
    /// <param name="generatedCppFilenameBase">
    /// The module's <c>GeneratedCPPFilenameBase</c> from the manifest, or
    /// null to use the module name as the base.
    /// </param>
    /// <param name="workingDirectory">
    /// The action's working directory, captured at construction (audit
    /// fix R7-C2). MUST be the manifest's <c>RootLocalPath</c> (the
    /// canonical engine root) so two builds run from different shell
    /// CWDs produce identical action commands. When null,
    /// <see cref="Directory.GetCurrentDirectory"/> is captured once at
    /// construction and stored verbatim.
    /// </param>
    /// <param name="tier">Owning module's tier or null. See <see cref="Tier"/>.</param>
    /// <param name="configuration">
    /// Build configuration. Folds into <see cref="IExternalAction.CommandVersion"/>.
    /// </param>
    /// <param name="platform">
    /// Target platform. Folds into <see cref="IExternalAction.CommandVersion"/>.
    /// </param>
    /// <param name="simPath">True for sim-path translation units.</param>
    /// <param name="dependencyListFile">
    /// Phase-2 header-dependency hook. Phase 1 callers pass null.
    /// </param>
    public EmitReflectionAction(
        string moduleName,
        string xhtExecutablePath,
        string manifestJsonPath,
        string outputDirectory,
        IReadOnlyList<FileItem> reflectionInputs,
        IReadOnlyList<string> reflectionHeaderRelativePaths,
        string? generatedCppFilenameBase = null,
        string? workingDirectory = null,
        string? tier = null,
        BuildConfiguration configuration = default,
        Platform platform = default,
        bool simPath = false,
        FileItem? dependencyListFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentException.ThrowIfNullOrEmpty(xhtExecutablePath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(reflectionInputs);
        ArgumentNullException.ThrowIfNull(reflectionHeaderRelativePaths);

        _moduleName = moduleName;
        _xhtExecutablePath = xhtExecutablePath;
        _manifestJsonPath = manifestJsonPath;
        _outputDirectory = outputDirectory;
        _generatedCppFilenameBase = generatedCppFilenameBase;
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _tier = tier;
        _configuration = configuration;
        _platform = platform;
        _simPath = simPath;
        _dependencyListFile = dependencyListFile;

        // Snapshot the header-relative-path list (ordinal-sorted, deduped)
        // for stable pre-discovery + a stable cache key.
        List<string> headerPaths = new(reflectionHeaderRelativePaths.Count);
        foreach (string p in reflectionHeaderRelativePaths)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                headerPaths.Add(p);
            }
        }
        headerPaths.Sort(StringComparer.Ordinal);
        // Dedupe.
        if (headerPaths.Count > 1)
        {
            List<string> deduped = new(headerPaths.Count);
            string? last = null;
            foreach (string p in headerPaths)
            {
                if (!string.Equals(last, p, StringComparison.Ordinal))
                {
                    deduped.Add(p);
                    last = p;
                }
            }
            headerPaths = deduped;
        }
        _reflectionHeaderRelativePaths = headerPaths;

        // Prerequisites: every reflection input + the XHT executable + the
        // manifest. Same intern + sort + dedupe pipeline as
        // ParseHeadersAction.
        List<FileItem> prereqs = new(reflectionInputs.Count + 2);
        foreach (FileItem src in reflectionInputs)
        {
            prereqs.Add(src);
        }
        prereqs.Add(FileItem.GetItemByPath(xhtExecutablePath));
        prereqs.Add(FileItem.GetItemByPath(manifestJsonPath));

        prereqs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        if (prereqs.Count > 1)
        {
            List<FileItem> deduped = new(prereqs.Count);
            string? last = null;
            foreach (FileItem fi in prereqs)
            {
                if (!string.Equals(last, fi.FullPath, StringComparison.Ordinal))
                {
                    deduped.Add(fi);
                    last = fi.FullPath;
                }
            }
            prereqs = deduped;
        }
        _prerequisiteItems = prereqs;

        // Pre-discovery: derive every output filename from the module +
        // header set. Mirrors XHT.Emitter's filename derivation byte-for-byte
        // (cross-tool-verified).
        //
        // Audit fix R7-C6: a HashSet-on-stems used to silently drop
        // collisions when two headers reduced to the same stem
        // (Public/Foo/XValve.h vs Public/Bar/XValve.h -- both produce
        // XValve.gen.h). Silent elision lost the metadata for the
        // dropped header at run time, with no diagnostic. The fix:
        // detect at action emit time and throw with a clear diagnostic
        // naming both colliding headers + the colliding stem. The
        // caller catches XBTException and surfaces it as a build
        // failure (exit code 50 -- ManifestMalformed -- since the
        // collision is a configuration/discovery defect, not a
        // toolchain or runtime error).
        Dictionary<string, string> stemToFirstHeader = new(StringComparer.Ordinal);
        List<FileItem> produced = new();
        foreach (string headerRelative in _reflectionHeaderRelativePaths)
        {
            string genH = XhtOutputNaming.GenHeaderFileName(headerRelative);
            string stem = Path.GetFileNameWithoutExtension(genH); // ".gen.h" stripped twice -> module-local stem.
            // GetFileNameWithoutExtension on "X.gen.h" -> "X.gen"; strip
            // the ".gen" component for the dedupe key so two headers with
            // the same stem in different folders collapse to a single
            // produced entry. Without the strip, "X.gen" vs "X.gen" would
            // dedupe anyway; the canonical form is the stem of the
            // original header.
            string canonicalStem = stem.EndsWith(".gen", StringComparison.Ordinal)
                ? stem[..^4]
                : stem;
            if (stemToFirstHeader.TryGetValue(canonicalStem, out string? firstHeader))
            {
                // Distinct rel paths but identical stem -- collision.
                // (When the two reflectionHeaderRelativePaths inputs are
                // the same string, the upstream Sort + Dedupe already
                // collapsed them; this branch only fires when stems
                // collide across distinct paths.)
                throw new XBTException(
                    $"Module '{moduleName}' has two reflection headers that reduce to the " +
                    $"same generated stem '{canonicalStem}.gen.h':\n" +
                    $"  '{firstHeader}'\n" +
                    $"  '{headerRelative}'\n" +
                    "Rename one of the headers (e.g. give them distinct stems) or move them " +
                    "into separate modules. Silently dropping one would lose reflection " +
                    "metadata for the elided header at run time.",
                    exitCode: 50);
            }
            stemToFirstHeader[canonicalStem] = headerRelative;
            produced.Add(FileItem.GetItemByPath(
                Path.Combine(outputDirectory, XhtOutputNaming.GenHeaderFileName(headerRelative))));
            produced.Add(FileItem.GetItemByPath(
                Path.Combine(outputDirectory, XhtOutputNaming.GenSourceFileName(headerRelative))));
        }

        // Per-module aggregator + manifest -- emitted unconditionally so
        // the action graph has a stable join point even for modules with
        // zero reflection headers (XHT.html Section 9.3.1 expects them
        // anyway: "for every module containing at least one .h or .cs:
        // expect {Base}.init.gen.cpp and {Base}.gen.manifest").
        produced.Add(FileItem.GetItemByPath(
            Path.Combine(outputDirectory,
                XhtOutputNaming.ModuleInitFileName(moduleName, _generatedCppFilenameBase))));
        produced.Add(FileItem.GetItemByPath(
            Path.Combine(outputDirectory,
                XhtOutputNaming.GenManifestFileName(moduleName, _generatedCppFilenameBase))));

        produced.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        _producedItems = produced;

        // Command line.
        _commandArguments = new[]
        {
            "emit-module",
            $"-Manifest={manifestJsonPath}",
            $"-Module={moduleName}",
            $"-Out={outputDirectory}",
        };
    }

    /// <inheritdoc/>
    protected override IoHash ComputeCommandVersion()
    {
        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. ActionType ordinal.
        BitConverter.TryWriteBytes(intBuffer, (int)ActionType);
        hasher.Update(intBuffer);

        // 2. CommandPath.
        UpdateUtf8(hasher, _xhtExecutablePath);

        // 3. CommandArguments (ordered, length-prefixed).
        BitConverter.TryWriteBytes(intBuffer, _commandArguments.Count);
        hasher.Update(intBuffer);
        foreach (string arg in _commandArguments)
        {
            UpdateUtf8(hasher, arg);
        }

        // 4. Module + base + manifest path + output dir.
        UpdateUtf8(hasher, _moduleName);
        UpdateUtf8(hasher, _generatedCppFilenameBase ?? string.Empty);
        UpdateUtf8(hasher, _manifestJsonPath);
        UpdateUtf8(hasher, _outputDirectory);

        // 5. Reflection header relative paths (ordered, length-prefixed).
        //    Determines the pre-discovered output set; adding / removing
        //    a header changes the produced-items set so the action's
        //    identity must change too.
        BitConverter.TryWriteBytes(intBuffer, _reflectionHeaderRelativePaths.Count);
        hasher.Update(intBuffer);
        foreach (string path in _reflectionHeaderRelativePaths)
        {
            UpdateUtf8(hasher, path);
        }

        // 6. WorkingDirectory + Configuration + Platform + SimPath
        //    (audit fix R7-C2). See ParseHeadersAction.ComputeCommandVersion
        //    for rationale.
        UpdateUtf8(hasher, _workingDirectory);
        BitConverter.TryWriteBytes(intBuffer, (int)_configuration);
        hasher.Update(intBuffer);
        BitConverter.TryWriteBytes(intBuffer, (int)_platform);
        hasher.Update(intBuffer);
        Span<byte> simPathByte = stackalloc byte[1];
        simPathByte[0] = _simPath ? (byte)1 : (byte)0;
        hasher.Update(simPathByte);

        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }
}
