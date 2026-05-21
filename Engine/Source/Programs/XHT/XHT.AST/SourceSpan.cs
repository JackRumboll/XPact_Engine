// Copyright Simgenics. All Rights Reserved.

namespace Simgenics.XPact.XHT.AST;

/// <summary>
/// Column-precise source location for an AST node per
/// <c>/Documents/XHT.html</c> Rev 8 Section 4.1 + Section 12.1.
/// </summary>
/// <remarks>
/// <para>
/// Every reflected AST node carries a <see cref="SourceSpan"/> so XHT's
/// MSBuild-format diagnostics (Section 12.1) can render
/// <c>&lt;file&gt;(&lt;line&gt;,&lt;column&gt;): &lt;severity&gt; XHT&lt;NNN&gt;: ...</c>
/// strings IDEs underline as a precise red squiggle, and the streaming
/// JSON channel (Section 12.4) can carry the matching range. This is one
/// of XHT's deliberate divergences from UHT (Section 25.3 footgun #1
/// preempt): UHT carries only the start line.
/// </para>
/// <para>
/// <see cref="Line"/> and <see cref="Column"/> are <em>1-based</em>
/// matching the MSBuild diagnostic convention. <see cref="Length"/> is
/// the count of characters in the span (used for IDE highlight ranges).
/// </para>
/// </remarks>
/// <param name="SourceFilePath">
/// Absolute path of the source file. Empty string when the span is
/// <see cref="Synthetic"/> -- generated nodes the parser injects (e.g.,
/// dispatcher EndOfType tokens) carry no file anchor.
/// </param>
/// <param name="Line">1-based source line number.</param>
/// <param name="Column">1-based source column number (UTF-16 code-unit offset).</param>
/// <param name="Length">Length of the span in characters; zero for point spans.</param>
public sealed record SourceSpan(
    string SourceFilePath,
    int Line,
    int Column,
    int Length)
{
    /// <summary>
    /// Sentinel value for AST nodes the resolver / emitter synthesises
    /// after the parse phase (e.g., generated meta entries, pairing
    /// companion nodes). The path is empty and the location is (0, 0, 0)
    /// so diagnostic rendering falls back to the leading-severity
    /// MSBuild form when a synthetic node is the source of an error.
    /// </summary>
    public static SourceSpan Synthetic { get; } = new(string.Empty, 0, 0, 0);
}
