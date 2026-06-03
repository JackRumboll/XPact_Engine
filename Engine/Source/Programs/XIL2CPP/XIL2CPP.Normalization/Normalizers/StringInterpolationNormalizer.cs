// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Simgenics.XPact.XIL2CPP.Frontend;

namespace Simgenics.XPact.XIL2CPP.Normalization;

/// <summary>
/// Pass-2 normalizer for C# string interpolation per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.5 (the
/// <c>$"hello {name}"</c> -&gt; <c>String.Format("hello {0}", name)</c>
/// -&gt; XPact FString-builder lowering). It annotates every
/// <see cref="InterpolatedStringExpressionSyntax"/> with a
/// <see cref="StringInterpolationAnnotation"/> recording the ordered parts
/// (literal text vs interpolation hole), the synthesized
/// <c>String.Format</c>-style template (literal text with <c>{0}</c> /
/// <c>{1}</c> ... placeholders), the hole expressions in source order, and
/// any per-hole alignment / format specifiers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Annotate, do not rewrite.</b> Per the Section 3.1 + 3.2 annotation-
/// layer design this normalizer records its lowering decision as additive
/// metadata keyed on the original Roslyn node; it never mutates the Pass-1
/// trees or compilation. Pass 3 / the emitter consumes the annotation to
/// drive the actual <c>XDefaultInterpolatedStringHandler</c> emit described
/// in Section 5.5.
/// </para>
/// <para>
/// <b>Template fidelity.</b> The synthesized <see cref="StringInterpolationAnnotation.Template"/>
/// is a valid composite-format string: literal <c>{</c> / <c>}</c> in the
/// interpolated text are escaped as <c>{{</c> / <c>}}</c>, holes become
/// <c>{index[,alignment][:format]}</c> with the alignment / format copied
/// verbatim from the source, so the emitter can either drive the handler
/// directly off the parts or fall back to a <c>String.Format</c> call off
/// the template.
/// </para>
/// <para>
/// <b>Determinism.</b> Files are visited in <see cref="Pass1Result.ParsedFiles"/>
/// order and nodes in document (span) order via
/// <c>SyntaxNode.DescendantNodes</c>, so two runs over identical
/// input record identical annotations (gates X-IL2CPP-MANGLE-DET /
/// X-IL2CPP-CSPATH-DET, Section 9.9).
/// </para>
/// </remarks>
public sealed class StringInterpolationNormalizer : INormalizer
{
    /// <inheritdoc />
    public string Name => "StringInterpolationNormalizer";

