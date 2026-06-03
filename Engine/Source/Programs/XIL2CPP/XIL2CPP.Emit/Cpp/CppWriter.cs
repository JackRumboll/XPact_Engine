// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// A deterministic, structured C++ code builder for the XIL2CPP emit passes
/// (Pass 6 / Pass 7) per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 3.2 +
/// Section 5.1. It accumulates emitted C++ into an internal
/// <see cref="StringBuilder"/> with explicit <c>\n</c> newlines (never
/// <see cref="Environment.NewLine"/>) and a fixed four-space indent, and
/// exposes block / comment / static-assert / extern-"C" helpers the
/// class / method / property emitters + the body-lowering rules call into.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> The writer carries NO
/// ambient state: no <see cref="DateTime"/>, no <see cref="Guid"/>, no
/// culture-sensitive formatting. Newlines are an explicit literal
/// <c>'\n'</c> so output is byte-identical on Windows and Linux. Two
/// builders driven by the identical sequence of calls produce a
/// byte-identical <see cref="Build"/> string. The numeric helpers format
/// through <see cref="CultureInfo.InvariantCulture"/>.
/// </para>
/// <para>
/// <b>Indentation model.</b> An internal indent depth (0-based) is rendered
/// as <see cref="IndentUnit"/> (four spaces) repeated per level at the start
/// of every line written through <see cref="AppendLine(string)"/> /
/// <see cref="AppendComment"/> / <see cref="AppendStaticAssert"/> /
/// <see cref="AppendExternC"/> / <see cref="BeginBlock"/> /
/// <see cref="EndBlock"/>. <see cref="BeginBlock"/> writes the header line
/// then increments the depth; <see cref="EndBlock"/> decrements the depth
/// then writes the closing brace. The raw <see cref="Append(string)"/> does
/// NOT inject indentation (it is the escape hatch for mid-line fragments);
/// <see cref="Indent"/> / <see cref="Unindent"/> adjust the depth directly.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> A single <see cref="CppWriter"/> instance is
/// owned by exactly one emit thread (one per emitted file). Parallel-safety
/// across files comes from each file owning its own writer, never from
/// sharing one.
/// </para>
/// </remarks>
public sealed class CppWriter
{
    /// <summary>The single indentation unit: four ASCII spaces.</summary>
    public const string IndentUnit = "    ";

    private readonly StringBuilder _sb = new();
    private int _depth;

    /// <summary>
    /// The current indent depth (0-based; number of <see cref="IndentUnit"/>
    /// repetitions prepended to each indented line). Never negative.
    /// </summary>
    public int Depth => _depth;

