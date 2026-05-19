// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Blake3;

namespace Simgenics.XPact.XBT.Core;

/// <summary>
/// Content-addressable hash. 32 bytes (256 bits) of BLAKE3 output.
/// </summary>
/// <remarks>
/// <para>
/// XBT uses BLAKE3 as its content-hash algorithm for all three cache layers
/// per <c>/Documents/XBT.html</c> Section 15.5; the choice mirrors the
/// intent of Unreal Engine's <c>IoHash</c> while using BLAKE3 (which is
/// significantly faster than UE's BLAKE3 wrapping and unkeyed by default).
/// </para>
/// <para>
/// Immutable value type. Equality is bytewise. <see cref="GetHashCode"/> hashes
/// the first 4 bytes of the digest because BLAKE3's avalanche makes any
/// 32-bit slice a uniform-random selector.
/// </para>
/// </remarks>
public readonly struct IoHash : IEquatable<IoHash>
{
    /// <summary>Length of the digest in bytes.</summary>
    public const int Length = 32;

    /// <summary>Hex string length (two characters per byte).</summary>
    public const int HexLength = Length * 2;

    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;

    /// <summary>The all-zero hash, used as a sentinel "not computed".</summary>
    public static IoHash Zero => default;

    /// <summary>
    /// Construct from a 32-byte digest. Throws if the span is not exactly
    /// 32 bytes.
    /// </summary>
    public IoHash(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != Length)
        {
            throw new ArgumentException(
                $"IoHash requires exactly {Length} bytes (got {digest.Length}).",
                nameof(digest));
        }

        // Copy into four little-endian ulongs. Endianness does not matter
        // for equality/hashing; we only need the same conversion on both
        // sides of the boundary.
        _w0 = BitConverter.ToUInt64(digest[0..8]);
        _w1 = BitConverter.ToUInt64(digest[8..16]);
        _w2 = BitConverter.ToUInt64(digest[16..24]);
        _w3 = BitConverter.ToUInt64(digest[24..32]);
    }

    /// <summary>
    /// Write the 32-byte digest into <paramref name="destination"/>. Throws
    /// if the destination is smaller than 32 bytes.
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                $"Destination span must be at least {Length} bytes (got {destination.Length}).",
                nameof(destination));
        }

        BitConverter.TryWriteBytes(destination[0..8],  _w0);
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

    /// <summary>Compute the BLAKE3 hash of an in-memory buffer.</summary>
    public static IoHash Compute(ReadOnlySpan<byte> data)
    {
        using Hasher hasher = Hasher.New();
        hasher.Update(data);
        Span<byte> digest = stackalloc byte[Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    /// <summary>
    /// Compute the BLAKE3 hash of a stream's remaining contents. Reads in
    /// 64 KiB chunks; allocates one heap buffer for the read window.
    /// </summary>
    public static IoHash Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        const int chunkSize = 64 * 1024;
        byte[] buffer = new byte[chunkSize];

        using Hasher hasher = Hasher.New();
        int read;
        while ((read = stream.Read(buffer, 0, chunkSize)) > 0)
        {
            hasher.Update(buffer.AsSpan(0, read));
        }

        Span<byte> digest = stackalloc byte[Length];
        hasher.Finalize(digest);
        return new IoHash(digest);
    }

    /// <summary>Parse an IoHash from a 64-character lowercase hex string.</summary>
    public static IoHash Parse(ReadOnlySpan<char> hex)
    {
        if (hex.Length != HexLength)
        {
            throw new FormatException(
                $"IoHash hex must be {HexLength} characters (got {hex.Length}).");
        }

        Span<byte> bytes = stackalloc byte[Length];
        for (int i = 0; i < Length; i++)
        {
            int hi = HexNibble(hex[i * 2]);
            int lo = HexNibble(hex[i * 2 + 1]);
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return new IoHash(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexNibble(char c)
    {
        if (c >= '0' && c <= '9') { return c - '0'; }
        if (c >= 'a' && c <= 'f') { return c - 'a' + 10; }
        if (c >= 'A' && c <= 'F') { return c - 'A' + 10; }
        throw new FormatException($"Invalid hex digit '{c}'.");
    }

    public bool Equals(IoHash other)
        => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

    public override bool Equals(object? obj)
        => obj is IoHash other && Equals(other);

    public override int GetHashCode()
        => unchecked((int)(_w0 ^ (_w0 >> 32)));

    public static bool operator ==(IoHash a, IoHash b) => a.Equals(b);
    public static bool operator !=(IoHash a, IoHash b) => !a.Equals(b);

    /// <summary>Render the digest as a 64-character lowercase hex string.</summary>
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
}
