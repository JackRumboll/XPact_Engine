// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Core;
using Simgenics.XPact.XIL2CPP.Frontend;
using Simgenics.XPact.XIL2CPP.Normalization;

namespace Simgenics.XPact.XIL2CPP.Analysis;

/// <summary>
/// The loop construct a <see cref="LongLoopSite"/> was recorded for. The four
/// C# loop forms each lower to a C++ loop whose back-edge carries (or omits)
/// the Phase 6.g <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> per Section 6.5.
/// </summary>
public enum LoopKind
{
    /// <summary>A C# <c>for</c> loop (<see cref="ForStatementSyntax"/>).</summary>
    For,

    /// <summary>A C# <c>while</c> loop (<see cref="WhileStatementSyntax"/>).</summary>
    While,

    /// <summary>A C# <c>do</c> / <c>while</c> loop (<see cref="DoStatementSyntax"/>).</summary>
    DoWhile,

    /// <summary>A C# <c>foreach</c> loop (<see cref="ForEachStatementSyntax"/> / <see cref="ForEachVariableStatementSyntax"/>).</summary>
    ForEach,
}

/// <summary>
/// One recorded loop site per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 6.5
/// (loop back-edge safe points + long-loop detection). The Pass-6 loop-lowering
/// rules consult these sites (keyed by source span) to decide whether to emit
/// the <c>XPACT_BACKEDGE_SAFEPOINT_CHECK()</c> at the loop back-edge: a
/// <see cref="IsLong"/> loop (or any loop with NO recorded site -- the safe
/// default) emits the check; a provably-short loop omits it.
/// </summary>
/// <param name="Span">The 1-based source span of the loop statement.</param>
/// <param name="Kind">Which C# loop construct the site describes.</param>
/// <param name="IsLong">
/// True iff the loop is NOT provably short -- i.e. it may run enough iterations
/// that the GC must be able to preempt it at its back-edge, so the safe-point
/// check must be emitted. False ONLY when the loop is provably short (a
/// statically-bounded iteration count <see cref="LongLoopAnalyzer.ShortLoopThreshold"/>
/// or fewer), in which case the check is safely omitted. The conservative
/// default is <c>true</c>: a loop whose bound cannot be proven short is treated
/// as long so a needed safe point is never silently skipped.
/// </param>
public sealed record LongLoopSite(
    SourceSpan Span,
    LoopKind Kind,
    bool IsLong);

