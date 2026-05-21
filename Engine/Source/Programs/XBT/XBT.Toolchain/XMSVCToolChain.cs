// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// MSVC toolchain integration for Win64 builds. Translates per-module
/// rules into the <c>cl.exe</c>/<c>link.exe</c> flag set; emits the
/// unconditional reproducibility envelope per
/// <c>/Documents/XBT.html</c> Rev 4 Section 19.1 and Toolchain Contract
/// Rev 13.2 Section 2.1.
/// </summary>
/// <remarks>
/// <para>
/// Supported front-ends: MSVC <c>cl.exe</c> &ge; 17.10 (VS 2026 BuildTools)
/// and <c>clang-cl.exe</c> &ge; 18 as an alternative. Discovery is via
/// <see cref="VCEnvironment.TryDiscover"/> (probes <c>vswhere.exe</c>).
/// </para>
/// <para>
/// Reproducibility envelope (emitted on every compile + link, unconditional):
/// </para>
/// <list type="bullet">
///   <item><c>/Brepro</c> -- strip compiler-side .obj timestamp.</item>
///   <item><c>/BREPRO</c> (link) -- strip linker-side PE timestamp.</item>
///   <item><c>/TIMESTAMP:0</c> (link) -- pin PE header timestamp to zero.</item>
///   <item><c>/INCREMENTAL:NO</c> (link) -- incremental linking is incompatible with /Brepro.</item>
///   <item><c>/pathmap:&lt;RepoRoot&gt;=X:/R</c> -- normalize absolute paths in DWARF/PDB.</item>
///   <item><c>/d2:-cgmanifestencoded-</c> -- suppress an undocumented host-name embed.</item>
///   <item><c>/cgthreads:8</c> (link) -- pin LTO codegen thread count for reproducibility under /GL + /LTCG (Contract Section 2.1). Emitted unconditionally; harmless when LTO is off.</item>
/// </list>
/// <para>
/// SimPath modules additionally receive <c>/fp:precise</c> +
/// <c>/FI XSimPathMathOverrides.h</c>; banned flags
/// (<c>/fp:fast</c>, <c>/fp:except</c>) cause a build failure with
/// exit code 41.
/// </para>
/// </remarks>
public sealed class XMSVCToolChain : XToolChain
{
    /// <summary>
    /// The default Win32 system import libraries every native module
    /// links against. This is the same list the MSVC IDE silently
    /// injects via <c>VCProject.DefaultLibraries</c> (see Microsoft's
    /// documentation of <c>/DEFAULTLIB</c>); replicating it explicitly
    /// makes the link line self-describing and reproducible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order is fixed (intentionally not alphabetic) so the
    /// /VERBOSE:LIB report cross-references the same column ordering
    /// across two clean builds. The list is the union of:
    /// <list type="bullet">
    ///   <item>kernel32, user32, gdi32: core Win32 surface.</item>
    ///   <item>winspool, comdlg32, advapi32, shell32: shell + crypto helpers
    ///   (advapi32 carries RegOpenKeyEx + the CSP shims).</item>
    ///   <item>ole32, oleaut32, uuid: COM + Automation interop the C runtime
    ///   itself pulls in via <c>&lt;objbase.h&gt;</c>.</item>
    ///   <item>odbc32, odbccp32: ODBC + the data-access stack (kept for parity
    ///   with cl.exe's compiled-in defaults).</item>
    /// </list>
    /// </para>
    /// </remarks>
    private static readonly string[] DefaultSystemLibs =
    {
        "kernel32.lib",
        "user32.lib",
        "gdi32.lib",
        "winspool.lib",
        "comdlg32.lib",
        "advapi32.lib",
        "shell32.lib",
        "ole32.lib",
        "oleaut32.lib",
        "uuid.lib",
        "odbc32.lib",
        "odbccp32.lib",
    };

    private readonly VCEnvironment _environment;
    private readonly string _repoRoot;

    /// <inheritdoc/>
    public override Platform Platform => Platform.Win64;

    /// <inheritdoc/>
    public override string ToolchainVersion => _environment.CompilerVersion;

    /// <summary>The active MSVC environment record (compiler/linker paths, includes, libs).</summary>
    public VCEnvironment Environment => _environment;

    /// <summary>
    /// Construct from a discovered environment. Tests pass a synthetic
    /// environment via <see cref="VCEnvironment.ForTesting"/>; production
    /// constructs from <see cref="VCEnvironment.TryDiscover"/>.
    /// </summary>
    /// <param name="environment">Discovered MSVC environment.</param>
    /// <param name="repoRoot">
    /// Absolute path to the repo root, used for the
    /// <c>/pathmap:&lt;root&gt;=X:/R</c> normalization flag. Pass
    /// <see cref="RepoRoot.GetRepoRoot"/> at production sites; pass a
    /// synthetic path in tests.
    /// </param>
    public XMSVCToolChain(VCEnvironment environment, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        _environment = environment;
        _repoRoot = repoRoot;
    }

    /// <inheritdoc/>
    public override void DiscoverEnvironment()
    {
        // The environment is passed in via the constructor; nothing to
        // do here at present. Hook exists so future probe-refresh paths
        // (e.g. detecting a VS upgrade mid-session) can override.
    }

