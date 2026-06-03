// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph.Actions;

/// <summary>
/// XIL2CPP per-module transpile action. Maps to
/// <see cref="XActionType.XIL2CPPAction"/> (slot 4) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 9.8 +
/// <c>/Documents/XBT.html</c> Rev 11 Section 5.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 6.a honesty constraint -- this is a PARSE/BIND VALIDATION
/// action, not a codegen producer.</b> In Phase 6.a the XIL2CPP front-end
/// runs Roslyn Pass 1 (parse + bind) for one module and reports
/// diagnostics; it does NOT emit any <c>.cs.cpp</c> / <c>.cs.h</c> yet
/// (C++ emit is Phase 6.e). Modelling this node as a codegen action with a
/// fabricated <c>.cs.cpp</c> <see cref="ProducedItems"/> entry would lie
/// about an output the subprocess never writes, so this action declares NO
/// produced items and instead behaves like the
/// <see cref="ValidateCopyrightAction"/> validation pass: it invokes the
/// XIL2CPP <c>transpile-module</c> subprocess, which returns
/// <c>0</c> on a clean parse/bind and <c>63</c>
/// (<c>Xil2CppInternalFailure</c>) when Pass 1 reports error-severity
/// diagnostics, and the build's exit code surfaces that verbatim through
/// <see cref="ProcessActionRunner"/>. The subprocess shape (CommandPath =
/// the XIL2CPP exe, the <c>transpile-module</c> CLI arguments) is mirrored
/// from <see cref="ReferenceCompileCSharpAction"/>; when Phase 6.e wires
/// real C++ emit, this action grows a real <see cref="ProducedItems"/> set
/// and the <c>-Out=</c> argument, at which point the
/// <see cref="bUseActionHistory"/> override below is dropped.
/// </para>
/// <para>
/// <b>No-product validation pattern (mirrors
/// <see cref="ValidateCopyrightAction"/> with a null marker).</b> An action
/// with no <see cref="ProducedItems"/> is trivially considered up-to-date
/// by <c>ActionHistory.IsActionOutdated</c> (the produced-item loops are
/// empty), so it would be skipped after its first run -- which would defeat
/// a validation pass whose whole purpose is to re-run when its inputs
/// change. <see cref="ValidateCopyrightAction"/> solves the cached case by
/// writing a sentinel marker file from its in-process runner; XIL2CPP runs
/// as an out-of-process subprocess and the <c>transpile-module</c> mode
/// writes no file in Phase 6.a, so fabricating a stamp would require either
/// a lie (claim a produced file the subprocess never writes) or a runner
/// special-case (which the wiring contract avoids -- the existing
/// <see cref="ProcessActionRunner"/> handles this action with no
/// special-casing). Instead this action sets
/// <see cref="bUseActionHistory"/> to <c>false</c> so the executor runs the
/// validation on every build (the same "runs unconditionally" semantics
/// <see cref="ValidateCopyrightAction"/> uses when constructed with a null
/// marker path). Phase 6.e replaces this with real produced outputs +
/// content-hash caching.
/// </para>
/// <para>
/// <b>Subprocess shape.</b> Invokes the <c>xil2cpp</c> executable with the
/// <c>transpile-module</c> mode (XIL2CPP public CLI per
/// <c>/Documents/XIL2CPP.html</c> Section 9.8 + the
/// <c>TranspileModuleMode</c> Pass-1 front-end):
/// </para>
/// <code>
/// xil2cpp transpile-module
///     -Manifest=&lt;path-to-XBT-Manifest.json&gt;
///     -Module=&lt;ModuleName&gt;
/// </code>
/// <para>
/// <b>Prerequisites (the Section 9.8 ordering chain).</b> The ordinal-sorted
/// union of (1) the module's own <c>.cs</c> sources (Roslyn parses these),
/// (2) each dependency module's <c>&lt;Dep&gt;.refonly.dll</c> -- the
/// reference-only assembly the dependency's
/// <see cref="ReferenceCompileCSharpAction"/> produces -- so the action
/// graph orders <c>ReferenceCompileCSharpAction(M_dep)</c> BEFORE
/// <c>XIL2CPPAction(M)</c> via the produced/consumed <see cref="FileItem"/>
/// edge (the same edge mechanism <see cref="EmitReflectionAction"/> uses to
/// depend on <see cref="ParseHeadersAction"/>), (3) the XIL2CPP executable,
/// and (4) the XBT manifest. Each is interned through
/// <see cref="FileItem.GetItemByPath"/> so two instances for the same
/// module produce structurally identical lists.
/// </para>
/// <para>
/// <b>Determinism.</b> The action's <see cref="CommandVersion"/> is
/// deterministic across reconstruction with the same inputs: the hash folds
/// in the action-type ordinal, the XIL2CPP executable path, the command
/// arguments, the module name, the manifest path, the intermediate
/// directory, every source-file path (sorted ordinal), the working
/// directory, the configuration, the platform, and the sim-path flag. The
/// XIL2CPP exe's content hash + the manifest's content hash + each
/// dependency reference DLL's content hash flow through the standard
/// prerequisite-content-hash channel.
/// </para>
/// <para>
/// <b>Dependency-ABI cache surfacing.</b> Each dependency's reference DLL is
/// surfaced explicitly in <see cref="CacheKeyComponents"/> as a
/// <c>RefOnlyDep=&lt;name&gt;@&lt;path&gt;</c> component (the same shape
/// <see cref="ReferenceCompileCSharpAction"/> uses) so that once Phase 6.e
/// turns this into a cached producer, a dependency's public-surface (ABI)
/// change is observable in this action's cache-key identity (Section 9.8).
/// </para>
/// </remarks>
public sealed class XIL2CPPAction : ActionBase
{
    private readonly string _moduleName;
    private readonly string _xil2cppExecutablePath;
    private readonly string _manifestJsonPath;
    private readonly string _intermediateDirectory;
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<FileItem> _prerequisiteItems;
    private readonly IReadOnlyList<string> _commandArguments;
    private readonly IReadOnlyList<string> _sortedSourcePaths;
    private readonly IReadOnlyList<DependencyReference> _dependencyReferences;
    private readonly IReadOnlyList<string> _cacheKeyComponents;
    private readonly string? _tier;
    private readonly bool _simPath;
    private readonly BuildConfiguration _configuration;
    private readonly Platform _platform;

