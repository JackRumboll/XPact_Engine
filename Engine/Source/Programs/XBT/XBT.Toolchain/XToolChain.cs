// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.ActionGraph;
using Simgenics.XPact.XBT.Configuration;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Abstract base for every platform-toolchain integration. Subclasses
/// (<see cref="XMSVCToolChain"/>, <see cref="XClangToolChain"/>) translate
/// per-module rule enums (<see cref="FPSemantics"/>,
/// <see cref="OptimizeCodeMode"/>, <see cref="SimdLevel"/>, sim-path
/// posture) into the per-platform compile-flag set, and construct the
/// <see cref="IExternalAction"/> instances the action graph dispatches.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>/Documents/XBT.html</c> Rev 4 Section 7.1 and Toolchain Contract
/// Rev 13 Section 4. The seams below intentionally split flag emission
/// by enum dimension so subclasses can re-use the shared logic in this
/// base class without duplicating per-dimension translation tables.
/// </para>
/// </remarks>
public abstract class XToolChain
{
    /// <summary>The target platform this toolchain produces output for.</summary>
    public abstract Platform Platform { get; }

    /// <summary>
    /// Semantic version of the underlying compiler. Tracked against
    /// <see cref="ModuleRules.MinimumToolchainVersion"/>; mismatch exits
    /// with Contract Rev 13 Section 13 exit code 23.
    /// </summary>
    public abstract string ToolchainVersion { get; }

    /// <summary>
    /// Probe the host environment for the underlying toolchain. Idempotent;
    /// safe to call multiple times. Subclass implementations cache the
    /// discovery to avoid re-running expensive probes per build.
    /// </summary>
    public abstract void DiscoverEnvironment();

    /// <summary>
    /// Produce the action(s) that compile a single source file. Most
    /// toolchains produce a single <see cref="XActionType.CompileCppAction"/>;
    /// the multi-action return shape is reserved for toolchains that
    /// emit a coupled preprocess + compile pair (e.g. the
    /// banned-API-on-preprocessed-text pass per Contract Section 4.4).
    /// </summary>
    /// <param name="module">The module the source belongs to.</param>
    /// <param name="target">The target driving the build.</param>
    /// <param name="sourceFile">The source TU.</param>
    /// <param name="outputDir">Absolute path to the intermediate output directory.</param>
    /// <param name="pch">
    /// Optional precompiled-header binding produced by
    /// <see cref="GeneratePCH"/>. When non-null, the toolchain inserts
    /// the <c>/Yu</c>+<c>/Fp</c>+<c>/FI</c> (MSVC) or
    /// <c>-include-pch</c> (Clang) flags so the TU consumes the PCH
    /// per Toolchain Contract Rev 13 Section 1.5.
    /// </param>
    /// <param name="moduleSourceDir">
    /// Optional absolute path of the module's source root (the
    /// directory the module's <c>.Build.toml</c> / <c>.Build.cs</c>
    /// descriptor lives in). When set and <paramref name="sourceFile"/>
    /// is under it, the toolchain composes the .obj/.o output path
    /// under <c>{outputDir}/{srcRelDir}/{basename}.{ext}</c> where
    /// <c>srcRelDir</c> is the source file's parent directory relative
    /// to <paramref name="moduleSourceDir"/>. This mirrors the UE
    /// intermediate layout and disambiguates same-basename TUs that
    /// live in different subdirectories of the same module (e.g.
    /// <c>Tests/HAL/FAtomicInt32.Tests/CASContention.cpp</c> vs.
    /// <c>Tests/HAL/FAtomicInt64.Tests/CASContention.cpp</c>). When
    /// empty (default) or when the source file is not located under
    /// the named directory, the toolchain falls back to flat
    /// basename-only naming directly under <paramref name="outputDir"/>.
    /// </param>
    /// <param name="effectiveIncludePaths">
    /// Optional override for the per-module include path list emitted as
    /// <c>/I</c> (MSVC) or <c>-I</c> (Clang) flags. When non-null, the
    /// toolchain uses this list verbatim INSTEAD of
    /// <see cref="ModuleRules.PublicIncludePaths"/> +
    /// <see cref="ModuleRules.PrivateIncludePaths"/>. Production callers
    /// pass an absolute-path list pre-resolved by BuildMode that includes:
    /// (a) the module's own public + private include paths, resolved
    /// relative to the module's <c>BaseDirectory</c>; and (b) the
    /// transitive set of every dependency module's
    /// <see cref="ModuleRules.PublicIncludePaths"/>, also resolved to
    /// absolute paths. This is the proper resolution of the dependency
    /// propagation rule documented in
    /// <see cref="ModuleRules.PublicIncludePaths"/> (visible to consumers
    /// via transitive propagation through
    /// <see cref="ModuleRules.PublicDependencyModuleNames"/>).
    /// When null (the default) the toolchain falls back to the raw
    /// <see cref="ModuleRules.PublicIncludePaths"/> +
    /// <see cref="ModuleRules.PrivateIncludePaths"/> for backwards
    /// compatibility with the existing test fixtures that pass synthetic
    /// modules without dependency resolution.
    /// </param>
    public abstract IReadOnlyList<IExternalAction> CompileSource(
        ModuleRules module,
        TargetRules target,
        FileItem sourceFile,
        string outputDir,
        PCHBinding? pch = null,
        string moduleSourceDir = "",
        IReadOnlyList<string>? effectiveIncludePaths = null);

