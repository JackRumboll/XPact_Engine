// Copyright Simgenics. All Rights Reserved.

using System;
using System.Text;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;
using Xunit;

namespace Simgenics.XPact.XIL2CPP.Tests.Tests.Emit;

/// <summary>
/// Tests for <see cref="StringLiteralTable"/> (WU-E3): content-addressed
/// interning (same value &#8594; same <c>_String_&lt;hash16&gt;</c> symbol),
/// the deterministic ordinal-sorted <c>inline constinit const</c> emit shape,
/// and the UTF-8 byte-length argument.
/// </summary>
public sealed class StringLiteralTableTests
{
    [Fact]
    public void Intern_SameContent_ReturnsSameSymbol()
    {
        StringLiteralTable table = new();

        string a = table.Intern("hello");
        string b = table.Intern("hello");

        Assert.Equal(a, b);
        Assert.Equal(1, table.Count);
        Assert.StartsWith("_String_", a);
    }

    [Fact]
    public void Intern_DistinctContent_ReturnsDistinctSymbols()
    {
        StringLiteralTable table = new();

        string a = table.Intern("hello");
        string b = table.Intern("world");

        Assert.NotEqual(a, b);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void Intern_SymbolNameIsFirst16HexOfSha256()
    {
        StringLiteralTable table = new();
        string symbol = table.Intern("hello");

        string expectedHash16 = ExpectedHash16("hello");
        Assert.Equal("_String_" + expectedHash16, symbol);
        Assert.Equal(16, expectedHash16.Length);
    }

    [Fact]
    public void SymbolFor_MatchesIntern()
    {
        StringLiteralTable table = new();
        Assert.Equal(table.Intern("payload"), StringLiteralTable.SymbolFor("payload"));
    }

    [Fact]
    public void EmitTable_EmptyTable_EmitsNothing()
    {
        StringLiteralTable table = new();
        CppWriter writer = new();
        table.EmitTable(writer);
        Assert.Equal(string.Empty, writer.Build());
    }

    [Fact]
    public void EmitTable_InlineConstinitFstringShape()
    {
        StringLiteralTable table = new();
        table.Intern("hello");

        CppWriter writer = new();
        table.EmitTable(writer);

        string hash16 = ExpectedHash16("hello");
        int utf8Len = Encoding.UTF8.GetByteCount("hello");
        string expected =
            "inline constinit const ::XCore::Container::FString _String_" + hash16
            + "(\"hello\", " + utf8Len + ");\n";

        Assert.Equal(expected, writer.Build());
    }

    [Fact]
    public void EmitTable_Utf8ByteLength_CountsBytesNotChars()
    {
        // A multi-byte UTF-8 character: "e" + combining/extended chars.
        const string value = "café"; // 'é' is 2 UTF-8 bytes -> 5 bytes total.
        StringLiteralTable table = new();
        table.Intern(value);

        CppWriter writer = new();
        table.EmitTable(writer);

        Assert.Contains(", 5);", writer.Build());
    }

    [Fact]
    public void EmitTable_SortedByHashOrdinal_Deterministic()
    {
        // Insert in two different orders; the emitted table must be identical
        // because EmitTable sorts by the hash symbol suffix (ordinal).
        StringLiteralTable forward = new();
        forward.Intern("alpha");
        forward.Intern("beta");
        forward.Intern("gamma");

        StringLiteralTable reverse = new();
        reverse.Intern("gamma");
        reverse.Intern("beta");
        reverse.Intern("alpha");

        CppWriter a = new();
        CppWriter b = new();
        forward.EmitTable(a);
        reverse.EmitTable(b);

        Assert.Equal(a.Build(), b.Build());

        // Verify the lines really are sorted by the hash16 suffix ordinal.
        string[] lines = a.Build().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < lines.Length; i++)
        {
            Assert.True(string.CompareOrdinal(lines[i - 1], lines[i]) <= 0);
        }
    }

    [Fact]
    public void EmitTable_EncodesSpecialCharacters()
    {
        StringLiteralTable table = new();
        table.Intern("a\"b\\c\nd");

        CppWriter writer = new();
        table.EmitTable(writer);

        // EncodeCStringLiteral escapes quote, backslash, newline.
        Assert.Contains("\\\"", writer.Build());
        Assert.Contains("\\\\", writer.Build());
        Assert.Contains("\\n", writer.Build());
    }

    private static string ExpectedHash16(string value)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        StringBuilder sb = new(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(digest[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