    /// <inheritdoc/>
    public override XActionType ActionType => XActionType.XIL2CPPAction;

    /// <inheritdoc/>
    public override IReadOnlyList<FileItem> PrerequisiteItems => _prerequisiteItems;

    /// <inheritdoc/>
    /// <remarks>
    /// Empty in Phase 6.a: the <c>transpile-module</c> mode is a parse/bind
    /// validation pass and writes no file (no C++ emit until Phase 6.e). See
    /// the class remarks for why a fabricated stamp is deliberately avoided.
    /// </remarks>
    public override IReadOnlyList<FileItem> ProducedItems => EmptyFileItems;

    /// <inheritdoc/>
    public override string CommandPath => _xil2cppExecutablePath;

    /// <inheritdoc/>
    public override IReadOnlyList<string> CommandArguments => _commandArguments;

    /// <inheritdoc/>
    /// <remarks>
    /// Audit fix R7-C2 discipline (see
    /// <see cref="ReferenceCompileCSharpAction.WorkingDirectory"/>): captured
    /// at construction; never re-read at execution so two builds from
    /// different shell CWDs produce identical command-version hashes.
    /// </remarks>
    public override string WorkingDirectory => _workingDirectory;

    /// <inheritdoc/>
    public override string CommandDescription => "XIL2CPP.Transpile";

    /// <inheritdoc/>
    public override string StatusDescription => _moduleName;

    /// <inheritdoc/>
    /// <remarks>
    /// Phase 6.a: this is a parse/bind VALIDATION pass with no produced
    /// items. An action with no produced items is trivially up-to-date in
    /// <c>ActionHistory.IsActionOutdated</c> and would be skipped after its
    /// first run, so -- exactly as <see cref="ValidateCopyrightAction"/>
    /// does when constructed without a marker file -- this action opts out
    /// of the action-history cache and runs the validation on every build.
    /// Phase 6.e flips this back to the default <c>true</c> once real
    /// produced outputs anchor the cache.
    /// </remarks>
    public override bool bUseActionHistory => false;

    /// <inheritdoc/>
    /// <remarks>
    /// XIL2CPP reads only input files declared as prerequisites (the
    /// module's sources, its dependencies' reference DLLs, the manifest) and
    /// writes nothing in Phase 6.a; no system-resource access beyond that,
    /// so the action is safely transportable to a Phase 2 remote executor.
    /// </remarks>
    public override bool CanExecuteRemotely => true;

    /// <inheritdoc/>
    /// <remarks>
    /// A per-module parse/bind is roughly peer to a per-source compile;
    /// weight 1.0 matches <see cref="ReferenceCompileCSharpAction.Weight"/>
    /// so the scheduler treats the XIL2CPP passes as ordinary work items.
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
    /// <remarks>
    /// Each dependency module's reference-DLL identity is surfaced as one
    /// <c>RefOnlyDep=&lt;name&gt;@&lt;path&gt;</c> component (the same shape
    /// <see cref="ReferenceCompileCSharpAction"/> emits) so that once Phase
    /// 6.e makes this a cached producer, a dependency ABI change is
    /// observable in the cache-key identity (Section 9.8). Components are
    /// emitted in ordinal-sorted dependency-name order for determinism.
    /// </remarks>
    public override IReadOnlyList<string> CacheKeyComponents => _cacheKeyComponents;

    /// <summary>The module this action transpile-validates.</summary>
    public string ModuleName => _moduleName;

    /// <summary>
    /// Absolute path to the XIL2CPP executable (e.g.
    /// <c>&lt;EngineRoot&gt;/Binaries/Win64/xil2cpp.exe</c>).
    /// </summary>
    public string Xil2CppExecutablePath => _xil2cppExecutablePath;