    /// <inheritdoc/>
    public override IReadOnlyList<IExternalAction> CompileSource(
        ModuleRules module,
        TargetRules target,
        FileItem sourceFile,
        string outputDir,
        PCHBinding? pch = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        List<string> args = new();

        // === Reproducibility envelope (XBT.html Section 19.1) ===
        args.Add("/c");
        args.Add("/nologo");
        args.Add("/Brepro");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        args.Add("/d2:-cgmanifestencoded-");

        // === Determinism + SimPath ===
        args.AddRange(GetCompileArguments_FPSemantics_Resolved(module));
        if (module.SimPath)
        {
            args.AddRange(GetCompileArguments_SimPath(module));
        }

        // === SIMD lever (Section 4.6) ===
        SimdLevel resolved = ResolveSimdLevel(module, target);
        args.AddRange(GetCompileArguments_Simd(resolved, module.SimPath));

        // === Optimisation ===
        args.AddRange(GetCompileArguments_OptimizeCode(module.OptimizeCode, target.Configuration));

        // === Exceptions + RTTI ===
        if (module.bEnableExceptions)
        {
            args.Add("/EHsc");
        }
        if (!module.bUseRTTI)
        {
            args.Add("/GR-");
        }

        // === Warnings as errors ===
        if (module.bWarningsAsErrors)
        {
            args.Add("/WX");
        }

        // === StationRole compile-define (Section 4.10) ===
        if (target.TargetType == BuildTargetType.Game
         || (target.TargetType == BuildTargetType.Editor && target.StationRole == StationRole.Engineer))
        {
            string roleName = target.StationRole switch
            {
                StationRole.Engineer => "Engineer",
                StationRole.Instructor => "Instructor",
                StationRole.Trainee => "Trainee",
                _ => "Engineer",
            };
            args.Add($"/DXPACT_STATION_ROLE={roleName}");
        }

        // === Per-module preprocessor ===
        foreach (string def in module.PublicDefinitions)
        {
            args.Add($"/D{def}");
        }
        foreach (string def in module.PrivateDefinitions)
        {
            args.Add($"/D{def}");
        }

        // === Include paths ===
        // Order is fixed: PCH header directory (when a PCH is bound)
        // first so /FI<header> resolves regardless of the consumer
        // module's include paths (audit fix C4); then the module's
        // public includes, then private includes, then the system
        // (MSVC + Windows SDK) includes.
        // The composite system list comes from VCEnvironment.IncludePaths
        // (Phase 1.4a: MSVC headers come first, then um/shared/ucrt/winrt
        // under the chosen SDK version, in that order). The list is
        // constructed once at VCEnvironment construction time so two
        // CompileSource calls in the same process emit byte-identical
        // /I flag sequences -- a determinism requirement for the
        // reproducibility envelope.
        if (pch is not null && !string.IsNullOrEmpty(pch.PchHeaderDirectory))
        {
            args.Add($"/I{pch.PchHeaderDirectory}");
        }
        foreach (string inc in module.PublicIncludePaths)
        {
            args.Add($"/I{inc}");
        }
        foreach (string inc in module.PrivateIncludePaths)
        {
            args.Add($"/I{inc}");
        }
        foreach (string inc in _environment.IncludePaths)
        {
            args.Add($"/I{inc}");
        }

        // === PCH consumption (Contract Section 1.5; Phase 1.3) ===
        // When a per-module PCH was generated (via GeneratePCH), the
        // module's TUs consume it via /Yu<header> + /Fp<pch> + /FI<header>
        // so cl.exe finds and includes the PCH automatically. The
        // PCH header directory was already added to /I above
        // (audit fix C4).
        if (pch is not null)
        {
            args.Add($"/Yu{pch.PchHeaderName}");
            args.Add($"/Fp{pch.PchOutputFile.FullPath}");
            args.Add("/FI");
            args.Add(pch.PchHeaderName);
        }

        // === Output ===
        string objName = Path.GetFileNameWithoutExtension(sourceFile.FullPath) + ".obj";
        string objPath = Path.Combine(outputDir, objName);
        args.Add($"/Fo{objPath}");

        // === Header dependency tracking (audit fix R3-C1) ===
        // /sourceDependencies emits a JSON file alongside the .obj
        // listing every transitively-included header. CppDependencyCache
        // parses this file post-compile and records each header so the
        // next build's IsActionOutdated correctly invalidates when a
        // transitively-included header (not in the PCH, not in
        // PrerequisiteItems) changes.
        //
        // MSVC 17.4+ is required for /sourceDependencies; XBT's VS 2026
        // BuildTools floor is 17.10 so the flag is always available in
        // a supported environment. The path is alongside the .obj so the
        // orphan-temp-file sweep tracks the same parent directory.
        string depJsonPath = Path.Combine(outputDir, objName + ".deps.json");
        args.Add($"/sourceDependencies");
        args.Add(depJsonPath);

        // === Banned-flag check (audit fix R3-M7) ===
        // Defence-in-depth pass over the emitted args. Catches a future
        // refactor that lands a banned flag in any of the emission
        // helpers. ModuleRules.AdditionalCompilerArguments is still a
        // Phase 2 addition; this check is independent of that field.
        VerifyNoBannedFlags(module, args);

        // === Source file LAST ===
        args.Add(sourceFile.FullPath);

        // PrerequisiteItems must be sorted ordinal. When a PCH is
        // bound, the consumer compile depends on both the source file
        // and the PCH artefact + the PCH header (the header content
        // change must invalidate downstream compiles too).
        FileItem[] prereqs;
        if (pch is not null)
        {
            List<FileItem> sortedPrereqs = new(3) { sourceFile, pch.PchOutputFile, pch.PchHeaderFile };
            sortedPrereqs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
            prereqs = sortedPrereqs.ToArray();
        }
        else
        {
            prereqs = new[] { sourceFile };
        }

        // ProducedItems must be sorted ordinal. The .obj path is a prefix
        // of the .obj.deps.json path so the .obj comes first under
        // string.CompareOrdinal.
        FileItem depJsonItem = FileItem.GetItemByPath(depJsonPath);
        FileItem objItem = FileItem.GetItemByPath(objPath);
        FileItem[] produced = { objItem, depJsonItem };
        Array.Sort(produced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        return new[]
        {
            ExternalAction.Create(new ExternalAction
            {
                ActionType = XActionType.CompileCppAction,
                PrerequisiteItems = prereqs,
                ProducedItems = produced,
                CommandPath = _environment.CompilerPath,
                CommandArguments = args,
                WorkingDirectory = _repoRoot,
                CommandDescription = "Compile",
                StatusDescription = Path.GetFileName(sourceFile.FullPath),
                Module = module.Name,
                Tier = module.Tier.ToString(),
                SimPath = module.SimPath,
                Configuration = target.Configuration,
                Platform = target.Platform,
                CacheKeyComponents = BuildCompileCacheKeyComponents(resolved, module, target, pch),
                // Audit fix R3-C1: point CppDependencyCache at the .deps.json
                // so the post-compile parse can discover the transitive
                // header set.
                DependencyListFile = depJsonItem,
                // Audit fix R5-C2: cl.exe writes /Fo<obj> and the .deps.json
                // directly to their final paths; opt out of the executor's
                // temp-file-then-rename contract so the post-run check
                // validates final-path existence rather than nonexistent
                // .tmp paths.
                bProducerWritesFinalPath = true,
            }),
        };
    }

    /// <summary>
    /// Audit fix R8-M2 / R8-M3 / R8-M4: compose the CacheKeyComponents
    /// list for an MSVC compile action. Mirrors the Clang toolchain's
    /// equivalent: envelope-flag hash, per-module descriptor hash, and
    /// XBT-binary content hash all participate in the cache key so a
    /// toolchain upgrade, an envelope-flag default shift, a descriptor
    /// edit, or an XBT rebuild correctly invalidates cached compiles.
    /// </summary>
    private string[] BuildCompileCacheKeyComponents(
        SimdLevel resolvedSimd,
        ModuleRules module,
        TargetRules target,
        PCHBinding? pch)
    {
        List<string> components = new(11)
        {
            $"SimdLevel={resolvedSimd}",
            $"FPSemantics={ResolveFPSemantics(module)}",
            $"SimPath={module.SimPath}",
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            $"PCH={(pch is null ? "none" : pch.PchHeaderName)}",
            // Phase 1.4a: changing the MSVC version or the Windows SDK
            // version invalidates the compile cache. The system headers'
            // definitions of WIN32_LEAN_AND_MEAN helpers / WINVER macros
            // / etc. differ between SDK versions, so a build switching
            // SDKs must re-compile.
            $"MsvcVersion={_environment.CompilerVersion}",
            $"WinSdkVersion={_environment.WindowsSdkVersion}",
            // Audit fix R8-M2: envelope flags hash captures every
            // reproducibility-envelope flag emitted unconditionally
            // (/Brepro, /pathmap=, /d2:-cgmanifestencoded-, /cgthreads:8).
            // A toolchain upgrade that silently changes any envelope
            // flag's default rotates the hash, which invalidates the
            // cache; without this contribution, the silent shift would
            // produce silent staleness.
            $"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash()}",
            // Audit fix R8-M3: per-module descriptor content hash. A
            // .Build.toml or .Build.cs edit that does not change any
            // single parsed field still rotates this hash; the cache
            // invalidates for every compile in the module.
            $"DescriptorHash={ResolveDescriptorHash(module)}",
            // Audit fix R8-M4: XBT binary content hash. A rebuild of
            // XBT itself (logic change in command-line construction)
            // invalidates every cached compile so we never serve
            // outputs produced with the old logic.
            $"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}",
        };
        return components.ToArray();
    }

    /// <summary>
    /// Audit fix R8-M2: compute a stable BLAKE3-16 hash over the MSVC
    /// reproducibility-envelope flag list. The list mirrors the actual
    /// emission order in CompileSource / GeneratePCH / LinkModule so
    /// the hash reflects what is on the command line. New envelope
    /// flags MUST be appended (do not insert in the middle).
    /// </summary>
    private string ComputeEnvelopeFlagsHash()
    {
        // Order matches the per-emission-site ordering. Includes both
        // compile-side (/Brepro, /pathmap=, /d2:-cgmanifestencoded-)
        // and link-side (/BREPRO, /TIMESTAMP:0, /INCREMENTAL:NO,
        // /cgthreads:8) envelope flags -- a link-side drift must also
        // invalidate the compile cache because the action graph treats
        // them as peers in the reproducibility envelope contract.
        string[] envelope =
        {
            "/Brepro",
            $"/pathmap:{_repoRoot}=X:/R",
            "/d2:-cgmanifestencoded-",
            "/BREPRO",
            "/TIMESTAMP:0",
            "/INCREMENTAL:NO",
            "/cgthreads:8",
        };
        string joined = string.Join('\n', envelope);
        IoHash digest = IoHash.Compute(System.Text.Encoding.UTF8.GetBytes(joined));
        return digest.ToString()[..16];
    }

    /// <summary>
    /// Audit fix R8-M3: resolve the descriptor content hash off a
    /// <see cref="ModuleRules"/>. Falls back to a sentinel when the
    /// hash is null (test-only construction without a descriptor on
    /// disk); the sentinel keeps the cache key well-formed and stable
    /// across reads.
    /// </summary>
    private static string ResolveDescriptorHash(ModuleRules module)
    {
        string? hash = module.DescriptorContentHash;
        return string.IsNullOrEmpty(hash) ? "(no-descriptor)" : hash[..Math.Min(16, hash.Length)];
    }

    /// <inheritdoc/>
    public override PCHBinding GeneratePCH(
        ModuleRules module,
        TargetRules target,
        string pchHeaderName,
        FileItem pchHeaderFile,
        string outputDir)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(pchHeaderName);
        ArgumentNullException.ThrowIfNull(pchHeaderFile);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        // Defence-in-depth gate: sim-path modules cannot generate
        // shared-PCH PCHs. The parser-side validator catches this
        // earlier; re-check at toolchain emit time.
        EnforceSimPathPchGate(module);

        // Generate a wrapper .cpp that just includes the PCH header.
        // cl.exe's /Yc mechanism compiles this wrapper to produce the
        // .pch artefact downstream TUs consume via /Yu.
        string wrapperName = module.Name + ".PCH.cpp";
        string wrapperPath = Path.Combine(outputDir, wrapperName);
        Directory.CreateDirectory(outputDir);
        // Use literal '\n' (LF) regardless of host OS so the wrapper's
        // content hash is identical on Windows and Linux. The MSVC
        // compiler accepts either CRLF or LF.
        string wrapperBody =
            "// Copyright Simgenics. All Rights Reserved.\n" +
            $"// AUTO-GENERATED by XMSVCToolChain.GeneratePCH for module '{module.Name}'.\n" +
            $"#include \"{pchHeaderName}\"\n";
        // Write the wrapper deterministically. The wrapper's content is
        // a stable function of (module name, pchHeaderName) so two
        // builds of the same descriptor produce byte-identical wrappers.
        // Audit fix M13: write-only-if-different so a re-run does not
        // touch the file's mtime (downstream make-style consumers may
        // observe mtime even though XBT itself uses content hashes).
        WriteIfDifferent(wrapperPath, wrapperBody);
        FileItem wrapperItem = FileItem.GetItemByPath(wrapperPath);

        // Output: <outputDir>/<module>.pch
        string pchOutputName = module.Name + ".pch";
        string pchOutputPath = Path.Combine(outputDir, pchOutputName);
        FileItem pchOutputItem = FileItem.GetItemByPath(pchOutputPath);

        // Also produce a .pch.obj sidecar (cl.exe's /Yc emits this);
        // we don't currently link the .pch.obj into the module (the
        // consuming TUs each emit their own .obj that lazily-binds the
        // PCH symbols at compile time).
        string pchObjPath = Path.Combine(outputDir, module.Name + ".PCH.obj");
        FileItem pchObjItem = FileItem.GetItemByPath(pchObjPath);

        // === Build the action ===
        List<string> args = new();
        args.Add("/c");
        args.Add("/nologo");
        args.Add("/Brepro");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        args.Add("/d2:-cgmanifestencoded-");

        // PCH-specific:
        //   /Yc<header>      -- create the PCH from this header
        //   /Fp<pchpath>     -- the .pch output path
        //   /FI<header>      -- force-include the header so the wrapper
        //                       resolves the relative include
        args.Add($"/Yc{pchHeaderName}");
        args.Add($"/Fp{pchOutputPath}");
        args.Add("/FI");
        args.Add(pchHeaderName);
        args.Add($"/Fo{pchObjPath}");

        // === Header dependency tracking (audit fix R5-C1) ===
        // The PCH wrapper transitively includes everything the PCH header
        // pulls in. Without /sourceDependencies, an edit to any of those
        // headers does NOT invalidate the PCH (only the wrapper.cpp +
        // pchHeaderFile are in PrerequisiteItems), and every consumer
        // .obj that depends on the cached .pch then serves silently-stale
        // codegen. /sourceDependencies emits the full transitive set
        // post-compile; CppDependencyCache parses it and records each
        // header so the next build's IsActionOutdated re-invalidates
        // the PCH on any transitive-header edit.
        string pchDepJsonPath = Path.Combine(outputDir, pchOutputName + ".deps.json");
        args.Add($"/sourceDependencies");
        args.Add(pchDepJsonPath);
        FileItem pchDepJsonItem = FileItem.GetItemByPath(pchDepJsonPath);

        // Include paths (mirror what a regular compile sees so the PCH
        // header resolves the same way). The composite system paths from
        // VCEnvironment.IncludePaths bring in the MSVC headers + the
        // Windows SDK headers (Phase 1.4a) in the same deterministic
        // order CompileSource uses, so any header the PCH transitively
        // pulls in resolves identically between the PCH-generating
        // compile and the downstream consumer compiles.
        foreach (string inc in module.PublicIncludePaths)
        {
            args.Add($"/I{inc}");
        }
        foreach (string inc in module.PrivateIncludePaths)
        {
            args.Add($"/I{inc}");
        }
        foreach (string inc in _environment.IncludePaths)
        {
            args.Add($"/I{inc}");
        }

        // Exception + RTTI posture matches consumer TUs.
        if (module.bEnableExceptions)
        {
            args.Add("/EHsc");
        }
        if (!module.bUseRTTI)
        {
            args.Add("/GR-");
        }

        // Wrapper.cpp is the source.
        args.Add(wrapperPath);

        // Sort produced items by FullPath ordinal for the
        // ExternalAction sort invariant.
        FileItem[] produced = new[] { pchOutputItem, pchObjItem, pchDepJsonItem };
        Array.Sort(produced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        FileItem[] prereqs = new[] { wrapperItem, pchHeaderFile };
        Array.Sort(prereqs, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.PCHGenerationAction,
            PrerequisiteItems = prereqs,
            ProducedItems = produced,
            CommandPath = _environment.CompilerPath,
            CommandArguments = args,
            WorkingDirectory = _repoRoot,
            CommandDescription = "GeneratePCH",
            StatusDescription = pchOutputName,
            Module = module.Name,
            Tier = module.Tier.ToString(),
            SimPath = module.SimPath,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,                         // PCH gen is heavier than a regular compile.
            // Audit fix R8-M2 / R8-M3 / R8-M4: PCH cache also gated by
            // envelope flags hash, descriptor hash, and XBT binary
            // hash. Mirrors the discipline applied to compile + link
            // actions; a toolchain or descriptor or XBT-binary change
            // invalidates the PCH same as the consumer compiles it
            // serves.
            CacheKeyComponents = new[]
            {
                $"FipsMode={target.FipsMode}",
                $"StationRole={target.StationRole}",
                $"MsvcVersion={_environment.CompilerVersion}",
                $"WinSdkVersion={_environment.WindowsSdkVersion}",
                $"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash()}",
                $"DescriptorHash={ResolveDescriptorHash(module)}",
                $"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}",
            },
            // Audit fix R5-C1: surface the .deps.json to the cache layer
            // so the post-PCH-build parse records the transitive header
            // set keyed off the wrapper.cpp source path. Editing any
            // header included by the PCH then correctly invalidates the
            // PCH on the next build.
            DependencyListFile = pchDepJsonItem,
            // Audit fix R5-C2: cl.exe writes /Fp<pch>, /Fo<obj>, and the
            // .deps.json directly to their final paths.
            bProducerWritesFinalPath = true,
        });

        return new PCHBinding(
            Action: action,
            PchHeaderFile: pchHeaderFile,
            PchHeaderName: pchHeaderName,
            PchOutputFile: pchOutputItem);
    }

