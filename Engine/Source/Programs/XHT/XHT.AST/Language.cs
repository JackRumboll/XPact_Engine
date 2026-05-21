// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// Source language tag carried by every AST node per
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.1.
/// </summary>
/// <remarks>
/// <para>
/// XHT's AST is a <em>unified</em> hierarchy across both languages, not
/// parallel hierarchies. Emit branches on this tag at the few sites that
/// matter (body-macro suffixes are C++-specific; IL2CPP-side metadata
/// mangling is C#-aware); everywhere else the language is transparent.
/// This deliberately diverges from UHT's Verse-bolt-on flag-explosion
/// approach (Section 25.3 footgun #3).
/// </para>
/// </remarks>
public enum Language
{
    /// <summary>The node was parsed from a C++ header (<c>.h</c> / <c>.hpp</c>).</summary>
    Cpp,

    /// <summary>The node was parsed from a C# source file (<c>.cs</c>).</summary>
    CSharp,
}