    /// <summary>Absolute path to XBT's emitted <c>Manifest.json</c> XIL2CPP reads.</summary>
    public string ManifestJsonPath => _manifestJsonPath;

    /// <summary>
    /// Absolute path to the per-build intermediate root for the module's
    /// XIL2CPP work. Folds into the command version so a relocation of the
    /// intermediate tree busts the action even though Phase 6.a writes no
    /// file there yet.
    /// </summary>
    public string IntermediateDirectory => _intermediateDirectory;

    /// <summary>
    /// The module's C# source paths that contributed to the prerequisite +
    /// command-version inputs, ordinal-sorted.
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

    /// <summary>Construct an XIL2CPP transpile-validation action for one module.</summary>
    /// <param name="moduleName">
    /// The module's <c>Name</c> from the manifest. XIL2CPP receives this via
    /// <c>-Module=</c>.
    /// </param>
    /// <param name="xil2cppExecutablePath">
    /// Absolute path to <c>xil2cpp.exe</c> (Win64) or <c>xil2cpp</c>
    /// (Linux/macOS). Resolved by the caller the same way
    /// <see cref="ReferenceCompileCSharpAction"/>'s executable path is (the
    /// action stores the supplied path verbatim).
    /// </param>
    /// <param name="manifestJsonPath">
    /// Absolute path to XBT's emitted <c>Manifest.json</c>. XIL2CPP receives
    /// this via <c>-Manifest=</c>.
    /// </param>
    /// <param name="intermediateDirectory">
    /// Absolute path to the per-build intermediate root for the module's
    /// XIL2CPP work. Phase 6.a writes nothing there (parse/bind only); the
    /// path folds into the command version for relocation sensitivity.
    /// </param>
    /// <param name="sourceFiles">
    /// The module's C# source set: every <c>.cs</c> file Roslyn parses. The
    /// constructor sorts this list ordinal so the action-graph invariant
    /// holds. May be empty.
    /// </param>
    /// <param name="dependencyReferences">
    /// Each dependency module's reference-DLL identity (name + absolute
    /// <c>.refonly.dll</c> path). These DLLs become prerequisites (so the
    /// <c>ReferenceCompileCSharpAction(M_dep) -&gt; XIL2CPPAction(M)</c>
    /// Section 9.8 ordering edge holds) and surface in
    /// <see cref="CacheKeyComponents"/>. May be empty.
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
    public XIL2CPPAction(
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
        bool simPath = false)
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

        // Snapshot the dependency-reference identities, ordinal-sorted by
        // module name + deduped (defensive against a caller listing a
        // dependency twice). The sort makes both the prerequisite ordering
        // and the cache-key component ordering deterministic. Mirrors
        // ReferenceCompileCSharpAction's dependency-snapshot pipeline.
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
        // command-version inputs.
        List<string> sourcePaths = new(sourceFiles.Count);
        foreach (FileItem src in sourceFiles)
        {
            sourcePaths.Add(src.FullPath);
        }
        sourcePaths.Sort(StringComparer.Ordinal);
        _sortedSourcePaths = sourcePaths;

        // Prerequisites: every source file + each dependency's reference DLL
        // + the XIL2CPP executable + the manifest. Same intern + sort + dedupe
        // pipeline as ReferenceCompileCSharpAction.
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
        // ArgumentList so quoting is handled by the OS). Phase 6.a:
        // transpile-module is a parse/bind validation pass and does not
        // require -Out= (TranspileModuleMode.Parse uses requireOutput:
        // false), so no -Out= is emitted -- it would name a file the
        // subprocess does not write.
        _commandArguments = new[]
        {
            "transpile-module",
            $"-Manifest={manifestJsonPath}",
            $"-Module={moduleName}",
        };

        // CacheKeyComponents: surface each dependency's reference-DLL
        // identity. Already ordinal-sorted by dependency-module name above.
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
    /// manifest path, the intermediate directory, every source-file path
    /// (sorted ordinal), the working directory, and the configuration /
    /// platform / sim-path triple. The XIL2CPP executable's content hash,
    /// the manifest's content hash, and each dependency reference-DLL's
    /// content hash flow through the standard prerequisite-content-hash
    /// channel.
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

        // 4. ModuleName, ManifestJsonPath, IntermediateDirectory -- explicit
        //    so a future args-format refactor that drops one of them from
        //    the command line still busts the cache.
        UpdateUtf8(hasher, _moduleName);
        UpdateUtf8(hasher, _manifestJsonPath);
        UpdateUtf8(hasher, _intermediateDirectory);

        // 5. Sorted source paths (ordered, length-prefixed).
        BitConverter.TryWriteBytes(intBuffer, _sortedSourcePaths.Count);
        hasher.Update(intBuffer);
        foreach (string path in _sortedSourcePaths)
        {
            UpdateUtf8(hasher, path);
        }

        // 6. WorkingDirectory + Configuration + Platform + SimPath (audit fix
        //    R7-C2): same discipline as ReferenceCompileCSharpAction.
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
