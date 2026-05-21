// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Simgenics.XPact.XHT.Tables;

/// <summary>
/// Frozen lookup table of every C++ keyword the XHT tokenizer recognises,
/// per <c>/Documents/XHT.html</c> Rev 8 Section 3.1 + Section 3.4 +
/// Contract Section 1.1 (the XHT marker macros).
/// </summary>
/// <remarks>
/// <para>
/// <b>Case sensitivity.</b> Lookups are exact (case-sensitive) because C++
/// keywords are case-sensitive at the language level. The XHT markers
/// (<c>XCLASS</c>, etc.) are also case-sensitive per Contract Section 1.1's
/// upper-case-only convention. (Specifier <em>values</em> inside
/// <c>XCLASS(...)</c> are case-insensitive per Section 7.2; that case-
/// folding lives in <see cref="SpecifierRegistry"/>, not here.)
/// </para>
/// <para>
/// <b>Frozen backing store.</b> The table is built once at static init via
/// <see cref="FrozenDictionary"/> and never mutated; lookups are constant-
/// time and lock-free. Plugin-side extension of the keyword table is a
/// Phase-2 surface (Section 18.1 <c>[XhtKeyword]</c>) and is not part of
/// Phase 1's locked vocabulary.
/// </para>
/// </remarks>
public static class CppKeywordTable
{
    private static readonly IReadOnlyList<CppKeyword> s_allList = BuildAll();

    private static readonly FrozenDictionary<string, CppKeyword> s_byOrdinal
        = BuildFrozenIndex(s_allList);

    /// <summary>
    /// Snapshot of every registered keyword. The order is declaration order
    /// inside <see cref="BuildAll"/>; callers requiring deterministic
    /// ordering can rely on it.
    /// </summary>
    public static IReadOnlyCollection<CppKeyword> All => s_allList;