/// <summary>
/// Pass-3 long-loop detection per <c>/Documents/XIL2CPP.html</c> Rev 4
/// Section 6.5 (loop back-edge safe points). Walks every <c>for</c> /
/// <c>while</c> / <c>do</c> / <c>foreach</c> statement in deterministic
/// document order and records a <see cref="LongLoopSite"/> classifying the
/// loop as long (<see cref="LongLoopSite.IsLong"/> true -- the back-edge
/// safe-point check must be emitted) unless it is PROVABLY short (a
/// statically-bounded iteration count of <see cref="ShortLoopThreshold"/> or
/// fewer detectable from the loop condition), in which case the check is
/// safely omitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provably-short detection (the only path to <see cref="LongLoopSite.IsLong"/> false).</b>
/// <list type="bullet">
///   <item><description>
///     <b><c>for</c></b> -- a single relational condition <c>i &lt; N</c> /
///     <c>i &lt;= N</c> (the canonical ascending counted loop) whose bound
///     <c>N</c> is a compile-time-constant non-negative integer with
///     <c>N &lt;= 16</c> (strict <c>&lt;</c>) / <c>N &lt; 16</c> (inclusive
///     <c>&lt;=</c>, so the iteration count stays at or below the threshold).
///     The constant may sit on either side of the operator (the analyzer
///     normalises <c>N &gt; i</c>). The iteration count of such a loop is
///     bounded by <c>N</c> regardless of the (unread) initializer / increment,
///     because the loop cannot continue once the variable passes <c>N</c>.
///     This is a deliberately CONSERVATIVE over-approximation: a loop that
///     would actually run fewer iterations is still classed short, but a loop
///     whose condition does not match this shape (a method-call condition, a
///     compound <c>&amp;&amp;</c>, a non-constant bound, a missing condition --
///     the infinite <c>for (;;)</c>) is classed LONG.
///   </description></item>
///   <item><description>
///     <b><c>while</c></b> -- provably short only when the condition is the
///     compile-time constant <c>false</c> (the loop body never runs: zero
///     iterations). Every other <c>while</c> condition (including a relational
///     <c>i &lt; N</c>, whose loop variable update lives in the unread body,
///     not the condition) is classed long.
///   </description></item>
///   <item><description>
///     <b><c>do</c> / <c>while</c></b> -- provably short only when the condition
///     is the compile-time constant <c>false</c> (the body runs exactly once,
///     then exits). Every other condition is classed long.
///   </description></item>
///   <item><description>
///     <b><c>foreach</c></b> -- never provably short here (the element count is
///     a runtime property of the source collection); always classed long so
///     the container-iteration lowering (Phase 6.f) inherits the safe default.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why over-approximate toward long.</b> A missed safe point on a genuinely
/// long loop is a GC-correctness defect (a determined-long loop the collector
/// cannot preempt); a redundant safe point on a loop that turns out short is a
/// no-op (the macro is a cheap poll). Per the PRIME DIRECTIVE the analyzer
/// therefore NEVER classes a loop short unless it can statically prove the
/// bound, and the loop rules treat a MISSING record as long as well.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Parsed files are visited in
/// their canonical Pass-1 ordinal order and nodes in document (span) order via
/// the Roslyn <c>DescendantNodesAndSelf</c> pre-order walk; constants are read
/// through <see cref="SemanticModel.GetConstantValue(SyntaxNode, System.Threading.CancellationToken)"/>
/// and formatted through <see cref="CultureInfo.InvariantCulture"/>. No ambient
/// state, <c>DateTime</c>, or <c>Random</c>.
/// </para>
/// </remarks>
public sealed class LongLoopAnalyzer : ISemanticAnalyzer
{
    /// <summary>
    /// The inclusive iteration-count threshold a counted loop must be provably
    /// at or below to be classed short (its back-edge safe point omitted). A
    /// loop bounded by this many iterations or fewer is short; anything larger
    /// (or unprovable) is long. Per Section 6.5.
    /// </summary>
    public const int ShortLoopThreshold = 16;

    /// <inheritdoc/>
    public string Name => "LongLoopAnalyzer";

