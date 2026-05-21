// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Simgenics.XPact.XHT.AST;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 6 (<see cref="ResolvePhase.Properties"/>): resolve property
/// types via the symbol table; resolve <c>ReplicatedUsing</c> callback
/// method references and validate their signatures per
/// <c>/Documents/XHT.html</c> Rev 7 Section 5.1 + Section 6.2 XHT113.
/// </summary>
/// <remarks>
/// <para>
/// <b>Container properties.</b> When a property's type identifier
/// matches the <c>TArray&lt;X&gt;</c> / <c>TMap&lt;K,V&gt;</c> /
/// <c>TSet&lt;X&gt;</c> shape, the resolver extracts the inner element
/// type and resolves that via
/// <see cref="SymbolTable.Lookup(string)"/>. For
/// <c>TMap&lt;K,V&gt;</c> the value type (V) is the resolved one --
/// the key resolution is left as a Phase 2 extension when the parser
/// surfaces container metadata more richly. The extraction walker uses
/// a depth-counter to handle nested containers and complex keys (e.g.
/// <c>TMap&lt;TPair&lt;int,int&gt;, V&gt;</c>) correctly per C4 audit.
/// </para>
/// <para>
/// <b>Primitive types.</b> Primitive types (<c>int32</c>,
/// <c>float</c>, <c>string</c>, etc.) intentionally fall through
/// without diagnostic; the resolver only records resolutions for
/// reflected types in
/// <see cref="ResolverContext.ResolvedPropertyTypes"/>.
/// </para>
/// </remarks>
internal sealed class StepResolveProperties : IResolverStep
{
    /// <inheritdoc />
    public void Execute(ResolverContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        IReadOnlyList<XhtTypeBase> ordered = ResolverPipeline.OrderedTypesSnapshot(ctx.Symbols);

        for (int i = 0; i < ordered.Count; i++)
        {
            XhtTypeBase t = ordered[i];
            switch (t)
            {
                case XhtClass cls:
                    ResolveClassProperties(ctx, cls);
                    break;
                case XhtStruct st:
                    ResolveStructProperties(ctx, st);
                    break;
                default:
                    break;
            }
        }
    }

    private static void ResolveClassProperties(ResolverContext ctx, XhtClass cls)
    {
        if (cls.Properties is null)
        {
            return;
        }

        foreach (XhtProperty p in cls.Properties)
        {
            ctx.PropertyContainers[p] = cls;
            ResolvePropertyType(ctx, p);
            ResolveRepNotify(ctx, p, cls);
        }
    }

    private static void ResolveStructProperties(ResolverContext ctx, XhtStruct st)
    {
        if (st.Properties is null)
        {
            return;
        }

        foreach (XhtProperty p in st.Properties)
        {
            ctx.PropertyContainers[p] = st;
            ResolvePropertyType(ctx, p);
            // Replicated on a struct is an error per Section 6.2; the
            // Final phase emits XHT112 for it. RepNotify resolution
            // doesn't run on structs because there's no enclosing
            // class-method scope to dispatch the callback through.
        }
    }

    private static void ResolvePropertyType(ResolverContext ctx, XhtProperty p)
    {
        if (string.IsNullOrEmpty(p.TypeIdentifier))
        {
            return;
        }

        // For container properties extract the inner type identifier
        // and try resolution on it. The parser sets IsContainer when
        // it detected TArray / TMap / TSet at parse time.
        string lookup = p.TypeIdentifier;
        if (p.IsContainer)
        {
            lookup = ExtractInnerTypeIdentifier(p.TypeIdentifier) ?? p.TypeIdentifier;
        }
        else
        {
            // For pointer / reference types strip the trailing * / & so
            // a "XValve*" property looks up "XValve".
            lookup = StripPointerOrReference(lookup);
        }

        XhtTypeBase? hit = ctx.Symbols.Lookup(lookup);
        if (hit is not null)
        {
            ctx.ResolvedPropertyTypes[p] = hit;
        }
        // Primitives and unresolved types both fall through without a
        // diagnostic at this phase; XHT103 only fires when the type is
        // expected to be reflected (e.g., super resolution). Property-
        // type misses are surfaced in the Final cross-language
        // consistency check (XHT120 / XHT121).
    }