    /// <summary>
    /// Produce the link action for a module. One
    /// <see cref="XActionType.LinkModuleAction"/> per module.
    /// </summary>
    /// <param name="additionalLibraries">
    /// Optional pre-resolved absolute paths to additional libraries the
    /// link must include (e.g. dependency module import libs, vendored
    /// static archives). The toolchain appends these to the link line
    /// after the per-source object files but before the system default
    /// libs. BuildMode pre-resolves <see cref="ModuleRules.AdditionalLibraries"/>
    /// + transitive dependency module artefacts here. When null or empty,
    /// the link line only carries the toolchain's intrinsic system-lib
    /// set (libpaths + DefaultSystemLibs).
    /// </param>
    /// <param name="additionalPrerequisites">
    /// Optional pre-resolved absolute paths to additional action-graph
    /// prerequisites (typically the producer-visible <c>.dll</c> paths
    /// for transitive dependency modules; on Win64 the .lib appears on
    /// the link line but the .dll is the action-graph producer output
    /// because link.exe writes the .lib only when exports exist).
    /// These paths land in
    /// <see cref="IExternalAction.PrerequisiteItems"/> but do NOT
    /// appear on the link command line; they exist purely to drive
    /// the topological sort.
    /// </param>
    /// <param name="moduleDefFileAbsolute">
    /// Phase 1g Sleef wiring: optional pre-resolved absolute path to a
    /// Microsoft module-definition (<c>.def</c>) file listing the
    /// symbols this module's DLL must export. When non-null, the MSVC
    /// toolchain emits <c>/DEF:&lt;abs&gt;</c> on the link command line
    /// so link.exe writes the named symbols into the DLL's export
    /// table and produces the matching import library
    /// (<c>{Module}.lib</c>) next to <c>{Module}.dll</c>. BuildMode
    /// resolves <see cref="ModuleRules.ModuleDefFile"/> to absolute
    /// before invoking. The Clang toolchain ignores the parameter
    /// (Linux/Android use ELF visibility rather than .def files). The
    /// resolved file is added to the link action's
    /// <see cref="IExternalAction.PrerequisiteItems"/> so an edit
    /// invalidates the cached link.
    /// </param>
    public abstract IExternalAction LinkModule(
        ModuleRules module,
        TargetRules target,
        IReadOnlyList<FileItem> objectFiles,
        string outputDir,
        IReadOnlyList<string>? additionalLibraries = null,
        IReadOnlyList<string>? additionalPrerequisites = null,
        string? moduleDefFileAbsolute = null);

