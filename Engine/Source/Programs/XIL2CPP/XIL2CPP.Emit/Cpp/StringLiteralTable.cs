// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// XIL2CPP Pass-6 string-literal interning table (WU-E3) per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.5 (string lowering) +
/// Section 3.2 (Pass 6). It accumulates the unique string-literal values a
/// module references and emits each one exactly once as an
/// <c>inline constinit const ::XCore::Container::FString</c> definition named
/// by a content hash, so multiple translation units that intern the same
/// literal fold to one definition at link time (the <c>inline</c> linkage) and
/// two compilations of the same module produce a byte-identical table (the
/// content-hash name + ordinal sort).
/// </summary>
/// <remarks>
/// <para>
/// <b>The symbol name.</b> Each value is named <c>_String_&lt;hash16&gt;</c>
/// where <c>hash16</c> is the first 16 hexadecimal characters (lowercase) of
/// the SHA-256 of the value's UTF-8 bytes. Content-addressing makes the symbol
/// a pure function of the literal: the same value always interns to the same
/// symbol (so a later wave's body-lowering rules can name the symbol from the
/// literal alone), and distinct values get distinct symbols with overwhelming
/// probability.
/// </para>
/// <para>
/// <b>The definition shape.</b>
/// <c>inline constinit const ::XCore::Container::FString _String_&lt;hash16&gt;(&lt;encoded&gt;, &lt;utf8ByteLen&gt;);</c>
/// where <c>&lt;encoded&gt;</c> is the value rendered as a C++ string literal
/// via <see cref="CppWriter.EncodeCStringLiteral"/> (byte-identical to XHT's
/// encoder) and <c>&lt;utf8ByteLen&gt;</c> is the UTF-8 byte length. The
/// <c>inline</c> keyword makes the definition mergeable across TUs (ODR-safe
/// multi-TU folding); <c>constinit</c> guarantees constant initialization with
/// no static-init-order dependence.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Interning de-dups by ordinal
/// string equality; <see cref="EmitTable"/> sorts by the hash symbol ordinal
/// before emitting; SHA-256 + UTF-8 encoding carry no ambient state. Two
/// instances fed the same set of values (in any order) emit a byte-identical
/// table.
/// </para>
/// </remarks>
public sealed class StringLiteralTable
{
    /// <summary>The interned-literal symbol-name prefix.</summary>
    public const string SymbolPrefix = "_String_";

    /// <summary>The number of leading SHA-256 hex characters that name a literal.</summary>
    public const int HashHexLength = 16;

    /// <summary>The engine container string type the interned literals are typed as.</summary>
    public const string FStringType = "::XCore::Container::FString";

    // value -> hash16 symbol suffix. De-dups by ordinal string equality so the
    // same literal interns once. Insertion order is irrelevant; EmitTable sorts.
    private readonly Dictionary<string, string> _hashByValue = new(StringComparer.Ordinal);

    /// <summary>The number of distinct interned literals.</summary>
    public int Count => _hashByValue.Count;

    /// <summary>
    /// Intern <paramref name="value"/> and return its
    /// <c>_String_&lt;hash16&gt;</c> symbol name. Interning the same value
    /// again returns the same symbol and does not grow the table.
    /// </summary>
    /// <param name="value">The literal string value to intern. Must not be null.</param>
    /// <returns>The interned literal's C++ symbol name.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="value"/> is null.</exception>
    public string Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_hashByValue.TryGetValue(value, out string? existing))
        {
            return SymbolPrefix + existing;
        }

        string hash16 = ComputeHash16(value);
        _hashByValue[value] = hash16;
        return SymbolPrefix + hash16;
    }

    /// <summary>
    /// Compute the <c>_String_&lt;hash16&gt;</c> symbol name for
    /// <paramref name="value"/> WITHOUT interning it. A pure function of the
    /// value, so a caller that only needs the name (not the definition) gets a
    /// name byte-identical to the one <see cref="Intern"/> would assign.
    /// </summary>
    /// <param name="value">The literal string value. Must not be null.</param>
    /// <returns>The literal's C++ symbol name.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="value"/> is null.</exception>
    public static string SymbolFor(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SymbolPrefix + ComputeHash16(value);
    }

    /// <summary>
    /// Emit the interned-literal definitions into <paramref name="writer"/>,
    /// one indented line per literal, sorted by the <c>hash16</c> symbol suffix
    /// (ordinal). An empty table emits nothing.
    /// </summary>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> is null.</exception>
    public void EmitTable(CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Sort by the hash symbol suffix (ordinal) for a deterministic order
        // independent of interning order.
        List<KeyValuePair<string, string>> rows = new(_hashByValue);
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));

        foreach (KeyValuePair<string, string> row in rows)
        {
            string value = row.Key;
            string hash16 = row.Value;
            int utf8ByteLen = Encoding.UTF8.GetByteCount(value);

            // inline constinit const ::XCore::Container::FString
            //     _String_<hash16>(<encoded>, <utf8ByteLen>);
            writer.AppendLine(
                "inline constinit const " + FStringType + " " + SymbolPrefix + hash16
                + "(" + CppWriter.EncodeCStringLiteral(value) + ", "
                + utf8ByteLen.ToString(CultureInfo.InvariantCulture) + ");");
        }
    }

    /// <summary>
    /// Compute the first <see cref="HashHexLength"/> lowercase hex characters of
    /// the SHA-256 of <paramref name="value"/>'s UTF-8 bytes.
    /// </summary>
    private static string ComputeHash16(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        byte[] digest = SHA256.HashData(utf8);

        // 16 hex chars == 8 bytes. Render lowercase, invariant.
        StringBuilder sb = new(HashHexLength);
        int bytesNeeded = HashHexLength / 2;
        for (int i = 0; i < bytesNeeded; i++)
        {
            sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
