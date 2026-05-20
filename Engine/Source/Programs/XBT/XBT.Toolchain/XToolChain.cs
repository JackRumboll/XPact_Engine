// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
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
    public abstract IReadOnlyList<IExternalAction> CompileSource(
        ModuleRules module,
        TargetRules target,
        FileItem sourceFile,
        string outputDir,
        PCHBinding? pch = null);

    /// <summary>
    /// Produce the link action for a module. One
    /// <see cref="XActionType.LinkModuleAction"/> per module.
    /// </summary>
    public abstract IExternalAction LinkModule(
        ModuleRules module,
        TargetRules target,
        IReadOnlyList<FileItem> objectFiles,
        string outputDir);

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
        string outputDir);

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
        string outputDir);

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
