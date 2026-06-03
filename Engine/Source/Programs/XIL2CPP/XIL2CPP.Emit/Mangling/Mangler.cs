// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Core;
using DiagnosticSeverity = Simgenics.XPact.XIL2CPP.Core.DiagnosticSeverity;

namespace Simgenics.XPact.XIL2CPP.Emit.Mangling;

/// <summary>
/// The LOCKED, contract-versioned XIL2CPP symbol mangler per
/// <c>/Documents/XToolchainContract.html</c> Section 2.2 / 2.3 / 2.4. It is a
/// pure function (no ambient state -- no clock, no random, no process-global
/// logger, no insertion-order dependence) so two runs over the same symbol
/// produce a byte-identical mangle (gate X-IL2CPP-MANGLE-DET).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule (Contract Section 2.2).</b> The conceptual mangled name is
/// <c>{ContractVersion}__{Namespace}::{Type}::{Method}{GenericArgManglings}{ParamManglings}</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>{ContractVersion}</c>: the literal <c>_v</c> followed by the
///     contract-version short tag (passed in by the caller; never hashed
///     here -- the tag is the only build-derived input and it is stable
///     across the contract's lifetime).
///   </description></item>
///   <item><description>
///     <c>{Namespace}::{Type}::{Method}</c>: the dot-separated namespace with
///     <c>.</c> replaced by <c>::</c>, then the nested-type chain joined by
///     <c>::</c>, then the simple method name. Operator overloads use the CLR
///     convention (<c>op_Add</c>, <c>op_Equality</c>, ...); constructors are
///     <c>$ctor</c>; static constructors are <c>$cctor</c>.
///   </description></item>
///   <item><description>
///     <c>{GenericArgManglings}</c>: for a generic method, <c>_G_</c>
///     followed by the comma-separated mangled generic argument types in
///     angle brackets. Empty when not generic.
///   </description></item>
///   <item><description>
///     <c>{ParamManglings}</c>: <c>_P_</c> followed by a parenthesized
///     comma-separated list of mangled parameter types. Reference types
///     prefix <c>R</c>, value types <c>V</c>, by-ref <c>B</c>, in <c>I</c>,
///     out <c>O</c>. Empty parentheses for a parameterless method. Per the
///     Section 2.3 free-function form, an instance method's implicit
///     <c>self</c> is the FIRST parameter (a reference to the containing
///     type); a static method has no <c>self</c>.
///   </description></item>
/// </list>
/// <para>
/// <b>The linker translation (Contract Section 2.3).</b>
/// <see cref="CanonicalToLinkerSymbol"/> turns the conceptual form into the
/// linker-visible symbol: <c>::</c> becomes <c>__</c>, parentheses are
/// dropped, and commas / spaces become <c>_</c>. The translation is
/// deterministic and reversible enough for the hot-reload trampoline
/// (Section 2.3).
/// </para>
/// <para>
/// <b>Forward commitments (Phase 6.e decisions).</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b><c>ref readonly</c> (C# 12).</b> Contract Section 2.2 only locks
///     <c>R / V / B / I / O</c>; the <c>K</c> discriminator for
///     <c>ref readonly</c> is a Rev-14 amendment (FIX-B-HIGH-03). Until then
///     XIL2CPP emits a private-extension <c>_KQ_</c> marker before the
///     discriminator and raises <see cref="DiagnosticCodes.ManglingDiscriminatorsMissing"/>
///     (<c>XIL2CPP179</c>) once per such parameter so the non-conformance is
///     visible. The byte form is fixed so it is still deterministic.
///   </description></item>
///   <item><description>
///     <b>Generic nesting.</b> One level of generic-argument mangling is
///     implemented for 6.e (matching the Pass-3 depth bound). A generic
///     argument that is ITSELF a constructed generic emits a
///     <c>_G_DEFERRED</c> placeholder for that argument and raises
///     <see cref="DiagnosticCodes.GenericInstantiationDepthExceeded"/>
///     (<c>XIL2CPP123</c>).
///   </description></item>
/// </list>
/// </remarks>
public static class Mangler
{
    /// <summary>The literal contract-version prefix per Contract Section 2.2 ("the literal <c>_v</c>").</summary>
    public const string ContractVersionPrefix = "_v";

