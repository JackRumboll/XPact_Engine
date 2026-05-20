// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Simgenics.XPact.XBT.Core;

namespace Simgenics.XPact.XBT.Toolchain;

/// <summary>
/// Rewrites a per-module PCH header's <c>#include</c> directives in
/// alphabetical-by-logical-path order per Toolchain Contract Rev 13
/// Section 1.5. The rule locks PCH compile determinism in lockstep
/// with the rest of the reproducibility envelope: two builds of the
/// same source produce byte-identical <c>.pch</c>/<c>.pchi</c> files
/// even when developers hand-order their includes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> The rewriter only re-orders contiguous blocks of
/// <c>#include</c> directives. Non-include lines (comments, other
/// preprocessor directives, blank lines) bound each block. Inside a
/// block the rewriter sorts by the include-target text (the part
/// between the quotes / angle brackets) using ordinal string compare.
/// </para>
/// <para>
/// <b>Determinism.</b> The sort is ordinal so two runs produce the
/// same byte sequence. The rewriter preserves whether each include
/// used <c>"…"</c> or <c>&lt;…&gt;</c>; only the order changes.
/// </para>
/// <para>
/// <b>Diagnostic.</b> When the rewrite changes the file's content,
/// the caller emits a <see cref="Logger.Info"/> diagnostic naming
/// the PCH header and the number of reorderings. This is informational
/// — the build does not warn or fail.
/// </para>
/// </remarks>
public static class PCHIncludeOrderRewriter
{
    /// <summary>
    /// Outcome of a rewrite pass.
    /// </summary>
    /// <param name="OriginalText">The PCH header's bytes as read.</param>
    /// <param name="RewrittenText">
    /// The PCH header's bytes with every contiguous include block
    /// re-sorted alphabetically. Equal to <see cref="OriginalText"/>
    /// when the file was already in canonical order.
    /// </param>
    /// <param name="BlocksRewritten">
    /// Number of include blocks whose order changed. Zero = no-op;
    /// positive = the rewriter made a change.
    /// </param>
    public readonly record struct RewriteResult(
        string OriginalText,
        string RewrittenText,
        int BlocksRewritten)
    {
        /// <summary>True iff the rewrite actually changed the file's bytes.</summary>
        public bool Changed => BlocksRewritten > 0;
    }

    /// <summary>
    /// Rewrite a PCH header's include blocks in alphabetical order.
    /// Does not touch the file on disk; the caller decides whether
    /// to persist <see cref="RewriteResult.RewrittenText"/>.
    /// </summary>
    public static RewriteResult Rewrite(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Preserve the source's line-ending style. We split on '\n'
        // and inspect each line; the trailing '\r' (if any) is
        // preserved in the line text so reassembly matches the
        // original endings byte-for-byte.
        string[] lines = source.Split('\n');
        StringBuilder sb = new(source.Length);

        int blocksRewritten = 0;

        int i = 0;
        while (i < lines.Length)
        {
            if (!IsIncludeLine(lines[i]))
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1)
                {
                    sb.Append('\n');
                }
                i++;
                continue;
            }

            // Collect the contiguous include block.
            int blockStart = i;
            List<string> block = new();
            while (i < lines.Length && IsIncludeLine(lines[i]))
            {
                block.Add(lines[i]);
                i++;
            }

            // Sort by include-target (the substring between the
            // quote / angle pair). Ordinal compare.
            List<string> sorted = new(block);
            sorted.Sort(static (a, b) =>
                string.CompareOrdinal(GetIncludeTarget(a), GetIncludeTarget(b)));

            bool changed = false;
            for (int k = 0; k < block.Count; k++)
            {
                if (!string.Equals(block[k], sorted[k], StringComparison.Ordinal))
                {
                    changed = true;
                    break;
                }
            }
            if (changed)
            {
                blocksRewritten++;
            }

            for (int k = 0; k < sorted.Count; k++)
            {
                sb.Append(sorted[k]);
                int absoluteIndex = blockStart + k;
                if (absoluteIndex < lines.Length - 1)
                {
                    sb.Append('\n');
                }
            }
        }

        return new RewriteResult(
            OriginalText: source,
            RewrittenText: sb.ToString(),
            BlocksRewritten: blocksRewritten);
    }

    /// <summary>
    /// Read the file at <paramref name="pchHeaderPath"/>, rewrite its
    /// include blocks in alphabetical order, persist the rewritten
    /// bytes back to disk if anything changed, and emit a Logger.Info
    /// diagnostic in that case. Returns true iff the file was rewritten.
    /// </summary>
    /// <remarks>
    /// This is a destructive operation on the source file. Builds are
    /// expected to be allowed to canonicalise the PCH header text in
    /// the working tree — it's how XBT enforces the alphabetical
    /// include-order policy across developer hand-edits. The change is
    /// idempotent: a second invocation is a no-op.
    /// </remarks>
    public static bool RewriteFile(string pchHeaderPath, string? moduleName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pchHeaderPath);

        if (!File.Exists(pchHeaderPath))
        {
            return false;
        }
        string text = File.ReadAllText(pchHeaderPath);
        RewriteResult result = Rewrite(text);
        if (!result.Changed)
        {
            return false;
        }

        File.WriteAllText(pchHeaderPath, result.RewrittenText);
        Logger.Info(
            $"PCH header '{pchHeaderPath}' include order was rewritten alphabetically " +
            $"({result.BlocksRewritten} block(s) reordered) per Toolchain Contract Section 1.5.",
            new DiagnosticContext
            {
                Action = "rewrite-pch-includes",
                Module = moduleName,
                File = pchHeaderPath,
            });
        return true;
    }

    private static bool IsIncludeLine(string line)
    {
        string trimmed = line.TrimStart();
        // Strip a leading '\r' (left over from CRLF split).
        if (trimmed.Length > 0 && trimmed[0] == '\r')
        {
            trimmed = trimmed.Substring(1);
        }
        return trimmed.StartsWith("#include", StringComparison.Ordinal)
            && trimmed.Length > "#include".Length
            && (trimmed["#include".Length] == ' ' || trimmed["#include".Length] == '\t'
                || trimmed["#include".Length] == '"' || trimmed["#include".Length] == '<');
    }

    private static string GetIncludeTarget(string line)
    {
        int quoteStart = line.IndexOf('"');
        if (quoteStart >= 0)
        {
            int quoteEnd = line.IndexOf('"', quoteStart + 1);
            if (quoteEnd > quoteStart)
            {
                return line.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
            }
        }
        int angleStart = line.IndexOf('<');
        if (angleStart >= 0)
        {
            int angleEnd = line.IndexOf('>', angleStart + 1);
            if (angleEnd > angleStart)
            {
                return line.Substring(angleStart + 1, angleEnd - angleStart - 1);
            }
        }
        return line;
    }
}
