// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Simgenics.XPact.XIL2CPP.Analysis;
using Simgenics.XPact.XIL2CPP.Emit.Mangling;
using Simgenics.XPact.XIL2CPP.Tiering;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// XIL2CPP Pass-6 property emitter (WU-E3) per <c>/Documents/XIL2CPP.html</c>
/// Rev 4 Section 3.2 (Pass 6) + Section 5 (member-emit mapping). It lowers a
/// C# auto-property (or an <c>[XProperty]</c>-style reflected property) into
/// the two contract-versioned <c>extern "C"</c> free-function accessors that
/// the Section 2.3 free-function ABI defines: a getter that reads the
/// synthesized <c>__BackingField_&lt;Name&gt;</c> slot and a setter that writes
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shapes (Section 2.3 free-function form).</b> For a property
/// <c>Foo</c> of type <c>T</c> on type <c>S</c> the getter is emitted as
/// <c>extern "C" &lt;T&gt; &lt;get_LinkerSymbol&gt;(::S* self) noexcept { return self-&gt;__BackingField_Foo; }</c>
/// and the setter as
/// <c>extern "C" void &lt;set_LinkerSymbol&gt;(::S* self, &lt;T&gt; value) noexcept { self-&gt;__BackingField_Foo = value; }</c>.
/// The <c>get_</c> / <c>set_</c> linker symbols come from the Pass-5
/// <see cref="ManglingRecord.LinkerSymbol"/> resolved through
/// <see cref="EmitContext.FindMangling"/> on the accessor's
/// <see cref="StableId"/>.
/// </para>
/// <para>
/// <b>The 6.h write barrier (gate X-IL2CPP-BARRIER-EMIT).</b> When <c>T</c> is
/// XObject-derived the setter writes its backing field through the
/// <c>XPACT_GC_STORE(self, &amp;(self-&gt;__BackingField_&lt;Name&gt;), value);</c>
/// reference-store write barrier INSTEAD of a plain field store: the macro
/// records the reference with the collector AND performs the store, so emitting
/// a plain <c>self-&gt;__BackingField_&lt;Name&gt; = value;</c> too would
/// double-write. For a value-typed / non-XObject <c>T</c> no barrier is emitted
/// -- a plain field store carries no GC obligation.
/// </para>
/// <para>
/// <b>Determinism (gate X-IL2CPP-CSPATH-DET).</b> Every emit is a pure
/// function of the property symbol + <see cref="EmitContext"/>: no clock, no
/// <see cref="Guid"/>, no <see cref="System.Random"/>; type rendering uses a
/// fixed lookup table and ordinal string handling; newlines flow through
/// <see cref="CppWriter"/>'s explicit <c>'\n'</c>. Two emits of the same
/// property produce byte-identical C++.
/// </para>
/// </remarks>
public sealed class PropertyEmitter
{
    /// <summary>The synthesized backing-field name prefix (Section 10.2 reflection shape).</summary>
    public const string BackingFieldPrefix = "__BackingField_";

    /// <summary>The Phase 6.h write-barrier macro an XObject-derived property setter emits.</summary>
    public const string WriteBarrierMacro = "XPACT_GC_STORE";

    /// <summary>
    /// The C++ namespace + type for the engine root reference smart-pointer
    /// template the <c>XObject</c>-derived setter barrier will key on.
    /// </summary>
    private const string XObjectPointerTemplate = "XPtr";

    /// <summary>
    /// Emit the getter + setter accessor pair for <paramref name="property"/>
    /// into <paramref name="writer"/>. The getter is emitted first, then the
    /// setter (when the property has a set / init accessor). A read-only
    /// property emits only the getter; a write-only property only the setter.
    /// </summary>
    /// <param name="property">The property symbol to emit. Must not be null.</param>
    /// <param name="context">The per-module emit context (Pass-5 manglings are read from it). Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="property"/>, <paramref name="context"/>, or <paramref name="writer"/> is null.</exception>
    public void Emit(IPropertySymbol property, EmitContext context, CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        if (property.GetMethod is { } getter)
        {
            EmitGetter(property, getter, context, writer);
        }

        if (property.SetMethod is { } setter)
        {
            EmitSetter(property, setter, context, writer);
        }
    }

