// Copyright Simgenics. All Rights Reserved.

using System;
using System.Text;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="IoHash"/>. Phase 1b uses SHA-256 as the
/// placeholder algorithm per XHT.html Section 16 STAGE B deferral; the
/// surface (32 bytes, 16-char hex prefix, FromUtf8 / FromString /
/// FromStream factories) stays the same after the Phase 1c BLAKE3 swap.
/// </summary>
public class Blake3HashTests
{
    [Fact]
    public void Hex16_IsExactly16LowercaseHexChars()
    {
        IoHash h = IoHash.FromString("hello world");
        string hex = h.Hex16();
        Assert.Equal(16, hex.Length);
        foreach (char c in hex)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            Assert.True(ok, $"Hex16 produced non-lowercase-hex char '{c}'.");
        }
    }

    [Fact]
    public void FromString_Stable_AcrossCalls()
    {
        IoHash h1 = IoHash.FromString("XValve");
        IoHash h2 = IoHash.FromString("XValve");
        Assert.Equal(h1, h2);
        Assert.Equal(h1.Hex16(), h2.Hex16());
        Assert.Equal(h1.ToString(), h2.ToString());
    }

    [Fact]
    public void FromString_DiffersForDifferentInputs()
    {
        IoHash h1 = IoHash.FromString("XValve");
        IoHash h2 = IoHash.FromString("XActor");
        Assert.NotEqual(h1, h2);
        Assert.NotEqual(h1.Hex16(), h2.Hex16());
    }

    [Fact]
    public void FromUtf8_AgreesWithFromString_OnAsciiText()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes("hello world");
        IoHash h1 = IoHash.FromUtf8(utf8);
        IoHash h2 = IoHash.FromString("hello world");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void FromString_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => IoHash.FromString(null!));
    }

    [Fact]
    public void FullHex_Is64Chars()
    {
        IoHash h = IoHash.FromString("X");
        Assert.Equal(64, h.ToString().Length);
    }

    [Fact]
    public void Hex16_IsPrefixOfFullHex()
    {
        IoHash h = IoHash.FromString("X");
        Assert.Equal(h.ToString().Substring(0, 16), h.Hex16());
    }

    [Fact]
    public void Zero_DefaultValue_HasAllZerosHex()
    {
        IoHash z = IoHash.Zero;
        Assert.Equal("0000000000000000", z.Hex16());
        Assert.Equal(new string('0', 64), z.ToString());
    }

    [Fact]
    public void Constructor_RejectsWrongLengthSpan()
    {
        Assert.Throws<ArgumentException>(() => new IoHash(new byte[16]));
        Assert.Throws<ArgumentException>(() => new IoHash(new byte[64]));
    }

    [Fact]
    public void CopyTo_AndBack_RoundTrips()
    {
        IoHash original = IoHash.FromString("test-value");
        Span<byte> buffer = stackalloc byte[IoHash.Length];
        original.CopyTo(buffer);
        IoHash reconstructed = new(buffer);
        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public void EqualityOperators_Behave()
    {
        IoHash a = IoHash.FromString("a");
        IoHash a2 = IoHash.FromString("a");
        IoHash b = IoHash.FromString("b");
        Assert.True(a == a2);
        Assert.False(a == b);
        Assert.True(a != b);
        Assert.False(a != a2);
    }
}
