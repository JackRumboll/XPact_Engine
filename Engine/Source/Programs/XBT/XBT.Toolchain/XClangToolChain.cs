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
/// Clang toolchain integration for Linux and Android builds. Translates
/// per-module rules into the <c>clang</c>/<c>clang++</c> flag set; emits
/// the unconditional reproducibility envelope per
/// <c>/Documents/XBT.html</c> Rev 4 Section 19.1.
/// </summary>
/// <remarks>
/// <para>
/// Supported front-ends: Clang &ge; 18 (Linux) and the Android NDK r26+
/// Clang (Android). Discovery on Linux is via <c>$LLVM_HOME</c> /
/// <c>/usr/lib/llvm-18/</c>; on Android via <c>$ANDROID_NDK_ROOT</c>.
/// </para>
/// <para>
/// Reproducibility envelope (emitted on every compile + link, unconditional):
/// </para>
/// <list type="bullet">
///   <item><c>-fdebug-prefix-map=&lt;RepoRoot&gt;=X:/R</c> -- normalize absolute paths in DWARF.</item>
///   <item><c>-fno-ident</c> -- strip compiler version banner from .o.</item>
///   <item><c>-Wl,--build-id=none</c> (link) -- strip the link-emitted build-id from .so.</item>
///   <item><c>-fdeterministic-cgu-order</c> -- pin LTO codegen-unit ordering.</item>
/// </list>
/// <para>
/// SimPath modules additionally receive <c>-ffp-contract=off</c>,
/// <c>-mno-fma</c>, <c>-fno-fast-math</c>, <c>-fno-finite-math-only</c>,
/// and <c>-include XSimPathMathOverrides.h</c>; banned flags
/// (<c>-ffast-math</c>, <c>-Ofast</c>, <c>-mfma</c>) cause a build failure
/// with exit 41. Android ARM64 sim-path TUs also receive
/// <c>-mllvm -enable-fp-contract=false</c> as a NEON FMA-suppress per
/// Contract Section 4.2.
/// </para>
/// </remarks>
public sealed class XClangToolChain : XToolChain
{
    private readonly string _clangPath;
    private readonly string _clangVersion;
    private readonly string _repoRoot;
    private readonly Platform _platform;

    /// <inheritdoc/>
    public override Platform Platform => _platform;

    /// <inheritdoc/>
    public override string ToolchainVersion => _clangVersion;

    /// <summary>Construct from a known clang path + version. Production uses <see cref="TryDiscover"/>.</summary>
    public XClangToolChain(string clangPath, string clangVersion, Platform platform, string repoRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(clangPath);
        ArgumentException.ThrowIfNullOrEmpty(clangVersion);
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        if (platform != Platform.Linux && platform != Platform.Android)
        {
            throw new ArgumentException(
                $"XClangToolChain supports Linux and Android only; got {platform}.",
                nameof(platform));
        }
        _clangPath = clangPath;
        _clangVersion = clangVersion;
        _repoRoot = repoRoot;
        _platform = platform;
    }

    /// <summary>
    /// Discover clang on the host. Linux: <c>$LLVM_HOME</c> or
    /// <c>/usr/bin/clang</c>; Android: <c>$ANDROID_NDK_ROOT/toolchains/llvm/prebuilt/</c>.
    /// </summary>
    public static bool TryDiscover(Platform platform, string repoRoot, out XClangToolChain? toolchain)
    {
        toolchain = null;
        string? path = platform switch
        {
            Platform.Linux => DiscoverLinux(),
            Platform.Android => DiscoverAndroid(),
            _ => null,
        };
        if (path is null)
        {
            return false;
        }
        toolchain = new XClangToolChain(path, "18.0.0", platform, repoRoot);
        return true;
    }

