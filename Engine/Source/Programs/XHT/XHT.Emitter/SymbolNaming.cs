// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// The reflected-type roles XHT's singleton-getter and ConstInit naming
/// surfaces distinguish. Maps to the <c>&lt;Kind&gt;</c> component in the
/// <c>Z_Construct_X&lt;Kind&gt;_&lt;Module&gt;_&lt;Type&gt;</c> format per
/// <c>/Documents/XHT.html</c> Rev 5 Section 10.2 + Contract Section 2.2.
/// </summary>
/// <remarks>
/// <para>
/// The format suffix string per role is locked at XHT.html Section 10.2:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Class"/> -&gt; <c>XClass</c></description></item>
///   <item><description><see cref="Struct"/> -&gt; <c>XStruct</c></description></item>
///   <item><description><see cref="Enum"/> -&gt; <c>XEnum</c></description></item>
///   <item><description><see cref="Interface"/> -&gt; <c>XInterface</c></description></item>
///   <item><description><see cref="Delegate"/> -&gt; <c>XDelegateFunction</c></description></item>
///   <item><description><see cref="Function"/> -&gt; <c>XFunction</c></description></item>
///   <item><description><see cref="Property"/> -&gt; <c>XProperty</c></description></item>
/// </list>
/// </remarks>
public enum EngineRole
{
    /// <summary>Reflected class (<c>XCLASS</c> / <c>[XClass]</c>); emits <c>XClass</c>.</summary>
    Class,

    /// <summary>Reflected struct (<c>XSTRUCT</c> / <c>[XStruct]</c>); emits <c>XStruct</c>.</summary>
    Struct,

    /// <summary>Reflected enum (<c>XENUM</c> / <c>[XEnum]</c>); emits <c>XEnum</c>.</summary>
    Enum,

    /// <summary>Reflected interface (<c>XINTERFACE</c> / <c>[XInterface]</c>); emits <c>XInterface</c>.</summary>
    Interface,

    /// <summary>Reflected delegate (<c>XDELEGATE</c> family / <c>[XDelegate]</c>); emits <c>XDelegateFunction</c>.</summary>
    Delegate,

    /// <summary>Reflected function (<c>XFUNCTION</c> / <c>[XFunction]</c>); emits <c>XFunction</c>.</summary>
    Function,

    /// <summary>Reflected property (<c>XPROPERTY</c> / <c>[XProperty]</c>); emits <c>XProperty</c>.</summary>
    Property,
}

/// <summary>
/// Symbol-name builders for XHT's emit surface per
/// <c>/Documents/XHT.html</c> Rev 5 Section 10 + Contract Section 1.4 +
/// Contract Section 2.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism contract (Section 14).</b> Every builder here is a pure
/// function of its inputs -- identical inputs produce identical bytes.
/// No timestamps, no random, no culture-sensitive comparisons. The
/// implementation uses <see cref="CultureInfo.InvariantCulture"/> for
/// integer-to-decimal conversions so e.g. <c>14</c> never renders as
/// the Turkish locale's variant.
/// </para>
/// <para>
/// <b>Hex-escape rule (Contract Section 1.4).</b> Bytes in a logical-path
/// segment outside <c>[A-Za-z0-9]</c> are length-prefixed and emitted as
/// their hex escape so the grammar stays bijective even when paths contain
/// dashes, spaces, or non-ASCII characters. The implementation operates on
/// UTF-8 bytes (the engine-wide UTF-8 commitment at Contract Section 6) so
/// diacritics decompose into multi-byte hex escapes deterministically.
/// </para>
/// <para>
/// <b>Reserved suffix vocabulary.</b> The body-macro suffix passed to
/// <see cref="FileId"/> is whatever the caller chooses; XHT.html
/// Section 8.1 + Contract Section 1.3 lock the Phase-1 valid set to
/// <c>_PROLOG</c>, <c>_GENERATED_BODY</c>, <c>_INCLASS</c>,
/// <c>_RPC_WRAPPERS</c>, <c>_ACCESSORS</c>, <c>_FIELDNOTIFY</c>,
/// <c>_VINTERFACES</c>, <c>_STANDARD_CONSTRUCTORS</c>. The encoder does
/// not enforce the vocabulary; the body-macro emitter (Section 8.1) does.
/// </para>
/// </remarks>
public static class SymbolNaming
{
    private const string SymbolPrefix = "_XID_";

