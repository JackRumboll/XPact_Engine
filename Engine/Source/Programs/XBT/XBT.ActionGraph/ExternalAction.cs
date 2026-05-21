// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph;

/// <summary>
/// Sealed, immutable implementation of <see cref="IExternalAction"/>.
/// Records the full action payload as <c>init</c>-only properties;
/// constructor verifies determinism invariants
/// (sorted prerequisites / produces / deletes) and computes
/// <see cref="CommandVersion"/> automatically on first access.
/// </summary>
/// <remarks>
/// <para>
/// Equality is reference-based for graph identity. Two distinct action
/// objects with the same payload are still distinct nodes (different
/// modules / TUs / passes). Graph topology uses
/// <see cref="CommandVersion"/> for content equality where it matters.
/// </para>
/// <para>
/// Construction goes through <see cref="Create"/> which enforces the
/// sort invariants. The default <c>record</c> constructor exists so
/// the C# <c>with</c>-expression continues to work; new actions built
/// from raw inputs must enter through <see cref="Create"/>.
/// </para>
/// </remarks>
public sealed record ExternalAction : IExternalAction
{
    /// <summary>The empty FileItem list, shared to avoid per-action allocations.</summary>
    private static readonly IReadOnlyList<FileItem> EmptyFileItems = Array.Empty<FileItem>();

    /// <summary>The empty string-list, shared.</summary>
    private static readonly IReadOnlyList<string> EmptyStrings = Array.Empty<string>();

