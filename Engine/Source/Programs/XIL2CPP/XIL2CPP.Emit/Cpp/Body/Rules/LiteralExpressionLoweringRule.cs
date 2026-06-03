// Copyright Simgenics. All Rights Reserved.

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// Lowers a C# literal expression (<see cref="LiteralExpressionSyntax"/>) to
/// its C++ literal form, per <c>/Documents/XIL2CPP.html</c> Rev 4 Section 5
/// (expression mapping). Covers the integer (<c>int</c> / <c>uint</c> /
/// <c>long</c> / <c>ulong</c>), floating-point (<c>float</c> / <c>double</c>),
/// <c>bool</c>, <c>null</c>, <c>char</c>, and string literal kinds, each
/// rendered with the correct C++ suffix / spelling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Value, not source text.</b> Numeric literals are lowered from the bound
/// constant <em>value</em> (<see cref="SyntaxToken.Value"/>), not the raw
/// source spelling, so C# digit separators (<c>1_000</c>), hex / binary radices
/// (<c>0x1F</c>, <c>0b1010</c>), and the case of the suffix all normalize to a
/// single canonical decimal C++ literal -- two C# spellings of the same value
/// produce byte-identical C++. The C++ suffix is chosen from the literal's CLR
/// runtime type, which encodes both the explicit C# suffix and the
/// integer-literal default-type promotion rules Roslyn already applied.
/// </para>
/// <para>
/// <b>Strings are interned, not inlined.</b> A string literal lowers to a
/// reference to a process-interned <c>::XCore::Container::FString</c> whose
/// backing <c>constinit</c> static (<c>_String_&lt;hash16&gt;</c>) is emitted
/// once per distinct value by the string-literal table (unit E3). The reference
/// form is <c>::XCore::Container::FString{&amp;_String_&lt;hash16&gt;}</c> where
/// <c>hash16</c> is <see cref="ComputeStringHash16"/> -- the first 16 lowercase
/// hex characters of the SHA-256 of the UTF-8 bytes of the value. This rule only
/// emits the reference; <see cref="ComputeStringHash16"/> is the single shared
/// definition of the hash so the collector (E3) and the reference here agree
/// byte-for-byte.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> All numeric formatting goes
/// through <see cref="CultureInfo.InvariantCulture"/>; the string hash is a pure
/// function of the value's UTF-8 bytes; no ambient state participates.
/// </para>
/// </remarks>
public sealed class LiteralExpressionLoweringRule : IBodyLoweringRule
{
    /// <summary>
    /// The C++ symbol-name prefix for an interned string-literal backing static
    /// (<c>_String_&lt;hash16&gt;</c>). Shared with the string-literal table
    /// (unit E3) so the reference here and the definition there agree.
    /// </summary>
    public const string StringSymbolPrefix = "_String_";

    /// <summary>
    /// The fully-qualified C++ type the string literals are interned as.
    /// </summary>
    public const string FStringType = "::XCore::Container::FString";

    /// <summary>The number of leading hex characters of the SHA-256 digest used as the string symbol's stable suffix.</summary>
    public const int StringHashHexLength = 16;

    /// <inheritdoc/>
    public string Name => "Expr.Literal";

    /// <inheritdoc/>
    public bool CanHandle(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node is LiteralExpressionSyntax;
    }