    /// <summary>
    /// Encode a body-macro FileId per Contract Section 1.4. The grammar is
    /// <c>_XID_N&lt;PluginNameLen&gt;&lt;PluginName&gt;N&lt;LogicalPathSegCount&gt;{N&lt;SegLen&gt;&lt;SegName&gt;}_L&lt;LineNumber&gt;_&lt;Suffix&gt;</c>.
    /// </summary>
    /// <param name="pluginName">Plugin name as declared in the descriptor; <c>"Engine"</c> for engine code. Must not be null / empty.</param>
    /// <param name="logicalPathSegments">Logical path segments inside the plugin (e.g. <c>[XGameFramework, Public, Valves, XValve]</c>). Must not be null; each segment must not be null / empty.</param>
    /// <param name="lineNumber">1-based source line of the <c>XGENERATED_BODY()</c> macro. Must be non-negative.</param>
    /// <param name="suffix">Suffix string (e.g. <c>"_GENERATED_BODY"</c>). Must not be null. Must begin with an underscore so the decoder can split the line / suffix boundary.</param>
    /// <returns>The encoded FileId symbol string.</returns>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="pluginName"/> is empty, <paramref name="logicalPathSegments"/> contains an empty entry, or <paramref name="suffix"/> does not begin with an underscore.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="lineNumber"/> is negative.</exception>
    public static string FileId(
        string pluginName,
        IReadOnlyList<string> logicalPathSegments,
        int lineNumber,
        string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginName);
        ArgumentNullException.ThrowIfNull(logicalPathSegments);
        ArgumentNullException.ThrowIfNull(suffix);
        if (lineNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber), lineNumber,
                "Line numbers must be non-negative.");
        }
        if (suffix.Length == 0 || suffix[0] != '_')
        {
            throw new ArgumentException(
                $"Suffix '{suffix}' must begin with an underscore (e.g. \"_GENERATED_BODY\").",
                nameof(suffix));
        }

        StringBuilder sb = new(capacity: 64);
        sb.Append(SymbolPrefix);

        // N<PluginNameLen><PluginName>
        AppendLengthPrefixedToken(sb, pluginName, nameof(pluginName));

        // N<LogicalPathSegCount>{N<SegLen><SegName>}
        sb.Append('N');
        sb.Append(logicalPathSegments.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < logicalPathSegments.Count; i++)
        {
            string seg = logicalPathSegments[i];
            if (string.IsNullOrEmpty(seg))
            {
                throw new ArgumentException(
                    $"Logical path segment at index {i} is null or empty.",
                    nameof(logicalPathSegments));
            }
            AppendLengthPrefixedToken(sb, seg, $"{nameof(logicalPathSegments)}[{i}]");
        }

        // _L<LineNumber>
        sb.Append("_L");
        sb.Append(lineNumber.ToString(CultureInfo.InvariantCulture));

        // _<Suffix> -- suffix begins with its own underscore per the
        // table; the format string above produces _L<line><suffix> e.g.
        // "_L14_GENERATED_BODY".
        sb.Append(suffix);

        return sb.ToString();
    }

    /// <summary>
    /// Build the per-type singleton-getter symbol per XHT.html
    /// Section 10.2 + Section 13.2. Stable across non-renaming edits;
    /// Live Coding's patch link uses this name.
    /// </summary>
    /// <param name="moduleName">Module name as registered in the manifest. Must not be null / empty.</param>
    /// <param name="typeName">Source type name (case preserved, e.g. <c>AXValve</c>). Must not be null / empty.</param>
    /// <param name="role">The reflected-type role.</param>
    /// <returns>The singleton-getter symbol (e.g. <c>Z_Construct_XClass_XGameFramework_AXValve</c>).</returns>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> or <paramref name="typeName"/> is null / empty.</exception>
    public static string SingletonGetter(string moduleName, string typeName, EngineRole role)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        return $"Z_Construct_{RoleToken(role)}_{moduleName}_{typeName}";
    }

    /// <summary>
    /// Build the ConstInit static-data symbol per Contract Section 7.1.
    /// Format: <c>Z_ConstInit_X&lt;Kind&gt;_&lt;ModuleName&gt;_&lt;TypeName&gt;</c>.
    /// </summary>
    /// <param name="moduleName">Module name as registered in the manifest. Must not be null / empty.</param>
    /// <param name="typeName">Source type name (case preserved, e.g. <c>AXValve</c>). Must not be null / empty.</param>
    /// <param name="role">The reflected-type role.</param>
    /// <returns>The ConstInit symbol (e.g. <c>Z_ConstInit_XClass_XGameFramework_AXValve</c>).</returns>
    /// <exception cref="ArgumentException">If <paramref name="moduleName"/> or <paramref name="typeName"/> is null / empty.</exception>
    public static string ConstInitSymbol(string moduleName, string typeName, EngineRole role)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleName);
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        return $"Z_ConstInit_{RoleToken(role)}_{moduleName}_{typeName}";
    }

    /// <summary>
    /// Return the symbol form (<c>XClass</c>, <c>XStruct</c>, ...) per
    /// XHT.html Section 10.2 for a given role.
    /// </summary>
    /// <param name="role">The reflected-type role.</param>
    /// <returns>The symbol form prefixed with <c>X</c>.</returns>
    public static string RoleToken(EngineRole role) => role switch
    {
        EngineRole.Class => "XClass",
        EngineRole.Struct => "XStruct",
        EngineRole.Enum => "XEnum",
        EngineRole.Interface => "XInterface",
        EngineRole.Delegate => "XDelegateFunction",
        EngineRole.Function => "XFunction",
        EngineRole.Property => "XProperty",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown EngineRole."),
    };

    private static void AppendLengthPrefixedToken(StringBuilder sb, string token, string contextForErrors)
    {
        // Per Contract Section 1.4: reserved characters (any byte outside
        // [A-Za-z0-9]) are themselves length-prefixed and emitted as
        // their hex escape so the grammar stays bijective. The Itanium-
        // ABI-style example in Contract Section 1.4 + XHT.html
        // Section 10.4 shows the simple-alnum case; here we extend to
        // non-alnum bytes via the deterministic hex encode.
        //
        // Algorithm: convert the source string to UTF-8 bytes; for each
        // byte either pass it through (alnum) or emit "Xhh" where hh is
        // its two-digit lowercase hex (the only reserved code so we
        // avoid <Token> chars themselves, and "X" is distinct from "N"
        // so the decoder can read the escape unambiguously).
        //
        // The "Xhh" escape token itself contributes 3 bytes to the
        // encoded-token-length so the decoder reads exactly N bytes for
        // the token. This satisfies Contract Section 1.4's "any byte
        // outside [A-Za-z0-9] is itself length-prefixed and emitted as
        // its hex escape" rule -- the escaped form is part of the
        // length-counted body, not a separate run.
        ArgumentException.ThrowIfNullOrEmpty(token);

        // Build the encoded form first so we can prefix the correct
        // length count.
        StringBuilder encoded = new(token.Length);
        ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(token);
        for (int i = 0; i < utf8.Length; i++)
        {
            byte b = utf8[i];
            if (IsAlnum(b))
            {
                encoded.Append((char)b);
            }
            else
            {
                // Escape: "X" + two-digit lowercase hex.
                encoded.Append('X');
                AppendByteAsHex2(encoded, b);
            }
        }

        sb.Append('N');
        sb.Append(encoded.Length.ToString(CultureInfo.InvariantCulture));
        sb.Append(encoded);
        _ = contextForErrors; // suppress unused-arg warning under TreatWarningsAsErrors
    }

    private static bool IsAlnum(byte b)
    {
        return (b >= (byte)'A' && b <= (byte)'Z')
            || (b >= (byte)'a' && b <= (byte)'z')
            || (b >= (byte)'0' && b <= (byte)'9');
    }

    private static void AppendByteAsHex2(StringBuilder sb, byte b)
    {
        const string lookup = "0123456789abcdef";
        sb.Append(lookup[b >> 4]);
        sb.Append(lookup[b & 0x0F]);
    }
}
