// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Reflection;
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
/// <b>Audit fix R3-M2: source-of-truth is the XBT assembly file, not
/// the host process.</b> The previous implementation read
/// <see cref="System.Diagnostics.Process"/>'s
/// <c>MainModule.FileName</c>, which under <c>dotnet test</c> resolves
/// to <c>testhost.exe</c> -- a value totally unrelated to the XBT logic
/// being tested. Tests verified relative behaviour (override rotation)
/// but never the absolute production hash. The Round-3 fix hashes the
/// <see cref="ToolchainSelfHash"/> assembly's own <c>Location</c>
/// (i.e. <c>XBT.Core.dll</c> in production), which is stable under
/// both production and the test harness. Single-file-published
/// scenarios (where <c>Assembly.Location</c> is empty) fail loudly via
/// a <see cref="InvalidOperationException"/> -- the previous
/// <c>"(no-self-hash)"</c> sentinel masked a real correctness gap, so
/// the round-3 fix prefers a clear failure over silent staleness.
/// </para>
/// <para>
/// The hash is computed once on first access; subsequent reads pay
/// only a volatile read.
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
    /// XBT assembly file on disk. Computed lazily once per process;
    /// subsequent reads return the cached value. When the test-only
    /// override is set (via <see cref="__SetForTesting"/>), the
    /// override value is returned instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the XBT assembly's <see cref="Assembly.Location"/>
    /// is empty (single-file-published scenarios) and no test override
    /// is installed. The throw is intentional: a silent
    /// <c>"(no-self-hash)"</c> sentinel would mask a real cache-key
    /// gap. The fix for a single-file-published scenario is to embed
    /// the hash at publish time and supply it via a release-only
    /// override, not to swallow the missing-location case.
    /// </exception>
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

    /// <summary>
    /// Compute the XBT-binary content hash. Reads the
    /// <see cref="ToolchainSelfHash"/> assembly's <c>Location</c>
    /// (the canonical on-disk DLL/EXE that contains XBT logic) and
    /// hashes those bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assembly choice matters: <c>ToolchainSelfHash</c> lives in
    /// <c>XBT.Core</c>, but the meaningful hash is "the running XBT
    /// binary's content". XBT.Core is loaded by every XBT process
    /// (BuildMode, every other mode, the test harness) and its
    /// <c>Location</c> is the absolute path to <c>XBT.Core.dll</c>
    /// in every supported scenario except single-file-publish. In
    /// the test harness <c>typeof(...).Assembly.Location</c> returns
    /// <c>...\bin\Debug\net8.0\XBT.Core.dll</c>, which is the actual
    /// XBT logic being tested -- exactly what we want.
    /// </para>
    /// </remarks>
    private static string Compute()
    {
        Assembly assembly = typeof(ToolchainSelfHash).Assembly;
        string location = assembly.Location;

        if (string.IsNullOrEmpty(location))
        {
            // Single-file-publish: Assembly.Location returns empty.
            // Fail loudly so the operator knows the cache-key surface
            // is incomplete. A test-only override is the supported
            // path; production should embed the hash at publish time
            // and install it via the override.
            throw new InvalidOperationException(
                "ToolchainSelfHash.XbtBinaryHash cannot be computed: "
                + $"assembly '{assembly.GetName().FullName}' has an empty Location "
                + "(typical of single-file-published binaries). The XBT cache-key "
                + "surface depends on this hash; configure a release-time override "
                + "via ToolchainSelfHash.__SetForTesting before the first cache-key "
                + "read, or build without single-file-publish.");
        }

        if (!File.Exists(location))
        {
            // Assembly resolved a location but the file is no longer on
            // disk (deleted mid-run, virtualised filesystem, etc.). Same
            // fail-loud rationale as the empty-location case.
            throw new InvalidOperationException(
                $"ToolchainSelfHash.XbtBinaryHash cannot be computed: "
                + $"assembly file '{location}' does not exist on disk. "
                + "The XBT cache-key surface depends on this hash; the missing "
                + "binary indicates a corrupt installation.");
        }

        // Audit fix R7-C3: AV-retry-wrap the read so a sibling
        // antivirus scan locking the just-loaded binary surfaces as
        // retries on the standard back-off schedule rather than as a
        // build failure on the first read.
        return FileSystemOps.RetryOnTransientIOException(() =>
        {
            using FileStream fs = File.OpenRead(location);
            IoHash digest = IoHash.Compute(fs);
            return digest.ToString()[..16];
        });
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
