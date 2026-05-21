// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using Simgenics.XPact.XHT.AST;

namespace Simgenics.XPact.XHT.Resolver.Phases;

/// <summary>
/// Phase 6 (<see cref="ResolvePhase.Properties"/>): resolve property
/// types via the symbol table; resolve <c>ReplicatedUsing</c> callback
/// method references and validate their signatures per
/// <c>/Documents/XHT.html</c> Rev 5 Section 5.1 + Section 6.2 XHT113.
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

        // Signature: zero parameters OR exactly one parameter whose
        // type matches the property's type (or its C++ reference form).
        int paramCount = callback.Parameters?.Count ?? 0;
        if (paramCount == 0)
        {
            ctx.ResolvedRepNotifyMethods[p] = callback;
            return;
        }

        if (paramCount == 1)
        {
            string paramType = callback.Parameters![0].TypeIdentifier ?? string.Empty;
            string propertyType = p.TypeIdentifier ?? string.Empty;

            if (RepNotifyTypeCompatible(paramType, propertyType))
            {
                ctx.ResolvedRepNotifyMethods[p] = callback;
                return;
            }
        }

        PhaseHelpers.Error(
            ctx,
            DiagnosticCodes.RepNotifyInvalidSignature,
            $"Property '{cls.FullyQualifiedName}.{p.Name}' ReplicatedUsing callback '{callbackName}' has invalid signature; expected zero-param OR one-param matching '{p.TypeIdentifier}'.",
            p.Span);
    }

    private static bool RepNotifyTypeCompatible(string paramType, string propertyType)
    {
        // Tolerate C++ reference form: "const T&" / "T const&" / "T&"
        // / "const T &" match the bare "T" property.
        string normalizedParam = NormalizeTypeForComparison(paramType);
        string normalizedProp = NormalizeTypeForComparison(propertyType);

        return string.Equals(normalizedParam, normalizedProp, StringComparison.Ordinal);
    }

    private static string NormalizeTypeForComparison(string t)
    {
        if (string.IsNullOrEmpty(t))
        {
            return t;
        }

        // Strip qualifiers and reference markers.
        string s = t.Trim();
        s = s.Replace("const ", string.Empty, StringComparison.Ordinal);
        s = s.Replace(" const", string.Empty, StringComparison.Ordinal);
        s = s.Replace("&", string.Empty, StringComparison.Ordinal);
        // Strip a trailing '*' so pointer-to-T matches T (the
        // RepNotify-by-value convention covers both).
        s = s.TrimEnd('*');
        s = s.Trim();
        return s;
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
