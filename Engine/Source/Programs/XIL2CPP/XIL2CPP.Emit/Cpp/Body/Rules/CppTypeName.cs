// Copyright Simgenics. All Rights Reserved.

using System.Text;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Analysis;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp.Body.Rules;

/// <summary>
/// A small, self-contained, deterministic renderer of a C# type symbol into the
/// C++ type spelling the WU-D5 body-lowering rules (cast / object-creation /
/// type-pattern) emit. It is NOT the full Pass-6 type lowerer (that is a later
/// wave) -- it covers exactly what the cast / null / object-creation rules need:
/// the C# special-type keyword mapping to its fixed-width C++ type, and the
/// <c>::</c>-qualified name for a user / library named type (matching the
/// <see cref="Mangling.Mangler"/>'s <c>{Namespace}::{Type}</c> spelling, with a
/// leading global-scope <c>::</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a local helper, not a shared lowerer.</b> The shared C# -&gt; C++
/// type lowerer is a later Pass-6 unit; WU-D5 must not depend on a type that
/// does not yet exist, and must not edit a foundation file. This helper is a
/// NEW file owned by WU-D5; it is not an <see cref="IBodyLoweringRule"/>, so the
/// reflection-discovery in <see cref="BodyLoweringRuleRegistry"/> never picks it
/// up. When the canonical type lowerer lands, these rules switch to it and this
/// helper is retired.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Pure function of the symbol:
/// ordinal switch on <see cref="SpecialType"/>, ordinal string building, no
/// clock / culture / random.
/// </para>
/// <para>
/// <b>Reference-type pointer convention.</b> An XObject-derived (engine
/// reference) type lowers to a raw pointer (<c>::Ns::T*</c>) in the C++ object
/// model, so <see cref="RenderReference"/> appends the <c>*</c>. Value types and
/// the special-type keywords render by value.
/// </para>
/// </remarks>
internal static class CppTypeName
{
    /// <summary>
    /// Render <paramref name="type"/> as its C++ by-value spelling: the
    /// fixed-width C++ type for a C# special type, else the global-scope
    /// <c>::</c>-qualified named-type spelling. A null / unresolved (error)
    /// type renders the documented <c>/*?*/ auto</c> placeholder so the gap is
    /// visible and the emitted C++ never silently drops the type.
    /// </summary>
    /// <param name="type">The C# type symbol (may be null / an error type).</param>
    /// <returns>The C++ by-value type spelling.</returns>
    public static string Render(ITypeSymbol? type)
    {
        if (type is null || type.TypeKind == TypeKind.Error)
        {
            return "/*?*/ auto";
        }

        string? special = SpecialTypeKeyword(type.SpecialType);
        if (special is not null)
        {
            return special;
        }

        if (type is INamedTypeSymbol named)
        {
            return QualifiedName(named);
        }

        if (type is IArrayTypeSymbol array)
        {
            // Arrays are an engine span/container in the object model; the
            // canonical lowerer owns the real spelling. Render a visible,
            // deterministic placeholder rather than a wrong concrete type.
            return $"/*array*/ {Render(array.ElementType)}";
        }

        if (type is ITypeParameterSymbol tp)
        {
            return tp.Name;
        }

        return type.Name;
    }

    /// <summary>
    /// Render <paramref name="type"/> as the C++ spelling used at a reference
    /// site: an XObject-derived (engine reference) type as a raw pointer
    /// (<c>::Ns::T*</c>); everything else exactly as <see cref="Render"/>.
    /// </summary>
    /// <param name="type">The C# type symbol (may be null / an error type).</param>
    /// <returns>The C++ reference-site type spelling.</returns>
    public static string RenderReference(ITypeSymbol? type)
    {
        string rendered = Render(type);
        if (type is INamedTypeSymbol named && AnalyzerHelpers.IsXObjectDerived(named))
        {
            return rendered + "*";
        }
        return rendered;
    }

    /// <summary>
    /// True iff <paramref name="type"/> is an XObject-derived engine reference
    /// type (the object model represents it as a raw pointer, so the cast /
    /// type-pattern rules use a pointer-style cast).
    /// </summary>
    /// <param name="type">The C# type symbol (may be null).</param>
    /// <returns>True iff the type is XObject-derived.</returns>
    public static bool IsXObjectReference(ITypeSymbol? type)
        => type is INamedTypeSymbol named && AnalyzerHelpers.IsXObjectDerived(named);

    /// <summary>
    /// Render a named type as a global-scope <c>::</c>-qualified spelling:
    /// <c>::Namespace::Outer::Type</c> (namespace dots and the nested-type chain
    /// both become <c>::</c>). Matches the <see cref="Mangling.Mangler"/>'s
    /// conceptual <c>{Namespace}::{Type}</c> spelling, prefixed with the
    /// leading global-scope <c>::</c> the emitted C++ uses.
    /// </summary>
    private static string QualifiedName(INamedTypeSymbol named)
    {
        StringBuilder sb = new();

        if (named.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            foreach (string part in ns.ToDisplayString().Split('.'))
            {
                sb.Append("::");
                sb.Append(part);
            }
        }

        // Nested-type chain, outermost first.
        int insertAt = sb.Length;
        for (INamedTypeSymbol? t = named; t is not null; t = t.ContainingType)
        {
            sb.Insert(insertAt, t.Name);
            sb.Insert(insertAt, "::");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Map a C# special type to its fixed-width C++ spelling, or null when the
    /// type is not a special type. Fixed-width integer spellings
    /// (<c>int32_t</c>, ...) keep the ABI deterministic across platforms.
    /// </summary>
    private static string? SpecialTypeKeyword(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Void => "void",
        SpecialType.System_Boolean => "bool",
        SpecialType.System_Char => "char16_t",
        SpecialType.System_SByte => "int8_t",
        SpecialType.System_Byte => "uint8_t",
        SpecialType.System_Int16 => "int16_t",
        SpecialType.System_UInt16 => "uint16_t",
        SpecialType.System_Int32 => "int32_t",
        SpecialType.System_UInt32 => "uint32_t",
        SpecialType.System_Int64 => "int64_t",
        SpecialType.System_UInt64 => "uint64_t",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        SpecialType.System_IntPtr => "intptr_t",
        SpecialType.System_UIntPtr => "uintptr_t",
        _ => null,
    };
}