    /// <inheritdoc/>
    public override PCHBinding GenerateSharedPCH(
        string headerFile,
        IReadOnlyList<ModuleRules> participants,
        FileItem headerFileItem,
        TargetRules target,
        string outputDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerFile);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(headerFileItem);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        if (participants.Count == 0)
        {
            throw new ArgumentException(
                "GenerateSharedPCH requires at least one participant.",
                nameof(participants));
        }

        // Defence-in-depth: SimPath modules cannot share a PCH per
        // Contract Rev 13 Section 1.5. The parser catches this earlier;
        // re-check at toolchain emit time.
        EnforceSimPathSharedPchGate(participants);

        // Group-keyed hash names the on-disk artefacts so two builds of
        // the same group produce byte-identical paths.
        string headerAbsolutePath = headerFileItem.FullPath;
        string groupHash = ComputeSharedPchHash(headerAbsolutePath, participants);

        // Place artefacts under {outputDir}/SharedPCH/ so they don't
        // collide with any participant's per-module intermediate files.
        string sharedDir = Path.Combine(outputDir, "SharedPCH");
        Directory.CreateDirectory(sharedDir);

        // Wrapper .cpp -- cl.exe's /Yc mechanism needs a source file to
        // compile; the wrapper just #includes the shared header.
        string headerLeafName = Path.GetFileName(headerFile);
        string wrapperName = "SharedPCH." + groupHash + ".cpp";
        string wrapperPath = Path.Combine(sharedDir, wrapperName);
        string wrapperBody =
            "// Copyright Simgenics. All Rights Reserved.\n" +
            $"// AUTO-GENERATED by XMSVCToolChain.GenerateSharedPCH for shared PCH group '{groupHash}'.\n" +
            $"// Participants: {string.Join(", ", SortedParticipantNames(participants))}\n" +
            $"#include \"{headerLeafName}\"\n";
        // Audit fix M13: write-only-if-different (atomic + idempotent).
        WriteIfDifferent(wrapperPath, wrapperBody);
        FileItem wrapperItem = FileItem.GetItemByPath(wrapperPath);

