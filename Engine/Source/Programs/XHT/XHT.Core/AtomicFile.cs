// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// Atomic-write helpers. Writes go to a uniquely-named temp file in the
/// destination directory, fsync, then <see cref="File.Move(string,string,bool)"/>
/// over the target. The rename is atomic on every supported filesystem
/// (NTFS, ext4, APFS); an external observer never sees a torn output.
/// Mirrors the temp-file + fsync + AV-retry pattern in
/// <c>XBT.Manifest.ManifestJson.AtomicWriteAllBytes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Per <c>/Documents/XHT.html</c> Rev 5 Section 8.5 + Section 14.2: every
/// XHT output -- per-header <c>.gen.h</c>, per-header <c>.gen.cpp</c>,
/// per-module <c>.init.gen.cpp</c>, per-module <c>.gen.manifest</c> --
/// MUST be written through an atomic-rename pattern so partial writes
/// never escape into the on-disk artefact set. This helper is the
/// canonical implementation; raw <c>File.WriteAllText</c> calls in the
/// XHT.Manifest / XHT.Emitter paths are forbidden.
/// </para>
/// <para>
/// <b>Temp-file naming.</b> The temp filename is
/// <c>{basename}.tmp.{pid}.{guid}</c> with no timestamp anywhere; this
/// matches the Contract Section 2.1 ban on timestamps in any artefact
/// name. The PID + GUID combination is the de-collision strategy for
/// the (rare) case where two XHT processes target the same intermediate
/// directory.
/// </para>
/// <para>
/// <b>fsync before rename.</b> <see cref="FileStream.Flush(bool)"/> with
/// <c>flushToDisk = true</c> issues an fsync before the move; a power
/// loss after rename cannot leave a zero-byte post-allocation hole in
/// the destination. Without fsync the rename can complete while the data
/// still sits in the OS page cache; a power loss in that window leaves
/// a corrupt destination.
/// </para>
/// <para>
/// <b>AV-retry on rename.</b> The rename is wrapped in
/// <see cref="FileSystemOps.RetryOnTransientIOException"/> so a Windows
/// Defender scan window during the write does not fail the build.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>
    /// Atomically write <paramref name="content"/> to
    /// <paramref name="destPath"/>. Encoding is UTF-8 without a
    /// byte-order mark (matching the Contract Section 6 engine-wide UTF-8
    /// commitment).
    /// </summary>
    /// <param name="destPath">Absolute filesystem path of the output file. Must not be null or empty.</param>
    /// <param name="content">Text to write. Must not be null; empty string is allowed and produces an empty file.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="destPath"/> or <paramref name="content"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="destPath"/> is empty or whitespace.</exception>
    public static void WriteAllText(string destPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        ArgumentNullException.ThrowIfNull(content);

        // UTF-8 without BOM matches every other XPact tool's on-disk
        // encoding (XBT, XIL2CPP, the engine runtime). Encoding.UTF8 by
        // default emits a BOM; use a fresh UTF8Encoding(false) instance.
        UTF8Encoding utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        byte[] bytes = utf8NoBom.GetBytes(content);
        WriteAllBytes(destPath, bytes);
    }

    /// <summary>
    /// Atomically write <paramref name="bytes"/> to
    /// <paramref name="destPath"/>. Used for binary artefacts (FBS
    /// sidecars, Brotli-compressed AST caches, etc.).
    /// </summary>
    /// <param name="destPath">Absolute filesystem path of the output file. Must not be null or empty.</param>
    /// <param name="bytes">Bytes to write.</param>
    /// <exception cref="ArgumentException">If <paramref name="destPath"/> is empty or whitespace.</exception>
    public static void WriteAllBytes(string destPath, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);

        string? directory = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        string parent = string.IsNullOrEmpty(directory) ? "." : directory;

        // Temp-file naming: <basename>.tmp.<pid>.<guid> -- no timestamp
        // anywhere per Contract Section 2.1 (footgun preempt).
        int pid = Environment.ProcessId;
        string nonce = Guid.NewGuid().ToString("N");
        string baseName = Path.GetFileName(destPath);
        string tempPath = Path.Combine(parent, $"{baseName}.tmp.{pid}.{nonce}");

        // Span<byte> can't cross the using-statement boundary cleanly;
        // copy into a heap buffer for the write (the data already comes
        // in as a span, but FileStream.Write accepts byte[] directly so
        // we materialise once).
        byte[] heap = bytes.ToArray();
        using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(heap, 0, heap.Length);
            // fsync before rename so the rename's atomic window does
            // not include an empty / partially-flushed file.
            fs.Flush(flushToDisk: true);
        }

        // AV-retry on the rename per FileSystemOps' documented schedule.
        FileSystemOps.RetryOnTransientIOException(
            () => File.Move(tempPath, destPath, overwrite: true),
            operationDescription: $"AtomicFile.WriteAllBytes rename to {destPath}");
    }
}
