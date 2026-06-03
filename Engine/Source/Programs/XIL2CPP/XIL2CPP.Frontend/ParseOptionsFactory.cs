// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Simgenics.XPact.XIL2CPP.Frontend;

/// <summary>
/// Single source of truth for the deterministic
/// <see cref="CSharpParseOptions"/> XIL2CPP Pass 1 uses for every C# source
/// file per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 ("Pass 1 --
/// Roslyn Parse + Bind"). Mirrors the XHT parser's LangVersion-pin
/// convention
/// (<c>/Engine/Source/Programs/XHT/XHT.Parser/CSharp/CSharpMarkerWalker.cs</c>)
/// and the XBT <c>BuildCsCompiler</c>'s
/// <c>.WithFeatures(Array.Empty&lt;...&gt;())</c> ambient-flag-kill
/// discipline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a single factory.</b> The parse options participate in the
/// XIL2CPPAction cache key (Section 3.4) and in cross-machine
/// reproducibility (Section 9.9, gate X-IL2CPP-CSPATH-DET). Two call sites
/// constructing options independently risk drifting on a flag (language
/// version, documentation mode, an ambient feature toggle) and silently
/// breaking byte-identical output. Funnelling every parse through this
/// factory makes the options a single audited surface.
/// </para>
/// <para>
/// <b>The locked settings.</b>
/// <list type="bullet">
///   <item><description>
///     <see cref="LanguageVersion.CSharp12"/> -- the MVP language version
///     (Locked Commitment 2, Section 2.2). Pinned explicitly so a future
///     SDK default rolling to C# 13+ cannot change the parse surface.
///   </description></item>
///   <item><description>
///     <see cref="DocumentationMode.Parse"/> -- doc comments are parsed
///     (not <c>Diagnose</c>, which would raise warnings the transpiler does
///     not own, and not <c>None</c>, which would drop the trivia later
///     passes may consult for tooltip extraction). Matches the XHT parser.
///   </description></item>
///   <item><description>
///     <see cref="SourceCodeKind.Regular"/> -- not script. Matches XHT / XBT.
///   </description></item>
///   <item><description>
///     <c>.WithFeatures([])</c> -- clears any ambient
///     <c>&lt;Features&gt;</c> MSBuild property or environment-derived
///     experimental feature flags so the parse is identical regardless of
///     the host build configuration. Matches <c>BuildCsCompiler</c>.
///   </description></item>
///   <item><description>
///     Preprocessor symbols -- supplied explicitly from the manifest's
///     defines / conditional-symbols when present, so <c>#if</c> directives
///     resolve identically across machines (no reliance on an ambient
///     <c>DefineConstants</c>).
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public static class ParseOptionsFactory
{
    /// <summary>The single MVP-supported C# language version (Section 2.2).</summary>
    public const LanguageVersion PinnedLanguageVersion = LanguageVersion.CSharp12;

    private static readonly IReadOnlyList<KeyValuePair<string, string>> s_emptyFeatures =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>
    /// Build the deterministic parse options with no preprocessor symbols.
    /// Used for the common case (a module that declares no defines /
    /// conditional symbols).
    /// </summary>
    /// <returns>The pinned, ambient-flag-free parse options.</returns>
    public static CSharpParseOptions Create() => Create(Array.Empty<string>());

    /// <summary>
    /// Build the deterministic parse options with the given preprocessor
    /// symbols. The symbols come from the manifest (a module's
    /// <c>PublicDefines</c> plus any forward-compat
    /// <c>ConditionalSymbols</c>); they are de-duplicated and ordinal-sorted
    /// so the resulting options are identical regardless of the manifest's
    /// list order, keeping the parse a stable cache-key input.
    /// </summary>
    /// <param name="preprocessorSymbols">
    /// Preprocessor symbols (<c>#if</c> conditionals). Must not be null;
    /// individual entries that are null / empty / whitespace are skipped.
    /// </param>
    /// <returns>The pinned, ambient-flag-free parse options.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="preprocessorSymbols"/> is null.</exception>
    public static CSharpParseOptions Create(IEnumerable<string> preprocessorSymbols)
    {
        ArgumentNullException.ThrowIfNull(preprocessorSymbols);

        // De-duplicate (ordinal) + sort so the symbol set is canonical
        // regardless of manifest ordering. Roslyn treats the preprocessor
        // symbol set as an unordered collection semantically, but pinning a
        // canonical order keeps the constructed options object stable for
        // any caller that hashes its string form.
        SortedSet<string> canonical = new(StringComparer.Ordinal);
        foreach (string symbol in preprocessorSymbols)
        {
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                canonical.Add(symbol);
            }
        }

        CSharpParseOptions options = new CSharpParseOptions(
                languageVersion: PinnedLanguageVersion,
                documentationMode: DocumentationMode.Parse,
                kind: SourceCodeKind.Regular)
            .WithFeatures(s_emptyFeatures);

        if (canonical.Count > 0)
        {
            options = options.WithPreprocessorSymbols(canonical);
        }

        return options;
    }
}
