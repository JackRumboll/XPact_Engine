// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Blake3;
using Simgenics.XPact.XBT.Core;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.ActionGraph.Actions;

/// <summary>
/// Common base for first-class XBT-authored <see cref="IExternalAction"/>
/// implementations such as <see cref="ValidateCopyrightAction"/>. Provides
/// lazy <see cref="CommandVersion"/> derivation, empty-by-default
/// collection properties, and the boilerplate <see cref="IExternalAction"/>
/// surface so concrete actions can focus on their own
/// <see cref="ComputeCommandVersion"/> override.
/// </summary>
/// <remarks>
/// <para>
/// The class mirrors the field shape and the determinism invariants of
/// <see cref="ExternalAction"/> (which is the sealed record used by
/// toolchain-emitted actions). The difference is that
/// <see cref="ActionBase"/> is a base class for XBT-internal action types
/// whose <see cref="CommandVersion"/> is computed from the action's
/// own custom inputs (the union of source-file hashes for
/// <c>ValidateCopyrightAction</c>; the PCH header's content for
/// <c>PCHGenerationAction</c> when that lands) rather than from the
/// command-line bytes.
/// </para>
/// <para>
/// Per Toolchain Contract Rev 13 Section 10.3 every <c>IExternalAction</c>
/// must surface a stable, deterministic <see cref="CommandVersion"/>.
/// Concrete subclasses override <see cref="ComputeCommandVersion"/> and
/// hash whatever set of inputs influences their output; the base class
/// caches the value behind a double-checked-locking idiom so first access
/// pays the BLAKE3 cost once and every subsequent access pays a single
/// load.
/// </para>
/// </remarks>
public abstract class ActionBase : IExternalAction
{
    /// <summary>Shared empty <see cref="FileItem"/> list.</summary>
    protected static readonly IReadOnlyList<FileItem> EmptyFileItems = Array.Empty<FileItem>();

    /// <summary>Shared empty string list.</summary>
    protected static readonly IReadOnlyList<string> EmptyStrings = Array.Empty<string>();

    /// <inheritdoc/>
    public abstract XActionType ActionType { get; }

    /// <inheritdoc/>
    public virtual IReadOnlyList<FileItem> PrerequisiteItems => EmptyFileItems;

    /// <inheritdoc/>
    public virtual IReadOnlyList<FileItem> ProducedItems => EmptyFileItems;

    /// <inheritdoc/>
    public virtual IReadOnlyList<FileItem> DeleteItems => EmptyFileItems;

    /// <inheritdoc/>
    public virtual string CommandPath => "<in-process>";

    /// <inheritdoc/>
    public virtual IReadOnlyList<string> CommandArguments => EmptyStrings;

    /// <inheritdoc/>
    public virtual string? ResponseFileContents => null;

    /// <inheritdoc/>
    public virtual string WorkingDirectory => string.Empty;

    /// <inheritdoc/>
    public virtual string CommandDescription => string.Empty;

    /// <inheritdoc/>
    public virtual string StatusDescription => string.Empty;

    /// <inheritdoc/>
    public virtual bool CanExecuteRemotely => false;

    /// <inheritdoc/>
    public virtual IReadOnlyList<string> CacheKeyComponents => EmptyStrings;

    /// <inheritdoc/>
    public virtual double Weight => 1.0;

    /// <inheritdoc/>
    public virtual string? CacheBucket => null;

    /// <inheritdoc/>
    public virtual bool bUseActionHistory => true;

    /// <inheritdoc/>
    public virtual string? Module => null;

    /// <inheritdoc/>
    public virtual string? Tier => null;

    /// <inheritdoc/>
    public virtual bool SimPath => false;

    /// <inheritdoc/>
    public virtual BuildConfiguration Configuration => default;

    /// <inheritdoc/>
    public virtual Platform Platform => default;

    /// <inheritdoc/>
    /// <remarks>
    /// Phase 1 XBT-internal actions (copyright validation, in-process
    /// emit-only passes) do not emit depfiles, so the default is null.
    /// Subclasses whose subprocess emits an MSVC <c>/sourceDependencies</c>
    /// JSON or a Clang <c>-MF</c> <c>.d</c> file override this with the
    /// emitted file path; the cache layer will parse it in Phase 2.
    /// </remarks>
    public virtual FileItem? DependencyListFile => null;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Audit fix R5-C2: XBT-internal action subclasses default to the
    /// temp-rename contract (the safer choice for in-process / text-
    /// emission actions where the executor has full control over the
    /// byte stream). Subclasses whose runner writes directly to the
    /// final path (rare for XBT-internal actions) override to true.
    /// </para>
    /// </remarks>
    public virtual bool bProducerWritesFinalPath => false;

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
            Interlocked.CompareExchange(ref _commandVersionCache, new IoHashHolder(computed), null);
            return computed;
        }
    }

    /// <summary>
    /// Subclass override producing the BLAKE3 hash of the action's
    /// effective inputs. Must be a pure function of init-only state so
    /// concurrent calls converge on the same value.
    /// </summary>
    protected abstract IoHash ComputeCommandVersion();

    /// <summary>
    /// UTF-8 length-prefixed update helper used by subclass
    /// <see cref="ComputeCommandVersion"/> implementations to feed
    /// strings into the BLAKE3 hasher deterministically (no encoding
    /// ambiguity, no collision between two strings sharing a prefix).
    /// </summary>
    protected static void UpdateUtf8(Hasher hasher, string s)
    {
        Span<byte> intBuffer = stackalloc byte[4];
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