    /// <inheritdoc/>
    public XActionType ActionType { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<FileItem> PrerequisiteItems { get; init; } = EmptyFileItems;

    /// <inheritdoc/>
    public IReadOnlyList<FileItem> ProducedItems { get; init; } = EmptyFileItems;

    /// <inheritdoc/>
    public IReadOnlyList<FileItem> DeleteItems { get; init; } = EmptyFileItems;

    /// <inheritdoc/>
    public string CommandPath { get; init; } = string.Empty;

    /// <inheritdoc/>
    public IReadOnlyList<string> CommandArguments { get; init; } = EmptyStrings;

    /// <inheritdoc/>
    public string? ResponseFileContents { get; init; }

    /// <inheritdoc/>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <inheritdoc/>
    public string CommandDescription { get; init; } = string.Empty;

    /// <inheritdoc/>
    public string StatusDescription { get; init; } = string.Empty;

    /// <inheritdoc/>
    public bool CanExecuteRemotely { get; init; } = true;

    /// <inheritdoc/>
    public IReadOnlyList<string> CacheKeyComponents { get; init; } = EmptyStrings;

    /// <inheritdoc/>
    public double Weight { get; init; } = 1.0;

    /// <inheritdoc/>
    public string? CacheBucket { get; init; }

    /// <inheritdoc/>
    public bool bUseActionHistory { get; init; } = true;

    /// <inheritdoc/>
    public string? Module { get; init; }

    /// <inheritdoc/>
    public string? Tier { get; init; }

    /// <inheritdoc/>
    public bool SimPath { get; init; }

    /// <inheritdoc/>
    public BuildConfiguration Configuration { get; init; }

    /// <inheritdoc/>
    public Platform Platform { get; init; }

    /// <inheritdoc/>
    /// <remarks>
    /// Phase 1 implementations leave this null. The interface property
    /// exists so its later population by Phase 2 toolchains (which will
    /// emit <c>-MD</c>/<c>-MF</c> <c>.d</c> files for Clang and
    /// <c>/sourceDependencies</c> JSON for MSVC) does not produce a
    /// schema bump in <see cref="ActionHistory.CurrentVersion"/>.
    /// </remarks>
    public FileItem? DependencyListFile { get; init; }

    /// <summary>
    /// Single-cell reference holding the lazily-computed
    /// <see cref="CommandVersion"/>. Read with <c>Volatile.Read</c>; the
    /// double-checked-lock idiom is used so first access pays the BLAKE3
    /// cost once and every subsequent access pays a single load.
    /// </summary>
    private IoHashHolder? _commandVersionCache;

    /// <inheritdoc/>
    public IoHash CommandVersion
    {
        get
        {
            IoHashHolder? cached = Volatile.Read(ref _commandVersionCache);
            if (cached is not null)
            {
                return cached.Value;
            }
            IoHash computed = ComputeCommandVersion();
            // Best-effort -- races converge on the same value because the
            // computation is a pure function of init-only properties.
            Interlocked.CompareExchange(ref _commandVersionCache, new IoHashHolder(computed), null);
            return computed;
        }
    }

    /// <summary>
    /// Public parameterless constructor exists for the C#
    /// <c>with</c>-expression. New instances built from raw inputs must
    /// enter through <see cref="Create"/> which validates the sort
    /// invariants.
    /// </summary>
    public ExternalAction()
    {
    }

    /// <summary>
    /// Factory that validates determinism invariants on a freshly-built
    /// action. Use this instead of direct construction when building
    /// actions from raw inputs; tests instantiate the record directly when
    /// they need to construct degenerate cases.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when any of the file-item lists is not sorted by
    /// <see cref="FileItem.FullPath"/> ordinal or contains a duplicate,
    /// or when <see cref="ActionType"/> names a reserved Phase 2 slot
    /// (audit fix R8-Mi3).
    /// </exception>
    public static ExternalAction Create(ExternalAction prototype)
    {
        ArgumentNullException.ThrowIfNull(prototype);

        ValidateNotReservedSlot(prototype.ActionType);
        ValidateSorted(prototype.PrerequisiteItems, nameof(PrerequisiteItems));
        ValidateSorted(prototype.ProducedItems, nameof(ProducedItems));
        ValidateSorted(prototype.DeleteItems, nameof(DeleteItems));

        return prototype;
    }

    /// <summary>
    /// Audit fix R8-Mi3: reject any <see cref="XActionType"/> whose
    /// name starts with <c>Reserved_</c>. The reserved slot range
    /// (9-12, 14-15) exists so Phase 2 systems can land without
    /// rotating <see cref="ActionHistory.CurrentVersion"/>; an
    /// accidental Phase 1 emit into a reserved slot would alias a
    /// legitimate Phase 2 action type at the moment that addendum
    /// lands, silently invalidating every <c>ActionHistory</c> entry
    /// that ever used the reserved ordinal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tests that need to construct reserved-slot actions for
    /// graph-shape coverage can bypass this gate by constructing the
    /// record directly (<c>new ExternalAction { ActionType = ... }</c>)
    /// rather than going through <see cref="Create"/>; the
    /// determinism-invariant assertion runs only in <see cref="Create"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="actionType"/>'s enum name starts
    /// with <c>Reserved_</c>.
    /// </exception>
    internal static void ValidateNotReservedSlot(XActionType actionType)
    {
        string name = actionType.ToString();
        if (name.StartsWith("Reserved_", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"ExternalAction.Create rejected ActionType={name} (slot {(int)actionType}): " +
                "the reserved-slot range is preallocated for Phase 2 systems " +
                "(distributed cache, live coding, etc.) and MUST NOT be emitted " +
                "by Phase 1 code. Pick an existing named slot from XActionType " +
                "(CompileCppAction, PCHGenerationAction, ...) appropriate to the " +
                "action's behaviour, or wait for the Phase 2 addendum that " +
                "promotes the reserved slot to a named type.",
                nameof(actionType));
        }
    }

    private static void ValidateSorted(IReadOnlyList<FileItem> items, string fieldName)
    {
        for (int i = 1; i < items.Count; i++)
        {
            string previous = items[i - 1].FullPath;
            string current = items[i].FullPath;
            int cmp = string.CompareOrdinal(previous, current);
            if (cmp > 0)
            {
                throw new ArgumentException(
                    $"ExternalAction.{fieldName} must be sorted by FullPath ordinal; "
                    + $"got '{previous}' before '{current}' at index {i}.",
                    fieldName);
            }
            if (cmp == 0)
            {
                throw new ArgumentException(
                    $"ExternalAction.{fieldName} contains the duplicate entry '{current}'.",
                    fieldName);
            }
        }
    }

    /// <summary>
    /// BLAKE3 of the action's payload. Includes everything that influences
    /// the on-disk command line (executable + arguments + response file +
    /// working directory) and the explicit <see cref="CacheKeyComponents"/>.
    /// Excludes scheduling metadata (Weight, CacheBucket) which do not
    /// influence the output.
    /// </summary>
    private IoHash ComputeCommandVersion()
    {
        using Hasher hasher = Hasher.New();
        Span<byte> intBuffer = stackalloc byte[4];

        // 1. ActionType ordinal. Determinism: int writes as little-endian.
        BitConverter.TryWriteBytes(intBuffer, (int)ActionType);
        hasher.Update(intBuffer);

        // 2. CommandPath.
        UpdateUtf8(hasher, CommandPath);

        // 3. CommandArguments (ordered; per-arg length-prefixed so
        //    ["a", "bc"] and ["ab", "c"] never collide).
        BitConverter.TryWriteBytes(intBuffer, CommandArguments.Count);
        hasher.Update(intBuffer);
        foreach (string arg in CommandArguments)
        {
            UpdateUtf8(hasher, arg);
        }

        // 4. ResponseFileContents (nullable; length-prefix the discriminator).
        if (ResponseFileContents is null)
        {
            BitConverter.TryWriteBytes(intBuffer, -1);
            hasher.Update(intBuffer);
        }
        else
        {
            UpdateUtf8(hasher, ResponseFileContents);
        }

        // 5. WorkingDirectory.
        UpdateUtf8(hasher, WorkingDirectory);

        // 6. CacheKeyComponents (ordered).
        BitConverter.TryWriteBytes(intBuffer, CacheKeyComponents.Count);
        hasher.Update(intBuffer);
        foreach (string component in CacheKeyComponents)
        {
            UpdateUtf8(hasher, component);
        }

        // 7. Configuration + Platform (these affect output in subtle ways
        //    -- e.g. macro definitions -- but might not appear in the
        //    command line for in-process actions like WriteManifest).
        BitConverter.TryWriteBytes(intBuffer, (int)Configuration);
        hasher.Update(intBuffer);
        BitConverter.TryWriteBytes(intBuffer, (int)Platform);
        hasher.Update(intBuffer);

        // 8. SimPath flag (one byte).
        Span<byte> simPathByte = stackalloc byte[1];
        simPathByte[0] = SimPath ? (byte)1 : (byte)0;
        hasher.Update(simPathByte);

        Span<byte> digest = stackalloc byte[IoHash.Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    private static void UpdateUtf8(Hasher hasher, string s)
    {
        Span<byte> intBuffer = stackalloc byte[4];

        // Bound-check the UTF-8 byte count without allocating: small
        // strings go on the stack; larger strings rent a pooled buffer.
        int byteCount = Encoding.UTF8.GetByteCount(s);
        BitConverter.TryWriteBytes(intBuffer, byteCount);
        hasher.Update(intBuffer);

        if (byteCount == 0)
        {
            return;
        }

        if (byteCount <= 256)
        {
            Span<byte> stackBuffer = stackalloc byte[256];
            int written = Encoding.UTF8.GetBytes(s, stackBuffer);
            hasher.Update(stackBuffer[..written]);
            return;
        }

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int written = Encoding.UTF8.GetBytes(s, buffer);
            hasher.Update(buffer.AsSpan(0, written));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Single-cell holder boxing the <see cref="IoHash"/> struct so the
    /// double-checked-locking idiom works on a reference type.
    /// </summary>
    private sealed class IoHashHolder
    {
        public readonly IoHash Value;
        public IoHashHolder(IoHash value) { Value = value; }
    }
}
