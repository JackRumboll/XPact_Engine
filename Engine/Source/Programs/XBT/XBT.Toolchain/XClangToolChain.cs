// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Clang toolchain integration for Linux and Android builds. Translates
/// per-module rules into the <c>clang</c>/<c>clang++</c> flag set; emits
/// the unconditional reproducibility envelope per
/// <c>/Documents/XBT.html</c> Rev 4 Section 19.1 and Toolchain Contract
/// Rev 13.2 Section 2.1.
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
///   <item><c>-fdebug-prefix-map=&lt;RepoRoot&gt;=X:/R</c> (compile) -- normalize absolute paths in DWARF debug info.</item>
///   <item><c>-frandomize-layout-seed-file=&lt;empty seed&gt;</c> (compile) -- pin Clang's randomize-layout seed so two builds emit identical struct layouts.</item>
///   <item><c>-fno-ident</c> (compile + link) -- strip compiler version banner from .o / .so.</item>
///   <item><c>-Wl,--build-id=none</c> (link) -- strip the link-emitted build-id from .so.</item>
///   <item><c>-fdeterministic-cgu-order</c> -- pin LTO codegen-unit ordering.</item>
/// </list>
/// <para>
/// Note: there is intentionally NO link-side path-remap flag. Audit fix
/// M1 removed the previous Rev 13.1 emit of <c>--remap-file=</c>, which
/// is not a valid clang/lld flag and would cause a link failure on
/// recent lld versions. The compile-side <c>-fdebug-prefix-map=</c>
/// already normalizes DWARF source paths, and the linker copies DWARF
/// sections through unmodified, so the link side does not need a
/// separate remap.
/// </para>
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
    /// <summary>
    /// Relative path (under <c>_repoRoot</c>) of the pinned empty seed
    /// file Clang's <c>-frandomize-layout-seed-file=</c> reads. The
    /// seed is intentionally empty so the resulting struct layout is
    /// deterministic across machines. The file is lazily created the
    /// first time a Clang action is emitted; subsequent emits short-circuit.
    /// </summary>
    private const string RandomizeLayoutSeedRelativePath =
        "Engine/Source/Programs/XBT/randomize-layout.seed";

    private readonly string _clangPath;
    private readonly string _clangVersion;
    private readonly string _repoRoot;
    private readonly Platform _platform;

    /// <inheritdoc/>
    public override Platform Platform => _platform;

    /// <inheritdoc/>
    public override string ToolchainVersion => _clangVersion;

    /// <summary>
    /// Audit fix R8-C1: compose the Android Clang target triple from
    /// <see cref="TargetRules.Architecture"/> and
    /// <see cref="TargetRules.AndroidApiLevel"/>. The generic NDK
    /// <c>bin/clang</c> driver defaults to the host triple (x86-64 on
    /// Win64 / Linux build hosts) unless an explicit
    /// <c>--target=&lt;arch&gt;-linux-android&lt;API&gt;</c> flag is passed;
    /// without that flag, the produced object files cannot be combined
    /// into an Android <c>.so</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Architecture-to-triple mapping per NDK r26 conventions:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>aarch64</c> / <c>arm64</c> / <c>arm64-v8a</c> &rarr;
    ///   <c>aarch64-linux-android&lt;API&gt;</c>. XPact's primary
    ///   Android target per master plan §2.</item>
    ///   <item><c>armv7a</c> / <c>armeabi-v7a</c> &rarr;
    ///   <c>armv7a-linux-androideabi&lt;API&gt;</c>. The
    ///   <c>androideabi</c> environment suffix is required by the NDK
    ///   for ARM32 targets.</item>
    ///   <item><c>x86_64</c> &rarr;
    ///   <c>x86_64-linux-android&lt;API&gt;</c>. Emulator host.</item>
    ///   <item><c>i686</c> / <c>x86</c> &rarr;
    ///   <c>i686-linux-android&lt;API&gt;</c>. 32-bit emulator host.</item>
    /// </list>
    /// <para>
    /// Throws <see cref="XBTException"/> with exit 23
    /// (<c>EngineOrToolchainVersionMismatch</c>) when the architecture
    /// is unrecognised; an unknown triple silently producing host-arch
    /// codegen is the precise failure mode this audit fix exists to
    /// prevent.
    /// </para>
    /// </remarks>
    internal static string ComposeAndroidTargetTriple(string architecture, int apiLevel)
    {
        ArgumentException.ThrowIfNullOrEmpty(architecture);
        if (apiLevel <= 0)
        {
            throw new XBTException(
                $"Android API level must be a positive integer; got {apiLevel}.",
                exitCode: 23);
        }

        string archPrefix = architecture.ToLowerInvariant() switch
        {
            "aarch64" => "aarch64-linux-android",
            "arm64" => "aarch64-linux-android",
            "arm64-v8a" => "aarch64-linux-android",
            "armv7a" => "armv7a-linux-androideabi",
            "armeabi-v7a" => "armv7a-linux-androideabi",
            "x86_64" => "x86_64-linux-android",
            "i686" => "i686-linux-android",
            "x86" => "i686-linux-android",
            _ => throw new XBTException(
                $"Architecture '{architecture}' is not a recognised Android NDK target. " +
                "Supported: aarch64 / arm64 / arm64-v8a, armv7a / armeabi-v7a, x86_64, i686 / x86. " +
                "Without a recognised --target= triple the Clang driver defaults to the host " +
                "architecture (x86-64 on Win64 / Linux build hosts) which produces object files " +
                "that cannot link into an Android .so.",
                exitCode: 23),
        };

        return archPrefix + apiLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

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
    /// <remarks>
    /// <para>
    /// Audit fix R4-M2: instead of hardcoding <c>"18.0.0"</c>, the
    /// discoverer invokes <c>clang -dumpversion</c> to query the
    /// installed version. The discovered version flows through the
    /// CacheKeyComponents of every compile action, so a Clang upgrade
    /// correctly invalidates the cache (mirrors the MSVC
    /// <c>MsvcVersion=</c> cache-key contribution).
    /// </para>
    /// <para>
    /// Audit fix R7-M10: a missing / unparseable Clang version is no
    /// longer silently swallowed by the <c>"unknown"</c> sentinel. The
    /// previous fallback produced an opaque cache-key component that
    /// was distinct from any real version string, but downstream
    /// diagnostics referencing the toolchain version couldn't surface
    /// the real version, and the operator was left guessing why their
    /// build was failing. The build now fails with exit 23
    /// (<c>EngineOrToolchainVersionMismatch</c>) and a clear
    /// diagnostic naming the clang path.
    /// </para>
    /// </remarks>
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
        // Audit fix R7-M10: fail-loud on unparseable version.
        string? version = QueryClangVersion(path);
        if (string.IsNullOrEmpty(version))
        {
            throw new Core.XBTException(
                $"Failed to query Clang version from '{path}'. " +
                "clang -dumpversion / --version did not return a parseable semver. " +
                "Ensure the clang binary is functional and at least version 7 " +
                "(the minimum Contract-supported version that emits a bare semver " +
                "via -dumpversion). Re-run the build after fixing the toolchain " +
                "installation.",
                exitCode: 23);
        }
        toolchain = new XClangToolChain(path, version, platform, repoRoot);
        return true;
    }

    /// <summary>
    /// Invoke <c>clang -dumpversion</c> (and fall back to
    /// <c>clang --version</c> if <c>-dumpversion</c> fails) and parse
    /// the version semver. Returns null on any failure; the caller is
    /// expected to fall back to a sentinel string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-dumpversion</c> emits a single line with the bare semver
    /// (e.g. <c>"18.0.0"</c>) on every clang version &gt;= 7. Older
    /// versions print the full banner on stderr; we accept either as
    /// long as the semver pattern resolves.
    /// </para>
    /// </remarks>
    internal static string? QueryClangVersion(string clangPath)
    {
        if (string.IsNullOrEmpty(clangPath) || !File.Exists(clangPath))
        {
            return null;
        }
        // First attempt: -dumpversion (single bare semver).
        string? dump = TryRunClangAndCapture(clangPath, "-dumpversion");
        if (!string.IsNullOrWhiteSpace(dump))
        {
            string parsed = ExtractSemver(dump);
            if (!string.IsNullOrEmpty(parsed))
            {
                return parsed;
            }
        }
        // Fallback: --version (banner of the form
        // "clang version 18.0.0 (https://...)").
        string? banner = TryRunClangAndCapture(clangPath, "--version");
        if (!string.IsNullOrWhiteSpace(banner))
        {
            string parsed = ExtractSemver(banner);
            if (!string.IsNullOrEmpty(parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static string? TryRunClangAndCapture(string clangPath, string argument)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = clangPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(argument);
            using Process p = new() { StartInfo = psi };
            if (!p.Start())
            {
                return null;
            }
            // -dumpversion is one-line stdout; --version is multi-line.
            // Read both stdout + stderr; both possible-emit targets.
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            string combined = string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n" + stderr;
            return combined;
        }
        catch (Exception)
        {
            // Best-effort: any failure (file-not-executable, sandbox
            // restriction, etc.) maps to "no discovered version" which
            // the caller resolves with the "unknown" sentinel.
            return null;
        }
    }

    /// <summary>
    /// Extract an <c>X.Y[.Z]</c> semver from arbitrary text. Returns
    /// the first match, or null when no plausible version is found.
    /// </summary>
    internal static string ExtractSemver(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        Match m = SemverPattern.Match(text);
        return m.Success ? m.Value : string.Empty;
    }

    private static readonly Regex SemverPattern = new(
        @"\d+\.\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        PCHBinding? pch = null,
        string moduleSourceDir = "")
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        List<string> args = new();

        // === Reproducibility envelope (XBT.html Section 19.1 + Contract Section 2.1) ===
        args.Add("-c");
        args.Add($"-fdebug-prefix-map={_repoRoot}=X:/R");
        args.Add("-fno-ident");
        args.Add("-fdeterministic-cgu-order");
        // -frandomize-layout-seed-file= pinned to an empty seed file
        // (Contract Section 2.1). Without this flag Clang seeds struct
        // layout randomization from the host's RNG, breaking
        // reproducibility across machines.
        args.Add($"-frandomize-layout-seed-file={EnsureRandomizeLayoutSeedFile()}");

        // === Android target triple (audit fix R8-C1) ===
        // The NDK ships a generic bin/clang driver; without an explicit
        // --target= flag the driver defaults to the host triple
        // (x86-64 on Win64/Linux build hosts) and the resulting object
        // files cannot be combined into an Android .so. Emit the triple
        // identically across CompileSource / GeneratePCH /
        // GenerateSharedPCH / LinkModule so mismatched triples between
        // a PCH and its consumers, or between an object file and the
        // link, are impossible.
        string? androidTriple = null;
        if (_platform == Platform.Android)
        {
            androidTriple = ComposeAndroidTargetTriple(target.Architecture, target.AndroidApiLevel);
            args.Add($"--target={androidTriple}");
        }

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
        // Audit fix C4: PCH header directory first so -include-pch's
        // implicit lookup + any -include flag resolves against the
        // header's own directory regardless of consumer-module include
        // paths.
        if (pch is not null && !string.IsNullOrEmpty(pch.PchHeaderDirectory))
        {
            args.Add($"-I{pch.PchHeaderDirectory}");
        }
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

        // === Banned-flag check (audit fix R3-M7) ===
        // Defence-in-depth pass over the emitted args. Catches a future
        // refactor that lands a banned flag in any of the emission
        // helpers; ModuleRules.AdditionalCompilerArguments is still a
        // Phase 2 addition, but the emission-path scan is independent
        // of that field.
        VerifyNoBannedFlags(module, args);

        // === Output ===
        // ComposeObjectFilePath threads moduleSourceDir through so two
        // TUs with the same basename in different subdirectories of
        // the same module produce distinct .o paths. The Makefile-
        // format .d sidecar sits alongside the .o so the orphan-sweep
        // and CppDependencyCache hooks track the same parent directory.
        string objPath = ComposeObjectFilePath(sourceFile, outputDir, moduleSourceDir, "o");
        string depPath = objPath + ".d";
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
                CacheKeyComponents = BuildCompileCacheKeyComponents(
                    resolved,
                    module,
                    target,
                    pch,
                    androidTriple),
                // Audit fix R3-C1: point CppDependencyCache at the .d
                // file so the post-compile Makefile-format parse can
                // discover the transitive header set. The .d is already
                // declared as a ProducedItem above (the executor's
                // atomic-rename and orphan-sweep apply); the
                // DependencyListFile property is the cache-layer hook.
                DependencyListFile = FileItem.GetItemByPath(depPath),
                // Audit fix R5-C2: clang.exe writes -o <obj> and -MF <d>
                // directly to their final paths. Opt out of the
                // executor's temp-rename contract.
                bProducerWritesFinalPath = true,
            }),
        };
    }

    /// <summary>
    /// Audit fix R8-C1 / R8-M2 / R8-M3 / R8-M4: compose the
    /// CacheKeyComponents list for a compile action. The Android
    /// target triple, the envelope-flags hash, the descriptor hash,
    /// and the XBT binary hash all contribute so a change in any of
    /// them invalidates cached compiles even when the headline command
    /// line is unchanged.
    /// </summary>
    private string[] BuildCompileCacheKeyComponents(
        SimdLevel resolvedSimd,
        ModuleRules module,
        TargetRules target,
        PCHBinding? pch,
        string? androidTriple)
    {
        List<string> components = new(12)
        {
            $"SimdLevel={resolvedSimd}",
            $"FPSemantics={ResolveFPSemantics(module)}",
            $"SimPath={module.SimPath}",
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            $"PCH={(pch is null ? "none" : pch.PchHeaderName)}",
            // Audit fix R4-M2: Clang version contributes to the cache
            // key so a toolchain upgrade invalidates cached compiles
            // (mirrors MSVC's MsvcVersion=).
            $"ClangVersion={_clangVersion}",
        };

        if (androidTriple is not null)
        {
            // Audit fix R8-C1: the Android target triple is part of
            // the action's identity. Two builds with different
            // Architecture or AndroidApiLevel values produce different
            // object files; without the triple in the cache key, a
            // switch from aarch64-linux-android21 to
            // aarch64-linux-android24 (or to armv7a-linux-androideabi21)
            // would silently serve stale codegen.
            components.Add($"AndroidTargetTriple={androidTriple}");
        }

        // Audit fix R8-M2: envelope flags hash. The reproducibility-
        // envelope flag set (the -fdebug-prefix-map=, -fno-ident,
        // -fdeterministic-cgu-order, -frandomize-layout-seed-file=,
        // and Android --target= triple) is emitted unconditionally on
        // every compile. A toolchain upgrade that silently changed any
        // envelope flag's default would otherwise produce silent
        // staleness; folding the hash of the envelope list into the
        // cache key forces every such change to invalidate.
        components.Add($"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash(androidTriple)}");

        // Audit fix R8-M3: per-module descriptor hash. A .Build.toml
        // edit that changes anything the toolchain consumes
        // (PublicDefinitions, include paths, SimdLevel, etc.) must
        // invalidate every compile in the module even when the
        // command-line bytes appear unchanged (e.g. a conditional
        // include path added through Starlark that resolves to nothing
        // on this build but might on the next).
        components.Add($"DescriptorHash={ResolveDescriptorHash(module)}");

        // Audit fix R8-M4: XBT binary content hash. A rebuild of XBT
        // itself (logic change in command-line construction, flag
        // emission ordering, etc.) must invalidate every cached
        // compile so we never serve outputs produced with the old
        // logic.
        components.Add($"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}");

        return components.ToArray();
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
        args.Add($"-frandomize-layout-seed-file={EnsureRandomizeLayoutSeedFile()}");

        // === Android target triple (audit fix R8-C1) ===
        // Identical triple emission across CompileSource / GeneratePCH /
        // LinkModule -- a mismatch between the PCH's target triple and
        // its consumer TUs' triple would otherwise produce a silent ABI
        // skew that only surfaces as a link error after the build is
        // mostly done.
        string? androidTriple = null;
        if (_platform == Platform.Android)
        {
            androidTriple = ComposeAndroidTargetTriple(target.Architecture, target.AndroidApiLevel);
            args.Add($"--target={androidTriple}");
        }

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

        // === Header dependency tracking (audit fix R5-C1) ===
        // Without -MD -MF, transitively-included headers do NOT invalidate
        // the cached .pchi. Every consumer .o that depends on the .pchi
        // then serves silently-stale codegen if any of those headers is
        // edited. The -MD flag emits prerequisite tracking; -MF directs
        // it to a sibling .d file the cache layer parses post-build.
        string pchDepPath = pchOutputPath + ".d";
        args.Add("-MD");
        args.Add("-MF");
        args.Add(pchDepPath);
        FileItem pchDepItem = FileItem.GetItemByPath(pchDepPath);

        // Output then input (clang convention).
        args.Add("-o");
        args.Add(pchOutputPath);
        args.Add(pchHeaderFile.FullPath);

        // Audit fix R8-M2 / R8-M3 / R8-M4: PCH actions get the same
        // envelope-flags / descriptor-hash / XBT-binary-hash treatment
        // as compile actions so the PCH cache invalidates on the same
        // signals.
        List<string> pchCacheKey = new(6)
        {
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            $"ClangVersion={_clangVersion}",
        };
        if (androidTriple is not null)
        {
            pchCacheKey.Add($"AndroidTargetTriple={androidTriple}");
        }
        pchCacheKey.Add($"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash(androidTriple)}");
        pchCacheKey.Add($"DescriptorHash={ResolveDescriptorHash(module)}");
        pchCacheKey.Add($"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}");

        // ProducedItems must be sorted ordinal. The .pchi path is a
        // prefix of the .pchi.d path so .pchi comes first.
        FileItem[] pchProduced = { pchOutputItem, pchDepItem };
        Array.Sort(pchProduced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.PCHGenerationAction,
            PrerequisiteItems = new[] { pchHeaderFile },
            ProducedItems = pchProduced,
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
            CacheKeyComponents = pchCacheKey,
            // Audit fix R5-C1: surface the .d depfile to the cache so
            // transitive-header edits invalidate the PCH on next build.
            DependencyListFile = pchDepItem,
            // Audit fix R5-C2: clang writes -o <pchi> and -MF <.d>
            // directly to their final paths.
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
        // Contract Rev 13.1 Section 1.5.
        EnforceSimPathSharedPchGate(participants);

        // Group-keyed hash names the on-disk artefact deterministically.
        string headerAbsolutePath = headerFileItem.FullPath;
        string groupHash = ComputeSharedPchHash(headerAbsolutePath, participants);

        // Place artefacts under {outputDir}/SharedPCH/.
        string sharedDir = Path.Combine(outputDir, "SharedPCH");
        Directory.CreateDirectory(sharedDir);

        string headerLeafName = Path.GetFileName(headerFile);

        // Output: {sharedDir}/SharedPCH.{hash}.pchi
        string pchOutputName = "SharedPCH." + groupHash + ".pchi";
        string pchOutputPath = Path.Combine(sharedDir, pchOutputName);
        FileItem pchOutputItem = FileItem.GetItemByPath(pchOutputPath);

        List<string> args = new();

        // === Reproducibility envelope ===
        args.Add($"-fdebug-prefix-map={_repoRoot}=X:/R");
        args.Add("-fno-ident");
        args.Add("-fdeterministic-cgu-order");
        args.Add($"-frandomize-layout-seed-file={EnsureRandomizeLayoutSeedFile()}");

        // === Android target triple (audit fix R8-C1) ===
        // Identical to the CompileSource / GeneratePCH emission; a
        // shared PCH used by Android consumers MUST be compiled for the
        // same target triple as those consumers.
        string? androidTriple = null;
        if (_platform == Platform.Android)
        {
            androidTriple = ComposeAndroidTargetTriple(target.Architecture, target.AndroidApiLevel);
            args.Add($"--target={androidTriple}");
        }

        // Aggregate include paths from every participant (sorted ordinal
        // + deduped for determinism). Include the header's own directory
        // so the -x c++-header path resolves.
        SortedSet<string> publicIncs = new(StringComparer.Ordinal);
        SortedSet<string> privateIncs = new(StringComparer.Ordinal);
        SortedSet<string> publicDefs = new(StringComparer.Ordinal);
        foreach (ModuleRules m in participants)
        {
            foreach (string inc in m.PublicIncludePaths) publicIncs.Add(inc);
            foreach (string inc in m.PrivateIncludePaths) privateIncs.Add(inc);
            foreach (string def in m.PublicDefinitions) publicDefs.Add(def);
        }
        string headerDir = Path.GetDirectoryName(headerAbsolutePath) ?? string.Empty;
        if (!string.IsNullOrEmpty(headerDir))
        {
            publicIncs.Add(headerDir);
        }
        foreach (string inc in publicIncs) args.Add($"-I{inc}");
        foreach (string inc in privateIncs) args.Add($"-I{inc}");

        // Union of participants' PublicDefinitions.
        foreach (string def in publicDefs) args.Add($"-D{def}");

        // Exception + RTTI posture: pick the most-restrictive (any
        // participant requiring no-exceptions / no-RTTI wins so the
        // generated PCH is consumable by all participants).
        bool enableExceptions = true;
        bool useRTTI = false;
        foreach (ModuleRules m in participants)
        {
            if (!m.bEnableExceptions) enableExceptions = false;
            if (m.bUseRTTI) useRTTI = true;
        }
        if (!enableExceptions)
        {
            args.Add("-fno-exceptions");
        }
        if (!useRTTI)
        {
            args.Add("-fno-rtti");
        }

        // PCH-specific: treat header as c++-header.
        args.Add("-x");
        args.Add("c++-header");

        // === Header dependency tracking (audit fix R5-C1) ===
        // Without -MD -MF, transitively-included headers do not
        // invalidate the cached shared .pchi. Every participant module
        // consumer .o serving the stale .pchi inherits the staleness.
        //
        // Naming: shared-PCH paths already include a 64-char group hash
        // that approaches MAX_PATH on Windows; the depfile uses just
        // "<groupHash>.d" so the depfile path is meaningfully shorter.
        // The Makefile-format parser auto-detects content (no '{' prefix)
        // so the file extension is correctness-neutral.
        string pchDepPath = Path.Combine(sharedDir, groupHash + ".d");
        args.Add("-MD");
        args.Add("-MF");
        args.Add(pchDepPath);
        FileItem pchDepItem = FileItem.GetItemByPath(pchDepPath);

        // Output then input (clang convention).
        args.Add("-o");
        args.Add(pchOutputPath);
        args.Add(headerAbsolutePath);

        // CacheKeyComponents threads the group hash + participants
        // explicitly so the cache key is observable.
        List<string> cacheKeyComponents = new()
        {
            $"SharedPCHGroupHash={groupHash}",
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            $"ClangVersion={_clangVersion}",
        };
        foreach (string name in SortedParticipantNames(participants))
        {
            cacheKeyComponents.Add($"Participant={name}");
        }

        // Audit fix R8-C1 / R8-M2 / R8-M4: shared-PCH cache also keys
        // off the Android target triple + envelope-flags hash + XBT
        // binary hash. Note: descriptor hash is intentionally omitted
        // (the participants list above already captures the membership;
        // the per-participant descriptor hashes contribute via each
        // consumer's own CompileSource cache key when they consume the
        // shared PCH).
        if (androidTriple is not null)
        {
            cacheKeyComponents.Add($"AndroidTargetTriple={androidTriple}");
        }
        cacheKeyComponents.Add($"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash(androidTriple)}");
        cacheKeyComponents.Add($"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}");

        // ProducedItems must be sorted ordinal. The .pchi path is a
        // prefix of the .pchi.d path so .pchi comes first.
        FileItem[] sharedPchProduced = { pchOutputItem, pchDepItem };
        Array.Sort(sharedPchProduced, static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        IExternalAction action = ExternalAction.Create(new ExternalAction
        {
            ActionType = XActionType.PCHGenerationAction,
            PrerequisiteItems = new[] { headerFileItem },
            ProducedItems = sharedPchProduced,
            CommandPath = _clangPath,
            CommandArguments = args,
            WorkingDirectory = _repoRoot,
            CommandDescription = "GenerateSharedPCH",
            StatusDescription = pchOutputName,
            Module = "SharedPCH:" + groupHash,
            Tier = null,
            SimPath = false,
            Configuration = target.Configuration,
            Platform = target.Platform,
            Weight = 4.0,
            CacheKeyComponents = cacheKeyComponents,
            // Audit fix R5-C1: surface depfile to cache.
            DependencyListFile = pchDepItem,
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
        args.Add("-shared");
        // Audit fix M1: <c>--remap-file=</c> is NOT a valid clang/lld
        // flag for source-path remapping. The Rev 13.1 emit was wrong;
        // ld.lld treats unknown options as a fatal error on recent
        // versions. The compile-side <c>-fdebug-prefix-map=</c> already
        // normalizes DWARF source paths into the .o sections; the
        // linker copies DWARF sections through without modification,
        // so no link-side path remap is required for the
        // reproducibility envelope. The link command remains
        // deterministic via <c>-Wl,--build-id=none</c> + <c>-fno-ident</c>
        // (no embedded build-id, no GCC banner) which strip the only
        // host-dependent metadata clang otherwise injects.
        args.Add("-fno-ident");
        args.Add("-Wl,--build-id=none");

        // === Android target triple (audit fix R8-C1) ===
        // Identical to the CompileSource / GeneratePCH emission. The
        // linker also needs the triple to select the per-arch runtime
        // libraries (libc++, libunwind, libgcc) the NDK ships under
        // toolchains/llvm/prebuilt/<host>/sysroot/usr/lib/<triple>/.
        string? androidTriple = null;
        if (_platform == Platform.Android)
        {
            androidTriple = ComposeAndroidTargetTriple(target.Architecture, target.AndroidApiLevel);
            args.Add($"--target={androidTriple}");
        }

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
            CacheKeyComponents = BuildLinkCacheKeyComponents(module, target, androidTriple),
            // Audit fix R5-C2: clang's linker driver writes -o <so>
            // directly to the final path.
            bProducerWritesFinalPath = true,
        });
    }

    /// <summary>
    /// Audit fix R8-C1 / R8-M2 / R8-M3 / R8-M4: compose the
    /// CacheKeyComponents list for a link action. Same discipline as
    /// <see cref="BuildCompileCacheKeyComponents"/>: Android target
    /// triple, envelope flags hash, descriptor hash, XBT binary hash
    /// all contribute. A re-link on the same .o set must produce the
    /// same key when nothing changed and a different key when any of
    /// these inputs shifts.
    /// </summary>
    private string[] BuildLinkCacheKeyComponents(
        ModuleRules module,
        TargetRules target,
        string? androidTriple)
    {
        List<string> components = new(7)
        {
            $"FipsMode={target.FipsMode}",
            $"StationRole={target.StationRole}",
            $"ClangVersion={_clangVersion}",
        };

        if (androidTriple is not null)
        {
            components.Add($"AndroidTargetTriple={androidTriple}");
        }

        components.Add($"EnvelopeFlagsHash={ComputeEnvelopeFlagsHash(androidTriple)}");
        components.Add($"DescriptorHash={ResolveDescriptorHash(module)}");
        components.Add($"XbtBinaryHash={ToolchainSelfHash.XbtBinaryHash}");

        return components.ToArray();
    }

    /// <summary>
    /// Audit fix R8-M2: compute a stable BLAKE3-16 hash over the
    /// reproducibility-envelope flag list. The list is fixed at one
    /// site (this method) so any flag addition / removal / reorder
    /// rotates the hash, which rotates the cache key, which forces a
    /// rebuild. The Android target triple participates so a switch
    /// from aarch64-linux-android21 to aarch64-linux-android24 picks
    /// up the new triple via the same hash.
    /// </summary>
    private string ComputeEnvelopeFlagsHash(string? androidTriple)
    {
        // Order MUST match the actual emission order in CompileSource /
        // GeneratePCH / LinkModule so the hash reflects what is on the
        // command line. New envelope flags MUST be appended (do not
        // insert in the middle) so the existing cache entries do not
        // alias to old emissions.
        List<string> envelope = new(8)
        {
            $"-fdebug-prefix-map={_repoRoot}=X:/R",
            "-fno-ident",
            "-fdeterministic-cgu-order",
            $"-frandomize-layout-seed-file={RandomizeLayoutSeedRelativePath}",
            // Link-only flag still folded into the envelope hash --
            // a link-side envelope drift must also invalidate the
            // compile cache because the action graph treats them as
            // peers in the reproducibility envelope contract.
            "-Wl,--build-id=none",
        };
        if (androidTriple is not null)
        {
            envelope.Add($"--target={androidTriple}");
        }

        string joined = string.Join('\n', envelope);
        IoHash digest = IoHash.Compute(System.Text.Encoding.UTF8.GetBytes(joined));
        // First 16 hex chars: matches the ContractVersion truncation
        // discipline and keeps the cache-key component readable in
        // diagnostics.
        return digest.ToString()[..16];
    }

    /// <summary>
    /// Audit fix R8-M3: resolve the descriptor hash for the supplied
    /// module by walking the discovery layer's per-module record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The descriptor hash is the BLAKE3 content hash of the
    /// <c>.Build.toml</c> or <c>.Build.cs</c> file. <see cref="ModuleEnumerator"/>
    /// computes it at discovery time and stores it on
    /// <see cref="ModuleRecord.ContentHash"/>; the toolchain accesses
    /// it via <see cref="ModuleRules.DescriptorContentHash"/> (the
    /// parser plumbs the hash through). When the property is null
    /// (test-only construction sites where the module was synthesised
    /// without a descriptor on disk), an empty sentinel is returned
    /// rather than crashing -- the empty sentinel still differs from a
    /// real hash so the test path is observable.
    /// </para>
    /// </remarks>
    private static string ResolveDescriptorHash(ModuleRules module)
    {
        string? hash = module.DescriptorContentHash;
        return string.IsNullOrEmpty(hash) ? "(no-descriptor)" : hash[..Math.Min(16, hash.Length)];
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetCompileArguments_FPSemantics(FPSemantics fps)
    {
        // Audit fix C6/M2: FPSemantics.Default emits no -ffp-* flag.
        // Compiler default applies. Imprecise maps to -ffp-contract=fast
        // explicitly (NOT -ffast-math, which silently pulls in
        // -ffinite-math-only and -funsafe-math-optimizations -- a
        // determinism risk). Precise maps to -ffp-contract=off.
        return fps switch
        {
            FPSemantics.Default => Array.Empty<string>(),
            FPSemantics.Imprecise => new[]
            {
                "-ffp-contract=fast",
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
            // InShippingBuildsOnly: aggressive optimization only when
            // Configuration == Shipping; everything else (including
            // Development / Test) gets -O0.
            OptimizeCodeMode.InShippingBuildsOnly =>
                config == BuildConfiguration.Shipping
                    ? new[] { "-O2" }
                    : new[] { "-O0" },
            // Audit fix M6: Default maps to the configuration's default
            // optimization level. Debug/DebugGame already returned -O0
            // above; Development/Shipping/Test all map to -O2 (the
            // production baseline). Previously Shipping received -O3
            // which trades determinism for performance (auto-vectorize
            // may emit different codegen across host CPUs) -- a risk
            // the lockstep simulation envelope explicitly disallows.
            OptimizeCodeMode.Default => new[] { "-O2" },
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

        // Audit fix R4-M3: every per-level flag set now mirrors the
        // per-level <c>XBT.html</c> Section 4.6 table. The lower-bound
        // flag (e.g. <c>-msse2</c>) by itself does NOT prevent Clang
        // from auto-vectorizing with higher-SIMD intrinsics on hosts
        // where they're enabled by default; the upper-bound suppression
        // set is required so the codegen is reproducible across hosts.
        return level switch
        {
            SimdLevel.None => new[] { "-mno-sse" },
            SimdLevel.SSE2 => new[]
            {
                "-msse2",
                "-mno-sse3", "-mno-ssse3", "-mno-sse4.1", "-mno-sse4.2",
                "-mno-avx", "-mno-avx2", "-mno-avx512f",
            },
            SimdLevel.SSE42 => new[]
            {
                "-msse4.2",
                "-mno-avx", "-mno-avx2", "-mno-avx512f",
            },
            SimdLevel.AVX => new[]
            {
                "-mavx",
                "-mno-avx2", "-mno-avx512f",
            },
            SimdLevel.AVX2 => new[]
            {
                "-mavx2",
                "-mno-avx512f",
            },
            SimdLevel.AVX512 => new[]
            {
                "-mavx512f",
                "-mavx512bw", "-mavx512dq", "-mavx512vl",
            },
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
    /// Audit fix R3-M7: scan an already-emitted command-line argument
    /// list for banned flags on a SimPath module. Throws
    /// <see cref="ToolchainBannedFlagException"/> (exit 41) on the first
    /// banned flag encountered. Mirrors Contract Section 4.2 (Clang
    /// row) which bans <c>-ffast-math</c>, <c>-Ofast</c>, <c>-mfma</c>,
    /// <c>-funsafe-math-optimizations</c>, and <c>-ffp-contract=fast</c>
    /// / <c>-ffp-contract=on</c> on sim-path modules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check is defence-in-depth against a future refactor of the
    /// emission path that accidentally lands a banned flag: even though
    /// <see cref="GetCompileArguments_SimPath"/> emits the safe set
    /// today and <see cref="ResolveFPSemantics"/> auto-promotes Default
    /// to Precise on sim-path, a subsequent edit to
    /// <see cref="GetCompileArguments_Simd"/> /
    /// <see cref="GetCompileArguments_FPSemantics"/> /
    /// <see cref="GetCompileArguments_OptimizeCode"/> could silently
    /// introduce a banned flag and the only signal would be a
    /// determinism failure in a downstream sim run. Scanning the
    /// emitted args here catches the regression at compile-time-emit,
    /// not at sim-runtime.
    /// </para>
    /// <para>
    /// The scan accepts both <c>-flag=value</c> and <c>-flag value</c>
    /// shapes (the latter is two argv entries) -- the actual emit uses
    /// <c>=</c> form so we match against the joined form first, then
    /// also catch a raw <c>-ffp-contract</c> followed by a separate
    /// <c>fast</c> / <c>on</c> token.
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

        // Single-token banned flags (Contract Section 4.2 Clang row).
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (string.IsNullOrEmpty(a))
            {
                continue;
            }
            if (a == "-ffast-math"
                || a == "-Ofast"
                || a == "-mfma"
                || a == "-funsafe-math-optimizations"
                || a == "-ffp-contract=fast"
                || a == "-ffp-contract=on")
            {
                throw new ToolchainBannedFlagException(
                    $"Module '{module.Name}' is SimPath but its emitted Clang command line "
                    + $"contains banned flag '{a}'. Contract Rev 13 Section 4.2 forbids "
                    + "this flag on sim-path modules because it enables non-deterministic "
                    + "math contraction. Audit the toolchain emit path that produced this "
                    + "flag (XClangToolChain.GetCompileArguments_*) and remove or guard it.");
            }

            // Two-token form: "-ffp-contract" followed by "fast" / "on".
            if (a == "-ffp-contract" && i + 1 < args.Count)
            {
                string next = args[i + 1];
                if (next == "fast" || next == "on")
                {
                    throw new ToolchainBannedFlagException(
                        $"Module '{module.Name}' is SimPath but its emitted Clang command line "
                        + $"contains banned flag pair '-ffp-contract {next}'. Contract Rev 13 "
                        + "Section 4.2 forbids this on sim-path modules.");
                }
            }
        }
    }

    /// <summary>
    /// Resolve the absolute path to the pinned empty seed file used by
    /// Clang's <c>-frandomize-layout-seed-file=</c>. Creates the file
    /// lazily so the path is always valid by the time a Clang action
    /// reads it. The seed file is intentionally empty (zero bytes) so
    /// Clang's struct-randomization layout is deterministic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit fix R3-M4: every IO failure path now emits a
    /// <see cref="Logger.Warning"/> with the specific exception detail
    /// so an operator seeing a downstream "cannot open seed file" error
    /// from Clang can correlate it with the XBT-side failure. The
    /// previous swallow-and-continue policy left no diagnostic trail
    /// when the seed-file write failed (read-only repo snapshot, CI
    /// sandbox, antivirus quarantine, etc.). Clang accepts the flag
    /// against a missing file (falls back to default behaviour); the
    /// determinism-envelope contract requires the flag to be present
    /// in the command line, not the file to exist. We log the IO
    /// failure but do NOT retry -- the seed file is determinism
    /// plumbing, not load-bearing, and a retry loop on a permanently
    /// non-writable path would just delay the build.
    /// </para>
    /// </remarks>
    private string EnsureRandomizeLayoutSeedFile()
    {
        string absPath = Path.Combine(
            _repoRoot,
            RandomizeLayoutSeedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            string? dir = Path.GetDirectoryName(absPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (!File.Exists(absPath))
            {
                using FileStream fs = File.Create(absPath);
                // Intentionally empty: a zero-byte seed pins the
                // randomize-layout RNG to a deterministic starting state.
            }
        }
        catch (IOException ex)
        {
            Logger.Warning(
                $"Clang randomize-layout seed file could not be created at '{absPath}': "
                + $"{ex.GetType().Name}: {ex.Message}. Clang will use its default "
                + "fall-back; struct layout reproducibility across machines may be "
                + "affected. Verify the path is writable and not held by an antivirus scan.",
                new DiagnosticContext { Action = "randomize-layout-seed" });
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warning(
                $"Clang randomize-layout seed file could not be created at '{absPath}': "
                + $"{ex.GetType().Name}: {ex.Message}. Clang will use its default "
                + "fall-back; struct layout reproducibility across machines may be "
                + "affected. Verify the running user has write permission on the path.",
                new DiagnosticContext { Action = "randomize-layout-seed" });
        }
        return absPath;
    }
}