        // Output: {sharedDir}/SharedPCH.{hash}.pch
        string pchOutputName = "SharedPCH." + groupHash + ".pch";
        string pchOutputPath = Path.Combine(sharedDir, pchOutputName);
        FileItem pchOutputItem = FileItem.GetItemByPath(pchOutputPath);

        // .pch.obj sidecar (cl.exe /Yc emits this).
        string pchObjPath = Path.Combine(sharedDir, "SharedPCH." + groupHash + ".obj");
        FileItem pchObjItem = FileItem.GetItemByPath(pchObjPath);

        // === Build the action ===
        List<string> args = new();
        args.Add("/c");
        args.Add("/nologo");
        args.Add("/Brepro");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        args.Add("/d2:-cgmanifestencoded-");

        // PCH-specific (mirror the per-module GeneratePCH but use the
        // header leaf name as the /Yc / /FI argument so cl.exe finds it
        // via the include path search order).
        args.Add($"/Yc{headerLeafName}");
        args.Add($"/Fp{pchOutputPath}");
        args.Add("/FI");
        args.Add(headerLeafName);
        args.Add($"/Fo{pchObjPath}");

        // === Header dependency tracking (audit fix R5-C1) ===
        // Same rationale as the per-module GeneratePCH: without
        // /sourceDependencies, transitively-included headers do not
        // invalidate the shared PCH, and every participant module's
        // .obj that consumes the shared .pch then serves stale
        // codegen. The /sourceDependencies JSON is keyed off the
        // wrapper source path in CppDependencyCache.
        //
        // Naming: shared-PCH artefact names already include a 64-char
        // group hash that pushes the on-disk path close to MAX_PATH on
        // Windows. The depfile uses just "<groupHash>.deps.json" (drops
        // the "SharedPCH." prefix and ".pch" interior segment) so the
        // depfile path is meaningfully shorter than the artefact path
        // alongside it; the action's PathLengths validator still gates
        // the .pch itself, and the depfile parser identifies format by
        // content prefix (the leading '{') rather than by file
        // extension so a non-".pch.deps.json" suffix is correctness-
        // neutral.
        string pchDepJsonPath = Path.Combine(sharedDir, groupHash + ".deps.json");
        args.Add($"/sourceDependencies");
        args.Add(pchDepJsonPath);
        FileItem pchDepJsonItem = FileItem.GetItemByPath(pchDepJsonPath);

