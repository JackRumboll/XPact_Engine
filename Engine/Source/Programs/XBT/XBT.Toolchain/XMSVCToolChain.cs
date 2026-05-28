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
        PCHBinding? pch = null,
        string moduleSourceDir = "",
        IReadOnlyList<string>? effectiveIncludePaths = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        List<string> args = new();

        // === Reproducibility envelope (XBT.html Section 19.1) ===
        args.Add("/c");
        args.Add("/nologo");
        args.Add("/experimental:deterministic");
        args.Add("/Brepro");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        // NOTE: /d2:-cgmanifestencoded- is an internal MSVC switch that
        // suppresses an undocumented host-name embed for full
        // reproducibility. MSVC 14.44 (VS 2022 17.10) rejects it as
        // an unrecognized flag in p2; the syntax appears to have shifted
        // post-VS2019. The flag is reproducibility-only (NOT correctness)
        // so we emit it via the GetCompileArguments_Reproducibility helper
        // which gates it on the toolchain version. Per Phase 1g audit
        // R8-M14: when cl.exe rejects the flag the build silently loses
        // a small reproducibility property but compiles correctly; when
        // a future MSVC version restores the flag we can re-enable.
        // For now the flag is omitted from the per-emission site path;
        // /Brepro + /pathmap: + /experimental:deterministic together
        // already provide the bulk of the reproducibility envelope.

        // === C++ language standard (Contract Rev 13 Section 4.2) ===
        // XPact's runtime + tooling codebase requires C++20 features:
        // std::bit_cast (FName fast-equal path), nested namespace
        // definitions, concepts, designated initializers, three-way
        // comparison, std::expected polyfill via tl_expected. The engine
        // tier is C++20 by contract. /std:c++latest is rejected because
        // it pulls in pending C++23 features (deducing this, std::print)
        // that are not yet portable across MSVC 17.10 / Clang 18 / GCC
        // 13 -- the floor toolchains XBT supports per Section 4.1.
        args.Add("/std:c++20");
        // C++20 introduces a few language-level changes the engine
        // codebase already depends on; pin the conforming preprocessor
        // (/Zc:preprocessor) and the conforming __cplusplus macro value
        // (/Zc:__cplusplus) so feature-test macros report accurately.
        args.Add("/Zc:__cplusplus");
        args.Add("/Zc:preprocessor");

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
        //
        // The effectiveIncludePaths parameter, when non-null, is the
        // pre-resolved absolute-path list computed by BuildMode that
        // includes both the module's own paths (relative to its
        // BaseDirectory) AND the transitively-propagated PublicIncludePaths
        // of every dependency. This is the production path for the
        // build orchestrator. When null, fall back to the raw
        // module.PublicIncludePaths + module.PrivateIncludePaths for
        // the test fixtures that construct synthetic modules without
        // running the orchestrator's resolution pre-pass.
        if (pch is not null && !string.IsNullOrEmpty(pch.PchHeaderDirectory))
        {
            args.Add($"/I{pch.PchHeaderDirectory}");
        }
        if (effectiveIncludePaths is not null)
        {
            foreach (string inc in effectiveIncludePaths)
            {
                args.Add($"/I{inc}");
            }
        }
        else
        {
            foreach (string inc in module.PublicIncludePaths)
            {
                args.Add($"/I{inc}");
            }
            foreach (string inc in module.PrivateIncludePaths)
            {
                args.Add($"/I{inc}");
            }
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
        // ComposeObjectFilePath threads moduleSourceDir through so two
        // TUs with the same basename in different subdirectories of
        // the same module produce distinct .obj paths (the duplicate-
        // prerequisite bug that otherwise stops the link of any module
        // with same-named source files in sibling test directories
        // like FAtomicInt32.Tests / FAtomicInt64.Tests). The helper
        // creates the .obj's parent directory so cl.exe's /Fo write
        // does not fail when the relative subdirectory does not yet
        // exist on disk.
        string objPath = ComposeObjectFilePath(sourceFile, outputDir, moduleSourceDir, "obj");
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
        // a supported environment. The .deps.json sits alongside the
        // .obj (sharing its directory and basename) so the orphan-
        // temp-file sweep tracks the same parent directory and the
        // sidecar moves with the .obj whenever the .obj relocates.
        string depJsonPath = objPath + ".deps.json";
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
        // compile-side (/Brepro, /pathmap=) and link-side (/BREPRO,
        // /TIMESTAMP:0, /INCREMENTAL:NO, /cgthreads:8) envelope flags
        // -- a link-side drift must also invalidate the compile cache
        // because the action graph treats them as peers in the
        // reproducibility envelope contract.
        //
        // /d2:-cgmanifestencoded- was previously included here but
        // MSVC 14.44 (VS 2022 17.10+) rejects the flag at the compile
        // site; the per-emission-site emission was removed. The cache
        // key still reflects what the toolchain ACTUALLY emits, so the
        // flag is dropped from the envelope hash too.
        string[] envelope =
        {
            "/experimental:deterministic",
            "/Brepro",
            $"/pathmap:{_repoRoot}=X:/R",
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
        string outputDir,
        IReadOnlyList<string>? effectiveIncludePaths = null)
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
        args.Add("/experimental:deterministic");
        args.Add("/Brepro");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        // /d2:-cgmanifestencoded- omitted -- see CompileSource note.

        // === C++ language standard ===
        // Mirror CompileSource: the PCH MUST be compiled with the same
        // language standard as its downstream consumer TUs or cl.exe
        // refuses to load the .pch with a fatal C1853.
        args.Add("/std:c++20");
        args.Add("/Zc:__cplusplus");
        args.Add("/Zc:preprocessor");

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
        //
        // effectiveIncludePaths semantics mirror CompileSource: when
        // non-null, BuildMode has pre-resolved the absolute + transitive
        // dependency include path list, and we use it verbatim. When
        // null, we fall back to the raw module.PublicIncludePaths +
        // PrivateIncludePaths for the test fixtures.
        if (effectiveIncludePaths is not null)
        {
            foreach (string inc in effectiveIncludePaths)
            {
                args.Add($"/I{inc}");
            }
        }
        else
        {
            foreach (string inc in module.PublicIncludePaths)
            {
                args.Add($"/I{inc}");
            }
            foreach (string inc in module.PrivateIncludePaths)
            {
                args.Add($"/I{inc}");
            }
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
        string outputDir,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? effectiveIncludePathsByParticipant = null)
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
        args.Add("/experimental:deterministic");
        args.Add("/Brepro");
        args.Add($"/pathmap:{_repoRoot}=X:/R");
        // /d2:-cgmanifestencoded- omitted -- see CompileSource note.

        // === C++ language standard ===
        // Mirror CompileSource: shared PCH consumers compile with
        // /std:c++20; the PCH itself must match or cl.exe refuses to
        // load it with C1853.
        args.Add("/std:c++20");
        args.Add("/Zc:__cplusplus");
        args.Add("/Zc:preprocessor");

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
        //
        // When effectiveIncludePathsByParticipant is provided, BuildMode
        // has pre-resolved each participant's absolute + transitive
        // dependency include list; we union those (dedupe + sort
        // ordinal). When null, fall back to iterating each participant's
        // raw module.PublicIncludePaths + PrivateIncludePaths -- the
        // back-compat path for the SharedPchTests fixtures.
        SortedSet<string> aggregatedIncs = new(StringComparer.Ordinal);
        SortedSet<string> publicDefs = new(StringComparer.Ordinal);
        if (effectiveIncludePathsByParticipant is not null)
        {
            foreach (ModuleRules m in participants)
            {
                if (effectiveIncludePathsByParticipant.TryGetValue(m.Name, out IReadOnlyList<string>? incs))
                {
                    foreach (string inc in incs) aggregatedIncs.Add(inc);
                }
                foreach (string def in m.PublicDefinitions) publicDefs.Add(def);
            }
        }
        else
        {
            foreach (ModuleRules m in participants)
            {
                foreach (string inc in m.PublicIncludePaths) aggregatedIncs.Add(inc);
                foreach (string inc in m.PrivateIncludePaths) aggregatedIncs.Add(inc);
                foreach (string def in m.PublicDefinitions) publicDefs.Add(def);
            }
        }
        // Also include the directory holding the shared header so cl.exe
        // resolves the /FI <leaf> against an absolute prefix.
        string headerDir = Path.GetDirectoryName(headerAbsolutePath) ?? string.Empty;
        if (!string.IsNullOrEmpty(headerDir))
        {
            aggregatedIncs.Add(headerDir);
        }
        foreach (string inc in aggregatedIncs) args.Add($"/I{inc}");
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
    /// <remarks>
    /// <para>
    /// <b>Response-file pattern.</b> Modules with 250+ .obj files (e.g.
    /// XCore) blow past Windows' ~32 KB <c>CreateProcessW</c> command-
    /// line limit when their full link command is passed on argv. The
    /// resulting <c>Win32Exception: filename or extension is too long</c>
    /// is the textbook MSVC link failure and the canonical fix is the
    /// <c>@response.rsp</c> indirection: emit the full arg list (envelope
    /// flags + /LIBPATH + system libs + .obj paths + /OUT) into a
    /// sibling file and pass only <c>@&lt;path&gt;</c> to link.exe.
    /// </para>
    /// <para>
    /// XBT applies this UNCONDITIONALLY (no command-line-length
    /// heuristic) per the Prime Directive: the conditional path adds
    /// branch-coverage burden for no real benefit; the response file is
    /// the right pattern for every link. The
    /// <see cref="IExternalAction.ResponseFileContents"/> field carries
    /// the body; <see cref="ProcessActionRunner"/> materializes it on
    /// disk, appends <c>@&lt;path&gt;</c> after the toolchain's
    /// <see cref="IExternalAction.CommandArguments"/>, and deletes it on
    /// completion. The body participates in the action's
    /// <c>CommandVersion</c> + <c>ActionHistory</c> cache key, so two
    /// builds with identical bodies hit the cache and a single .obj
    /// path change invalidates the link.
    /// </para>
    /// <para>
    /// <see cref="IExternalAction.CommandArguments"/> remains empty: the
    /// runner appends the <c>@&lt;rsp&gt;</c> indirection unconditionally
    /// and link.exe accepts a command line consisting solely of the
    /// indirection (it reads the response file as if its contents were
    /// inserted at that point in the argument stream).
    /// </para>
    /// </remarks>
    public override IExternalAction LinkModule(
        ModuleRules module,
        TargetRules target,
        IReadOnlyList<FileItem> objectFiles,
        string outputDir,
        IReadOnlyList<string>? additionalLibraries = null,
        IReadOnlyList<string>? additionalPrerequisites = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(objectFiles);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        // Sort objectFiles up front so both the response file body AND
        // the action's PrerequisiteItems sort-invariant hold against the
        // same ordering. The caller is expected to pass a sorted list;
        // we defensively sort here. The sorted order is also what the
        // response file emits, so two builds with the same .obj set
        // produce byte-identical response file content and hit the
        // ActionHistory cache.
        List<FileItem> sortedObjs = new(objectFiles);
        sortedObjs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        // Response-file pattern: emit the full link arg list into a body
        // string. ProcessActionRunner materializes it to disk and appends
        // an "@<path>" indirection to CommandArguments before invoking
        // link.exe; the body participates in CommandVersion + the
        // ActionHistory cache key per ExternalAction.ComputeCommandVersion
        // (so a .obj path or system-lib change invalidates the cached
        // link, same as if these args were on the visible command line).
        //
        // Args are emitted ONE PER LINE for human readability when
        // diagnosing a link failure. link.exe parses both whitespace-
        // separated and newline-separated response files identically;
        // newlines are the canonical MSVC convention (see
        // /Documents/XBT.html Rev 4 Section 19.1).
        List<string> rspArgs = new();

        // === Reproducibility envelope (XBT.html Section 19.1) ===
        rspArgs.Add("/nologo");
        rspArgs.Add("/DLL");
        rspArgs.Add("/BREPRO");
        rspArgs.Add("/TIMESTAMP:0");
        rspArgs.Add("/INCREMENTAL:NO");
        rspArgs.Add($"/pathmap:{_repoRoot}=X:/R");
        // /cgthreads:8 pins the link-time codegen thread count (Contract
        // Section 2.1, Rev 13.2 audit fix). When LTO/WPO is active
        // (/GL + /LTCG), the default thread count is hardware-dependent
        // and breaks reproducibility across machines. Emitting the flag
        // unconditionally is harmless when LTO is off and avoids a
        // conditional-emission branch that would have to track LTO
        // state from elsewhere.
        rspArgs.Add("/cgthreads:8");

        string dllName = module.Name + ".dll";
        string dllPath = Path.Combine(outputDir, dllName);
        // Phase 5: explicit /IMPLIB: locks the import-lib output path
        // (matches the sibling-of-.dll default that consumers'
        // link lines reference). The .lib is not declared as a
        // produced item — link.exe writes it only when the module
        // has exports, which cannot be detected statically. The
        // producer relationship sits on the .dll instead; consumers
        // declare the .dll as their action-graph prerequisite.
        string implibPath = Path.Combine(outputDir, module.Name + ".lib");
        rspArgs.Add($"/OUT:{dllPath}");
        rspArgs.Add($"/IMPLIB:{implibPath}");

        // Library search paths: VCEnvironment.LibraryPaths is the composite
        // MSVC + Windows SDK path list constructed in a fixed order at
        // discovery time (Phase 1.4a). The /LIBPATH: flag order is
        // load-bearing because cl.exe's import-lib search walks them
        // left-to-right; we keep VCEnvironment as the single source of
        // truth so a future SDK move (e.g. ARM64 host adding arm64/x64
        // siblings) is a one-line change there, not here.
        foreach (string lp in _environment.LibraryPaths)
        {
            rspArgs.Add($"/LIBPATH:{lp}");
        }

        // Standard system libraries the Win32 runtime needs. These are the
        // "default" libs the MSVC IDE silently injects on every .vcxproj
        // link command; XBT replicates them here. Order matches the IDE
        // so /VERBOSE:LIB output cross-references cleanly when diagnosing
        // a missing import.
        foreach (string sysLib in DefaultSystemLibs)
        {
            rspArgs.Add(sysLib);
        }

        foreach (FileItem obj in sortedObjs)
        {
            rspArgs.Add(obj.FullPath);
        }

        // Additional libraries (transitive dependency import libs +
        // ModuleRules.AdditionalLibraries pre-resolved by BuildMode).
        // Appended AFTER the .obj paths so link.exe's left-to-right
        // symbol resolution lets the .obj references pull in symbols
        // from the libraries that follow.
        //
        // Sort defensively so two builds with the same library set
        // produce byte-identical response file content even if the
        // caller's set ordering varies.
        if (additionalLibraries is not null && additionalLibraries.Count > 0)
        {
            List<string> sortedLibs = new(additionalLibraries);
            sortedLibs.Sort(StringComparer.Ordinal);
            foreach (string lib in sortedLibs)
            {
                rspArgs.Add(lib);
            }
        }

        // Compute the prerequisite list. The .obj files plus any
        // additionalPrerequisites (typically the producer-visible
        // .dll paths of transitive deps). additionalLibraries is the
        // link COMMAND-LINE-only set (their .lib paths reach the
        // linker but their producer relationship sits on the .dll).
        List<FileItem> prereqs = new(sortedObjs);
        if (additionalPrerequisites is not null)
        {
            foreach (string p in additionalPrerequisites)
            {
                prereqs.Add(FileItem.GetItemByPath(p));
            }
        }
        prereqs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        string responseFileContents = FormatResponseFile(rspArgs);

        // ProducedItems: declare the .dll only. The .lib import
        // library is generated by link.exe as a sibling artefact when
        // exports exist; modules with no exports never produce a .lib
        // and we cannot detect export presence statically. Consumers
        // depending on this module's link line declare the .dll (not
        // the .lib) as their action-graph prerequisite via
        // BuildMode.ComputeDependencyLinkArtefacts.ProducerArtefacts,
        // which threads through to additionalPrerequisites above.
        FileItem[] producedItems = new[] { FileItem.GetItemByPath(dllPath) };

        return ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.LinkModuleAction,
            PrerequisiteItems = prereqs,
            ProducedItems = producedItems,
            CommandPath = _environment.LinkerPath,
            // CommandArguments deliberately empty: ProcessActionRunner
            // appends the "@<rsp-path>" indirection unconditionally when
            // ResponseFileContents is non-null, and link.exe accepts a
            // command line consisting solely of the indirection. Keeping
            // CommandArguments empty avoids splitting envelope flags
            // across two locations (rsp vs argv) which would confuse
            // both the cache-key invariant and any future diagnostics
            // that print the visible command line.
            CommandArguments = Array.Empty<string>(),
            ResponseFileContents = responseFileContents,
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
    /// Produce a per-test-cpp executable link action. Mirrors
    /// <see cref="LinkModule"/> but emits the executable flag set
    /// (no <c>/DLL</c>, explicit <c>/SUBSYSTEM:CONSOLE</c> +
    /// <c>/ENTRY:mainCRTStartup</c>, <c>.exe</c> extension).
    /// </summary>
    /// <inheritdoc/>
    public override IExternalAction LinkExecutable(
        ModuleRules module,
        TargetRules target,
        FileItem objectFile,
        string exeName,
        string outputDir,
        IReadOnlyList<string>? additionalLibraries = null,
        IReadOnlyList<string>? additionalPrerequisites = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(objectFile);
        ArgumentException.ThrowIfNullOrEmpty(exeName);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        List<string> rspArgs = new();

        // === Reproducibility envelope (XBT.html Section 19.1) ===
        // Mirror LinkModule's envelope: every link action emits the
        // same reproducibility set so a test .exe and a production
        // .dll share their cross-host bit-exactness invariants.
        rspArgs.Add("/nologo");
        // NO /DLL flag: defaults to executable.
        rspArgs.Add("/SUBSYSTEM:CONSOLE");
        rspArgs.Add("/ENTRY:mainCRTStartup");
        rspArgs.Add("/BREPRO");
        rspArgs.Add("/TIMESTAMP:0");
        rspArgs.Add("/INCREMENTAL:NO");
        rspArgs.Add($"/pathmap:{_repoRoot}=X:/R");
        rspArgs.Add("/cgthreads:8");

        string fullExeName = exeName + ".exe";
        string exePath = Path.Combine(outputDir, fullExeName);
        rspArgs.Add($"/OUT:{exePath}");

        // Library search paths.
        foreach (string lp in _environment.LibraryPaths)
        {
            rspArgs.Add($"/LIBPATH:{lp}");
        }

        // Standard system libraries.
        foreach (string sysLib in DefaultSystemLibs)
        {
            rspArgs.Add(sysLib);
        }

        // The single test .obj.
        rspArgs.Add(objectFile.FullPath);

        // Additional libraries (transitive dependency import libs +
        // the test module's own additional_libraries entries).
        // Sorted ordinal for deterministic output.
        if (additionalLibraries is not null && additionalLibraries.Count > 0)
        {
            List<string> sortedLibs = new(additionalLibraries);
            sortedLibs.Sort(StringComparer.Ordinal);
            foreach (string lib in sortedLibs)
            {
                rspArgs.Add(lib);
            }
        }

        // Prerequisite list: the .obj + every additional prerequisite
        // (typically the producer-visible .dll path of each transitive
        // dep so the topo sort orders this exe link after the dep's
        // link). The additionalLibraries paths reach the linker via
        // the response file but are NOT added here — their producer
        // sits on the .dll, not the .lib.
        List<FileItem> prereqs = new() { objectFile };
        if (additionalPrerequisites is not null)
        {
            foreach (string p in additionalPrerequisites)
            {
                prereqs.Add(FileItem.GetItemByPath(p));
            }
        }
        prereqs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        string responseFileContents = FormatResponseFile(rspArgs);

        return ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.LinkModuleAction,
            PrerequisiteItems = prereqs,
            ProducedItems = new[] { FileItem.GetItemByPath(exePath) },
            CommandPath = _environment.LinkerPath,
            CommandArguments = Array.Empty<string>(),
            ResponseFileContents = responseFileContents,
            WorkingDirectory = _repoRoot,
            CommandDescription = "LinkExe",
            StatusDescription = fullExeName,
            Module = module.Name,
            Tier = module.Tier.ToString(),
            SimPath = module.SimPath,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,
            CacheKeyComponents = BuildLinkCacheKeyComponents(module, target),
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
        // here; the header itself lives under XCore/Public/.
        //
        // ThirdParty sim-path modules (e.g. Sleef) are skipped: the
        // /FI override exists so consumers' calls to std::sin/cos/sqrt
        // resolve to the Sleef wrappers, but Sleef IS the wrapper
        // implementation — forcing it through its own override
        // produces an infinite-redirect chain and breaks the build
        // when Sleef has no transitive dep on XCore/Public/. Sleef's
        // own .c files implement the sim-path-safe primitives directly
        // and do not need the override layer.
        if (module.ModuleType == ModuleType.ThirdParty)
        {
            return Array.Empty<string>();
        }
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