    /// <inheritdoc />
    public void Normalize(Pass1Result pass1, NormalizedUnitBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(pass1);
        ArgumentNullException.ThrowIfNull(builder);

        // Visit files in canonical (ParsedFiles) order, then nodes in
        // document order. DescendantNodes yields nodes in span order, which
        // is the source-declaration order the determinism gates require.
        foreach (ModuleParser.ParsedFile file in pass1.ParsedFiles)
        {
            SyntaxNode root = file.Tree.GetRoot();
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (node is InterpolatedStringExpressionSyntax interpolated)
                {
                    StringInterpolationAnnotation annotation = BuildAnnotation(interpolated);
                    builder.AnnotateNode(interpolated, annotation);
                }
            }
        }
    }

    /// <summary>
    /// Decompose one interpolated-string expression into its ordered parts,
    /// composite-format template, and ordered hole expressions.
    /// </summary>
    private static StringInterpolationAnnotation BuildAnnotation(
        InterpolatedStringExpressionSyntax interpolated)
    {
        // A raw interpolated string opens with $""" / $$""" rather than $".
        // The start token kind distinguishes the two literal forms; the
        // content decomposition is otherwise identical.
        bool isRaw = IsRawStringStart(interpolated.StringStartToken);

        List<StringInterpolationPart> parts = new(interpolated.Contents.Count);
        List<ExpressionSyntax> holes = new();
        StringBuilder template = new();
        int holeIndex = 0;

        foreach (InterpolatedStringContentSyntax content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                {
                    // The decoded literal content -- the characters that
                    // appear verbatim in the final string. For NON-raw
                    // literals Roslyn's ValueText decodes string escapes
                    // (\n, \t, ...) but keeps the interpolation brace-doubling
                    // ({{ / }}) un-collapsed, so collapse it here. For RAW
                    // literals ValueText already carries single braces and no
                    // escape processing, so it is the literal content as-is.
                    string literal = isRaw
                        ? text.TextToken.ValueText
                        : CollapseDoubledBraces(text.TextToken.ValueText);
                    parts.Add(StringInterpolationPart.Literal(literal));

                    // The template is a composite-format string, so re-escape
                    // the literal braces uniformly from the decoded content.
                    AppendEscapedLiteral(template, literal);
                    break;
                }

                case InterpolationSyntax interpolation:
                {
                    string? alignment = ExtractAlignment(interpolation.AlignmentClause);
                    string? format = ExtractFormat(interpolation.FormatClause);

                    parts.Add(StringInterpolationPart.Hole(
                        holeIndex,
                        interpolation.Expression,
                        alignment,
                        format));
                    holes.Add(interpolation.Expression);
                    AppendPlaceholder(template, holeIndex, alignment, format);
                    holeIndex++;
                    break;
                }

                default:
                    // InterpolatedStringContentSyntax is a closed hierarchy
                    // (text or interpolation); no other content kind exists.
                    // Skip defensively rather than throw on well-formed input.
                    break;
            }
        }

        return new StringInterpolationAnnotation(
            parts.AsReadOnly(),
            template.ToString(),
            holes.AsReadOnly(),
            isRaw);
    }

    /// <summary>
    /// True iff the interpolated string opens with a raw-string start token
    /// (<c>$"""</c> / <c>$$"""</c> ...), i.e. a raw-string interpolation.
    /// </summary>
    private static bool IsRawStringStart(SyntaxToken startToken) =>
        startToken.IsKind(SyntaxKind.InterpolatedSingleLineRawStringStartToken)
        || startToken.IsKind(SyntaxKind.InterpolatedMultiLineRawStringStartToken);

    /// <summary>
    /// Extract the alignment-clause value text (the expression after the
    /// <c>,</c>, e.g. <c>-10</c>) or null when the hole has no alignment.
    /// </summary>
    private static string? ExtractAlignment(InterpolationAlignmentClauseSyntax? alignmentClause)
    {
        if (alignmentClause is null)
        {
            return null;
        }

        // The alignment value is an arbitrary constant expression; render it
        // from source trimmed of surrounding trivia so the template carries
        // exactly the written specifier.
        return alignmentClause.Value.ToString().Trim();
    }

    /// <summary>
    /// Extract the format-clause text (the specifier after the <c>:</c>, e.g.
    /// <c>X2</c> / <c>yyyy-MM-dd</c>) or null when the hole has no format.
    /// </summary>
    private static string? ExtractFormat(InterpolationFormatClauseSyntax? formatClause)
    {
        if (formatClause is null)
        {
            return null;
        }

        // FormatStringToken.ValueText is the decoded format specifier
        // content (without the leading colon).
        return formatClause.FormatStringToken.ValueText;
    }

    /// <summary>
    /// Collapse the interpolation brace-doubling (<c>{{</c> -&gt; <c>{</c>,
    /// <c>}}</c> -&gt; <c>}</c>) Roslyn leaves in a non-raw interpolated
    /// string's text <c>ValueText</c>, yielding the literal characters that
    /// appear in the final string. A well-formed interpolated string only
    /// contains braces in doubled pairs in its text runs (a lone brace would
    /// open / close an interpolation), so a paired collapse is exact.
    /// </summary>
    private static string CollapseDoubledBraces(string valueText)
    {
        // Fast path: no braces means nothing to collapse.
        if (valueText.IndexOf('{') < 0 && valueText.IndexOf('}') < 0)
        {
            return valueText;
        }

        StringBuilder sb = new(valueText.Length);
        int i = 0;
        while (i < valueText.Length)
        {
            char c = valueText[i];
            if ((c == '{' || c == '}') && i + 1 < valueText.Length && valueText[i + 1] == c)
            {
                // Doubled brace -> single brace; consume both source chars.
                sb.Append(c);
                i += 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Append literal text to the composite-format template, escaping the
    /// format metacharacters <c>{</c> and <c>}</c> as <c>{{</c> / <c>}}</c>.
    /// </summary>
    private static void AppendEscapedLiteral(StringBuilder template, string literal)
    {
        foreach (char c in literal)
        {
            switch (c)
            {
                case '{':
                    template.Append("{{");
                    break;
                case '}':
                    template.Append("}}");
                    break;
                default:
                    template.Append(c);
                    break;
            }
        }
    }

    /// <summary>
    /// Append a composite-format placeholder
    /// <c>{index[,alignment][:format]}</c> for one hole.
    /// </summary>
    private static void AppendPlaceholder(
        StringBuilder template,
        int index,
        string? alignment,
        string? format)
    {
        template.Append('{');
        template.Append(index.ToString(CultureInfo.InvariantCulture));
        if (alignment is not null)
        {
            template.Append(',');
            template.Append(alignment);
        }
        if (format is not null)
        {
            template.Append(':');
            template.Append(format);
        }
        template.Append('}');
    }
}

/// <summary>
/// One ordered segment of a lowered interpolated string: either a literal
/// text run or an interpolation hole. Recorded on the
/// <see cref="StringInterpolationAnnotation"/> in source order so the
/// emitter can replay the <c>AppendLiteral</c> / <c>AppendFormatted</c>
/// sequence of the Section 5.5 <c>XDefaultInterpolatedStringHandler</c>
/// lowering.
/// </summary>
public sealed class StringInterpolationPart
{
    private StringInterpolationPart(
        bool isLiteral,
        string? text,
        int holeIndex,
        ExpressionSyntax? expression,
        string? alignment,
        string? format)
    {
        IsLiteral = isLiteral;
        Text = text;
        HoleIndex = holeIndex;
        Expression = expression;
        Alignment = alignment;
        Format = format;
    }

    /// <summary>
    /// Create a literal-text part carrying the decoded literal content (the
    /// characters that appear verbatim in the final string).
    /// </summary>
    /// <param name="text">The decoded literal text. Must not be null.</param>
    /// <returns>A literal part.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="text"/> is null.</exception>
    public static StringInterpolationPart Literal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new StringInterpolationPart(
            isLiteral: true,
            text: text,
            holeIndex: -1,
            expression: null,
            alignment: null,
            format: null);
    }

    /// <summary>
    /// Create an interpolation-hole part carrying the placeholder index, the
    /// hole expression, and any alignment / format specifiers.
    /// </summary>
    /// <param name="holeIndex">The zero-based composite-format placeholder index. Must be &gt;= 0.</param>
    /// <param name="expression">The hole expression. Must not be null.</param>
    /// <param name="alignment">The alignment specifier text, or null when absent.</param>
    /// <param name="format">The format specifier text, or null when absent.</param>
    /// <returns>A hole part.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="expression"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="holeIndex"/> is negative.</exception>
    public static StringInterpolationPart Hole(
        int holeIndex,
        ExpressionSyntax expression,
        string? alignment,
        string? format)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentOutOfRangeException.ThrowIfNegative(holeIndex);
        return new StringInterpolationPart(
            isLiteral: false,
            text: null,
            holeIndex: holeIndex,
            expression: expression,
            alignment: alignment,
            format: format);
    }

    /// <summary>True iff this part is a literal-text run; false iff it is an interpolation hole.</summary>
    public bool IsLiteral { get; }

    /// <summary>
    /// The decoded literal text for a literal part (<see cref="IsLiteral"/>
    /// true); null for a hole part.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// The zero-based composite-format placeholder index for a hole part
    /// (<see cref="IsLiteral"/> false); <c>-1</c> for a literal part.
    /// </summary>
    public int HoleIndex { get; }

    /// <summary>
    /// The hole expression for a hole part (<see cref="IsLiteral"/> false);
    /// null for a literal part.
    /// </summary>
    public ExpressionSyntax? Expression { get; }

    /// <summary>
    /// The alignment specifier text for a hole part (the expression after the
    /// <c>,</c>, e.g. <c>-10</c>), or null when the hole has no alignment / is
    /// a literal part.
    /// </summary>
    public string? Alignment { get; }

    /// <summary>
    /// The format specifier text for a hole part (the content after the
    /// <c>:</c>, e.g. <c>X2</c>), or null when the hole has no format / is a
    /// literal part.
    /// </summary>
    public string? Format { get; }
}

