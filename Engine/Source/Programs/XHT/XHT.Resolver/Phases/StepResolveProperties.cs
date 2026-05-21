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
/// surfaces container metadata more richly.
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
            // a "AXValve*" property looks up "AXValve".
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

    private static string? ExtractInnerTypeIdentifier(string container)
    {
        // Cheap parser: find the first '<' and matching '>' and return
        // the contents. For TMap<K,V> return V (the value side).
        int lt = container.IndexOf('<');
        int gt = container.LastIndexOf('>');
        if (lt < 0 || gt < 0 || gt < lt)
        {
            return null;
        }

        string inner = container.Substring(lt + 1, gt - lt - 1).Trim();
        if (string.IsNullOrEmpty(inner))
        {
            return null;
        }

        // For TMap<K, V> the value type lives after the last top-level
        // comma. (Depth-counter is overkill for Phase 1d -- containers
        // of containers are rare.)
        int lastComma = inner.LastIndexOf(',');
        if (lastComma >= 0 && lastComma + 1 < inner.Length)
        {
            inner = inner.Substring(lastComma + 1).Trim();
        }

        return StripPointerOrReference(inner);
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