    /// <summary>The generic-arg-mangling marker per Contract Section 2.2.</summary>
    public const string GenericArgMarker = "_G_";

    /// <summary>The parameter-mangling marker per Contract Section 2.2.</summary>
    public const string ParamMarker = "_P_";

    /// <summary>The constructor method-name token per Contract Section 2.2.</summary>
    public const string ConstructorToken = "$ctor";

    /// <summary>The static-constructor method-name token per Contract Section 2.2.</summary>
    public const string StaticConstructorToken = "$cctor";

    /// <summary>
    /// The XIL2CPP-private forward-commit marker emitted before the
    /// <c>ref readonly</c> discriminator until Contract Rev 14 locks the
    /// <c>K</c> discriminator (raises <c>XIL2CPP179</c>).
    /// </summary>
    public const string RefReadonlyExtensionMarker = "_KQ_";

    /// <summary>
    /// The placeholder emitted for a generic argument that is itself a
    /// constructed generic (deeper than the one level Phase 6.e supports;
    /// raises <c>XIL2CPP123</c>).
    /// </summary>
    public const string GenericDeferredToken = "_G_DEFERRED";

    /// <summary>
    /// Mangle a method symbol per Contract Section 2.2 / 2.3.
    /// </summary>
    /// <param name="method">The method symbol to mangle (instance / static method, constructor, operator, accessor, local function). Must not be null.</param>
    /// <param name="contractVersionTag">The contract-version short tag (WITHOUT the leading <c>_v</c>; e.g. <c>1ab12cd34</c>). Must not be null / empty / whitespace.</param>
    /// <returns>The conceptual + linker forms and any forward-commit diagnostics.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="contractVersionTag"/> is null / empty / whitespace.</exception>
    public static MangledName MangleMethod(IMethodSymbol method, string contractVersionTag)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);

        List<DiagnosticRecord> diagnostics = new();

        StringBuilder canonical = new();
        canonical.Append(ContractVersionPrefix);
        canonical.Append(contractVersionTag);
        canonical.Append("__");

        // {Namespace}::{Type}::{Method}
        AppendNamespaceAndType(canonical, method.ContainingType);
        canonical.Append("::");
        canonical.Append(MethodNameToken(method));

        // {GenericArgManglings} -- one level (Phase 6.e bound).
        AppendGenericArgManglings(canonical, method, diagnostics);

        // {ParamManglings} -- _P_ + parenthesized comma-separated params; the
        // implicit `self` reference is the first parameter for instance
        // methods (Contract Section 2.3 free-function form).
        AppendParamManglings(canonical, method, diagnostics);

        string canonicalForm = canonical.ToString();
        string linkerSymbol = CanonicalToLinkerSymbol(canonicalForm);
        return new MangledName(canonicalForm, linkerSymbol, diagnostics);
    }

    /// <summary>
    /// Mangle a named type per the namespace / nested-type rules of Contract
    /// Section 2.2 (the conceptual <c>{Namespace}::{Type}</c> portion, with
    /// the contract-version prefix). Used for the FClass / type-level emit
    /// symbols.
    /// </summary>
    /// <param name="type">The named type to mangle. Must not be null.</param>
    /// <param name="contractVersionTag">The contract-version short tag (WITHOUT the leading <c>_v</c>). Must not be null / empty / whitespace.</param>
    /// <returns>The conceptual + linker forms and any forward-commit diagnostics.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="contractVersionTag"/> is null / empty / whitespace.</exception>
    public static MangledName MangleType(INamedTypeSymbol type, string contractVersionTag)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractVersionTag);

        List<DiagnosticRecord> diagnostics = new();

        StringBuilder canonical = new();
        canonical.Append(ContractVersionPrefix);
        canonical.Append(contractVersionTag);
        canonical.Append("__");
        AppendNamespaceAndType(canonical, type);

        // One-level generic-arg mangling for a constructed generic type.
        if (type.IsGenericType && !type.TypeArguments.IsDefaultOrEmpty)
        {
            AppendGenericArgList(canonical, type.TypeArguments, diagnostics);
        }

        string canonicalForm = canonical.ToString();
        string linkerSymbol = CanonicalToLinkerSymbol(canonicalForm);
        return new MangledName(canonicalForm, linkerSymbol, diagnostics);
    }

    /// <summary>
    /// Translate a conceptual mangled form to the linker-visible symbol per
    /// Contract Section 2.3: <c>::</c> becomes <c>__</c>, parentheses are
    /// dropped, and commas / spaces become <c>_</c>. Deterministic and
    /// order-preserving.
    /// </summary>
    /// <param name="canonicalForm">The conceptual mangled name. Must not be null.</param>
    /// <returns>The linker-visible symbol.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="canonicalForm"/> is null.</exception>
    public static string CanonicalToLinkerSymbol(string canonicalForm)
    {
        ArgumentNullException.ThrowIfNull(canonicalForm);

        StringBuilder sb = new(canonicalForm.Length);
        for (int i = 0; i < canonicalForm.Length; i++)
        {
            char c = canonicalForm[i];
            switch (c)
            {
                case ':':
                    // "::" -> "__". Each ':' maps to one '_' so a "::" pair
                    // becomes "__"; a lone ':' (never produced by this mangler)
                    // would map to a single '_'.
                    sb.Append('_');
                    break;
                case '(':
                case ')':
                case ',':
                    // Parentheses + commas are DROPPED. A parameter separator in
                    // the canonical form is ", " (comma + space); dropping the
                    // comma and converting the following space to a single '_'
                    // yields one '_' per separator, matching the doc-example
                    // translation ("Valve, V float" -> "Valve_V_float").
                    break;
                case ' ':
                    // Space -> single '_'.
                    sb.Append('_');
                    break;
                case '<':
                case '>':
                    // Angle brackets (generic arg lists) -> single '_' each so
                    // the linker form stays alphanumeric-plus-underscore.
                    sb.Append('_');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Compute the CLR-convention method-name token per Contract Section 2.2:
    /// <c>$ctor</c> for an instance constructor, <c>$cctor</c> for a static
    /// constructor, the <c>op_*</c> metadata name for an operator / conversion
    /// operator, the accessor metadata name (<c>get_X</c> / <c>set_X</c>) for
    /// a property / indexer accessor, else the simple method name.
    /// </summary>
    /// <param name="method">The method symbol. Must not be null.</param>
    /// <returns>The method-name token.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="method"/> is null.</exception>
    public static string MethodNameToken(IMethodSymbol method)
    {
        ArgumentNullException.ThrowIfNull(method);

        return method.MethodKind switch
        {
            MethodKind.Constructor => ConstructorToken,
            MethodKind.StaticConstructor => StaticConstructorToken,
            // Operators + conversion operators already carry their CLR
            // op_* metadata name (op_Addition, op_Equality, op_Implicit, ...).
            MethodKind.UserDefinedOperator => method.MetadataName,
            MethodKind.Conversion => method.MetadataName,
            // Property / indexer / event accessors carry get_/set_/add_/remove_
            // metadata names; emit them verbatim so a getter and setter mangle
            // distinctly.
            MethodKind.PropertyGet => method.MetadataName,
            MethodKind.PropertySet => method.MetadataName,
            MethodKind.EventAdd => method.MetadataName,
            MethodKind.EventRemove => method.MetadataName,
            // Finalizer: C#'s destructor metadata name is "Finalize".
            MethodKind.Destructor => method.MetadataName,
            _ => method.Name,
        };
    }

    private static void AppendNamespaceAndType(StringBuilder canonical, INamedTypeSymbol type)
    {
        // Namespace path (dot -> ::). The global namespace contributes nothing.
        string ns = NamespacePath(type);
        if (ns.Length > 0)
        {
            canonical.Append(ns);
            canonical.Append("::");
        }

        // Nested-type chain, outermost first, joined by ::.
        List<string> typeChain = new();
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            typeChain.Add(t.Name);
        }
        // typeChain is innermost-first; reverse to outermost-first.
        for (int i = typeChain.Count - 1; i >= 0; i--)
        {
            canonical.Append(typeChain[i]);
            if (i > 0)
            {
                canonical.Append("::");
            }
        }
    }

    private static string NamespacePath(INamedTypeSymbol type)
    {
        INamespaceSymbol? ns = type.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return string.Empty;
        }

        // Build outermost-first dotted path -> :: separated.
        List<string> parts = new();
        for (INamespaceSymbol? cursor = ns; cursor is not null && !cursor.IsGlobalNamespace; cursor = cursor.ContainingNamespace)
        {
            parts.Add(cursor.Name);
        }
        StringBuilder sb = new();
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            sb.Append(parts[i]);
            if (i > 0)
            {
                sb.Append("::");
            }
        }
        return sb.ToString();
    }

    private static void AppendGenericArgManglings(
        StringBuilder canonical,
        IMethodSymbol method,
        List<DiagnosticRecord> diagnostics)
    {
        if (!method.IsGenericMethod || method.TypeArguments.IsDefaultOrEmpty)
        {
            return;
        }
        AppendGenericArgList(canonical, method.TypeArguments, diagnostics);
    }

    private static void AppendGenericArgList(
        StringBuilder canonical,
        System.Collections.Immutable.ImmutableArray<ITypeSymbol> typeArguments,
        List<DiagnosticRecord> diagnostics)
    {
        canonical.Append(GenericArgMarker);
        canonical.Append('<');
        for (int i = 0; i < typeArguments.Length; i++)
        {
            if (i > 0)
            {
                canonical.Append(", ");
            }
            canonical.Append(MangleGenericArgType(typeArguments[i], diagnostics));
        }
        canonical.Append('>');
    }

    /// <summary>
    /// Mangle one generic argument type at the single supported nesting level.
    /// A nested constructed generic emits <see cref="GenericDeferredToken"/>
    /// and raises <c>XIL2CPP123</c> (matching the Pass-3 depth bound).
    /// </summary>
    private static string MangleGenericArgType(ITypeSymbol arg, List<DiagnosticRecord> diagnostics)
    {
        if (arg is INamedTypeSymbol named
            && named.IsGenericType
            && !named.TypeArguments.IsDefaultOrEmpty)
        {
            // Deeper than one level: defer per the Phase 6.e decision.
            diagnostics.Add(new DiagnosticRecord(
                DiagnosticSeverity.Warning,
                DiagnosticCodes.GenericInstantiationDepthExceeded,
                $"Mangling generic argument '{TypeMangle(arg)}' exceeds the one-level Phase 6.e nesting bound; emitting '{GenericDeferredToken}'."));
            return GenericDeferredToken;
        }
        return TypeMangle(arg);
    }

    private static void AppendParamManglings(
        StringBuilder canonical,
        IMethodSymbol method,
        List<DiagnosticRecord> diagnostics)
    {
        canonical.Append(ParamMarker);
        canonical.Append('(');

        bool first = true;

        // Section 2.3: instance methods take an explicit `self` first param
        // (a reference to the containing type). Static methods + constructors
        // do not (a constructor's `self` is the freshly-allocated object,
        // which the ABI passes separately; the doc example self is only on a
        // plain instance method). We follow the doc example: an instance
        // method's first conceptual param is `R <ContainingType>`.
        if (!method.IsStatic
            && method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor)
            && method.ContainingType is not null)
        {
            canonical.Append('R');
            canonical.Append(' ');
            canonical.Append(TypeMangle(method.ContainingType));
            first = false;
        }

        foreach (IParameterSymbol param in method.Parameters)
        {
            if (!first)
            {
                canonical.Append(", ");
            }
            first = false;
            AppendParam(canonical, param, diagnostics);
        }

        canonical.Append(')');
    }

    private static void AppendParam(
        StringBuilder canonical,
        IParameterSymbol param,
        List<DiagnosticRecord> diagnostics)
    {
        // ref readonly is the C# 12 RefKind.RefReadOnlyParameter; until
        // Contract Rev 14 locks the K discriminator, emit the private _KQ_
        // marker and raise XIL2CPP179. The remaining ref kinds map to the
        // Section 2.2 discriminators directly.
        switch (param.RefKind)
        {
            case RefKind.RefReadOnlyParameter:
                canonical.Append(RefReadonlyExtensionMarker);
                diagnostics.Add(new DiagnosticRecord(
                    DiagnosticSeverity.Warning,
                    DiagnosticCodes.ManglingDiscriminatorsMissing,
                    $"Parameter '{param.Name}' is 'ref readonly'; the 'K' mangling discriminator is a Contract Rev 14 forward commit. Emitting the XIL2CPP-private '{RefReadonlyExtensionMarker}' extension marker."));
                canonical.Append('K');
                break;
            case RefKind.Ref:
                canonical.Append('B');
                break;
            case RefKind.In:
                canonical.Append('I');
                break;
            case RefKind.Out:
                canonical.Append('O');
                break;
            default:
                // None: by-value -- R for a reference type, V for a value type.
                canonical.Append(IsReferenceMangleType(param.Type) ? 'R' : 'V');
                break;
        }

        canonical.Append(' ');
        canonical.Append(TypeMangle(param.Type));
    }

    /// <summary>
    /// True iff the by-value discriminator for <paramref name="type"/> is
    /// <c>R</c> (reference type) rather than <c>V</c> (value type). Type
    /// parameters are treated by their reference-type constraint when known,
    /// else conservatively as reference types (the common XObject case).
    /// </summary>
    private static bool IsReferenceMangleType(ITypeSymbol type)
    {
        return type.IsReferenceType
            || type.TypeKind == TypeKind.TypeParameter && !type.IsValueType;
    }

    /// <summary>
    /// Render a type's mangle: the C# special-type keyword where one exists
    /// (so <c>float</c> matches the doc example rather than
    /// <c>System::Single</c>), else the namespace-qualified
    /// <c>::</c>-separated name. One level of generic arguments is rendered;
    /// deeper nesting is the caller's responsibility (it is only entered from
    /// the generic-arg path, which guards depth).
    /// </summary>
    private static string TypeMangle(ITypeSymbol type)
    {
        // C# special types render as their keyword (float, int, bool, ...) so
        // the doc example's "V float" reproduces exactly.
        string? special = SpecialTypeKeyword(type.SpecialType);
        if (special is not null)
        {
            return special;
        }

        if (type is IArrayTypeSymbol array)
        {
            return TypeMangle(array.ElementType) + "[]";
        }

        if (type is ITypeParameterSymbol tp)
        {
            return tp.Name;
        }

        if (type is INamedTypeSymbol named)
        {
            string nsPath = NamedTypeFullName(named);
            return nsPath;
        }

        // Fallback: the minimally-qualified display.
        return type.Name;
    }

    private static string NamedTypeFullName(INamedTypeSymbol named)
    {
        StringBuilder sb = new();
        AppendNamespaceAndType(sb, named);
        return sb.ToString();
    }

    private static string? SpecialTypeKeyword(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Void => "void",
        SpecialType.System_Boolean => "bool",
        SpecialType.System_Char => "char",
        SpecialType.System_SByte => "sbyte",
        SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Single => "float",
        SpecialType.System_Double => "double",
        SpecialType.System_Decimal => "decimal",
        SpecialType.System_String => "string",
        SpecialType.System_Object => "object",
        SpecialType.System_IntPtr => "nint",
        SpecialType.System_UIntPtr => "nuint",
        _ => null,
    };
}