    /// <summary>
    /// Produce a link action that builds an EXECUTABLE (not a shared
    /// library) from a single object file plus the consumed module
    /// imports + additional libraries. One
    /// <see cref="XActionType.LinkModuleAction"/> per test
    /// translation unit. Phase 5 test-module wiring per
    /// <c>/Documents/XBT.html</c> Section 5.3 (test executables).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The MSVC difference vs <see cref="LinkModule"/>:
    /// <list type="bullet">
    ///   <item>No <c>/DLL</c> flag (default is executable).</item>
    ///   <item><c>/SUBSYSTEM:CONSOLE</c> + <c>/ENTRY:mainCRTStartup</c>
    ///   so <c>int main()</c> binds correctly.</item>
    ///   <item><c>/OUT:&lt;name&gt;.exe</c> instead of
    ///   <c>/OUT:&lt;name&gt;.dll</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The Clang difference: no <c>-shared</c>; <c>-o &lt;name&gt;.exe</c>
    /// on Windows, <c>-o &lt;name&gt;</c> on Linux/Android.
    /// </para>
    /// <para>
    /// Re-uses the existing <see cref="XActionType.LinkModuleAction"/>
    /// slot intentionally. A new slot would rotate the
    /// <see cref="ActionHistory.CurrentVersion"/> hash and invalidate
    /// every cache entry across the engine; the executable-vs-shared
    /// distinction is captured by the action's
    /// <see cref="IExternalAction.CommandVersion"/> (which hashes
    /// <see cref="IExternalAction.ResponseFileContents"/> per
    /// <see cref="ExternalAction.ComputeCommandVersion"/> item 4) and
    /// by the <see cref="IExternalAction.ProducedItems"/> extension
    /// (<c>.exe</c> vs <c>.dll</c>).
    /// </para>
    /// </remarks>
    /// <param name="module">The owning test module.</param>
    /// <param name="target">The target driving the build.</param>
    /// <param name="objectFile">
    /// The single <c>.obj</c> / <c>.o</c> produced from a test
    /// translation unit (one test cpp = one obj = one executable).
    /// </param>
    /// <param name="exeName">
    /// Output executable name (without extension). The toolchain
    /// appends the platform's executable extension (<c>.exe</c> on
    /// Win64; bare name on Linux/Android).
    /// </param>
    /// <param name="outputDir">Absolute path to the output directory.</param>
    /// <param name="additionalLibraries">
    /// Pre-resolved absolute paths to additional libraries the test
    /// executable must link against. Production callers pass the
    /// union of the test module's transitive dependency module import
    /// libs + the test module's own
    /// <see cref="ModuleRules.AdditionalLibraries"/>.
    /// </param>
    public abstract IExternalAction LinkExecutable(
        ModuleRules module,
        TargetRules target,
        FileItem objectFile,
        string exeName,
        string outputDir,
        IReadOnlyList<string>? additionalLibraries = null,
        IReadOnlyList<string>? additionalPrerequisites = null);

    /// <summary>
    /// Produce the per-module PCH generation action. Returns the
    /// <see cref="PCHBinding"/> the caller threads through to
    /// <see cref="CompileSource"/> on every TU in the same module so
    /// downstream compiles depend on the PCH and consume it via the
    /// platform's PCH-include mechanism.
    /// </summary>
    /// <param name="module">
    /// The owning module. Must have
    /// <see cref="ModuleRules.PCHUsage"/> in a PCH-emitting mode and
    /// must NOT be sim-path with a shared-PCH mode declared (the
    /// SimPath PCH lock per Contract Section 1.5).
    /// </param>
    /// <param name="target">The target driving the build.</param>
    /// <param name="pchHeaderName">
    /// The PCH header's filename, as declared by the module
    /// (<c>PrivatePCHHeaderFile</c> in the descriptor). The toolchain
    /// resolves this against the module's include path search order.
    /// </param>
    /// <param name="pchHeaderFile">
    /// Resolved <see cref="FileItem"/> for the header. Phase 1.3
    /// callers pass the FileItem of the on-disk header; later phases
    /// may pass a generated FileItem for XHT-emitted PCH headers.
    /// </param>
    /// <param name="outputDir">
    /// Absolute path to the intermediate output directory where the
    /// generated <c>.pch</c> / <c>.pchi</c> lives.
    /// </param>
    /// <param name="effectiveIncludePaths">
    /// Optional override for the per-module include path list (see
    /// <see cref="CompileSource"/> for the full contract). The PCH
    /// generation TU must see the same include path universe its
    /// downstream consumer TUs see, so production callers pass the same
    /// resolved list they pass to <see cref="CompileSource"/>.
    /// </param>
    /// <exception cref="ToolchainBannedFlagException">
    /// Thrown with exit 41 when <paramref name="module"/> is sim-path
    /// and its <see cref="ModuleRules.PCHUsage"/> is anything other
    /// than <see cref="PCHUsageMode.NoPCHs"/> or
    /// <see cref="PCHUsageMode.NoSharedPCHs"/>. The parser-side
    /// <see cref="TierValidator"/> catches this earlier; this method's
    /// re-check is defence-in-depth at the toolchain emit boundary.
    /// </exception>
    public abstract PCHBinding GeneratePCH(
        ModuleRules module,
        TargetRules target,
        string pchHeaderName,
        FileItem pchHeaderFile,
        string outputDir,
        IReadOnlyList<string>? effectiveIncludePaths = null);

