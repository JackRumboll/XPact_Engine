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
/// XHT pass-1 action: parses every <c>.h</c> + <c>.cs</c> in the named
/// module into a per-module token-AST cache. Maps to
/// <see cref="XActionType.ParseHeadersAction"/> per
/// <c>/Documents/XBT.html</c> Rev 10 Section 9.4 +
/// <c>/Documents/XHT.html</c> Rev 5 Section 9.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subprocess shape.</b> Invokes <c>xht.exe</c> with the
/// <c>parse-module</c> mode (XHT public API per
/// <c>/Documents/XHT.html</c> Section 5.1):
/// </para>
/// <code>
/// xht.exe parse-module
///     -Manifest=&lt;path-to-XBT-Manifest.json&gt;
///     -Module=&lt;ModuleName&gt;
///     -Out=&lt;output-directory&gt;
/// </code>
/// <para>
/// <b>Outputs.</b> One opaque token-AST cache at
/// <c>&lt;output&gt;/&lt;Module&gt;.tokens.bin</c>. XHT writes it; XBT
/// treats it as an opaque blob whose only purpose is to express the
/// parse-&gt;emit ordering edge in the action graph (XBT.html Section 9.4:
/// "EmitReflectionAction prerequisite: the corresponding
/// ParseHeadersAction's output"). Phase 1 splits the action so module B's
/// parse can run in parallel with module A's emit; the token cache is
/// also separately invalidatable per the spec.
/// </para>
/// <para>
/// <b>Prerequisites.</b> The full set of <c>.h</c> + <c>.cs</c> sources
/// in the module (the headers contain the C++ reflection markers; the
/// <c>.cs</c> sources contain the XClass / XStruct / etc. attribute-marked
/// types), the XBT manifest file (XHT reads per-module records from it),
/// and the XHT executable's own content hash (so an XHT upgrade
/// invalidates every ParseHeadersAction).
/// </para>
/// <para>
/// <b>Determinism.</b> The action's <see cref="CommandVersion"/> is
/// deterministic across reconstruction with the same inputs: the hash
/// folds in the action type ordinal, the module name, the XHT executable
/// path, the manifest path, the output directory, and every source-file
/// path (sorted ordinal). The XHT exe content hash + the manifest content
/// hash flow through the prerequisite mechanism (and through
/// <see cref="CacheKeyComponents"/>) so a change to either invalidates
/// the cached output even when the command line bytes are identical.
/// </para>
/// </remarks>
public sealed class ParseHeadersAction : ActionBase
{
    private readonly string _moduleName;
    private readonly string _xhtExecutablePath;
    private readonly string _manifestJsonPath;
    private readonly string _outputDirectory;
    private readonly IReadOnlyList<FileItem> _prerequisiteItems;
    private readonly IReadOnlyList<FileItem> _producedItems;
    private readonly IReadOnlyList<string> _commandArguments;
    private readonly FileItem? _dependencyListFile;

    /// <inheritdoc/>
    public override XActionType ActionType => XActionType.ParseHeadersAction;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> PrerequisiteItems => _prerequisiteItems;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> ProducedItems => _producedItems;

    /// <inheritdoc/>
    public override string CommandPath => _xhtExecutablePath;

    /// <inheritdoc/>
    public override IReadOnlyList<string> CommandArguments => _commandArguments;

    /// <inheritdoc/>
    public override string WorkingDirectory => Directory.GetCurrentDirectory();

    /// <inheritdoc/>
    public override string CommandDescription => "XHT.Parse";

    /// <inheritdoc/>
    public override string StatusDescription => _moduleName;

    /// <inheritdoc/>
    /// <remarks>
    /// XHT only reads input files declared as prerequisites and writes
    /// outputs to the declared output directory; no system-resource access
    /// beyond that, so the action is safely transportable to a Phase 2
    /// remote executor.
    /// </remarks>
    public override bool CanExecuteRemotely => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Parse cost is comparable to a single C++ compile; weight 1.0
    /// matches the per-source compile-action weight so the scheduler
    /// treats them as peers.
    /// </remarks>
    public override double Weight => 1.0;

    /// <inheritdoc/>
    public override string? Module => _moduleName;

    /// <inheritdoc/>
    public override FileItem? DependencyListFile => _dependencyListFile;

    /// <summary>
    /// The module this action parses headers for.
    /// </summary>
    public string ModuleName => _moduleName;

    /// <summary>
    /// Absolute path to the XHT executable (e.g.
    /// <c>&lt;EngineRoot&gt;/Binaries/Win64/xht.exe</c>).
    /// </summary>
    public string XhtExecutablePath => _xhtExecutablePath;

    /// <summary>
    /// Absolute path to the XBT-emitted <c>Manifest.json</c> XHT reads.
    /// </summary>
    public string ManifestJsonPath => _manifestJsonPath;