    /// <inheritdoc/>
    public void Emit(SyntaxNode node, EmitContext context, CppWriter writer, StatementEmitter parent)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parent);

        var literal = (LiteralExpressionSyntax)node;
        writer.Append(Render(literal));
    }

    /// <summary>
    /// Render <paramref name="literal"/> to its C++ literal text (the same text
    /// <see cref="Emit"/> appends). Exposed so a collector / test can obtain the
    /// rendered form without driving a full emitter.
    /// </summary>
    /// <param name="literal">The literal expression to render. Must not be null.</param>
    /// <returns>The C++ literal text.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="literal"/> is null.</exception>
    /// <exception cref="InvalidOperationException">If the literal kind / value is not a supported literal form.</exception>
    public static string Render(LiteralExpressionSyntax literal)
    {
        ArgumentNullException.ThrowIfNull(literal);

        switch (literal.Kind())
        {
            case SyntaxKind.NullLiteralExpression:
                return "nullptr";

            case SyntaxKind.TrueLiteralExpression:
                return "true";

            case SyntaxKind.FalseLiteralExpression:
                return "false";

            case SyntaxKind.CharacterLiteralExpression:
                return RenderChar((char)literal.Token.Value!);

            case SyntaxKind.StringLiteralExpression:
                return RenderStringReference((string)literal.Token.Value!);

            case SyntaxKind.NumericLiteralExpression:
                return RenderNumeric(literal.Token.Value
                    ?? throw new InvalidOperationException(
                        "Numeric literal has no bound constant value."));

            default:
                throw new InvalidOperationException(
                    $"LiteralExpressionLoweringRule reached an unsupported literal kind '{literal.Kind()}'.");
        }
    }

    /// <summary>
    /// The C++ reference form for a string literal of <paramref name="value"/>:
    /// <c>::XCore::Container::FString{&amp;_String_&lt;hash16&gt;}</c>. The
    /// backing <c>constinit</c> static is emitted by the string-literal table
    /// (unit E3); this is only the consuming reference.
    /// </summary>
    /// <param name="value">The (decoded) string-literal value. Must not be null.</param>
    /// <returns>The C++ interned-FString reference text.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="value"/> is null.</exception>
    public static string RenderStringReference(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat(
            FStringType,
            "{&",
            StringSymbolPrefix,
            ComputeStringHash16(value),
            "}");
    }

    /// <summary>
    /// The stable 16-character lowercase-hex symbol suffix for a string-literal
    /// value: the first <see cref="StringHashHexLength"/> hex characters of the
    /// SHA-256 of the value's UTF-8 bytes. This is the single shared definition
    /// the string-literal table (unit E3) and the reference form
    /// (<see cref="RenderStringReference"/>) both use, so a reference emitted
    /// here resolves to the static E3 emits.
    /// </summary>
    /// <param name="value">The string-literal value to hash. Must not be null.</param>
    /// <returns>The 16-character lowercase-hex hash prefix.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="value"/> is null.</exception>
    public static string ComputeStringHash16(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        // Lowercase hex of the leading bytes -- two hex chars per byte, so
        // StringHashHexLength / 2 bytes cover the requested character count.
        var sb = new StringBuilder(StringHashHexLength);
        int byteCount = StringHashHexLength / 2;
        for (int i = 0; i < byteCount; i++)
        {
            sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render a C++ character literal from <paramref name="c"/> with the
    /// standard escapes, single-quoted (e.g. <c>'a'</c>, <c>'\n'</c>,
    /// <c>'\''</c>, <c>'\x7f'</c>).
    /// </summary>
    private static string RenderChar(char c)
    {
        var sb = new StringBuilder(4);
        sb.Append('\'');
        switch (c)
        {
            case '\\': sb.Append("\\\\"); break;
            case '\'': sb.Append("\\'"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            case '\0': sb.Append("\\0"); break;
            default:
                if (c < 0x20 || c > 0x7e)
                {
                    // Non-printable / non-ASCII: hex-escape the UTF-16 code unit.
                    sb.Append("\\x");
                    sb.Append(((int)c).ToString("x", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
                break;
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>
    /// Render a numeric literal's boxed constant <paramref name="value"/> to a
    /// canonical decimal C++ literal with the suffix implied by the CLR runtime
    /// type (<c>int</c> -&gt; none, <c>uint</c> -&gt; <c>u</c>, <c>long</c> -&gt;
    /// <c>ll</c>, <c>ulong</c> -&gt; <c>ull</c>, <c>float</c> -&gt; <c>f</c>,
    /// <c>double</c> -&gt; no suffix with a forced decimal point).
    /// </summary>
    private static string RenderNumeric(object value) => value switch
    {
        int i => i.ToString(CultureInfo.InvariantCulture),
        uint u => u.ToString(CultureInfo.InvariantCulture) + "u",
        long l => l.ToString(CultureInfo.InvariantCulture) + "ll",
        ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "ull",
        float f => RenderFloat(f),
        double d => RenderDouble(d),
        _ => throw new InvalidOperationException(
            $"LiteralExpressionLoweringRule cannot render numeric literal of type '{value.GetType().FullName}'."),
    };

    /// <summary>
    /// Render a <c>float</c> with the <c>f</c> suffix, using the round-trip
    /// (<c>R</c>) format so the C++ literal parses back to the identical IEEE-754
    /// single-precision value, and forcing a decimal point so the literal is
    /// unambiguously floating-point.
    /// </summary>
    private static string RenderFloat(float f)
    {
        string text = f.ToString("R", CultureInfo.InvariantCulture);
        text = EnsureFloatingPointForm(text);
        return text + "f";
    }

    /// <summary>
    /// Render a <c>double</c> using the round-trip (<c>R</c>) format so the C++
    /// literal parses back to the identical IEEE-754 double-precision value,
    /// forcing a decimal point so the literal is unambiguously floating-point
    /// (a bare integer-valued double would otherwise be an <c>int</c> literal in
    /// C++).
    /// </summary>
    private static string RenderDouble(double d)
    {
        string text = d.ToString("R", CultureInfo.InvariantCulture);
        return EnsureFloatingPointForm(text);
    }

    /// <summary>
    /// Ensure <paramref name="text"/> reads as a C++ floating-point literal: if
    /// it carries neither a decimal point nor an exponent, append <c>.0</c>.
    /// </summary>
    private static string EnsureFloatingPointForm(string text)
    {
        bool hasPointOrExponent =
            text.IndexOf('.') >= 0
            || text.IndexOf('e') >= 0
            || text.IndexOf('E') >= 0;
        return hasPointOrExponent ? text : text + ".0";
    }
}
