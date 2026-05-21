// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Simgenics.XPact.XHT.Core;

/// <summary>
/// 32-byte content-addressable hash used by XHT. Phase 1b uses SHA-256
/// as a placeholder implementation; the algorithm swap to vendored
/// BLAKE3 is tracked at <c>/Documents/XHT.html</c> Rev 6 Section 16
/// (cache-key invalidation + content-hash discipline) and Section 25.1
/// (vendored BLAKE3 pattern shared with XBT).
/// </summary>
/// <remarks>
/// <para>
/// TODO Phase 1c (or later): swap to vendored BLAKE3 per XHT.html
/// Section 16 + XBT's Blake3 vendoring pattern at
/// <c>Engine/Source/Programs/XBT/XBT.Core/IoHash.cs</c>. The 32-byte
/// surface, the <see cref="Hex16"/> 16-char prefix discipline, and the
/// <see cref="Length"/> constant all stay; only the algorithm changes.
/// The XHT-vs-XBT hash equivalence under the BLAKE3 swap is Phase 1c
/// acceptance criteria. SHA-256 is acceptable per Section 16's "STAGE B:
/// actual algorithm" deferral.
/// </para>
/// <para>
/// <b>Hex16 format.</b> The 16-char lowercase hex prefix is the canonical
/// hash form in the per-module <c>.gen.manifest</c>'s
/// <c>[Generated]</c> + <c>[Inputs]</c> sections per
/// <c>/Documents/XHT.html</c> Rev 6 Section 9.2: "Hashes in the
/// <c>[Generated]</c> section are the 16-hex prefix of BLAKE3" -- the
/// truncation discipline XBT uses for <c>ContractStructureHash</c> and
/// per-action cache keys.
/// </para>
/// </remarks>
public readonly struct IoHash : IEquatable<IoHash>
{
    /// <summary>Length of the digest in bytes (32 = 256 bits).</summary>
    public const int Length = 32;

    /// <summary>Hex string length for the full digest (two characters per byte).</summary>
    public const int HexLength = Length * 2;

    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;

    /// <summary>The all-zero hash, used as a sentinel "not computed".</summary>
    public static IoHash Zero => default;

    /// <summary>
    /// Construct from a 32-byte digest. Throws if the span is not
    /// exactly 32 bytes.
    /// </summary>
    /// <param name="digest">The 32-byte digest.</param>
    /// <exception cref="ArgumentException">If <paramref name="digest"/> is not exactly 32 bytes.</exception>
    public IoHash(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Length)
        {
            throw new ArgumentException(
                $"IoHash requires exactly {Length} bytes (got {digest.Length}).",
                nameof(digest));
        }

        _w0 = BitConverter.ToUInt64(digest[0..8]);
        _w1 = BitConverter.ToUInt64(digest[8..16]);
        _w2 = BitConverter.ToUInt64(digest[16..24]);
        _w3 = BitConverter.ToUInt64(digest[24..32]);
    }

    /// <summary>
    /// Compute the content hash of a UTF-8 byte sequence. Phase 1b uses
    /// SHA-256; Phase 1c+ swaps to BLAKE3 per the TODO above.
    /// </summary>
    /// <param name="bytes">The byte sequence to hash.</param>
    /// <returns>The 32-byte content hash.</returns>
    public static IoHash FromUtf8(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[Length];
        bool ok = SHA256.TryHashData(bytes, digest, out int written);
        if (!ok || written != Length)
        {
            // SHA256.TryHashData always returns 32 bytes for a 32-byte
            // destination; the branch is defensive only.
            throw new InvalidOperationException(
                $"SHA-256 hash produced {written} bytes (expected {Length}).");
        }
        return new IoHash(digest);
    }

    /// <summary>
    /// Compute the content hash of a string by encoding it as UTF-8 and
    /// hashing the bytes. Convenience overload for the common
    /// "hash this string" case.
    /// </summary>
    /// <param name="s">The string to hash. Must not be null.</param>
    /// <returns>The 32-byte content hash.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="s"/> is null.</exception>
    public static IoHash FromString(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        // Use UTF8 without BOM so the hash is stable against any
        // accidental BOM differences across writers; matches the engine-
        // wide UTF-8 commitment at Contract Section 6.
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(s);
        return FromUtf8(bytes);
    }

    /// <summary>
    /// Compute the content hash of a stream's remaining contents. Reads
    /// in 64 KiB chunks.
    /// </summary>
    /// <param name="stream">The stream to hash. Must not be null.</param>
    /// <returns>The 32-byte content hash.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="stream"/> is null.</exception>
    public static IoHash FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using SHA256 sha = SHA256.Create();
        // SHA256.ComputeHash(Stream) handles the chunked read internally;
        // we don't need to re-implement the chunked loop here.
        byte[] digest = sha.ComputeHash(stream);
        return new IoHash(digest);
    }

    /// <summary>
    /// Write the 32-byte digest into <paramref name="destination"/>.
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                $"Destination span must be at least {Length} bytes (got {destination.Length}).",
                nameof(destination));
        }

        BitConverter.TryWriteBytes(destination[0..8], _w0);
        BitConverter.TryWriteBytes(destination[8..16], _w1);
        BitConverter.TryWriteBytes(destination[16..24], _w2);
        BitConverter.TryWriteBytes(destination[24..32], _w3);
    }

    /// <summary>Return the 32-byte digest as a new array.</summary>
    public byte[] ToByteArray()
    {
        byte[] result = new byte[Length];
        CopyTo(result);
        return result;
    }

    /// <summary>
    /// Return the 16-character lowercase hex of the first 8 bytes of
    /// the digest -- the manifest hash format per
    /// <c>/Documents/XHT.html</c> Rev 6 Section 9.2.
    /// </summary>
    /// <returns>16 lowercase hex characters (64 bits of collision resistance).</returns>
    public string Hex16()
    {
        Span<byte> bytes = stackalloc byte[Length];
        CopyTo(bytes);

        const string lookup = "0123456789abcdef";
        Span<char> result = stackalloc char[16];
        // First 8 bytes -> 16 hex chars.
        for (int i = 0; i < 8; i++)
        {
            byte b = bytes[i];
            result[i * 2]     = lookup[b >> 4];
            result[i * 2 + 1] = lookup[b & 0x0F];
        }
        return new string(result);
    }

    /// <summary>
    /// Render the full digest as a 64-character lowercase hex string.
    /// </summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Length];
        CopyTo(bytes);

        const string lookup = "0123456789abcdef";
        Span<char> result = stackalloc char[HexLength];
        for (int i = 0; i < Length; i++)
        {
            byte b = bytes[i];
            result[i * 2]     = lookup[b >> 4];
            result[i * 2 + 1] = lookup[b & 0x0F];
        }
        return new string(result);
    }

    /// <inheritdoc/>
    public bool Equals(IoHash other)
        => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    /// <inheritdoc/>
    public override bool Equals(object? obj)
        => obj is IoHash other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
        => unchecked((int)(_w0 ^ (_w0 >> 32)));

    /// <summary>Bytewise-equality operator.</summary>
    public static bool operator ==(IoHash a, IoHash b) => a.Equals(b);

    /// <summary>Bytewise-inequality operator.</summary>
    public static bool operator !=(IoHash a, IoHash b) => !a.Equals(b);
}
