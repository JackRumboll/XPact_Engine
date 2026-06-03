// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph.Actions;

/// <summary>
/// XIL2CPP reference-compile action: produces one module's
/// <c>&lt;Module&gt;.refonly.dll</c> (a metadata-only reference assembly:
/// type + method signatures, no method bodies) so a downstream module's
/// XIL2CPP transpile can resolve cross-module C# types through Roslyn
/// against its dependencies' public surface. Maps to
/// <see cref="XActionType.ReferenceCompileCSharpAction"/> (slot 14) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8 +
/// <c>/Documents/XBT.html</c> Rev 11 Section 5.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Subprocess shape.</b> Invokes the <c>xil2cpp</c> executable with the
/// <c>refonly-compile</c> mode (XIL2CPP public CLI per
/// <c>/Documents/XIL2CPP.html</c> Section 9.8). Running the Roslyn emit as a
/// subprocess of the XIL2CPP binary keeps Roslyn OUT of
/// <c>XBT.ActionGraph</c> and reuses XIL2CPP.Frontend's compilation builder:
/// </para>
/// <code>
/// xil2cpp refonly-compile
///     -Manifest=&lt;path-to-XBT-Manifest.json&gt;
///     -Module=&lt;ModuleName&gt;
///     -Out=&lt;producedDllPath&gt;
/// </code>
/// <para>
/// <b>Output.</b> Exactly one reference-only DLL at
/// <c>&lt;intermediateDir&gt;/&lt;Module&gt;/Reference/&lt;Module&gt;.refonly.dll</c>
/// (Section 9.8: "The reference DLL is published to
/// <c>Intermediate/.../&lt;Module&gt;/Reference/M.refonly.dll</c>"). XBT
/// pre-discovers this path so the action graph can express the
/// <c>ReferenceCompileCSharpAction(M_dep) -&gt; XIL2CPPAction(M)</c> ordering
/// edge before the subprocess runs (Section 9.8 prerequisite chain). The
/// directory does not need to exist at construction time; the XIL2CPP mode
/// creates the <c>Reference/</c> subdirectory before writing.
/// </para>
/// <para>
/// <b>Prerequisites.</b> The ordinal-sorted union of (1) the module's own
/// <c>.cs</c> sources (Roslyn parses these to emit the reference assembly),
/// (2) each dependency module's <c>&lt;Dep&gt;.refonly.dll</c> (Roslyn
/// resolves cross-module types against them while emitting M's signatures),
/// (3) the XIL2CPP executable, and (4) the XBT manifest. Each is interned
/// through <see cref="FileItem.GetItemByPath"/> so two instances for the
/// same module produce structurally identical lists.
/// </para>
/// <para>
/// <b>Determinism.</b> The action's <see cref="CommandVersion"/> is
/// deterministic across reconstruction with the same inputs: the hash folds
/// in the action-type ordinal, the XIL2CPP executable path, the command
/// arguments, the module name, the manifest path, the output DLL path, every
/// source-file path (sorted ordinal), the working directory, the
/// configuration, the platform, and the sim-path flag. The XIL2CPP exe's
/// content hash + the manifest's content hash flow through the standard
/// prerequisite-content-hash channel (raw-source prerequisite recording in
/// <see cref="ParallelExecutor"/>), not through CommandVersion.
/// </para>
/// <para>
/// <b>Dependency-ABI cache invalidation.</b> A dependency's reference DLL is
/// both a prerequisite (so its content hash participates in the standard
/// staleness check) AND surfaced explicitly in
/// <see cref="CacheKeyComponents"/> as a <c>RefOnlyDep=&lt;name&gt;@&lt;path&gt;</c>
/// component, so a dependency's public-surface (ABI) change busts this
/// action's cache key even when M's own sources and command line are
/// byte-identical (Section 9.8 + XBT.html Section 5.1: a dependency reference
/// DLL is part of the consumer's cache key).
/// </para>
/// </remarks>
public sealed class ReferenceCompileCSharpAction : ActionBase
{
    /// <summary>
    /// The per-module reference-DLL subdirectory name per Section 9.8
    /// (<c>Intermediate/.../&lt;Module&gt;/Reference/</c>). Mirrors
    /// XIL2CPP.Frontend's <c>MetadataReferenceResolver.ReferenceSubdirectory</c>;
    /// duplicated here because XBT.ActionGraph does not reference the XIL2CPP
    /// projects (the join surface is the subprocess CLI + the on-disk DLL).
    /// </summary>
    public const string ReferenceSubdirectory = "Reference";

