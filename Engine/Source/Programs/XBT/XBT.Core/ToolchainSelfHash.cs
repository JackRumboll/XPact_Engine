// Copyright Simgenics. All Rights Reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Audit fix R8-M4: process-wide, lazily-computed BLAKE3 content hash
/// of the running XBT binary. The hash flows through every toolchain
/// emit site's <see cref="ActionGraph.IExternalAction.CacheKeyComponents"/>
/// so a rebuild of XBT itself (e.g. a logic change in command-line
/// construction, flag emission ordering, or path normalisation)
/// correctly invalidates every cached compile / PCH / link.
/// </summary>
/// <remarks>
/// <para>
/// Without this contribution, a fix to XBT's command-line construction
/// could land while every Phase 1 ActionHistory entry continues to be
/// re-used -- the existing entries' <see cref="ActionGraph.IExternalAction.CommandVersion"/>
/// only folds the produced command line, not the producer logic. The
/// XBT-binary hash closes that loop: every action emitted by a newer
/// XBT produces a distinct cache key from one emitted by an older XBT,
/// even when the resulting command-line strings are byte-identical.
/// </para>
/// <para>
/// The hash is computed once on first access; subsequent reads pay
/// only a volatile read. Best-effort: if the process's MainModule
/// cannot be resolved (rare -- typically only in unit-test harnesses
/// that load the assembly via reflection without spawning a process
/// for it), the sentinel string <c>"(no-self-hash)"</c> is returned.
/// The sentinel still participates in the cache key consistently, so
/// two reads in the same process produce equal keys -- the value only
/// breaks down across-process where reproducibility is governed by
/// the binary identity anyway.
/// </para>
/// <para>
/// The truncation to the first 16 hex characters matches the
/// <see cref="Manifest.ContractVersion"/> truncation discipline: 64
/// bits of collision space is sufficient for the lifetime of any
/// single build and keeps the cache-key component readable in
/// diagnostic dumps.
/// </para>
/// </remarks>
public static class ToolchainSelfHash
{
    private static string? s_cachedHash;
    private static string? s_overrideValue;
    private static readonly object s_gate = new();

    /// <summary>
    /// First 16 hex characters of the BLAKE3 content hash of the
    /// current process's main executable. Computed lazily once per
    /// process; subsequent reads return the cached value. When the
    /// test-only override is set (via <see cref="__SetForTesting"/>),
    /// the override value is returned instead.
    /// </summary>
    public static string XbtBinaryHash
    {
        get
        {
            // Volatile read so a concurrent test that calls
            // __SetForTesting between two reads observes the override
            // on the second read without a lock.
            string? overrideValue = Volatile.Read(ref s_overrideValue);
            if (overrideValue is not null)
            {
                return overrideValue;
            }
            string? cached = Volatile.Read(ref s_cachedHash);
            if (cached is not null)
            {
                return cached;
            }
            lock (s_gate)
            {
                if (s_cachedHash is null)
                {
                    s_cachedHash = Compute();
                }
                return s_cachedHash;
            }
        }
    }

    private static string Compute()
    {
        try
        {
            string? path = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "(no-self-hash)";
            }
            using FileStream fs = File.OpenRead(path);
            IoHash digest = IoHash.Compute(fs);
            return digest.ToString()[..16];
        }
        catch
        {
            // Best-effort. The reflection-only test harness path
            // sometimes fails to expose MainModule; the sentinel keeps
            // the cache key well-formed for downstream consumers.
            return "(no-self-hash)";
        }
    }

    /// <summary>
    /// Test-only override: force the self-hash to a specific value
    /// for deterministic test setup. Pass null to clear the override
    /// and resume normal computation on next read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This API is internal so production code cannot accidentally
    /// short-circuit the genuine hash. Tests reach it via
    /// <c>InternalsVisibleTo("XBT.Tests")</c> per the existing logger
    /// test-hook discipline.
    /// </para>
    /// </remarks>
    internal static void __SetForTesting(string? value)
    {
        Volatile.Write(ref s_overrideValue, value);
    }
}