    /// <summary>
    /// Emit a SHARED PCH generation action consumed by every module in
    /// the group. The header file lives at a stable absolute path
    /// independent of any single module; the toolchain places the
    /// generated artefact under
    /// <c>{outputDir}/SharedPCH/{ContentHash}.pch</c> (MSVC) or
    /// <c>{outputDir}/SharedPCH/{ContentHash}.pchi</c> (Clang). The
    /// content hash incorporates the header's absolute path and every
    /// participant's <see cref="ModuleRules.PublicDefinitions"/> so a
    /// change in any participant's defines invalidates the shared PCH.
    /// </summary>
    /// <param name="headerFile">
    /// Resolved <see cref="FileItem"/> for the shared header. The header's
    /// absolute canonical path is the group key.
    /// </param>
    /// <param name="participants">
    /// Every module that named the same <see cref="ModuleRules.SharedPCHHeaderFile"/>
    /// and is in the build's selected-module set. Must contain at least
    /// one element. SimPath modules are forbidden here per Contract Rev 13
    /// Section 1.5; the implementation re-checks at emit time as
    /// defence-in-depth and fails with exit 41 if violated.
    /// </param>
    /// <param name="target">The target driving the build.</param>
    /// <param name="outputDir">
    /// Absolute path to the intermediate output directory. The shared PCH
    /// artefacts live under a <c>SharedPCH/</c> subdirectory so they
    /// don't collide with any participant's per-module intermediate files.
    /// </param>
    /// <returns>
    /// A <see cref="PCHBinding"/> the orchestrator threads through to
    /// every participant's <see cref="CompileSource"/> invocation. Every
    /// consumer compile depends on the same shared <see cref="PCHBinding.Action"/>
    /// and consumes the same <see cref="PCHBinding.PchOutputFile"/>.
    /// </returns>
    /// <param name="effectiveIncludePathsByParticipant">
    /// Optional per-participant pre-resolved absolute-path include
    /// lists. Keyed by participant module name. When non-null, the
    /// toolchain unions the lists (dedupe + sort ordinal) and uses the
    /// result INSTEAD of iterating each participant's raw
    /// <see cref="ModuleRules.PublicIncludePaths"/>. Production callers
    /// populate this from BuildMode's per-module include resolution.
    /// </param>
    /// <exception cref="ToolchainBannedFlagException">
    /// Thrown with exit 41 when any element of <paramref name="participants"/>
    /// has <see cref="ModuleRules.SimPath"/> = true. Per Contract Rev 13
    /// Section 1.5, SimPath modules cannot participate in a shared PCH
    /// because shared-PCH non-determinism violates sim-path determinism.
    /// </exception>
    public abstract PCHBinding GenerateSharedPCH(
        string headerFile,
        IReadOnlyList<ModuleRules> participants,
        FileItem headerFileItem,
        TargetRules target,
        string outputDir,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? effectiveIncludePathsByParticipant = null);

    /// <summary>
    /// Defence-in-depth gate for <see cref="GenerateSharedPCH"/>: a
    /// shared PCH group must contain zero SimPath modules per Contract
    /// Rev 13 Section 1.5. Subclasses call this from their
    /// <see cref="GenerateSharedPCH"/> implementations; the parser-side
    /// validator catches the same case at descriptor-parse time.
    /// </summary>
    protected static void EnforceSimPathSharedPchGate(IReadOnlyList<ModuleRules> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        foreach (ModuleRules m in participants)
        {
            if (m.SimPath)
            {
                throw new ToolchainBannedFlagException(
                    $"Module '{m.Name}' is SimPath but appears in a shared-PCH group. " +
                    "Contract Rev 13 Section 1.5 forbids SimPath modules from " +
                    "participating in a shared PCH because shared-PCH non-determinism " +
                    "violates sim-path determinism. Drop the shared_pch_header_file " +
                    $"declaration from '{m.Name}' (use pch_header_file = ... for a " +
                    "private PCH instead).");
            }
        }
    }