        // Aggregate the participants' include paths so the header
        // resolves regardless of which participant's tree it physically
        // lives in. Sorted ordinal + deduped for determinism.
        SortedSet<string> publicIncs = new(StringComparer.Ordinal);
        SortedSet<string> privateIncs = new(StringComparer.Ordinal);
        SortedSet<string> publicDefs = new(StringComparer.Ordinal);
        foreach (ModuleRules m in participants)
        {
            foreach (string inc in m.PublicIncludePaths) publicIncs.Add(inc);
            foreach (string inc in m.PrivateIncludePaths) privateIncs.Add(inc);
            foreach (string def in m.PublicDefinitions) publicDefs.Add(def);
        }
        // Also include the directory holding the shared header so cl.exe
        // resolves the /FI <leaf> against an absolute prefix.
        string headerDir = Path.GetDirectoryName(headerAbsolutePath) ?? string.Empty;
        if (!string.IsNullOrEmpty(headerDir))
        {
            publicIncs.Add(headerDir);
        }
        foreach (string inc in publicIncs) args.Add($"/I{inc}");
        foreach (string inc in privateIncs) args.Add($"/I{inc}");
        foreach (string inc in _environment.IncludePaths) args.Add($"/I{inc}");

        // Union of participants' PublicDefinitions per Contract Rev 13
        // Section 1.5. Sorted ordinal so the command line is deterministic.
        foreach (string def in publicDefs) args.Add($"/D{def}");

