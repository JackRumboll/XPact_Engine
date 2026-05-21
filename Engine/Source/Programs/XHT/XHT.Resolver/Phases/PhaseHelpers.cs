// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;
using Simgenics.XPact.XHT.Core;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Helpers shared by resolver phase implementations. Stateless utility
/// methods for emitting diagnostics, matching specifiers, and walking
/// resolver-context maps.
/// </summary>
internal static class PhaseHelpers
{
    /// <summary>
    /// Find a specifier (case-insensitive name match per
    /// <c>/Documents/XHT.html</c> Rev 8 Section 7.2) on a type / function /
    /// property; returns null when the specifier is not present.
    /// </summary>
    /// <param name="specifiers">The specifier list. Must not be null.</param>
    /// <param name="name">The specifier name to look up. Must not be null.</param>
    /// <returns>The first matching specifier, or null when none matches.</returns>
    public static Specifier? FindSpecifier(IReadOnlyList<Specifier> specifiers, string name)
    {
        ArgumentNullException.ThrowIfNull(specifiers);
        ArgumentNullException.ThrowIfNull(name);

        for (int i = 0; i < specifiers.Count; i++)
        {
            Specifier s = specifiers[i];
            if (string.Equals(s.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns true if a specifier with the given name (case-insensitive)
    /// is present anywhere in the list.
    /// </summary>
    /// <param name="specifiers">The specifier list. Must not be null.</param>
    /// <param name="name">The specifier name to check. Must not be null.</param>
    public static bool HasSpecifier(IReadOnlyList<Specifier> specifiers, string name)
    {
        return FindSpecifier(specifiers, name) is not null;
    }

    /// <summary>
    /// Append an error-severity diagnostic anchored to the given span.
    /// </summary>
    public static void Error(
        ResolverContext ctx,
        string code,
        string message,
        SourceSpan span)
    {
        ctx.Diagnostics.Add(new DiagnosticRecord(
            DiagnosticSeverity.Error,
            code,
            message,
            File: NullIfEmpty(span.SourceFilePath),
            Line: NullIfZero(span.Line),
            Column: NullIfZero(span.Column),
            Module: ctx.ModuleName));
    }

    /// <summary>
    /// Append a warning-severity diagnostic anchored to the given span.
    /// </summary>
    public static void Warning(
        ResolverContext ctx,
        string code,
        string message,
        SourceSpan span)
    {
        ctx.Diagnostics.Add(new DiagnosticRecord(
            DiagnosticSeverity.Warning,
            code,
            message,
            File: NullIfEmpty(span.SourceFilePath),
            Line: NullIfZero(span.Line),
            Column: NullIfZero(span.Column),
            Module: ctx.ModuleName));
    }

    /// <summary>
    /// Append an info-severity diagnostic anchored to the given span.
    /// </summary>
    public static void Info(
        ResolverContext ctx,
        string code,
        string message,
        SourceSpan span)
    {
        ctx.Diagnostics.Add(new DiagnosticRecord(
            DiagnosticSeverity.Info,
            code,
            message,
            File: NullIfEmpty(span.SourceFilePath),
            Line: NullIfZero(span.Line),
            Column: NullIfZero(span.Column),
            Module: ctx.ModuleName));
    }

    private static string? NullIfEmpty(string s)
        => string.IsNullOrEmpty(s) ? null : s;

    private static int? NullIfZero(int v)
        => v == 0 ? null : v;

    /// <summary>
    /// Walks the inheritance chain via
    /// <see cref="ResolverContext.ResolvedSupers"/> starting at
    /// <paramref name="start"/> and yielding each ancestor in turn.
    /// Stops when a cycle would be revisited (to avoid infinite loops
    /// when XHT105 RecursiveStructCycle has not yet been emitted) or
    /// when no super is present.
    /// </summary>
    public static IEnumerable<XhtTypeBase> WalkSuperChain(
        ResolverContext ctx,
        XhtTypeBase start)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(start);

        HashSet<XhtTypeBase> seen = new() { start };
        XhtTypeBase current = start;
        while (ctx.ResolvedSupers.TryGetValue(current, out XhtTypeBase? super))
        {
            if (!seen.Add(super))
            {
                yield break;
            }
            yield return super;
            current = super;
        }
    }
}