    /// <summary>
    /// Compute the canonical content hash key for a shared PCH group per
    /// Contract Rev 13 Section 1.5 + <c>/Documents/XBT.html</c>
    /// Section 15.4. The key incorporates:
    /// </summary>
    /// <list type="bullet">
    ///   <item>The header's absolute canonical path (the group key).</item>
    ///   <item>Every participant's name, sorted ordinal.</item>
    ///   <item>Every participant's <see cref="ModuleRules.PublicDefinitions"/>
    ///   (deduped + sorted ordinal so define-order doesn't change the
    ///   hash).</item>
    /// </list>
    /// <remarks>
    /// The hash is a hex-encoded BLAKE3 digest. Used to name the on-disk
    /// PCH artefact so two builds of the same input set re-use the same
    /// file path.
    /// </remarks>
    protected static string ComputeSharedPchHash(
        string headerAbsolutePath,
        IReadOnlyList<ModuleRules> participants)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerAbsolutePath);
        ArgumentNullException.ThrowIfNull(participants);

        using Blake3.Hasher hasher = Blake3.Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. Header absolute path. Length-prefixed UTF-8.
        byte[] headerUtf8 = System.Text.Encoding.UTF8.GetBytes(headerAbsolutePath);
        BitConverter.TryWriteBytes(intBuffer, headerUtf8.Length);
        hasher.Update(intBuffer);
        hasher.Update(headerUtf8);

        // 2. Participants sorted by Name ordinal so the hash is independent
        //    of the discovery order.
        List<ModuleRules> sortedParticipants = new(participants);
        sortedParticipants.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        BitConverter.TryWriteBytes(intBuffer, sortedParticipants.Count);
        hasher.Update(intBuffer);

        foreach (ModuleRules m in sortedParticipants)
        {
            // Participant name.
            byte[] nameUtf8 = System.Text.Encoding.UTF8.GetBytes(m.Name);
            BitConverter.TryWriteBytes(intBuffer, nameUtf8.Length);
            hasher.Update(intBuffer);
            hasher.Update(nameUtf8);

            // PublicDefinitions, deduped + ordinal-sorted so define-order
            // does not change the hash.
            List<string> defs = new(new HashSet<string>(m.PublicDefinitions, StringComparer.Ordinal));
            defs.Sort(StringComparer.Ordinal);

            BitConverter.TryWriteBytes(intBuffer, defs.Count);
            hasher.Update(intBuffer);
            foreach (string def in defs)
            {
                byte[] defUtf8 = System.Text.Encoding.UTF8.GetBytes(def);
                BitConverter.TryWriteBytes(intBuffer, defUtf8.Length);
                hasher.Update(intBuffer);
                hasher.Update(defUtf8);
            }
        }

        Span<byte> digest = stackalloc byte[Core.IoHash.Length];
        hasher.Finalize(digest);
        return new Core.IoHash(digest).ToString();
    }

    /// <summary>
    /// Sim-path PCH gate. Per Contract Section 1.5 + Phase 1.3 spec, a
    /// sim-path module must declare <c>PCHUsage = NoPCHs</c> or
    /// <c>PCHUsage = NoSharedPCHs</c>; anything else fails with exit
    /// 41. Subclasses call this from their <see cref="GeneratePCH"/>
    /// implementation as defence-in-depth (the parser-side validator
    /// catches the same case at descriptor-parse time).
    /// </summary>
    protected static void EnforceSimPathPchGate(ModuleRules module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!module.SimPath)
        {
            return;
        }

        // Resolve Default to NoSharedPCHs implicitly -- sim-path modules
        // always treat Default as the safe baseline.
        PCHUsageMode resolved = module.PCHUsage;
        if (resolved is PCHUsageMode.UseSharedPCHs
                       or PCHUsageMode.UseExplicitOrSharedPCHs)
        {
            throw new ToolchainBannedFlagException(
                $"Module '{module.Name}' is SimPath but declares PCHUsage = {resolved}. " +
                "Contract Rev 13 Section 1.5 requires SimPath modules to declare " +
                "PCHUsage = NoPCHs or NoSharedPCHs; SharedPCH on a sim-path module is " +
                "banned because PCH non-determinism violates sim-path determinism.");
        }
    }

    /// <summary>
    /// Compose the absolute on-disk path for a per-source intermediate
    /// artefact (.obj on MSVC, .o on Clang) plus its parent directory.
    /// Centralised here so MSVC and Clang implementations of
    /// <see cref="CompileSource"/> share the same naming policy and
    /// can never drift apart on disambiguation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <paramref name="moduleSourceDir"/> is non-empty AND
    /// <paramref name="sourceFile"/> lives under it, the artefact lands
    /// at <c>{outputDir}/{srcRelDir}/{basename}.{extension}</c> where
    /// <c>srcRelDir</c> is <paramref name="sourceFile"/>'s parent
    /// directory relative to <paramref name="moduleSourceDir"/>. This
    /// mirrors the UE intermediate layout and disambiguates same-
    /// basename TUs in different subdirectories (the duplicate-
    /// prerequisite bug at the link layer).
    /// </para>
    /// <para>
    /// When <paramref name="moduleSourceDir"/> is empty OR the source
    /// is not under it (relative path escapes via <c>..</c>), the
    /// artefact falls back to flat <c>{outputDir}/{basename}.{extension}</c>.
    /// The fallback preserves the contract used by ad-hoc toolchain
    /// tests that don't declare a module source root.
    /// </para>
    /// <para>
    /// The parent directory of the returned path is created (via
    /// <see cref="Directory.CreateDirectory(string)"/>) so cl.exe / clang
    /// can write the artefact without a prior mkdir step from the
    /// orchestrator. Idempotent; safe for the orchestrator to also
    /// pre-create <paramref name="outputDir"/>.
    /// </para>
    /// </remarks>
    /// <param name="sourceFile">The source TU.</param>
    /// <param name="outputDir">
    /// Absolute path of the module's intermediate output root.
    /// </param>
    /// <param name="moduleSourceDir">
    /// Optional absolute path of the module's source root. Empty
    /// string selects the flat-basename fallback.
    /// </param>
    /// <param name="extension">
    /// Per-toolchain object-file extension WITHOUT the leading dot
    /// (e.g. <c>"obj"</c> for MSVC, <c>"o"</c> for Clang).
    /// </param>
    /// <returns>
    /// The absolute path of the to-be-produced object file.
    /// </returns>
    protected static string ComposeObjectFilePath(
        FileItem sourceFile,
        string outputDir,
        string moduleSourceDir,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        ArgumentException.ThrowIfNullOrEmpty(extension);

        string baseName = Path.GetFileNameWithoutExtension(sourceFile.FullPath) + "." + extension;

        string objPath;
        if (!string.IsNullOrEmpty(moduleSourceDir))
        {
            string sourceParent = Path.GetDirectoryName(sourceFile.FullPath) ?? string.Empty;
            string relParent = Path.GetRelativePath(moduleSourceDir, sourceParent);

            // GetRelativePath returns "." when the source's parent IS
            // the module source dir; treat that the same as an empty
            // relative subdir. Any path that escapes upward via ".."
            // means the source is outside the named module root --
            // fall back to flat naming so we never compose an .obj
            // path outside outputDir.
            bool isUnderModule =
                relParent != "."
                && !relParent.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relParent);

            objPath = isUnderModule
                ? Path.Combine(outputDir, relParent, baseName)
                : Path.Combine(outputDir, baseName);
        }
        else
        {
            objPath = Path.Combine(outputDir, baseName);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(objPath)!);
        return objPath;
    }

    /// <summary>
    /// Resolve a module's PCH-usage policy per Phase 1.3 spec. Shared-PCH
    /// modes are downgraded with a Logger.Warning ("shared PCH is Phase
    /// 1.4; falling back to NoSharedPCHs") so the build proceeds without
    /// shared-PCH support landing yet. Sim-path modules are gated upstream
    /// by <see cref="EnforceSimPathPchGate"/>.
    /// </summary>
    /// <returns>
    /// The effective PCH usage. Always one of <see cref="PCHUsageMode.NoPCHs"/>,
    /// <see cref="PCHUsageMode.NoSharedPCHs"/>, or <see cref="PCHUsageMode.Default"/>.
    /// </returns>
    public static PCHUsageMode ResolvePCHUsage(ModuleRules module)
    {
        ArgumentNullException.ThrowIfNull(module);

        return module.PCHUsage switch
        {
            PCHUsageMode.UseSharedPCHs => DowngradeShared(module, PCHUsageMode.UseSharedPCHs),
            PCHUsageMode.UseExplicitOrSharedPCHs => DowngradeShared(module, PCHUsageMode.UseExplicitOrSharedPCHs),
            _ => module.PCHUsage,
        };

        static PCHUsageMode DowngradeShared(ModuleRules module, PCHUsageMode declared)
        {
            Logger.Warning(
                $"Module '{module.Name}' declares PCHUsage = {declared}; shared PCH is Phase 1.4. " +
                "Falling back to NoSharedPCHs (per-module PCH only).",
                new DiagnosticContext { Module = module.Name, Tier = module.Tier.ToString() });
            return PCHUsageMode.NoSharedPCHs;
        }
    }

    /// <summary>
    /// Emit the per-platform flags for an <see cref="FPSemantics"/> value.
    /// Sim-path modules force <see cref="FPSemantics.Precise"/>; the
    /// shared helper in <see cref="GetCompileArguments_FPSemantics_Resolved"/>
    /// applies that clamp before calling subclass logic.
    /// </summary>
    protected abstract IEnumerable<string> GetCompileArguments_FPSemantics(FPSemantics fps);

    /// <summary>
    /// Emit the per-platform flags for an <see cref="OptimizeCodeMode"/>
    /// value, taking the build configuration into account (a
    /// <c>Debug</c> configuration ignores most optimization knobs).
    /// </summary>
    protected abstract IEnumerable<string> GetCompileArguments_OptimizeCode(
        OptimizeCodeMode mode,
        BuildConfiguration config);

    /// <summary>
    /// Emit the per-platform flags for a resolved <see cref="SimdLevel"/>
    /// value. Sim-path modules are clamped to <c>&lt;= SSE42</c> at flag
    /// derivation time per Contract Section 4.2 N7; the shared helper
    /// in <see cref="ResolveSimdLevel"/> applies that clamp and emits the
    /// resolved value.
    /// </summary>
    /// <param name="level">Resolved SIMD baseline.</param>
    /// <param name="simPath">True iff the owning module is sim-path.</param>
    protected abstract IEnumerable<string> GetCompileArguments_Simd(SimdLevel level, bool simPath);

    /// <summary>
    /// Emit the sim-path determinism flag set per Contract Section 4.2.
    /// Called on every sim-path TU regardless of <see cref="FPSemantics"/>
    /// (the precise-fp flag is part of the sim-path set, not the
    /// general FPSemantics path).
    /// </summary>
    protected abstract IEnumerable<string> GetCompileArguments_SimPath(ModuleRules module);

    /// <summary>
    /// Resolve a module's <see cref="SimdLevel"/> against the target's
    /// default and clamp to <c>&le; SSE42</c> for sim-path modules per
    /// Contract Section 4.2 N7. Throws <see cref="ToolchainBannedFlagException"/>
    /// (exit 41) if a sim-path module declares an explicit non-clamp-eligible
    /// level (AVX / AVX2 / AVX512).
    /// </summary>
    public static SimdLevel ResolveSimdLevel(ModuleRules module, TargetRules target)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(target);

        // Step 1: resolve Default.
        SimdLevel resolved = module.SimdLevel;
        if (resolved == SimdLevel.Default)
        {
            resolved = target.SimdLevelDefault;
            if (resolved == SimdLevel.Default)
            {
                // Target should never have Default itself; treat as
                // production baseline SSE42 to match Contract Section 4.2.
                resolved = SimdLevel.SSE42;
            }
        }

        // Step 2: enforce the SimPath ceiling.
        if (module.SimPath)
        {
            switch (resolved)
            {
                case SimdLevel.None:
                case SimdLevel.SSE2:
                case SimdLevel.SSE42:
                    // Eligible.
                    break;
                case SimdLevel.AVX:
                case SimdLevel.AVX2:
                case SimdLevel.AVX512:
                    throw new ToolchainBannedFlagException(
                        $"Module '{module.Name}' is SimPath but declares SimdLevel = {resolved}. "
                        + "Contract Rev 13 Section 4.2 clamps SimPath to <= SSE42; AVX/AVX2/AVX512 "
                        + "are banned because they enable FMA paths that violate determinism.");
                default:
                    throw new ToolchainBannedFlagException(
                        $"Module '{module.Name}' has unrecognized SimdLevel {resolved}.");
            }
        }

        return resolved;
    }

    /// <summary>
    /// Compute the resolved <see cref="FPSemantics"/> for a module. Sim-path
    /// modules are forced to <see cref="FPSemantics.Precise"/>; an explicit
    /// <see cref="FPSemantics.Imprecise"/> on a sim-path module throws
    /// <see cref="ToolchainBannedFlagException"/> (exit 41).
    /// </summary>
    public static FPSemantics ResolveFPSemantics(ModuleRules module)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (!module.SimPath)
        {
            return module.FPSemantics;
        }

        if (module.FPSemantics == FPSemantics.Imprecise)
        {
            throw new ToolchainBannedFlagException(
                $"Module '{module.Name}' is SimPath but declares FPSemantics = Imprecise. "
                + "Contract Rev 13 Section 4 bans Imprecise on sim-path modules; "
                + "use Precise (which is auto-promoted from Default).");
        }

        // Auto-promote Default to Precise on SimPath.
        return FPSemantics.Precise;
    }

    /// <summary>
    /// Convenience wrapper: resolve FPSemantics + dispatch to the
    /// subclass-provided <see cref="GetCompileArguments_FPSemantics"/>.
    /// </summary>
    protected IEnumerable<string> GetCompileArguments_FPSemantics_Resolved(ModuleRules module)
    {
        FPSemantics resolved = ResolveFPSemantics(module);
        return GetCompileArguments_FPSemantics(resolved);
    }

    /// <summary>
    /// Format a list of linker arguments as a response file body. One
    /// arg per line (LF terminators for cross-host determinism); args
    /// containing whitespace or embedded quotes are wrapped in double
    /// quotes with embedded quotes backslash-escaped. Both MSVC
    /// <c>link.exe</c> and the Clang driver parse response files using
    /// the CRT command-line rules captured here, so a single helper
    /// covers both <see cref="XMSVCToolChain.LinkModule"/> and
    /// <see cref="XClangToolChain.LinkModule"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why response files at all.</b> Modules with hundreds of object
    /// files (e.g. XCore at 250+ TUs) blow past the host's process-spawn
    /// command-line limit (~32 KB on Windows <c>CreateProcessW</c>;
    /// 256 KiB on glibc Linux <c>execve</c>). The standard MSVC + Clang
    /// fix is the <c>@response.rsp</c> indirection: the toolchain reads
    /// the file at startup and substitutes its contents into the
    /// argument stream. XBT applies the pattern unconditionally per the
    /// Prime Directive (no command-line-length heuristic).
    /// </para>
    /// <para>
    /// <b>Format.</b> One arg per line (the canonical MSVC convention,
    /// matches how the VS IDE writes its .rsp sidecars). LF (not CRLF)
    /// is used as the line terminator so the body's content hash is
    /// identical between Windows and Linux runners; both link.exe and
    /// clang accept either ending. Args containing whitespace (space,
    /// tab, newline, CR) or an embedded double-quote are wrapped in
    /// double quotes with embedded quotes prefixed by a backslash --
    /// the CRT command-line parsing both link.exe and clang follow.
    /// Empty args round-trip as <c>""</c>; a bare empty would otherwise
    /// vanish under the consecutive-whitespace-collapses rule.
    /// </para>
    /// </remarks>
    /// <param name="args">Argument list to serialize. Must not be null.</param>
    /// <returns>The response file body as a UTF-8-safe string.</returns>
    protected internal static string FormatResponseFile(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        StringBuilder sb = new();
        foreach (string arg in args)
        {
            AppendResponseFileArg(sb, arg);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Append <paramref name="arg"/> to <paramref name="sb"/>, quoting
    /// it with double-quotes when it contains whitespace or an embedded
    /// double-quote. Used by <see cref="FormatResponseFile"/>.
    /// </summary>
    private static void AppendResponseFileArg(StringBuilder sb, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            sb.Append("\"\"");
            return;
        }
        bool needsQuoting = false;
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '"')
            {
                needsQuoting = true;
                break;
            }
        }
        if (!needsQuoting)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '"')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        sb.Append('"');
    }
}

/// <summary>
/// Thrown by a toolchain when a module's declared flag combination is
/// banned by the contract (e.g. <c>SimPath = true</c> + <c>SimdLevel.AVX</c>,
/// or <c>SimPath = true</c> + <c>FPSemantics.Imprecise</c>). Maps to
/// Toolchain Contract Rev 13 Section 13 exit code 41
/// (<c>BannedApiOnSimPathTU</c> family -- the SimPath banned-flag check
/// shares the same exit code as the banned-API check).
/// </summary>
public sealed class ToolchainBannedFlagException : XBTException
{
    /// <summary>Construct a banned-flag exception with the given message.</summary>
    public ToolchainBannedFlagException(string message)
        : base(message, exitCode: 41)
    {
    }
}