    /// <summary>
    /// Look up a keyword by exact spelling. Returns null on miss.
    /// </summary>
    /// <param name="spelling">The candidate keyword text. Must not be null.</param>
    /// <returns>The matched keyword, or null when no entry has this exact spelling.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="spelling"/> is null.</exception>
    public static CppKeyword? Lookup(string spelling)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        return s_byOrdinal.TryGetValue(spelling, out CppKeyword? value) ? value : null;
    }

    /// <summary>
    /// Returns true iff <paramref name="spelling"/> is one of the XHT
    /// reflection markers (<c>XCLASS</c>, <c>XSTRUCT</c>, <c>XENUM</c>,
    /// <c>XINTERFACE</c>, <c>XFUNCTION</c>, <c>XPROPERTY</c>,
    /// <c>XDELEGATE</c>, <c>XPARAM</c>, <c>XMETA</c>,
    /// <c>XGENERATED_BODY</c>) per Contract Section 1.1.
    /// </summary>
    /// <param name="spelling">Candidate keyword text. Must not be null.</param>
    /// <returns>True when <paramref name="spelling"/> is an XHT marker keyword.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="spelling"/> is null.</exception>
    public static bool IsXhtMarker(string spelling)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        return s_byOrdinal.TryGetValue(spelling, out CppKeyword? value)
            && value.Kind == CppKeywordKind.XhtMarker;
    }

    /// <summary>
    /// Span overload of <see cref="IsXhtMarker(string)"/>. Equivalent
    /// behaviour without forcing a <see cref="string"/> allocation at the
    /// call site. Used by the tokenizer's zero-copy slice lookup path
    /// (Section 3.1's <c>StringView</c> token spans).
    /// </summary>
    /// <param name="spelling">Candidate keyword text as a span.</param>
    /// <returns>True when <paramref name="spelling"/> is an XHT marker keyword.</returns>
    public static bool IsXhtMarkerSpan(ReadOnlySpan<char> spelling)
    {
        // FrozenDictionary's lookup-by-span overload is only available with
        // an OrdinalStringComparer adapter; we materialise the span into a
        // string for the case-sensitive lookup. The allocation is benign
        // because marker recognition only fires on the small set of
        // capital-X identifiers; the hot path stays in the tokenizer's own
        // capital-X prefix gate before this method is invoked. The behaviour
        // is observably identical to the string overload.
        string candidate = spelling.ToString();
        return IsXhtMarker(candidate);
    }

    private static IReadOnlyList<CppKeyword> BuildAll()
    {
        List<CppKeyword> list = new(capacity: 80);

        // Type keywords.
        list.Add(new CppKeyword("class", CppKeywordKind.Type));
        list.Add(new CppKeyword("struct", CppKeywordKind.Type));
        list.Add(new CppKeyword("enum", CppKeywordKind.Type));
        list.Add(new CppKeyword("union", CppKeywordKind.Type));

        // Storage classes.
        list.Add(new CppKeyword("static", CppKeywordKind.StorageClass));
        list.Add(new CppKeyword("extern", CppKeywordKind.StorageClass));
        list.Add(new CppKeyword("mutable", CppKeywordKind.StorageClass));
        list.Add(new CppKeyword("thread_local", CppKeywordKind.StorageClass));
        list.Add(new CppKeyword("register", CppKeywordKind.StorageClass));

        // Cv-qualifiers.
        list.Add(new CppKeyword("const", CppKeywordKind.Qualifier));
        list.Add(new CppKeyword("volatile", CppKeywordKind.Qualifier));
        list.Add(new CppKeyword("restrict", CppKeywordKind.Qualifier));

        // Access specifiers.
        list.Add(new CppKeyword("public", CppKeywordKind.AccessModifier));
        list.Add(new CppKeyword("private", CppKeywordKind.AccessModifier));
        list.Add(new CppKeyword("protected", CppKeywordKind.AccessModifier));

        // Control flow.
        list.Add(new CppKeyword("if", CppKeywordKind.Control));
        list.Add(new CppKeyword("else", CppKeywordKind.Control));
        list.Add(new CppKeyword("for", CppKeywordKind.Control));
        list.Add(new CppKeyword("while", CppKeywordKind.Control));
        list.Add(new CppKeyword("do", CppKeywordKind.Control));
        list.Add(new CppKeyword("return", CppKeywordKind.Control));
        list.Add(new CppKeyword("switch", CppKeywordKind.Control));
        list.Add(new CppKeyword("case", CppKeywordKind.Control));
        list.Add(new CppKeyword("default", CppKeywordKind.Control));
        list.Add(new CppKeyword("break", CppKeywordKind.Control));
        list.Add(new CppKeyword("continue", CppKeywordKind.Control));
        list.Add(new CppKeyword("goto", CppKeywordKind.Control));

        // Boolean literals.
        list.Add(new CppKeyword("true", CppKeywordKind.Boolean));
        list.Add(new CppKeyword("false", CppKeywordKind.Boolean));

        // Named casts.
        list.Add(new CppKeyword("static_cast", CppKeywordKind.Cast));
        list.Add(new CppKeyword("reinterpret_cast", CppKeywordKind.Cast));
        list.Add(new CppKeyword("const_cast", CppKeywordKind.Cast));
        list.Add(new CppKeyword("dynamic_cast", CppKeywordKind.Cast));

        // Operator-as-keyword.
        list.Add(new CppKeyword("sizeof", CppKeywordKind.OperatorWord));
        list.Add(new CppKeyword("alignof", CppKeywordKind.OperatorWord));
        list.Add(new CppKeyword("typeid", CppKeywordKind.OperatorWord));
        list.Add(new CppKeyword("noexcept", CppKeywordKind.OperatorWord));
        list.Add(new CppKeyword("decltype", CppKeywordKind.OperatorWord));

        // Other.
        list.Add(new CppKeyword("nullptr", CppKeywordKind.Other));
        list.Add(new CppKeyword("this", CppKeywordKind.Other));
        list.Add(new CppKeyword("namespace", CppKeywordKind.Other));
        list.Add(new CppKeyword("using", CppKeywordKind.Other));
        list.Add(new CppKeyword("template", CppKeywordKind.Other));
        list.Add(new CppKeyword("typedef", CppKeywordKind.Other));
        list.Add(new CppKeyword("typename", CppKeywordKind.Other));
        list.Add(new CppKeyword("friend", CppKeywordKind.Other));
        list.Add(new CppKeyword("virtual", CppKeywordKind.Other));
        list.Add(new CppKeyword("override", CppKeywordKind.Other));
        list.Add(new CppKeyword("final", CppKeywordKind.Other));
        list.Add(new CppKeyword("explicit", CppKeywordKind.Other));
        list.Add(new CppKeyword("inline", CppKeywordKind.Other));
        list.Add(new CppKeyword("constexpr", CppKeywordKind.Other));
        list.Add(new CppKeyword("constinit", CppKeywordKind.Other));
        list.Add(new CppKeyword("consteval", CppKeywordKind.Other));
        list.Add(new CppKeyword("operator", CppKeywordKind.Other));
        list.Add(new CppKeyword("new", CppKeywordKind.Other));
        list.Add(new CppKeyword("delete", CppKeywordKind.Other));
        list.Add(new CppKeyword("auto", CppKeywordKind.Other));
        list.Add(new CppKeyword("throw", CppKeywordKind.Other));
        list.Add(new CppKeyword("try", CppKeywordKind.Other));
        list.Add(new CppKeyword("catch", CppKeywordKind.Other));

        // XHT markers per Contract Section 1.1.
        list.Add(new CppKeyword("XCLASS", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XSTRUCT", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XENUM", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XINTERFACE", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XFUNCTION", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XPROPERTY", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XDELEGATE", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XPARAM", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XMETA", CppKeywordKind.XhtMarker));
        list.Add(new CppKeyword("XGENERATED_BODY", CppKeywordKind.XhtMarker));

        return list;
    }

    private static FrozenDictionary<string, CppKeyword> BuildFrozenIndex(IReadOnlyList<CppKeyword> all)
    {
        Dictionary<string, CppKeyword> index = new(all.Count, StringComparer.Ordinal);
        foreach (CppKeyword kw in all)
        {
            // No collision is expected; the list above is hand-curated. If a
            // duplicate slips in, the index build throws here so the test
            // surface catches the regression at static-init time.
            index.Add(kw.Spelling, kw);
        }
        return index.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