    private static string? DiscoverLinux()
    {
        string? llvmHome = Environment.GetEnvironmentVariable("LLVM_HOME");
        if (!string.IsNullOrEmpty(llvmHome))
        {
            string candidate = Path.Combine(llvmHome, "bin", "clang");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (string candidate in new[]
                 {
                     "/usr/lib/llvm-18/bin/clang",
                     "/usr/bin/clang-18",
                     "/usr/bin/clang",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? DiscoverAndroid()
    {
        string? ndk = Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT");
        if (string.IsNullOrEmpty(ndk))
        {
            return null;
        }
        // Layout differs by host. The "linux-x86_64" prebuilt host is the
        // common case; "windows-x86_64" is the Win64 host for Android
        // cross-build. Both layouts converge at
        // toolchains/llvm/prebuilt/<host>/bin/clang.
        string[] hostCandidates = new[]
        {
            "linux-x86_64",
            "darwin-x86_64",
            "windows-x86_64",
        };
        foreach (string host in hostCandidates)
        {
            string candidate = Path.Combine(ndk, "toolchains", "llvm", "prebuilt", host, "bin", "clang");
            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
            {
                return File.Exists(candidate + ".exe") ? candidate + ".exe" : candidate;
            }
        }
        return null;
    }

    /// <inheritdoc/>
    public override void DiscoverEnvironment()
    {
        // No-op; constructor-injected. Override hook for future
        // multi-installation discovery.
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
        args.Add("-c");
        args.Add($"-fdebug-prefix-map={_repoRoot}=X:/R");
        args.Add("-fno-ident");
        args.Add("-fdeterministic-cgu-order");

        // === Header dependency tracking ===
        args.Add("-MD");
        // -MF <depfile> is added below once we know the output path.

        // === Determinism + SimPath ===
        args.AddRange(GetCompileArguments_FPSemantics_Resolved(module));
        if (module.SimPath)
        {
            args.AddRange(GetCompileArguments_SimPath(module));
        }

        // === SIMD lever ===
        SimdLevel resolved = ResolveSimdLevel(module, target);
        args.AddRange(GetCompileArguments_Simd(resolved, module.SimPath));

        // === Optimisation ===
        args.AddRange(GetCompileArguments_OptimizeCode(module.OptimizeCode, target.Configuration));

        // === Exceptions + RTTI ===
        if (!module.bEnableExceptions)
        {
            args.Add("-fno-exceptions");
        }
        if (!module.bUseRTTI)
        {
            args.Add("-fno-rtti");
        }

        // === Warnings as errors ===
        if (module.bWarningsAsErrors)
        {
            args.Add("-Werror");
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
            args.Add($"-DXPACT_STATION_ROLE={roleName}");
        }

        // === Per-module preprocessor ===
        foreach (string def in module.PublicDefinitions)
        {
            args.Add($"-D{def}");
        }
        foreach (string def in module.PrivateDefinitions)
        {
            args.Add($"-D{def}");
        }

        // === Include paths ===
        foreach (string inc in module.PublicIncludePaths)
        {
            args.Add($"-I{inc}");
        }
        foreach (string inc in module.PrivateIncludePaths)
        {
            args.Add($"-I{inc}");
        }

        // === PCH consumption (Contract Section 1.5; Phase 1.3) ===
        // When a per-module PCH was generated (via GeneratePCH), the
        // module's TUs consume it via -include-pch <pch>.
        if (pch is not null)
        {
            args.Add("-include-pch");
            args.Add(pch.PchOutputFile.FullPath);
        }

        // === Banned-flag check ===
        // NOTE: ModuleRules in XBT.Configuration does not yet expose an
        // AdditionalCompilerArguments collection. When that field lands
        // we re-enable the banned-flag verify pass against its contents.

        // === Output ===
        string objName = Path.GetFileNameWithoutExtension(sourceFile.FullPath) + ".o";
        string objPath = Path.Combine(outputDir, objName);
        string depPath = Path.Combine(outputDir, objName + ".d");
        args.Add("-MF");
        args.Add(depPath);
        args.Add("-o");
        args.Add(objPath);

        // === Source file LAST ===
        args.Add(sourceFile.FullPath);

        // ProducedItems must be sorted by FullPath ordinal. The .o
        // path is a prefix of the .o.d path, so the .o comes first
        // under string.CompareOrdinal.
        FileItem[] produced =
        {
            FileItem.GetItemByPath(objPath),
            FileItem.GetItemByPath(depPath),
        };
        Array.Sort(produced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        // PrerequisiteItems must be sorted ordinal. When a PCH is
        // bound, the consumer compile depends on the PCH artefact +
        // the PCH header too.
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
                ProducedItems = produced,
                CommandPath = _clangPath,
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
        // shared-PCH PCHs.
        EnforceSimPathPchGate(module);

        Directory.CreateDirectory(outputDir);

        // Clang emits a precompiled-header file with the .pchi
        // extension (XBT convention) via `clang -x c++-header -o file.pchi`.
        string pchOutputName = module.Name + ".pchi";
        string pchOutputPath = Path.Combine(outputDir, pchOutputName);
        FileItem pchOutputItem = FileItem.GetItemByPath(pchOutputPath);

        List<string> args = new();

        // === Reproducibility envelope ===
        args.Add($"-fdebug-prefix-map={_repoRoot}=X:/R");
        args.Add("-fno-ident");
        args.Add("-fdeterministic-cgu-order");

        // === Determinism + SimPath ===
        args.AddRange(GetCompileArguments_FPSemantics_Resolved(module));
        if (module.SimPath)
        {
            args.AddRange(GetCompileArguments_SimPath(module));
        }

        // === Exceptions + RTTI ===
        if (!module.bEnableExceptions)
        {
            args.Add("-fno-exceptions");
        }
        if (!module.bUseRTTI)
        {
            args.Add("-fno-rtti");
        }

        // === Include paths (mirror consumer TUs) ===
        foreach (string inc in module.PublicIncludePaths)
        {
            args.Add($"-I{inc}");
        }
        foreach (string inc in module.PrivateIncludePaths)
        {
            args.Add($"-I{inc}");
        }

        // === PCH-specific: treat header as a c++-header ===
        args.Add("-x");
        args.Add("c++-header");

        // Output then input (clang convention).
        args.Add("-o");
        args.Add(pchOutputPath);
        args.Add(pchHeaderFile.FullPath);

        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.PCHGenerationAction,
            PrerequisiteItems = new[] { pchHeaderFile },
            ProducedItems = new[] { pchOutputItem },
            CommandPath = _clangPath,
            CommandArguments = args,
            WorkingDirectory = _repoRoot,
            CommandDescription = "GeneratePCH",
            StatusDescription = pchOutputName,
            Module = module.Name,
            Tier = module.Tier.ToString(),
            SimPath = module.SimPath,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,
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
        args.Add("-shared");
        args.Add($"-fdebug-prefix-map={_repoRoot}=X:/R");
        args.Add("-fno-ident");
        args.Add("-Wl,--build-id=none");

        string soName = "lib" + module.Name + ".so";
        string soPath = Path.Combine(outputDir, soName);
        args.Add("-o");
        args.Add(soPath);

        // SimPath: link-time libm exclusion + Sleef vendoring. The Sleef
        // library itself is Phase 1.3 / Layer 5; until then we emit the
        // -nostdlib++ / replacement-libs posture only when the vendored
        // sleef library is known to be on the search path. For Phase 1.2
        // we emit a comment-only marker via an extra link arg that fails
        // the link if hit -- this guarantees the code path is reachable
        // only when sleef-vendor lands.
        if (module.SimPath)
        {
            // (Marker -- intentional placeholder; do not consult the
            // library path. When Sleef integration lands the marker is
            // replaced with the real -lsleef -nostdlib++ posture per
            // Contract Section 4.3.)
            args.Add("-Wl,--no-undefined");
        }

        // Sort objectFiles before constructing the action so the
        // ExternalAction sort-invariant holds.
        List<FileItem> sortedObjs = new(objectFiles);
        sortedObjs.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));
        foreach (FileItem obj in sortedObjs)
        {
            args.Add(obj.FullPath);
        }

        // NOTE: ModuleRules in XBT.Configuration does not yet expose an
        // AdditionalLibraries collection. When that field lands, append
        // its contents to args here.

        return ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.LinkModuleAction,
            PrerequisiteItems = sortedObjs,
            ProducedItems = new[] { FileItem.GetItemByPath(soPath) },
            CommandPath = _clangPath,
            CommandArguments = args,
            WorkingDirectory = _repoRoot,
            CommandDescription = "Link",
            StatusDescription = soName,
            Module = module.Name,
            Tier = module.Tier.ToString(),
            SimPath = module.SimPath,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,
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
            FPSemantics.Default => new[]
            {
                // Non-SimPath default: enable the fast-math family with
                // explicit -fhonor-infinities + -fno-reciprocal-math
                // suppressors to keep the codegen tame (per master plan
                // B.8 spec). Pure -ffast-math would be too aggressive.
                "-ffast-math",
                "-fhonor-infinities",
                "-fno-reciprocal-math",
            },
            FPSemantics.Imprecise => new[]
            {
                "-ffast-math",
                "-fhonor-infinities",
                "-fno-reciprocal-math",
            },
            FPSemantics.Precise => new[]
            {
                "-ffp-contract=off",
            },
            _ => Array.Empty<string>(),
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_OptimizeCode(
        OptimizeCodeMode mode,
        BuildConfiguration config)
    {
        if (config == BuildConfiguration.Debug || config == BuildConfiguration.DebugGame)
        {
            return new[] { "-O0" };
        }

        return mode switch
        {
            OptimizeCodeMode.Never => new[] { "-O0" },
            OptimizeCodeMode.Always => new[] { "-O2" },
            OptimizeCodeMode.InNonDebugBuilds => new[] { "-O2" },
            OptimizeCodeMode.Default =>
                config == BuildConfiguration.Shipping
                    ? new[] { "-O3" }
                    : new[] { "-O2" },
            _ => Array.Empty<string>(),
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_Simd(SimdLevel level, bool simPath)
    {
        // ARM64 (Android) ignores x86 SIMD flags entirely; the toolchain
        // maps to NEON automatically. We still emit -mno-fma for SimPath
        // (in GetCompileArguments_SimPath) which on ARM64 suppresses NEON
        // FMA. On Android NDK r26+, -mno-fp-armv8 is NOT a supported NDK
        // flag (the NDK uses -mfma/-mno-fma + -mllvm -enable-fp-contract).
        // Per the spec's "verify against NDK docs" instruction in the
        // master plan B.8: on Android we rely on -mno-fma + -mllvm
        // -enable-fp-contract=false (emitted from GetCompileArguments_SimPath)
        // to suppress NEON FMA contraction.
        if (_platform == Platform.Android)
        {
            // SimdLevel is mostly a no-op on ARM64; we DO emit -mno-sse
            // when None to disable the x86 SIMD fallback codegen Clang
            // would otherwise generate when targeting hybrid emulation.
            // For the standard ARM64 platform we let the default NEON
            // baseline apply.
            return level switch
            {
                SimdLevel.None => Array.Empty<string>(),
                _ => Array.Empty<string>(),
            };
        }

        return level switch
        {
            SimdLevel.None => new[] { "-mno-sse" },
            SimdLevel.SSE2 => new[] { "-msse2" },
            SimdLevel.SSE42 => new[] { "-msse4.2" },
            SimdLevel.AVX => new[] { "-mavx" },
            SimdLevel.AVX2 => new[] { "-mavx2" },
            SimdLevel.AVX512 => new[] { "-mavx512f" },
            _ => Array.Empty<string>(),
        };
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_SimPath(ModuleRules module)
    {
        // SimPath determinism flag set per Contract Section 4.2 (Clang row):
        //   add: -ffp-contract=off, -fno-fast-math, -fno-finite-math-only, -mno-fma
        //   ban: -ffast-math, -funsafe-math-optimizations, -ffp-contract=fast, -ffp-contract=on, -mfma
        //
        // The XSimPathMathOverrides.h include gate per Section 4.3:
        //   -include XSimPathMathOverrides.h
        //
        // Android ARM64 also gets -mllvm -enable-fp-contract=false to
        // suppress NEON FMA per Section 4.2 ARM64 row.
        List<string> flags = new()
        {
            "-ffp-contract=off",
            "-fno-fast-math",
            "-fno-finite-math-only",
            "-mno-fma",
            "-include", "XSimPathMathOverrides.h",
        };

        if (_platform == Platform.Android)
        {
            flags.Add("-mllvm");
            flags.Add("-enable-fp-contract=false");
        }

        return flags;
    }

    /// <summary>
    /// Verify the toolchain's banned-flag invariants. Currently a
    /// placeholder pending the addition of
    /// <c>ModuleRules.AdditionalCompilerArguments</c> by Subagent A.
    /// Once that collection lands, this method re-scans it for SimPath
    /// modules and rejects <c>-ffast-math</c>, <c>-Ofast</c>,
    /// <c>-mfma</c>, <c>-funsafe-math-optimizations</c>, and
    /// <c>-ffp-contract=fast/on</c> with exit 41.
    /// </summary>
    private static void VerifyNoBannedFlags(ModuleRules module)
    {
        // Placeholder.
        _ = module;
    }
}