/// <summary>
/// The Pass-2 lowering decision for one
/// <see cref="InterpolatedStringExpressionSyntax"/> per
/// <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5.5: the ordered parts, the
/// synthesized <c>String.Format</c>-style template, the ordered hole
/// expressions, and whether the source literal was a raw-string
/// interpolation.
/// </summary>
public sealed class StringInterpolationAnnotation : LoweredAnnotation
{
    /// <summary>
    /// Construct the annotation.
    /// </summary>
    /// <param name="parts">The ordered literal / hole parts. Must not be null.</param>
    /// <param name="template">The composite-format template with <c>{0}</c> / <c>{1}</c> ... placeholders. Must not be null.</param>
    /// <param name="holes">The hole expressions in source order. Must not be null.</param>
    /// <param name="isRaw">True iff the source literal was a raw-string interpolation (<c>$"""</c> ...).</param>
    /// <exception cref="ArgumentNullException">If <paramref name="parts"/>, <paramref name="template"/>, or <paramref name="holes"/> is null.</exception>
    public StringInterpolationAnnotation(
        IReadOnlyList<StringInterpolationPart> parts,
        string template,
        IReadOnlyList<ExpressionSyntax> holes,
        bool isRaw)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(holes);

        Parts = parts;
        Template = template;
        Holes = holes;
        IsRaw = isRaw;
    }

    /// <inheritdoc />
    public override string Kind => "string-interpolation";

    /// <summary>
    /// The ordered parts of the interpolated string (literal text runs and
    /// interpolation holes interleaved in source order). This is the
    /// <c>AppendLiteral</c> / <c>AppendFormatted</c> replay sequence for the
    /// Section 5.5 handler lowering.
    /// </summary>
    public IReadOnlyList<StringInterpolationPart> Parts { get; }

    /// <summary>
    /// The synthesized composite-format template: literal text (with literal
    /// braces escaped as <c>{{</c> / <c>}}</c>) interleaved with
    /// <c>{index[,alignment][:format]}</c> placeholders. A valid
    /// <c>String.Format</c> argument so the emitter can fall back to a
    /// format call off this template.
    /// </summary>
    public string Template { get; }

    /// <summary>
    /// The hole expressions in source order (the composite-format arguments,
    /// index 0 = first hole). Length equals the number of hole parts.
    /// </summary>
    public IReadOnlyList<ExpressionSyntax> Holes { get; }

    /// <summary>
    /// True iff the source literal was a raw-string interpolation
    /// (<c>$"""</c> / <c>$$"""</c> ...). Recorded so the emitter can apply
    /// the raw-string interning rule from Section 5.5.
    /// </summary>
    public bool IsRaw { get; }
}
