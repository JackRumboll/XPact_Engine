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
    public abstract IReadOnlyList<IExternalAction> CompileSource(
        ModuleRules module,
        TargetRules target,
        FileItem sourceFile,
        string outputDir);

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