    private static void ResolveRepNotify(ResolverContext ctx, XhtProperty p, XhtClass cls)
    {
        // ReplicatedUsing="MethodName" specifier names the callback.
        Specifier? rep = PhaseHelpers.FindSpecifier(p.Specifiers, "ReplicatedUsing");
        if (rep is null)
        {
            return;
        }

        if (rep.Values is null || rep.Values.Count == 0 || string.IsNullOrEmpty(rep.Values[0]))
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.RepNotifyInvalidSignature,
                $"Property '{cls.FullyQualifiedName}.{p.Name}' carries ReplicatedUsing without a callback name.",
                p.Span);
            return;
        }

        string callbackName = rep.Values[0];

        // Find the callback function in the owning class's Functions list.
        XhtFunction? callback = null;
        for (int i = 0; i < cls.Functions.Count; i++)
        {
            if (string.Equals(cls.Functions[i].Name, callbackName, StringComparison.Ordinal))
            {
                callback = cls.Functions[i];
                break;
            }
        }

        if (callback is null)
        {
            PhaseHelpers.Error(
                ctx,
                DiagnosticCodes.RepNotifyInvalidSignature,
                $"Property '{cls.FullyQualifiedName}.{p.Name}' ReplicatedUsing names callback '{callbackName}' which does not exist on the class.",
                p.Span);
            return;
        }

        // Signature is one of:
        //   (a) zero parameters
        //   (b) one parameter whose type matches the property's type (or
        //       its C++ const-reference form)
        //   (c) two parameters where param[0] is the old value and
        //       param[1] is the "delta" view for a static array
        //       (Round-2 audit M1 / M3). The static-array AST surface
        //       lands in Phase 2; today we accept the 2-param form
        //       structurally so the registration succeeds when XBT /
        //       parser surfaces the static-array flag.
        int paramCount = callback.Parameters?.Count ?? 0;
        if (paramCount == 0)
        {
            ctx.ResolvedRepNotifyMethods[p] = callback;
            return;
        }

        if (paramCount == 1)
        {
            XhtParam param = callback.Parameters![0];
            if (RepNotifyTypeCompatible(ctx, p, param))
            {
                ctx.ResolvedRepNotifyMethods[p] = callback;
                return;
            }
        }
        else if (paramCount == 2)
        {
            // 2-parm static-array form per Round-2 audit M1 + M3:
            //   void OnRepFoo(OldT old, const TArray<uint8>& delta)
            // The first parameter is the old value (same compatibility
            // rule as the 1-parm form); the second is the delta view.
            // Phase 1: we accept any 2-parm callback whose param[0]
            // matches the property type; the delta-view type checker
            // lands when the static-array AST flag does (Phase 2).
            XhtParam first = callback.Parameters![0];
            if (RepNotifyTypeCompatible(ctx, p, first))
            {
                ctx.ResolvedRepNotifyMethods[p] = callback;
                return;
            }
        }

        PhaseHelpers.Error(
            ctx,
            DiagnosticCodes.RepNotifyInvalidSignature,
            $"Property '{cls.FullyQualifiedName}.{p.Name}' ReplicatedUsing callback '{callbackName}' has invalid signature; expected zero-param, one-param matching '{p.TypeIdentifier}', or two-param static-array form (param[0] matches '{p.TypeIdentifier}').",
            p.Span);
    }

    /// <summary>
    /// Decide whether a callback parameter is type-compatible with the
    /// reflected property per Round-2 audit M1. Compatibility rules:
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><description>If both the property and the parameter
    ///   resolve to the same reflected type (via
    ///   <see cref="ResolverContext.ResolvedPropertyTypes"/>),
    ///   compatibility holds. Reference equality on the resolved
    ///   <see cref="XhtTypeBase"/> handles complex shapes like
    ///   <c>TSubclassOf&lt;X&gt;</c> and namespace-qualified spellings
    ///   that string-normalization cannot.</description></item>
    ///   <item><description>If neither resolves (both primitives), the
    ///   normalized primitive vocabulary check applies:
    ///   <c>int32 == int == Int32</c>, <c>bool == Boolean</c>, etc.
    ///   (See <see cref="NormalizePrimitive"/>.)</description></item>
    ///   <item><description>The C++ <c>const T&amp;</c> /
    ///   <c>T const&amp;</c> / <c>T&amp;</c> reference forms still
    ///   match the bare <c>T</c> after stripping qualifiers.</description></item>
    /// </list>
    /// </remarks>
    private static bool RepNotifyTypeCompatible(
        ResolverContext ctx,
        XhtProperty property,
        XhtParam param)
    {
        // 1. Resolved-type reference-equality path. The property's
        //    resolved type was populated by ResolvePropertyType earlier
        //    in this same phase. We try to resolve the parameter type
        //    the same way -- if both resolve to the same node, accept.
        if (ctx.ResolvedPropertyTypes.TryGetValue(property, out XhtTypeBase? resolvedProp)
            && resolvedProp is not null)
        {
            XhtTypeBase? resolvedParam = ResolveParameterType(ctx, param);
            if (resolvedParam is not null && ReferenceEquals(resolvedParam, resolvedProp))
            {
                return true;
            }
        }

        // 2. String-normalization path (primitives + the everything-
        //    else fallback). The const-reference pattern is collapsed
        //    via StripQualifiers; the primitive synonyms via
        //    NormalizePrimitive.
        string normalizedParam = NormalizePrimitive(StripQualifiers(param.TypeIdentifier));
        string normalizedProp = NormalizePrimitive(StripQualifiers(property.TypeIdentifier));

        if (string.Equals(normalizedParam, normalizedProp, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolve a parameter's type identifier the same way property
    /// resolution does (via the symbol table). Used by RepNotify
    /// compatibility checks so the parameter type can be compared via
    /// reference equality against the property's resolved type.
    /// </summary>
    private static XhtTypeBase? ResolveParameterType(ResolverContext ctx, XhtParam param)
    {
        if (string.IsNullOrEmpty(param.TypeIdentifier))
        {
            return null;
        }
        string lookup = StripQualifiers(param.TypeIdentifier);
        if (string.IsNullOrEmpty(lookup))
        {
            return null;
        }
        return ctx.Symbols.Lookup(lookup);
    }

    /// <summary>
    /// Strip C++ qualifiers (<c>const</c> / <c>volatile</c>) and trailing
    /// indirection markers (<c>*</c> / <c>&amp;</c>) from a raw type
    /// identifier. Handles <c>const T&amp;</c>, <c>T const&amp;</c>,
    /// <c>const T*</c>, and the bare-reference / bare-pointer forms.
    /// </summary>
    private static string StripQualifiers(string typeIdentifier)
    {
        if (string.IsNullOrEmpty(typeIdentifier))
        {
            return typeIdentifier;
        }

        string s = typeIdentifier.Trim();

        // Remove "const " and " const" tokens (whole-word). Avoid the
        // earlier global Replace("const ", "") which corrupts e.g.
        // "constField" if such an identifier ever appeared. The
        // whole-word approach is correct for the C++ surface XHT
        // accepts.
        s = RemoveTokenWithSpaces(s, "const");
        s = RemoveTokenWithSpaces(s, "volatile");

        // Trim trailing indirection markers + whitespace.
        s = s.TrimEnd();
        while (s.Length > 0 && (s[^1] == '*' || s[^1] == '&'))
        {
            s = s[..^1].TrimEnd();
        }
        return s.Trim();
    }

    /// <summary>
    /// Remove a whole-word token (with surrounding whitespace) from a
    /// type string. E.g. <c>"const Foo &amp;"</c> with token
    /// <c>"const"</c> becomes <c>"Foo &amp;"</c>. Used by
    /// <see cref="StripQualifiers"/>.
    /// </summary>
    private static string RemoveTokenWithSpaces(string input, string token)
    {
        // Walk the string identifying token occurrences flanked by
        // word-boundary characters (whitespace, start/end, or punctuation).
        StringBuilder sb = new(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            if (i + token.Length <= input.Length
                && string.CompareOrdinal(input, i, token, 0, token.Length) == 0
                && IsTokenBoundary(input, i - 1)
                && IsTokenBoundary(input, i + token.Length))
            {
                // Skip the token + any single trailing whitespace.
                i += token.Length;
                if (i < input.Length && input[i] == ' ')
                {
                    i++;
                }
                // Also drop a single preceding whitespace if any.
                if (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                }
                continue;
            }
            sb.Append(input[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsTokenBoundary(string s, int index)
    {
        if (index < 0 || index >= s.Length)
        {
            return true;
        }
        char c = s[index];
        return !(char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Collapse primitive-type synonyms to a canonical spelling per
    /// Round-2 audit M1. Engine spellings (<c>int32</c>, <c>uint8</c>),
    /// C++ spellings (<c>int</c>, <c>unsigned char</c>), and CLR
    /// spellings (<c>Int32</c>, <c>Byte</c>) all collapse so a C#
    /// <c>Int32</c> parameter matches a C++ <c>int32</c> property.
    /// </summary>
    /// <remarks>
    /// Non-primitive inputs pass through unchanged so the function
    /// remains safe to compose with <see cref="StripQualifiers"/>.
    /// </remarks>
    private static string NormalizePrimitive(string t)
    {
        if (string.IsNullOrEmpty(t))
        {
            return t;
        }
        return t switch
        {
            "int8" or "sbyte" or "SByte" or "signed char" => "int8",
            "uint8" or "byte" or "Byte" or "unsigned char" => "uint8",
            "int16" or "short" or "Int16" or "signed short" => "int16",
            "uint16" or "ushort" or "UInt16" or "unsigned short" => "uint16",
            "int" or "int32" or "Int32" or "signed int" => "int32",
            "uint" or "uint32" or "UInt32" or "unsigned int" or "unsigned" => "uint32",
            "long" or "int64" or "Int64" or "signed long long" or "signed long" => "int64",
            "ulong" or "uint64" or "UInt64" or "unsigned long" or "unsigned long long" => "uint64",
            "bool" or "Boolean" => "bool",
            "float" or "Single" => "float",
            "double" or "Double" => "double",
            "string" or "String" or "FString" => "FString",
            _ => t,
        };
    }

    /// <summary>
    /// Extract the inner element-type identifier from a container-type
    /// declaration string. Walks the <c>&lt;&gt;</c> depth explicitly
    /// so nested containers (<c>TArray&lt;TArray&lt;Foo&gt;&gt;</c>),
    /// multi-arg containers (<c>TMap&lt;K, V&gt;</c>), and inner
    /// commas inside type-arguments (<c>TMap&lt;TPair&lt;int,int&gt;, V&gt;</c>)
    /// are handled correctly. Per C4 audit (XHT.html Section 5.1).
    /// </summary>
    /// <param name="container">The full type identifier as authored.</param>
    /// <returns>
    /// The inner element-type identifier (the value type for
    /// <c>TMap</c>; the single argument otherwise) with pointer /
    /// reference markers stripped. Returns null when the input has no
    /// type-argument list.
    /// </returns>
    internal static string? ExtractInnerTypeIdentifier(string container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }

        // Find the outermost '<'.
        int lt = container.IndexOf('<');
        if (lt < 0)
        {
            return null;
        }

        // Find the matching '>' for the outermost '<' by depth counting.
        int depth = 0;
        int matchingGt = -1;
        for (int i = lt; i < container.Length; i++)
        {
            char c = container[i];
            if (c == '<') { depth++; }
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    matchingGt = i;
                    break;
                }
            }
        }
        if (matchingGt < 0)
        {
            // Unterminated angle bracket; bail.
            return null;
        }

        string argList = container.Substring(lt + 1, matchingGt - lt - 1).Trim();
        if (string.IsNullOrEmpty(argList))
        {
            return null;
        }

        // Find the last top-level ',' inside the arg list (depth==0).
        // For TMap<K, V> -> after last ',' is V. For nested forms like
        // TMap<TPair<int,int>, V> the inner commas are at depth > 0
        // and ignored.
        int lastTopComma = -1;
        depth = 0;
        for (int i = 0; i < argList.Length; i++)
        {
            char c = argList[i];
            if (c == '<') { depth++; }
            else if (c == '>') { depth--; }
            else if (c == ',' && depth == 0)
            {
                lastTopComma = i;
            }
        }

        string inner;
        if (lastTopComma >= 0 && lastTopComma + 1 < argList.Length)
        {
            inner = argList.Substring(lastTopComma + 1).Trim();
        }
        else
        {
            inner = argList;
        }

        return StripPointerOrReference(inner);
    }

    /// <summary>
    /// Extract both type arguments from a <c>TMap&lt;K, V&gt;</c> shape
    /// using the same depth-counting walker. Returns null when the
    /// shape isn't a comma-separated two-arg form. Per C4 audit.
    /// </summary>
    internal static (string Key, string Value)? ExtractMapKeyAndValue(string container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }

        int lt = container.IndexOf('<');
        if (lt < 0)
        {
            return null;
        }

        int depth = 0;
        int matchingGt = -1;
        for (int i = lt; i < container.Length; i++)
        {
            char c = container[i];
            if (c == '<') { depth++; }
            else if (c == '>')
            {
                depth--;
                if (depth == 0)
                {
                    matchingGt = i;
                    break;
                }
            }
        }
        if (matchingGt < 0)
        {
            return null;
        }

        string argList = container.Substring(lt + 1, matchingGt - lt - 1).Trim();

        // First top-level comma splits K from V.
        depth = 0;
        int firstTopComma = -1;
        for (int i = 0; i < argList.Length; i++)
        {
            char c = argList[i];
            if (c == '<') { depth++; }
            else if (c == '>') { depth--; }
            else if (c == ',' && depth == 0)
            {
                firstTopComma = i;
                break;
            }
        }

        if (firstTopComma < 0)
        {
            return null;
        }

        string k = argList.Substring(0, firstTopComma).Trim();
        string v = argList.Substring(firstTopComma + 1).Trim();
        return (StripPointerOrReference(k), StripPointerOrReference(v));
    }

    private static string StripPointerOrReference(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }
        string t = s.Trim();
        t = t.TrimEnd('*', '&').Trim();
        return t;
    }
}