    /// <summary>
    /// Append a raw fragment with NO indentation and NO trailing newline. The
    /// escape hatch for building a line in pieces; the caller is responsible
    /// for any newline. A null fragment is treated as the empty string.
    /// </summary>
    /// <param name="text">The fragment to append (null treated as empty).</param>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter Append(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _sb.Append(text);
        }
        return this;
    }

    /// <summary>
    /// Append an indented line: the current indentation, then
    /// <paramref name="text"/> (when non-empty), then a single <c>'\n'</c>.
    /// An empty / null <paramref name="text"/> emits a bare newline with NO
    /// indentation (a blank separator line carries no trailing whitespace).
    /// </summary>
    /// <param name="text">The line body (null / empty emits a blank line).</param>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter AppendLine(string? text = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            _sb.Append('\n');
            return this;
        }
        AppendIndent();
        _sb.Append(text);
        _sb.Append('\n');
        return this;
    }

    /// <summary>
    /// Open a brace block: write <paramref name="header"/> (indented) followed
    /// by <c>" {"</c> and a newline, then increase the indent depth by one.
    /// A null / empty header emits a bare <c>"{"</c> on its own (indented)
    /// line. Pair with <see cref="EndBlock(string?)"/>.
    /// </summary>
    /// <param name="header">The text preceding the opening brace (e.g. a class / namespace / function header).</param>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter BeginBlock(string? header = null)
    {
        AppendIndent();
        if (string.IsNullOrEmpty(header))
        {
            _sb.Append('{');
        }
        else
        {
            _sb.Append(header);
            _sb.Append(" {");
        }
        _sb.Append('\n');
        _depth++;
        return this;
    }

    /// <summary>
    /// Close a brace block: decrease the indent depth by one (floored at
    /// zero), then write the closing <c>'}'</c> (indented) followed by
    /// <paramref name="suffix"/> when supplied (e.g. <c>";"</c> for a
    /// class / struct definition, or <c>" // namespace Foo"</c>), then a
    /// newline. Pair with <see cref="BeginBlock(string?)"/>.
    /// </summary>
    /// <param name="suffix">Optional text after the closing brace (e.g. <c>";"</c>); null emits just the brace.</param>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter EndBlock(string? suffix = null)
    {
        Unindent();
        AppendIndent();
        _sb.Append('}');
        if (!string.IsNullOrEmpty(suffix))
        {
            _sb.Append(suffix);
        }
        _sb.Append('\n');
        return this;
    }

    /// <summary>
    /// Increase the indent depth by one level.
    /// </summary>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter Indent()
    {
        _depth++;
        return this;
    }

    /// <summary>
    /// Decrease the indent depth by one level, floored at zero (an unbalanced
    /// <see cref="Unindent"/> never produces a negative depth).
    /// </summary>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter Unindent()
    {
        if (_depth > 0)
        {
            _depth--;
        }
        return this;
    }

    /// <summary>
    /// Append a single-line C++ comment: the current indentation, then
    /// <c>"// "</c>, then <paramref name="text"/>, then a newline. A null
    /// <paramref name="text"/> emits a bare <c>"//"</c> line. The caller must
    /// not pass multi-line text; embedded newlines would break the
    /// indentation invariant.
    /// </summary>
    /// <param name="text">The comment body (single line; null emits a bare marker).</param>
    /// <returns>This writer (for chaining).</returns>
    public CppWriter AppendComment(string? text)
    {
        AppendIndent();
        if (string.IsNullOrEmpty(text))
        {
            _sb.Append("//");
        }
        else
        {
            _sb.Append("// ");
            _sb.Append(text);
        }
        _sb.Append('\n');
        return this;
    }

    /// <summary>
    /// Append a <c>static_assert(cond, "msg")</c> statement on one indented
    /// line. <paramref name="message"/> is emitted as a C++ string literal
    /// (the standard escapes are applied via <see cref="EncodeCStringLiteral"/>).
    /// </summary>
    /// <param name="condition">The constexpr condition expression (verbatim). Must not be null / empty / whitespace.</param>
    /// <param name="message">The diagnostic message (encoded as a C++ string literal). Must not be null.</param>
    /// <returns>This writer (for chaining).</returns>
    /// <exception cref="ArgumentException">If <paramref name="condition"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="message"/> is null.</exception>
    public CppWriter AppendStaticAssert(string condition, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        ArgumentNullException.ThrowIfNull(message);

        AppendIndent();
        _sb.Append("static_assert(");
        _sb.Append(condition);
        _sb.Append(", ");
        _sb.Append(EncodeCStringLiteral(message));
        _sb.Append(");");
        _sb.Append('\n');
        return this;
    }

    /// <summary>
    /// Append an <c>extern "C"</c> free-function declaration on one indented
    /// line: <c>extern "C" &lt;ret&gt; &lt;symbol&gt;(&lt;params&gt;)[ noexcept];</c>.
    /// The transpiled-symbol form per Contract Section 2.3 (free function with
    /// explicit <c>self</c>); the emitters compose
    /// <paramref name="parameterList"/> from the lowered C++ parameter types.
    /// </summary>
    /// <param name="returnType">The C++ return type (e.g. <c>void</c>). Must not be null / empty / whitespace.</param>
    /// <param name="linkerSymbol">The linker-visible mangled symbol (e.g. from <see cref="Mangling.MangledName.LinkerSymbol"/>). Must not be null / empty / whitespace.</param>
    /// <param name="parameterList">The parenthesized parameter list body WITHOUT the surrounding parentheses (e.g. <c>"::Foo* self, float x"</c>); empty for a no-arg function. Must not be null.</param>
    /// <param name="noexcept">When true, appends <c> noexcept</c> before the trailing semicolon (Tier 2 direct functions are noexcept; Tier 1 shims are not).</param>
    /// <returns>This writer (for chaining).</returns>
    /// <exception cref="ArgumentException">If <paramref name="returnType"/> or <paramref name="linkerSymbol"/> is null / empty / whitespace.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="parameterList"/> is null.</exception>
    public CppWriter AppendExternC(
        string returnType,
        string linkerSymbol,
        string parameterList,
        bool noexcept)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnType);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkerSymbol);
        ArgumentNullException.ThrowIfNull(parameterList);

        AppendIndent();
        _sb.Append("extern \"C\" ");
        _sb.Append(returnType);
        _sb.Append(' ');
        _sb.Append(linkerSymbol);
        _sb.Append('(');
        _sb.Append(parameterList);
        _sb.Append(')');
        if (noexcept)
        {
            _sb.Append(" noexcept");
        }
        _sb.Append(';');
        _sb.Append('\n');
        return this;
    }

    /// <summary>
    /// Materialize the accumulated C++ as a string. Calling
    /// <see cref="Build"/> does not reset the writer; a subsequent call
    /// returns the same content (plus anything appended in between). Two
    /// writers driven by an identical call sequence return byte-identical
    /// strings.
    /// </summary>
    /// <returns>The accumulated C++ text (LF newlines; no BOM).</returns>
    public string Build() => _sb.ToString();

    /// <summary>
    /// Encode <paramref name="s"/> as a C++ string literal (surrounding
    /// double quotes + standard escapes), matching the XHT emitter's
    /// <c>EncodeCStringLiteral</c> byte-for-byte so a pin block emitted here
    /// is identical to XHT's. A null input encodes as <c>nullptr</c>.
    /// </summary>
    /// <param name="s">The raw string to encode (null encodes as <c>nullptr</c>).</param>
    /// <returns>The C++ literal text.</returns>
    public static string EncodeCStringLiteral(string? s)
    {
        if (s is null)
        {
            return "nullptr";
        }
        StringBuilder sb = new(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    // Non-printable control characters get hex-escaped.
                    if (c < 0x20)
                    {
                        sb.Append('\\');
                        sb.Append('x');
                        sb.Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private void AppendIndent()
    {
        for (int i = 0; i < _depth; i++)
        {
            _sb.Append(IndentUnit);
        }
    }
}

/// <summary>
/// Static convenience extension over <see cref="CppWriter"/> for emitting a
/// run of indented lines from a collection without a hand-written loop at
/// every call site.
/// </summary>
public static class CppWriterExtensions
{
    /// <summary>
    /// Append each line in <paramref name="lines"/> through
    /// <see cref="CppWriter.AppendLine(string)"/> (in enumeration order).
    /// </summary>
    /// <param name="writer">The target writer. Must not be null.</param>
    /// <param name="lines">The lines to append, in order. Must not be null.</param>
    /// <returns>The writer (for chaining).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> or <paramref name="lines"/> is null.</exception>
    public static CppWriter AppendLines(this CppWriter writer, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(lines);
        foreach (string line in lines)
        {
            writer.AppendLine(line);
        }
        return writer;
    }
}