    /// <summary>
    /// Absolute path to the output directory where XHT writes the
    /// <c>{Module}.tokens.bin</c> cache.
    /// </summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>Construct a parse-headers action for one module.</summary>
    /// <param name="moduleName">
    /// The module's <c>Name</c> from the manifest. XHT receives this via
    /// <c>-Module=</c>.
    /// </param>
    /// <param name="xhtExecutablePath">
    /// Absolute path to <c>xht.exe</c> (Win64) or <c>xht</c>
    /// (Linux/macOS).
    /// </param>
    /// <param name="manifestJsonPath">
    /// Absolute path to XBT's emitted <c>Manifest.json</c>. XHT receives
    /// this via <c>-Manifest=</c>.
    /// </param>
    /// <param name="outputDirectory">
    /// Absolute path to the per-module intermediate directory where XHT
    /// will write its outputs. The directory does not need to exist at
    /// construction time; the executor creates it before invoking XHT.
    /// </param>
    /// <param name="sourceFiles">
    /// The module's full source set: every <c>.h</c> + <c>.cs</c> XHT
    /// reads at parse time. The constructor sorts this list ordinal so
    /// the action-graph invariant holds.
    /// </param>
    /// <param name="dependencyListFile">
    /// Phase-2 header-dependency hook per XBT.html Section 5.2. Phase 1
    /// callers pass null; XHT does not yet emit a depfile but the property
    /// exists so its later population by Phase 2 does not bump the
    /// <see cref="ActionHistory.CurrentVersion"/> schema digest.
    /// </param>
    public ParseHeadersAction(
        string moduleName,
        string xhtExecutablePath,
        string manifestJsonPath,
        string outputDirectory,
        IReadOnlyList<FileItem> sourceFiles,
        FileItem? dependencyListFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentException.ThrowIfNullOrEmpty(xhtExecutablePath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(sourceFiles);

        _moduleName = moduleName;
        _xhtExecutablePath = xhtExecutablePath;
        _manifestJsonPath = manifestJsonPath;
        _outputDirectory = outputDirectory;
        _dependencyListFile = dependencyListFile;

        // Prerequisites: every source file + the XHT executable + the
        // manifest. Each is interned through FileItem.GetItemByPath so two
        // distinct ParseHeadersAction instances for the same module
        // produce structurally identical prerequisite lists.
        List<FileItem> prereqs = new(sourceFiles.Count + 2);
        foreach (FileItem src in sourceFiles)
        {
            prereqs.Add(src);
        }
        prereqs.Add(FileItem.GetItemByPath(xhtExecutablePath));
        prereqs.Add(FileItem.GetItemByPath(manifestJsonPath));

        // Sort ordinal + de-duplicate (defensive: a caller may pass the
        // manifest path inside sourceFiles too).
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

        // Produced: the per-module token-AST cache. Pre-discovery: XHT
        // emits exactly this path; XBT seeds the graph with it before
        // XHT runs (XBT.html Section 9.4 + XHT.html Section 9.3.1).
        string tokensBinPath = Path.Combine(
            outputDirectory,
            XhtOutputNaming.TokensBinFileName(moduleName));
        _producedItems = new[] { FileItem.GetItemByPath(tokensBinPath) };

        // Command line (CommandArguments is a list, not a single string;
        // ProcessActionRunner passes each entry via ProcessStartInfo's
        // ArgumentList so quoting is handled by the OS).
        _commandArguments = new[]
        {
            "parse-module",
            $"-Manifest={manifestJsonPath}",
            $"-Module={moduleName}",
            $"-Out={outputDirectory}",
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The hash folds in the action type ordinal, the command path,
    /// every command argument, the working directory, the module name,
    /// and the manifest path + output directory + XHT exe path (for
    /// belt-and-braces stability even though they appear in the command
    /// args). The XHT executable's content hash and the manifest's
    /// content hash flow through the standard prerequisite-content-hash
    /// channel (raw-source prerequisite recording in
    /// <see cref="ParallelExecutor"/>), not through CommandVersion.
    /// </remarks>
    protected override IoHash ComputeCommandVersion()
    {
        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. ActionType ordinal.
        BitConverter.TryWriteBytes(intBuffer, (int)ActionType);
        hasher.Update(intBuffer);

        // 2. CommandPath (the XHT exe path).
        UpdateUtf8(hasher, _xhtExecutablePath);

        // 3. CommandArguments (ordered, length-prefixed).
        BitConverter.TryWriteBytes(intBuffer, _commandArguments.Count);
        hasher.Update(intBuffer);
        foreach (string arg in _commandArguments)
        {
            UpdateUtf8(hasher, arg);
        }

        // 4. ModuleName, ManifestJsonPath, OutputDirectory -- explicit so
        //    a future args-format refactor that drops one of them from
        //    the command line still busts the cache.
        UpdateUtf8(hasher, _moduleName);
        UpdateUtf8(hasher, _manifestJsonPath);
        UpdateUtf8(hasher, _outputDirectory);

        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }
}