    /// <inheritdoc/>
    public void Analyze(NormalizedUnit unit, Pass3ResultBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(builder);

        Pass1Result pass1 = unit.Pass1;

        foreach (ModuleParser.ParsedFile parsed in pass1.ParsedFiles)
        {
            SemanticModel model = pass1.GetSemanticModel(parsed.Tree);
            SyntaxNode root = parsed.Tree.GetRoot();

            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case ForStatementSyntax forStmt:
                        builder.Add(new LongLoopSite(
                            ToSpan(forStmt),
                            LoopKind.For,
                            IsLong: !IsProvablyShortFor(forStmt, model)));
                        break;

                    case WhileStatementSyntax whileStmt:
                        builder.Add(new LongLoopSite(
                            ToSpan(whileStmt),
                            LoopKind.While,
                            IsLong: !IsConstantFalse(whileStmt.Condition, model)));
                        break;

                    case DoStatementSyntax doStmt:
                        builder.Add(new LongLoopSite(
                            ToSpan(doStmt),
                            LoopKind.DoWhile,
                            IsLong: !IsConstantFalse(doStmt.Condition, model)));
                        break;

                    // Both the element form (foreach (var x in xs)) and the
                    // deconstruction form (foreach (var (a, b) in xs)) share the
                    // CommonForEachStatementSyntax base; neither is provably
                    // short here, so both record a long site.
                    case CommonForEachStatementSyntax forEach:
                        builder.Add(new LongLoopSite(
                            ToSpan(forEach),
                            LoopKind.ForEach,
                            IsLong: true));
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Return true iff <paramref name="forStmt"/> is a provably-short counted
    /// loop: a single relational condition <c>i &lt; N</c> / <c>i &lt;= N</c>
    /// whose constant bound <c>N</c> keeps the iteration count at or below
    /// <see cref="ShortLoopThreshold"/>. A missing condition (the infinite
    /// <c>for (;;)</c>), a non-relational / compound condition, or a
    /// non-constant bound is NOT provably short.
    /// </summary>
    private static bool IsProvablyShortFor(ForStatementSyntax forStmt, SemanticModel model)
    {
        if (forStmt.Condition is not BinaryExpressionSyntax condition)
        {
            return false;
        }

        SyntaxKind op = condition.Kind();
        if (op is not (SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression))
        {
            return false;
        }

        // Find the constant bound. The variable sits on one side, the bound on
        // the other; normalise so a strict upper bound (variable < N or
        // N > variable) and an inclusive upper bound (variable <= N or
        // N >= variable) are both recognised.
        bool inclusive;
        long? bound;
        if (TryGetConstantLong(condition.Right, model) is long rightBound)
        {
            // variable <op> CONST: an ascending upper-bound shape (< / <=).
            inclusive = op is SyntaxKind.LessThanOrEqualExpression;
            bool isUpperBound = op is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression;
            bound = isUpperBound ? rightBound : null;
        }
        else if (TryGetConstantLong(condition.Left, model) is long leftBound)
        {
            // CONST <op> variable: an upper-bound shape when op is > / >=
            // (N > i / N >= i), i.e. the constant bounds the variable from
            // above.
            inclusive = op is SyntaxKind.GreaterThanOrEqualExpression;
            bool isUpperBound = op is SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;
            bound = isUpperBound ? leftBound : null;
        }
        else
        {
            return false;
        }

        if (bound is not long n)
        {
            return false;
        }

        // A negative / huge bound is not provably short. The maximum iteration
        // count of a 0-based ascending counted loop is N (strict <) or N + 1
        // (inclusive <=); require that count to stay at or below the threshold.
        if (n < 0)
        {
            return false;
        }

        long maxIterations = inclusive ? n + 1 : n;
        return maxIterations <= ShortLoopThreshold;
    }

    /// <summary>
    /// Return true iff <paramref name="condition"/> is the compile-time
    /// constant <c>false</c> (a <c>while (false)</c> / <c>do { } while (false)</c>
    /// guard the loop never re-enters). Used to prove a <c>while</c> / <c>do</c>
    /// loop short.
    /// </summary>
    private static bool IsConstantFalse(ExpressionSyntax condition, SemanticModel model)
    {
        Optional<object?> constant = model.GetConstantValue(condition);
        return constant.HasValue && constant.Value is false;
    }

    /// <summary>
    /// Return the compile-time-constant integer value of
    /// <paramref name="expression"/> as a <see cref="long"/>, or null when it
    /// is not a constant integral value (a variable, a method call, a
    /// non-integral constant, or an out-of-range value).
    /// </summary>
    private static long? TryGetConstantLong(ExpressionSyntax expression, SemanticModel model)
    {
        Optional<object?> constant = model.GetConstantValue(expression);
        if (!constant.HasValue || constant.Value is null)
        {
            return null;
        }

        // Only integral constants bound an iteration count; a char counts as
        // its numeric code point, a bool / string / floating value does not.
        switch (constant.Value)
        {
            case sbyte or byte or short or ushort or int or uint or long:
                try
                {
                    return Convert.ToInt64(constant.Value, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    return null;
                }

            case ulong u:
                return u <= long.MaxValue ? (long)u : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Build a 1-based <see cref="SourceSpan"/> covering <paramref name="node"/>
    /// from its Roslyn line span. Matches the span the loop-lowering rules
    /// recompute from the same node (same tree, same <c>FilePath</c>), so the
    /// rule's span-keyed lookup of this site is exact.
    /// </summary>
    private static SourceSpan ToSpan(SyntaxNode node)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        Microsoft.CodeAnalysis.Text.LinePosition start = lineSpan.StartLinePosition;
        Microsoft.CodeAnalysis.Text.LinePosition end = lineSpan.EndLinePosition;
        return new SourceSpan(
            lineSpan.Path,
            start.Line + 1,
            start.Character + 1,
            end.Line + 1,
            end.Character + 1,
            validate: true);
    }
}