        // Exception + RTTI posture: pick the most-restrictive across
        // participants (any participant requiring no-exceptions / no-RTTI
        // wins to ensure compatible PCH consumption).
        bool enableExceptions = true;
        bool useRTTI = false;
        foreach (ModuleRules m in participants)
        {
            if (!m.bEnableExceptions) enableExceptions = false;
            if (m.bUseRTTI) useRTTI = true;
        }
        if (enableExceptions)
        {
            args.Add("/EHsc");
        }
        if (!useRTTI)
        {
            args.Add("/GR-");
        }

        // Wrapper.cpp is the source.
        args.Add(wrapperPath);

        // Sort produced items by FullPath ordinal for the
        // ExternalAction sort invariant.
        FileItem[] produced = new[] { pchOutputItem, pchObjItem, pchDepJsonItem };
        Array.Sort(produced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        FileItem[] prereqs = new[] { wrapperItem, headerFileItem };
        Array.Sort(prereqs, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        // CacheKeyComponents threads the group hash explicitly so the
        // shared-PCH cache key is observable in the action's identity.
        List<string> cacheKeyComponents = new()
        {
            $"SharedPCHGroupHash={groupHash}",
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            $"MsvcVersion={_environment.CompilerVersion}",
            $"WinSdkVersion={_environment.WindowsSdkVersion}",
        };
        foreach (string name in SortedParticipantNames(participants))
        {
            cacheKeyComponents.Add($"Participant={name}");
        }

        // Audit fix R8-M2 / R8-M4: envelope flags + XBT binary hash on
        // shared-PCH cache key too. DescriptorHash intentionally
        // omitted on shared-PCH (the participants list above already
        // captures the membership; per-participant descriptor hashes
        // contribute via each consumer's CompileSource cache key).
        cacheKeyComponents.Add($"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash()}");
        cacheKeyComponents.Add($"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}");

        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.PCHGenerationAction,
            PrerequisiteItems = prereqs,
            ProducedItems = produced,
            CommandPath = _environment.CompilerPath,
            CommandArguments = args,
            WorkingDirectory = _repoRoot,
            CommandDescription = "GenerateSharedPCH",
            StatusDescription = pchOutputName,
            // Module field is the canonical group hash so diagnostics
            // can attribute the action to the shared group rather than
            // any single participant.
            Module = "SharedPCH:" + groupHash,
            Tier = null,
            SimPath = false,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,
            CacheKeyComponents = cacheKeyComponents,
            // Audit fix R5-C1: depfile parse covers transitive headers.
            DependencyListFile = pchDepJsonItem,
            // Audit fix R5-C2: producer writes final paths.
            bProducerWritesFinalPath = true,
        });

