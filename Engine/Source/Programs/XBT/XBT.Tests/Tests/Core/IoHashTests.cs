// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.Core;
using Xunit;

namespace Simgenics.XPact.XBT.Tests.Tests.Core;

/// <summary>
/// Exercises <see cref="IoHash"/> against published BLAKE3 vectors and against
/// the invariants the rest of XBT relies on: stream / span parity, hex
/// roundtrip, bytewise equality.
/// </summary>
/// <remarks>
/// The expected digests are taken from the official BLAKE3 test vectors at
/// https://github.com/BLAKE3-team/BLAKE3 (the unkeyed mode, 32-byte output).
/// Hardcoding them here gives the test suite an independent oracle the
/// implementation cannot influence.
/// </remarks>
public sealed class IoHashTests
{
    /// <summary>BLAKE3("") -- the empty-input digest from the official vectors.</summary>
    private const string EmptyBlake3Hex =
        "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262";

    /// <summary>
    /// BLAKE3("hello world") -- 11-byte ASCII input. Verified independently
    /// against the b3sum reference CLI.
    /// </summary>
    private const string HelloWorldBlake3Hex =
        "d74981efa70a0c880b8d8c1985d075dbcbf679b99a5f9914e5aaf96b831a9e24";

    [Fact]
    public void Compute_OfEmptyInput_MatchesKnownVector()
    {
        IoHash hash = IoHash.Compute(ReadOnlySpan<byte>.Empty);
        Assert.Equal(EmptyBlake3Hex, hash.ToString());
    }

    [Fact]
    public void Compute_OfHelloWorld_MatchesKnownVector()
    {
        ReadOnlySpan<byte> input = Encoding.ASCII.GetBytes("hello world");
        IoHash hash = IoHash.Compute(input);
        Assert.Equal(HelloWorldBlake3Hex, hash.ToString());
    }

    [Fact]
    public void Compute_SpanAndStream_AgreeForSameBytes()
    {
        // Synthetic bytes that span multiple internal blocks so the streaming
        // path actually runs its loop more than once. 80 KiB > one chunk.
        byte[] data = new byte[80 * 1024];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) & 0xFF);
        }

        IoHash spanHash = IoHash.Compute(data);

        using MemoryStream stream = new(data, writable: false);
        IoHash streamHash = IoHash.Compute(stream);

        Assert.Equal(spanHash, streamHash);
        Assert.Equal(spanHash.ToString(), streamHash.ToString());
    }

    [Fact]
    public void Equality_IsBytewise_NotReferential()
    {
        ReadOnlySpan<byte> input = "xpact"u8;
        IoHash a = IoHash.Compute(input);
        IoHash b = IoHash.Compute(input);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesDifferentDigests()
    {
        IoHash a = IoHash.Compute("alpha"u8);
        IoHash b = IoHash.Compute("beta"u8);

        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.False(a == b);
    }

    [Fact]
    public void ToString_Returns64LowercaseHexCharacters()
    {
        IoHash hash = IoHash.Compute("test"u8);
        string hex = hash.ToString();

        Assert.Equal(64, hex.Length);
        foreach (char c in hex)
        {
            // Every character must be a hex digit AND lowercase.
            bool isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            Assert.True(isLowerHex, $"Found non-lowercase-hex character '{c}' in '{hex}'.");
        }
    }

    [Fact]
    public void ParseAndToString_AreRoundTripStable()
    {
        IoHash original = IoHash.Compute("rumboll"u8);
        string hex      = original.ToString();
        IoHash parsed   = IoHash.Parse(hex);

        Assert.Equal(original, parsed);
        Assert.Equal(hex, parsed.ToString());
    }

    [Fact]
    public void CopyTo_RoundsTripsViaConstructor()
    {
        IoHash original = IoHash.Compute("dependent"u8);
        Span<byte> bytes = stackalloc byte[IoHash.Length];
        original.CopyTo(bytes);
        IoHash rebuilt = new(bytes);

        Assert.Equal(original, rebuilt);
    }
}