    /// <summary>
    /// The reference-DLL filename suffix per Section 9.8
    /// (<c>&lt;Module&gt;.refonly.dll</c>). Mirrors XIL2CPP.Frontend's
    /// <c>MetadataReferenceResolver.RefOnlyDllSuffix</c>.
    /// </summary>
    public const string RefOnlyDllSuffix = ".refonly.dll";

    private readonly string _moduleName;
    private readonly string _xil2cppExecutablePath;
    private readonly string _manifestJsonPath;
    private readonly string _intermediateDirectory;
    private readonly string _producedDllPath;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<FileItem> _prerequisiteItems;
    private readonly IReadOnlyList<FileItem> _producedItems;
    private readonly IReadOnlyList<string> _commandArguments;
    private readonly IReadOnlyList<string> _sortedSourcePaths;
    private readonly IReadOnlyList<DependencyReference> _dependencyReferences;
    private readonly IReadOnlyList<string> _cacheKeyComponents;
    private readonly FileItem? _dependencyListFile;
    private readonly string? _tier;
    private readonly bool _simPath;
    private readonly BuildConfiguration _configuration;
    private readonly Platform _platform;

    /// <inheritdoc/>
    public override XActionType ActionType => XActionType.ReferenceCompileCSharpAction;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> PrerequisiteItems => _prerequisiteItems;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> ProducedItems => _producedItems;

    /// <inheritdoc/>
    public override string CommandPath => _xil2cppExecutablePath;

    /// <inheritdoc/>
    public override IReadOnlyList<string> CommandArguments => _commandArguments;

    /// <inheritdoc/>
    /// <remarks>
    /// Audit fix R7-C2 discipline (see
    /// <see cref="ParseHeadersAction.WorkingDirectory"/>): captured at
    /// construction; never re-read at execution so two builds from different
    /// shell CWDs produce identical command-version hashes.
    /// </remarks>
    public override string WorkingDirectory => _workingDirectory;

    /// <inheritdoc/>
    public override string CommandDescription => "XIL2CPP.RefCompile";

    /// <inheritdoc/>
    public override string StatusDescription => _moduleName;

    /// <inheritdoc/>
    /// <remarks>
    /// XIL2CPP reads only input files declared as prerequisites (the
    /// module's sources, its dependencies' reference DLLs, the manifest) and
    /// writes only the declared <c>.refonly.dll</c>; no system-resource
    /// access beyond that, so the action is safely transportable to a Phase 2
    /// remote executor.
    /// </remarks>
    public override bool CanExecuteRemotely => true;

    /// <inheritdoc/>
    /// <remarks>
    /// A reference compile is metadata-only (no method bodies emitted) and
    /// Section 9.8 notes the action is fast ("~1 sec per 1k LoC"); weight 1.0
    /// matches the per-source compile-action weight so the scheduler treats
    /// them as peers.
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

    /// <inheritdoc/>
    /// <remarks>
    /// In addition to the standard prerequisite-content-hash channel (each
    /// dependency <c>.refonly.dll</c> is also a prerequisite), the dependency
    /// reference-DLL identity is surfaced here as one
    /// <c>RefOnlyDep=&lt;name&gt;@&lt;path&gt;</c> component per dependency so a
    /// dependency ABI change is observable in this action's cache-key
    /// identity (Section 9.8). Components are emitted in ordinal-sorted
    /// dependency-name order for determinism.
    /// </remarks>
    public override IReadOnlyList<string> CacheKeyComponents => _cacheKeyComponents;