    /// <summary>
    /// Emit only the getter accessor for <paramref name="property"/> /
    /// <paramref name="getter"/>:
    /// <c>extern "C" &lt;T&gt; &lt;get_LinkerSymbol&gt;(&lt;self&gt;) noexcept { return self-&gt;__BackingField_&lt;Name&gt;; }</c>.
    /// </summary>
    /// <param name="property">The owning property symbol. Must not be null.</param>
    /// <param name="getter">The property's get-accessor method symbol. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    /// <exception cref="InvalidOperationException">If the getter has no Pass-5 mangling row in the context's mangling table.</exception>
    public void EmitGetter(
        IPropertySymbol property,
        IMethodSymbol getter,
        EmitContext context,
        CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        string linkerSymbol = ResolveLinkerSymbol(getter, context);
        string valueType = RenderCppType(property.Type);
        string selfParam = RenderSelfParameter(property);
        string backingField = BackingFieldPrefix + property.Name;

        // extern "C" <T> <get_LinkerSymbol>(::S* self) noexcept {
        writer.BeginBlock(
            "extern \"C\" " + valueType + " " + linkerSymbol + "(" + selfParam + ") noexcept");
        writer.AppendLine("return self->" + backingField + ";");
        writer.EndBlock();
    }

    /// <summary>
    /// Emit only the setter accessor for <paramref name="property"/> /
    /// <paramref name="setter"/>. For a value-typed property the body is the
    /// plain
    /// <c>extern "C" void &lt;set_LinkerSymbol&gt;(&lt;self&gt;, &lt;T&gt; value) noexcept { self-&gt;__BackingField_&lt;Name&gt; = value; }</c>;
    /// for an XObject-derived property the field store is replaced by the Phase
    /// 6.h write barrier
    /// <c>XPACT_GC_STORE(self, &amp;(self-&gt;__BackingField_&lt;Name&gt;), value);</c>
    /// (which performs the store itself, so the plain assignment is suppressed).
    /// </summary>
    /// <param name="property">The owning property symbol. Must not be null.</param>
    /// <param name="setter">The property's set / init accessor method symbol. Must not be null.</param>
    /// <param name="context">The per-module emit context. Must not be null.</param>
    /// <param name="writer">The C++ writer to emit into. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If any argument is null.</exception>
    /// <exception cref="InvalidOperationException">If the setter has no Pass-5 mangling row in the context's mangling table.</exception>
    public void EmitSetter(
        IPropertySymbol property,
        IMethodSymbol setter,
        EmitContext context,
        CppWriter writer)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(setter);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writer);

        string linkerSymbol = ResolveLinkerSymbol(setter, context);
        string valueType = RenderCppType(property.Type);
        string selfParam = RenderSelfParameter(property);
        string backingField = BackingFieldPrefix + property.Name;

        // extern "C" void <set_LinkerSymbol>(::S* self, <T> value) noexcept {
        writer.BeginBlock(
            "extern \"C\" void " + linkerSymbol + "(" + selfParam + ", " + valueType + " value) noexcept");

        if (IsXObjectReferenceType(property.Type))
        {
            // Phase 6.h reference-store write barrier (gate X-IL2CPP-BARRIER-EMIT):
            // the setter's slot write goes through XPACT_GC_STORE so the
            // collector records the reference. The macro performs the backing-
            // field store itself, so NO plain `self->__BackingField_<Name> =
            // value;` follows -- emitting it too would write the slot twice.
            writer.AppendLine(
                WriteBarrierMacro + "(self, &(self->" + backingField + "), value);");
        }
        else
        {
            // Value-typed slot: a plain field store carries no GC obligation.
            writer.AppendLine("self->" + backingField + " = value;");
        }

        writer.EndBlock();
    }

    /// <summary>
    /// Resolve <paramref name="accessor"/>'s Pass-5 linker symbol through the
    /// context's mangling table (keyed by the accessor's
    /// <see cref="StableId"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">If no mangling row exists for the accessor.</exception>
    private static string ResolveLinkerSymbol(IMethodSymbol accessor, EmitContext context)
    {
        StableId id = StableId.FromSymbol(accessor);
        ManglingRecord? record = context.FindMangling(id);
        if (record is null)
        {
            throw new InvalidOperationException(
                "No Pass-5 mangling row for property accessor '" + id.Value
                + "'. The accessor must be enumerated by Pass 5 before Pass 6 emits it.");
        }
        return record.Value.LinkerSymbol;
    }

    /// <summary>
    /// Render the explicit <c>self</c> parameter (Section 2.3 free-function
    /// form): a pointer to the property's containing type, e.g.
    /// <c>::Game::Widget* self</c>.
    /// </summary>
    private static string RenderSelfParameter(IPropertySymbol property)
    {
        string containingType = RenderNamedTypePath(property.ContainingType);
        return "::" + containingType + "* self";
    }

    /// <summary>
    /// Render the C++ type for a property's value type per the Section 5
    /// mapping: the C# special scalar types map to their fixed-width C++
    /// counterparts; <c>string</c> maps to <c>::XCore::Container::FString</c>;
    /// an XObject-derived reference maps to <c>XPtr&lt;T&gt;</c>; any other
    /// named type maps to its <c>::</c>-qualified path.
    /// </summary>
    /// <param name="type">The C# property type. Must not be null.</param>
    /// <returns>The C++ type spelling.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="type"/> is null.</exception>
    public static string RenderCppType(ITypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Special scalar / string types map through a fixed lookup so the emit
        // is culture-independent and deterministic.
        string? scalar = ScalarCppType(type.SpecialType);
        if (scalar is not null)
        {
            return scalar;
        }

        // An XObject-derived reference slot is emitted as the smart-pointer
        // template XPtr<T> (Section 5.7 slot mapping).
        if (type is INamedTypeSymbol named && AnalyzerHelpers.IsXObjectDerived(named))
        {
            return XObjectPointerTemplate + "<" + named.Name + ">";
        }

        if (type is INamedTypeSymbol other)
        {
            return "::" + RenderNamedTypePath(other);
        }

        // Type parameters / arrays / other: fall back to the simple name (the
        // later type-mapper wave widens this; the property emitter only needs
        // the cases an emittable auto-property surfaces).
        return type.Name;
    }

    /// <summary>
    /// True iff <paramref name="type"/> is an XObject-derived reference type
    /// (the slot that requires the 6.h write barrier).
    /// </summary>
    private static bool IsXObjectReferenceType(ITypeSymbol type)
        => type is INamedTypeSymbol named && AnalyzerHelpers.IsXObjectDerived(named);

    /// <summary>
    /// Render a named type's <c>::</c>-separated namespace + nested-type path
    /// (without a leading <c>::</c>), e.g. <c>Game::Outer::Inner</c>. Matches
    /// the conceptual mangle's namespace/type rendering.
    /// </summary>
    private static string RenderNamedTypePath(INamedTypeSymbol type)
    {
        List<string> parts = new();

        // Nested-type chain, innermost first.
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            parts.Add(t.Name);
        }

        // Namespace chain, innermost first.
        for (INamespaceSymbol? ns = type.ContainingNamespace;
             ns is not null && !ns.IsGlobalNamespace;
             ns = ns.ContainingNamespace)
        {
            parts.Add(ns.Name);
        }

        // parts is innermost-first; render outermost-first.
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

    /// <summary>
    /// Map a C# special scalar / string type to its C++ spelling, or null when
    /// <paramref name="specialType"/> is not one this mapper handles directly.
    /// <c>string</c> maps to the engine container <c>FString</c>; the integral
    /// / floating scalars map to the C++ fixed-width / keyword forms.
    /// </summary>
    private static string? ScalarCppType(SpecialType specialType) => specialType switch
    {
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
        SpecialType.System_String => "::XCore::Container::FString",
        _ => null,
    };
}