        return new PCHBinding(
            Action: action,
            PchHeaderFile: headerFileItem,
            PchHeaderName: headerLeafName,
            PchOutputFile: pchOutputItem);
    }

    /// <summary>
    /// Return the participants' names sorted ordinal for determinism.
    /// Used in wrapper-file comment generation and CacheKeyComponents.
    /// </summary>
    private static IReadOnlyList<string> SortedParticipantNames(
        IReadOnlyList<ModuleRules> participants)
    {
        List<string> names = new(participants.Count);
        foreach (ModuleRules m in participants) names.Add(m.Name);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <inheritdoc/>
    public override IExternalAction LinkModule(
        ModuleRules module,
        TargetRules target,
        IReadOnlyList<FileItem> objectFiles,
        string outputDir)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(objectFiles);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        List<string> args = new();
        args.Add("/nologo");
        args.Add("/DLL");
        args.Add("/BREPRO");
        args.Add("/TIMESTAMP:0");
        args.Add("/INCREMENTAL:NO");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        // /cgthreads:8 pins the link-time codegen thread count (Contract
        // Section 2.1, Rev 13.2 audit fix). When LTO/WPO is active
        // (/GL + /LTCG), the default thread count is hardware-dependent
        // and breaks reproducibility across machines. Emitting the flag
        // unconditionally is harmless when LTO is off and avoids a
        // conditional-emission branch that would have to track LTO
        // state from elsewhere.
        args.Add("/cgthreads:8");

        string dllName = module.Name + ".dll";
        string dllPath = Path.Combine(outputDir, dllName);
        args.Add($"/OUT:{dllPath}");

        // Library search paths: VCEnvironment.LibraryPaths is the composite
        // MSVC + Windows SDK path list constructed in a fixed order at
        // discovery time (Phase 1.4a). The /LIBPATH: flag order is
        // load-bearing because cl.exe's import-lib search walks them
        // left-to-right; we keep VCEnvironment as the single source of
        // truth so a future SDK move (e.g. ARM64 host adding arm64/x64
        // siblings) is a one-line change there, not here.
        foreach (string lp in _environment.LibraryPaths)
        {
            args.Add($"/LIBPATH:{lp}");
        }

        // Standard system libraries the Win32 runtime needs. These are the
        // "default" libs the MSVC IDE silently injects on every .vcxproj
        // link command; XBT replicates them here. Order matches the IDE
        // so /VERBOSE:LIB output cross-references cleanly when diagnosing
        // a missing import.
        foreach (string sysLib in DefaultSystemLibs)
        {
            args.Add(sysLib);
        }

        foreach (FileItem obj in objectFiles)
        {
            args.Add(obj.FullPath);
        }

        // NOTE: ModuleRules in XBT.Configuration does not yet expose an
        // AdditionalLibraries collection. When that field lands, append
        // its contents to args here. Until then, link-line extras flow
        // through TargetRules and the per-DLL link command picks up the
        // system + import libraries from VCEnvironment.LibraryPaths.

        // Sort objectFiles before constructing the action so the
        // ExternalAction sort-invariant holds. The caller is expected to
        // pass a sorted list; we defensively sort here.
        List<FileItem> sortedObjs = new(objectFiles);
        sortedObjs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        return ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.LinkModuleAction,
            PrerequisiteItems = sortedObjs,
            ProducedItems = new[] { FileItem.GetItemByPath(dllPath) },
            CommandPath = _environment.LinkerPath,
            CommandArguments = args,
            WorkingDirectory = _repoRoot,
            CommandDescription = "Link",
            StatusDescription = dllName,
            Module = module.Name,
            Tier = module.Tier.ToString(),
            SimPath = module.SimPath,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,           // links are heavier than compiles
            CacheKeyComponents = BuildLinkCacheKeyComponents(module, target),
            // Audit fix R5-C2: link.exe writes /OUT:<dll> directly to
            // the final path. Opt out of the executor's temp-rename
            // contract per the unified compile/PCH/link architecture.
            bProducerWritesFinalPath = true,
        });
    }

    /// <summary>
    /// Audit fix R8-M2 / R8-M3 / R8-M4: link-action cache-key
    /// composer for MSVC. Same discipline as the compile composer.
    /// </summary>
    private string[] BuildLinkCacheKeyComponents(ModuleRules module, TargetRules target)
    {
        return new[]
        {
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            // Phase 1.4a: link cache also gated by toolchain + SDK
            // version (so an SDK switch re-links even if the .obj
            // hashes are unchanged -- the import libs ABI may shift
            // between Win10 1809 and Win11 23H2 SDKs).
            $"MsvcVersion={_environment.CompilerVersion}",
            $"WinSdkVersion={_environment.WindowsSdkVersion}",
            $"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash()}",
            $"DescriptorHash={ResolveDescriptorHash(module)}",
            $"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}",
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_FPSemantics(FPSemantics fps)
    {
        // Audit fix C6: FPSemantics.Default emits NO /fp: flag. Compiler
        // default applies. Imprecise still maps to /fp:fast; Precise maps
        // to /fp:precise. Previous Rev 13 behaviour mapped Default to
        // /fp:fast which silently turned on unsafe-math semantics for
        // every non-SimPath module that didn't override the field. The
        // new locked policy delegates to the MSVC default (which is
        // /fp:precise on x86_64, identical to the explicit precise flag)
        // unless the descriptor opts in.
        return fps switch
        {
            FPSemantics.Default => Array.Empty<string>(),
            FPSemantics.Imprecise => new[] { "/fp:fast" },
            FPSemantics.Precise => new[] { "/fp:precise" },
            _ => Array.Empty<string>(),
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_OptimizeCode(
        OptimizeCodeMode mode,
        BuildConfiguration config)
    {
        // Debug / DebugGame always emit /Od regardless of declared mode.
        if (config == BuildConfiguration.Debug || config == BuildConfiguration.DebugGame)
        {
            return new[] { "/Od" };
        }

        return mode switch
        {
            OptimizeCodeMode.Never => new[] { "/Od" },
            OptimizeCodeMode.Always => new[] { "/O2" },
            OptimizeCodeMode.InNonDebugBuilds => new[] { "/O2" },
            // InShippingBuildsOnly: aggressive optimization only when
            // Configuration == Shipping; everything else (including
            // Development / Test) gets /Od.
            OptimizeCodeMode.InShippingBuildsOnly =>
                config == BuildConfiguration.Shipping
                    ? new[] { "/O2", "/Oi", "/Ot" }
                    : new[] { "/Od" },
            // Audit fix M6: Default maps to the configuration's default
            // optimization level. Debug/DebugGame already returned /Od
            // above; Development/Shipping/Test all map to /O2 -- the
            // production baseline. Previously Development received the
            // weaker /O1 which produced unrepresentative dev-build perf.
            OptimizeCodeMode.Default => new[] { "/O2" },
            _ => Array.Empty<string>(),
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_Simd(SimdLevel level, bool simPath)
    {
        return level switch
        {
            SimdLevel.None => Array.Empty<string>(),       // no /arch flag = scalar
            SimdLevel.SSE2 => new[] { "/arch:SSE2" },
            SimdLevel.SSE42 => Array.Empty<string>(),      // baseline on x86_64; no MSVC flag needed
            SimdLevel.AVX => new[] { "/arch:AVX" },
            SimdLevel.AVX2 => new[] { "/arch:AVX2" },
            SimdLevel.AVX512 => new[] { "/arch:AVX512" },
            _ => Array.Empty<string>(),
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_SimPath(ModuleRules module)
    {
        // SimPath determinism flag set per Contract Section 4.2 (MSVC row):
        //   add: /fp:precise
        //   ban: /fp:fast, /fp:except
        // The /fp:precise flag is also emitted by GetCompileArguments_FPSemantics
        // when ResolveFPSemantics returns Precise; emitting it again here is
        // harmless (cl.exe accepts the duplicate) but we keep this method
        // returning the additional sim-path-specific flags only -- the
        // /fp:precise piece is handled by the FPSemantics path. The
        // /FI XSimPathMathOverrides.h header gate per Section 4.3 is added
        // here; the header itself ships in Phase 1.3 / Layer 5 (Sleef
        // integration) -- for Phase 1.2 we tolerate its absence with the
        // /FI flag still emitted (the file will be missing until Phase
        // 1.3, at which point CompileSource's existing flag emission
        // suddenly resolves correctly without code changes).
        return new[]
        {
            "/FI", "XSimPathMathOverrides.h",
        };
    }

    /// <summary>
    /// Audit fix R3-M7: scan an already-emitted command-line argument
    /// list for banned flags on a SimPath module. Throws
    /// <see cref="ToolchainBannedFlagException"/> (exit 41) on the first
    /// banned flag encountered. Mirrors Contract Section 4.2 (MSVC row)
    /// which bans <c>/fp:fast</c>, <c>/fp:except</c>, and the FMA-
    /// enabling SIMD shapes on sim-path modules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defence-in-depth against a future refactor of the emission path
    /// that accidentally lands a banned flag. The check runs after the
    /// args list is fully constructed (see <see cref="CompileSource"/>);
    /// a banned flag from any helper -- <see cref="GetCompileArguments_FPSemantics_Resolved"/>,
    /// <see cref="GetCompileArguments_Simd"/>,
    /// <see cref="GetCompileArguments_OptimizeCode"/> -- is caught at
    /// compile-time-emit instead of at sim-runtime where the failure
    /// surfaces as non-determinism.
    /// </para>
    /// </remarks>
    private static void VerifyNoBannedFlags(ModuleRules module, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(args);
        if (!module.SimPath)
        {
            return;
        }

        foreach (string a in args)
        {
            if (string.IsNullOrEmpty(a))
            {
                continue;
            }
            // MSVC banned flags per Contract Section 4.2 (MSVC row).
            // /fp:fast enables FMA contraction; /fp:except enables FP-
            // exception trapping which is environment-sensitive.
            if (a == "/fp:fast"
                || a == "/fp:except"
                || a == "/fp:except+"
                || a == "-fp:fast"
                || a == "-fp:except")
            {
                throw new ToolchainBannedFlagException(
                    $"Module '{module.Name}' is SimPath but its emitted MSVC command line "
                    + $"contains banned flag '{a}'. Contract Rev 13 Section 4.2 forbids "
                    + "this flag on sim-path modules because it enables non-deterministic "
                    + "math contraction or environment-sensitive FP behaviour. Audit the "
                    + "toolchain emit path (XMSVCToolChain.GetCompileArguments_*) that "
                    + "produced this flag and remove or guard it.");
            }
        }
    }

    /// <summary>
    /// Audit fix M13 + R4-M4: write <paramref name="content"/> to
    /// <paramref name="path"/> only when the existing file's bytes
    /// differ; when a write is needed, use the atomic temp+fsync+rename
    /// pattern so a power loss between truncate and end-of-write does
    /// not leave a partial file at the destination. Idempotent: a
    /// second call with the same content is a no-op so the file's
    /// mtime does not flutter on repeated runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PCH wrapper bodies are a deterministic function of the module
    /// name + header name. After the first emission the wrapper's
    /// content is stable; writing again would be a no-op anyway, but
    /// <see cref="File.WriteAllText(string,string)"/> still updates
    /// mtime which can confuse make-style downstream consumers that
    /// observe mtime. The content-difference guard preserves the
    /// original mtime when the file's bytes are unchanged.
    /// </para>
    /// <para>
    /// Round-4 audit fix R4-M4: <see cref="File.WriteAllText(string,string)"/>
    /// is NOT atomic -- it truncates the destination then writes. A
    /// power-loss between truncate and end-of-write leaves a partial
    /// file at the destination. We now route the actual write through
    /// the same temp+fsync+rename pattern
    /// <see cref="ManifestJson.AtomicWriteAllBytes"/> uses so the
    /// "atomic + idempotent" wording on the method is now truthful
    /// end-to-end.
    /// </para>
    /// </remarks>
    private static void WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path))
        {
            try
            {
                string existing = File.ReadAllText(path);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
                // Read error -- proceed to write; the write may resolve
                // whatever filesystem condition tripped the read.
            }
        }

        // Atomic write via temp + fsync + rename. Mirrors the helper at
        // ManifestJson.AtomicWriteAllBytes. The temp name carries a
        // GUID nonce so two concurrent writers do not collide. No
        // timestamp is embedded (Toolchain Contract Rev 13 Section 2.1
        // footgun #1).
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string parent = string.IsNullOrEmpty(directory) ? "." : directory;
        int pid = System.Environment.ProcessId;
        string nonce = Guid.NewGuid().ToString("N");
        string baseName = Path.GetFileName(path);
        string tempPath = Path.Combine(parent, $"{baseName}.tmp.{pid}.{nonce}");

        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            // fsync before rename so the rename's atomic window does
            // not include an empty / partially-flushed file.
            fs.Flush(flushToDisk: true);
        }
        // Audit fix R6-C5: wrap File.Move in the AV-retry helper to
        // tolerate transient Windows Defender locks on the just-written
        // PCH wrapper.
        FileSystemOps.RetryOnTransientIOException(
            () => File.Move(tempPath, path, overwrite: true));
    }
}