    /// <summary>The module this action reference-compiles.</summary>
    public string ModuleName => _moduleName;

    /// <summary>
    /// Absolute path to the XIL2CPP executable (e.g.
    /// <c>&lt;EngineRoot&gt;/Binaries/Win64/xil2cpp.exe</c>).
    /// </summary>
    public string Xil2CppExecutablePath => _xil2cppExecutablePath;

    /// <summary>Absolute path to XBT's emitted <c>Manifest.json</c> XIL2CPP reads.</summary>
    public string ManifestJsonPath => _manifestJsonPath;

    /// <summary>
    /// Absolute path to the per-build intermediate root under which the
    /// module's <c>Reference/&lt;Module&gt;.refonly.dll</c> is published. The
    /// produced DLL path is
    /// <c>&lt;intermediateDirectory&gt;/&lt;Module&gt;/Reference/&lt;Module&gt;.refonly.dll</c>.
    /// </summary>
    public string IntermediateDirectory => _intermediateDirectory;

    /// <summary>
    /// Absolute path of the produced reference-only DLL (the single
    /// <see cref="ProducedItems"/> entry), supplied to the subprocess via
    /// <c>-Out=</c>.
    /// </summary>
    public string ProducedDllPath => _producedDllPath;

    /// <summary>
    /// The module's C# source paths that contributed to the prerequisite +
    /// command-version inputs, ordinal-sorted. Used by tests + tooling to
    /// inspect what XIL2CPP will parse.
    /// </summary>
    public IReadOnlyList<string> SortedSourcePaths => _sortedSourcePaths;

    /// <summary>
    /// The dependency-module reference DLLs this action consumes (name +
    /// absolute <c>.refonly.dll</c> path), ordinal-sorted by module name.
    /// </summary>
    public IReadOnlyList<DependencyReference> DependencyReferences => _dependencyReferences;

    /// <summary>
    /// One dependency module's reference-DLL identity: the dependency's
    /// module name and the absolute path of its
    /// <c>&lt;Dep&gt;.refonly.dll</c>.
    /// </summary>
    /// <param name="ModuleName">The dependency module's name.</param>
    /// <param name="RefOnlyDllPath">Absolute path of the dependency's reference DLL.</param>
    public readonly record struct DependencyReference(string ModuleName, string RefOnlyDllPath);

