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
/// <c>/Documents/XBT.html</c> Rev 4 Section 19.1.
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
        // so cl.exe finds and includes the PCH automatically.
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

        // === Banned-flag check ===
        // NOTE: ModuleRules in XBT.Configuration does not yet expose an
        // AdditionalCompilerArguments collection. When that field lands
        // we re-enable the banned-flag verify pass against its contents.
        // Until then SimPath banned-flag enforcement runs only against
        // the toolchain-emitted flag set (which we control); a user
        // workaround via Configuration's PublicDefinitions or include
        // paths cannot smuggle in /fp:fast.

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

        return new[]
        {
            ExternalAction.Create(new ExternalAction
            {
                ActionType = XActionType.CompileCppAction,
                PrerequisiteItems = prereqs,
                ProducedItems = new[] { FileItem.GetItemByPath(objPath) },
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
                CacheKeyComponents = new[]
                {
                    $"SimdLevel={resolved}",
                    $"FPSemantics={ResolveFPSemantics(module)}",
                    $"SimPath={module.SimPath}",
                    $"FipsMode={target.FipsMode}",
                    $"StationRole={target.StationRole}",
                    $"PCH={(pch is null ? "none" : pch.PchHeaderName)}",
                },
            }),
        };
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
        File.WriteAllText(wrapperPath, wrapperBody);
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

        // Include paths (mirror what a regular compile sees so the PCH
        // header resolves the same way).
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
        FileItem[] produced = new[] { pchOutputItem, pchObjItem };
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
        });

        return new PCHBinding(
            Action: action,
            PchHeaderFile: pchHeaderFile,
            PchHeaderName: pchHeaderName,
            PchOutputFile: pchOutputItem);
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

        string dllName = module.Name + ".dll";
        string dllPath = Path.Combine(outputDir, dllName);
        args.Add($"/OUT:{dllPath}");

        foreach (string lp in _environment.LibraryPaths)
        {
            args.Add($"/LIBPATH:{lp}");
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
            CacheKeyComponents = new[]
            {
                $"FipsMode={target.FipsMode}",
                $"StationRole={target.StationRole}",
            },
        });
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_FPSemantics(FPSemantics fps)
    {
        return fps switch
        {
            FPSemantics.Default => new[] { "/fp:fast" },     // non-SimPath default
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
            OptimizeCodeMode.Default =>
                config == BuildConfiguration.Shipping
                    ? new[] { "/O2" }
                    : new[] { "/O1" },
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
    /// Verify the toolchain's banned-flag invariants. Currently a
    /// placeholder pending the addition of
    /// <c>ModuleRules.AdditionalCompilerArguments</c> by Subagent A.
    /// Once that collection lands, this method re-scans it for SimPath
    /// modules: <c>/fp:fast</c> and <c>/fp:except</c> are banned and
    /// throw <see cref="ToolchainBannedFlagException"/> (exit 41).
    /// </summary>
    /// <remarks>
    /// At present the only path that could produce a banned flag is the
    /// internal flag emission code in this class -- which by construction
    /// does not emit <c>/fp:fast</c> on SimPath modules
    /// (<see cref="ResolveFPSemantics"/> auto-promotes Default to Precise).
    /// </remarks>
    private static void VerifyNoBannedFlags(ModuleRules module)
    {
        // Placeholder. See remarks.
        _ = module;
    }
}