    /// <summary>Construct a reference-compile action for one module.</summary>
    /// <param name="moduleName">
    /// The module's <c>Name</c> from the manifest. XIL2CPP receives this via
    /// <c>-Module=</c>. The produced DLL is named <c>&lt;moduleName&gt;.refonly.dll</c>.
    /// </param>
    /// <param name="xil2cppExecutablePath">
    /// Absolute path to <c>xil2cpp.exe</c> (Win64) or <c>xil2cpp</c>
    /// (Linux/macOS). Resolved by the caller the same way
    /// <see cref="ParseHeadersAction"/>'s <c>xhtExecutablePath</c> is (the
    /// action stores the supplied path verbatim).
    /// </param>
    /// <param name="manifestJsonPath">
    /// Absolute path to XBT's emitted <c>Manifest.json</c>. XIL2CPP receives
    /// this via <c>-Manifest=</c>.
    /// </param>
    /// <param name="intermediateDirectory">
    /// Absolute path to the per-build intermediate root under which the
    /// module's <c>Reference/&lt;Module&gt;.refonly.dll</c> is published per
    /// Section 9.8. The directory tree does not need to exist at construction
    /// time; the XIL2CPP mode creates the <c>Reference/</c> subdirectory
    /// before writing.
    /// </param>
    /// <param name="sourceFiles">
    /// The module's C# source set: every <c>.cs</c> file Roslyn parses to
    /// emit the reference assembly. The constructor sorts this list ordinal
    /// so the action-graph invariant holds. May be empty (a module with no
    /// sources still emits an empty reference assembly).
    /// </param>
    /// <param name="dependencyReferences">
    /// Each dependency module's reference-DLL identity (name + absolute
    /// <c>.refonly.dll</c> path). These DLLs become prerequisites (so the
    /// <c>ReferenceCompileCSharpAction(M_dep) -&gt; ReferenceCompileCSharpAction(M)</c>
    /// ordering edge holds) and surface in <see cref="CacheKeyComponents"/>
    /// so a dependency ABI change busts this action. May be empty.
    /// </param>
    /// <param name="workingDirectory">
    /// The action's working directory, captured at construction (audit fix
    /// R7-C2). MUST be the manifest's <c>RootLocalPath</c> (the canonical
    /// engine root) so two builds run from different shell CWDs produce
    /// identical action commands. When null,
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
    public ReferenceCompileCSharpAction(
        string moduleName,
        string xil2cppExecutablePath,
        string manifestJsonPath,
        string intermediateDirectory,
        IReadOnlyList<FileItem> sourceFiles,
        IReadOnlyList<DependencyReference>? dependencyReferences = null,
        string? workingDirectory = null,
        string? tier = null,
        BuildConfiguration configuration = default,
        Platform platform = default,
        bool simPath = false,
        FileItem? dependencyListFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentException.ThrowIfNullOrEmpty(xil2cppExecutablePath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(intermediateDirectory);
        ArgumentNullException.ThrowIfNull(sourceFiles);

        _moduleName = moduleName;
        _xil2cppExecutablePath = xil2cppExecutablePath;
        _manifestJsonPath = manifestJsonPath;
        _intermediateDirectory = intermediateDirectory;
        // Audit fix R7-C2: capture working directory once at construction.
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _tier = tier;
        _configuration = configuration;
        _platform = platform;
        _simPath = simPath;
        _dependencyListFile = dependencyListFile;

        // Pre-discovered output: <intermediate>/<Module>/Reference/<Module>.refonly.dll
        // (Section 9.8 layout). XBT seeds the graph with this path before
        // XIL2CPP runs; the refonly-compile mode emits exactly this path.
        _producedDllPath = Path.Combine(
            intermediateDirectory,
            moduleName,
            ReferenceSubdirectory,
            moduleName + RefOnlyDllSuffix);
        _producedItems = new[] { FileItem.GetItemByPath(_producedDllPath) };

        // Snapshot the dependency-reference identities, ordinal-sorted by
        // module name + deduped (defensive against a caller listing a
        // dependency twice). The sort makes both the prerequisite ordering
        // and the cache-key component ordering deterministic.
        List<DependencyReference> deps = new();
        if (dependencyReferences is not null)
        {
            List<DependencyReference> raw = new(dependencyReferences.Count);
            foreach (DependencyReference dep in dependencyReferences)
            {
                ArgumentException.ThrowIfNullOrEmpty(dep.ModuleName, nameof(dependencyReferences));
                ArgumentException.ThrowIfNullOrEmpty(dep.RefOnlyDllPath, nameof(dependencyReferences));
                raw.Add(dep);
            }
            raw.Sort(static (a, b) => string.CompareOrdinal(a.ModuleName, b.ModuleName));
            string? lastName = null;
            foreach (DependencyReference dep in raw)
            {
                if (!string.Equals(lastName, dep.ModuleName, StringComparison.Ordinal))
                {
                    deps.Add(dep);
                    lastName = dep.ModuleName;
                }
            }
        }
        _dependencyReferences = deps;

        // Snapshot the module's source paths, ordinal-sorted, for the
        // command-version inputs (the produced reference assembly's identity
        // changes when the source set changes).
        List<string> sourcePaths = new(sourceFiles.Count);
        foreach (FileItem src in sourceFiles)
        {
            sourcePaths.Add(src.FullPath);
        }
        sourcePaths.Sort(StringComparer.Ordinal);
        _sortedSourcePaths = sourcePaths;

        // Prerequisites: every source file + each dependency's reference DLL
        // + the XIL2CPP executable + the manifest. Same intern + sort + dedupe
        // pipeline as ParseHeadersAction.
        List<FileItem> prereqs = new(sourceFiles.Count + deps.Count + 2);
        foreach (FileItem src in sourceFiles)
        {
            prereqs.Add(src);
        }
        foreach (DependencyReference dep in deps)
        {
            prereqs.Add(FileItem.GetItemByPath(dep.RefOnlyDllPath));
        }
        prereqs.Add(FileItem.GetItemByPath(xil2cppExecutablePath));
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

        // Command line (CommandArguments is a list, not a single string;
        // ProcessActionRunner passes each entry via ProcessStartInfo's
        // ArgumentList so quoting is handled by the OS). The -Out= value is
        // the full produced DLL path (the XIL2CPP mode resolves the
        // Reference/ directory from it).
        _commandArguments = new[]
        {
            "refonly-compile",
            $"-Manifest={manifestJsonPath}",
            $"-Module={moduleName}",
            $"-Out={_producedDllPath}",
        };

        // CacheKeyComponents: surface each dependency's reference-DLL
        // identity so a dependency ABI change busts this action's cache key.
        // Already ordinal-sorted by dependency-module name above.
        List<string> cacheKeyComponents = new(deps.Count);
        foreach (DependencyReference dep in deps)
        {
            cacheKeyComponents.Add($"RefOnlyDep={dep.ModuleName}@{dep.RefOnlyDllPath}");
        }
        _cacheKeyComponents = cacheKeyComponents.Count == 0 ? EmptyStrings : cacheKeyComponents;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The hash folds in the action-type ordinal, the command path (the
    /// XIL2CPP exe path), every command argument, the module name, the
    /// manifest path, the produced DLL path, every source-file path (sorted
    /// ordinal), the working directory, and the configuration / platform /
    /// sim-path triple. The XIL2CPP executable's content hash and the
    /// manifest's content hash flow through the standard
    /// prerequisite-content-hash channel; the dependency reference-DLL
    /// content flows through both the prerequisite channel AND
    /// <see cref="CacheKeyComponents"/>.
    /// </remarks>
    protected override IoHash ComputeCommandVersion()
    {
        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. ActionType ordinal.
        BitConverter.TryWriteBytes(intBuffer, (int)ActionType);
        hasher.Update(intBuffer);

        // 2. CommandPath (the XIL2CPP exe path).
        UpdateUtf8(hasher, _xil2cppExecutablePath);

        // 3. CommandArguments (ordered, length-prefixed).
        BitConverter.TryWriteBytes(intBuffer, _commandArguments.Count);
        hasher.Update(intBuffer);
        foreach (string arg in _commandArguments)
        {
            UpdateUtf8(hasher, arg);
        }

        // 4. ModuleName, ManifestJsonPath, OutputDir (intermediate root),
        //    ProducedDllPath -- explicit so a future args-format refactor
        //    that drops one of them from the command line still busts the
        //    cache.
        UpdateUtf8(hasher, _moduleName);
        UpdateUtf8(hasher, _manifestJsonPath);
        UpdateUtf8(hasher, _intermediateDirectory);
        UpdateUtf8(hasher, _producedDllPath);

        // 5. Sorted source paths (ordered, length-prefixed). The reference
        //    assembly's identity changes when the source set changes, so the
        //    action's command-version must change too.
        BitConverter.TryWriteBytes(intBuffer, _sortedSourcePaths.Count);
        hasher.Update(intBuffer);
        foreach (string path in _sortedSourcePaths)
        {
            UpdateUtf8(hasher, path);
        }

        // 6. WorkingDirectory + Configuration + Platform + SimPath (audit fix
        //    R7-C2): same discipline as ParseHeadersAction.ComputeCommandVersion.
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
