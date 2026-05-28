// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Simgenics.XPact.XHT.AST;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// XCoreXObject Phase 5.g' schema-vector emit per
/// <c>/Documents/XCoreXObject.html</c> Rev 4 Section 7.4 + Section 7.4.1
/// (Rev 3 FIX-H-R2-3 / FIX-H-R2-4 emit rules table) + Section 10.6
/// (XHT-emitted <c>.gen.cpp</c> registration paths).
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose.</b> For each XHT-reflected class / struct that carries
/// at least one <see cref="XhtProperty"/> with an XObject reference
/// (directly via FObjectProperty / FInterfaceProperty / etc. or
/// indirectly via a container of XObject refs / a nested FStruct whose
/// own RefSchema is non-empty), this emitter generates the
/// <c>.gen.cpp</c> fragment that defines:
/// </para>
/// <list type="number">
///   <item>One <c>constexpr</c> static array
///   <c>s_&lt;Type&gt;_RefSchemaOps[]</c> of
///   <see href="../../../Runtime/XCore/Public/Reflection/FXObjectRefSchema.h"
///   >FXObjectRefSchemaOp</see> opcodes (one per reference-carrying
///   property, terminated by a <c>Terminator</c> sentinel).</item>
///   <item>One <c>constexpr</c> static
///   <see href="../../../Runtime/XCore/Public/Reflection/FXObjectRefSchema.h"
///   >FXObjectRefSchema</see> referencing the Ops array.</item>
/// </list>
/// <para>
/// The full integration into the per-header
/// <see cref="SourceEmitter"/> pipeline lands when the Stage-B
/// <c>FClass</c>-with-<c>RefSchema</c> emit shape is finalised (the
/// current Stage-A surface emits <c>XClassDescriptor</c> which has no
/// <c>RefSchema</c> field). This emitter is the schema-vector
/// emit logic in isolation; Stage-B's <c>FClass</c> initializer
/// references the schema by its emitted symbol name.
/// </para>
/// <para>
/// <b>Determinism (XHT Section 14).</b> LF newlines, ordinal sort on
/// every collection touched, no culture-dependent formatting. Two
/// byte-identical inputs produce byte-identical outputs.
/// </para>
/// <para>
/// <b>Emit-rules table (spec §7.4.1; Rev 3 FIX-H-R2-3).</b> Each row
/// maps an XCore-4b FProperty subclass to a
/// <see cref="SchemaOpKind"/> + the offset / stride / nested-schema
/// parameters:
/// </para>
/// <list type="bullet">
///   <item><c>FObjectProperty</c> -> <see cref="SchemaOpKind.Object"/></item>
///   <item><c>FWeakObjectProperty</c> -> <see cref="SchemaOpKind.WeakObject"/></item>
///   <item><c>FSoftObjectProperty</c> -> <see cref="SchemaOpKind.SoftObject"/></item>
///   <item><c>FInterfaceProperty</c> -> <see cref="SchemaOpKind.Interface"/></item>
///   <item><c>FClassProperty</c> -> <see cref="SchemaOpKind.ClassProperty"/></item>
///   <item><c>FSoftClassProperty</c> -> <see cref="SchemaOpKind.SoftClass"/></item>
///   <item><c>FStructProperty</c> (inline) -> <see cref="SchemaOpKind.Struct"/>
///   (NestedSchema points at the nested struct's schema)</item>
///   <item><c>FArrayProperty&lt;XPtr&gt;</c> -> <see cref="SchemaOpKind.ArrayOfObject"/></item>
///   <item><c>FArrayProperty&lt;XWeakPtr&gt;</c> -> <see cref="SchemaOpKind.StridedArrayOfObject"/></item>
///   <item><c>FArrayProperty&lt;FStruct&gt;</c> -> <see cref="SchemaOpKind.ArrayOfStruct"/></item>
///   <item><c>FMapProperty&lt;K,V&gt;</c> with K or V == XPtr -> <see cref="SchemaOpKind.MapOfObject_KeyValue"/></item>
///   <item><c>FSetProperty&lt;XPtr&gt;</c> -> <see cref="SchemaOpKind.SetOfObject"/></item>
///   <item><c>FDelegateProperty</c> -> <see cref="SchemaOpKind.Delegate"/></item>
///   <item><c>FMulticastInlineDelegateProperty</c> -> <see cref="SchemaOpKind.MulticastInlineDelegate"/></item>
///   <item><c>FMulticastSparseDelegateProperty</c> -> <see cref="SchemaOpKind.MulticastSparseDelegate"/></item>
///   <item><c>FOptionalProperty&lt;XPtr&gt;</c> -> <see cref="SchemaOpKind.OptionalObject"/></item>
///   <item>All primitive FProperty subclasses (FIntProperty, FStrProperty,
///   FNameProperty, FTextProperty, FEnumProperty, FByteProperty,
///   FInt8/16/32/64Property, FUInt16/32/64Property, FFloatProperty,
///   FDoubleProperty, FBoolProperty) -- SKIP (no opcode emitted; no
///   XObject refs).</item>
/// </list>
/// </remarks>
public sealed class SchemaVectorEmitter
{
    /// <summary>
    /// Render the schema-vector emit fragment for a single reflected
    /// type. Returns the empty string when the type has no XObject-
    /// reference-carrying properties (the FStruct's RefSchema slot
    /// stays nullptr per spec §7.4 prose).
    /// </summary>
    /// <param name="typeName">The unqualified type name (e.g. <c>"XActor"</c>).</param>
    /// <param name="properties">The reflected properties of the type, in declaration order.</param>
    /// <returns>The <c>.gen.cpp</c> fragment (UTF-8 LF newlines) or the empty string.</returns>
    public string Render(string typeName, IReadOnlyList<XhtProperty> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(properties);

        // 1. Classify every property; gather the opcodes for ref-carrying
        //    properties. Primitives are skipped per spec §7.4.1.
        List<SchemaOpEmit> opcodes = new();
        foreach (XhtProperty p in properties)
        {
            SchemaOpEmit? op = ClassifyProperty(p);
            if (op is not null)
            {
                opcodes.Add(op.Value);
            }
        }

        // If the type has zero GC-ref properties, no schema vector is
        // emitted -- the FStruct's RefSchema stays nullptr per spec
        // §7.4 prose. Return empty so the caller knows to skip the
        // assignment.
        if (opcodes.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder sb = new(capacity: 1024);

        // Section header.
        sb.Append("// === Schema-vector GC walker (XCoreXObject Rev 4 §7.4; Phase 5.g') ===\n");
        sb.Append("// One opcode per XObject-reference-carrying property + Terminator.\n");
        sb.Append("// Emitted per spec §7.4.1 emit-rules table (Rev 3 FIX-H-R2-3).\n");

        // Opcode array.
        sb.Append("static constexpr ::XCore::Reflect::FXObjectRefSchemaOp ");
        sb.Append(SchemaOpsArraySymbol(typeName));
        sb.Append("[] =\n{\n");

        foreach (SchemaOpEmit op in opcodes)
        {
            sb.Append("    { ::XCore::Reflect::EXObjectRefSchemaOp::");
            sb.Append(op.Kind.ToString());
            sb.Append(",");
            sb.Append(" /*_padOp=*/0,");
            sb.Append(" /*ArrayDim=*/");
            sb.Append(op.ArrayDim.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append("\n      /*Offset=*/offsetof(");
            sb.Append(typeName);
            sb.Append(", ");
            sb.Append(op.PropertyName);
            sb.Append("),");
            sb.Append(" /*StrideBytes=*/");
            sb.Append(op.StrideBytes.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(" /*_padAlign=*/0,");
            sb.Append(" /*NestedSchema=*/");
            sb.Append(op.NestedSchemaSymbol ?? "nullptr");
            sb.Append(" },\n");
        }

        // Terminator sentinel (Rev 3 FIX-H-R2-4 emit-prose: "appends a
        // single Terminator opcode at the end").
        sb.Append("    { ::XCore::Reflect::EXObjectRefSchemaOp::Terminator,");
        sb.Append(" 0, 0, 0, 0, 0, nullptr },\n");
        sb.Append("};\n");
        sb.Append('\n');

        // Schema header referencing the opcode array.
        sb.Append("static constexpr ::XCore::Reflect::FXObjectRefSchema ");
        sb.Append(SchemaSymbol(typeName));
        sb.Append(" =\n{\n");
        sb.Append("    /*NumOps=*/");
        // NumOps INCLUDES the Terminator sentinel per spec §7.4 emit
        // prose. The runtime walker iterates [0..NumOps) and probes
        // for Terminator as the redundant stop signal.
        sb.Append((opcodes.Count + 1).ToString(CultureInfo.InvariantCulture));
        sb.Append('u');
        sb.Append(",\n");
        sb.Append("    /*Version=*/::XCore::Reflect::kFXObjectRefSchemaCurrentVersion,\n");
        sb.Append("    /*Ops=*/");
        sb.Append(SchemaOpsArraySymbol(typeName));
        sb.Append(",\n");
        sb.Append("    /*_padTail=*/0,\n");
        sb.Append("};\n");

        return sb.ToString();
    }

    /// <summary>
    /// Derives the C++ identifier for the schema's opcode array.
    /// Mirrors the spec §10.6 emit convention
    /// <c>&lt;TypeName&gt;_RefSchemaOps</c>.
    /// </summary>
    public static string SchemaOpsArraySymbol(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return "s_" + SanitizeForCSymbol(typeName) + "_RefSchemaOps";
    }

    /// <summary>
    /// Derives the C++ identifier for the schema header. Mirrors the
    /// spec §10.6 emit convention <c>&lt;TypeName&gt;_RefSchema</c>.
    /// </summary>
    public static string SchemaSymbol(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return "s_" + SanitizeForCSymbol(typeName) + "_RefSchema";
    }

    /// <summary>
    /// Classify a single property to a schema opcode kind +
    /// emit-time parameters. Returns null for non-ref properties
    /// (primitives) -- the caller skips them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classification reads <see cref="XhtProperty.TypeIdentifier"/>
    /// as the source-form type string (e.g. <c>"XPtr&lt;XActor&gt;"</c>,
    /// <c>"TArray&lt;XPtr&lt;XComponent&gt;&gt;"</c>). The resolver
    /// Phase (Step 5.1) would normally have annotated each property
    /// with the resolved FProperty subclass kind; the schema emitter
    /// re-derives from the type-identifier string until that
    /// integration lands.
    /// </para>
    /// </remarks>
    public static SchemaOpEmit? ClassifyProperty(XhtProperty p)
    {
        ArgumentNullException.ThrowIfNull(p);

        string ti = p.TypeIdentifier?.Trim() ?? string.Empty;
        if (ti.Length == 0)
        {
            return null;
        }

        // ---------- Container detection (TArray / TMap / TSet / TOptional) ----------
        if (StartsWithGeneric(ti, "TArray"))
        {
            string inner = ExtractFirstGenericArgument(ti, "TArray");
            return ClassifyArrayInner(p.Name, inner);
        }
        if (StartsWithGeneric(ti, "TMap"))
        {
            // TMap<K, V>: emit MapOfObject_KeyValue if K or V carries
            // a ref. The stride is sizeof(TPair<K,V>) which the
            // runtime walker reads as the StrideBytes opcode field;
            // emit a placeholder 16 (a TPair<XPtr, XPtr> is 16 bytes;
            // mixed types may differ).
            //
            // Refined per-K/V analysis is a Stage-B integration when
            // the resolved property subtype is wired through.
            (string K, string V) = ExtractFirstTwoGenericArguments(ti, "TMap");
            if (IsObjectRefType(K) || IsObjectRefType(V))
            {
                return new SchemaOpEmit(
                    Kind: SchemaOpKind.MapOfObject_KeyValue,
                    PropertyName: p.Name,
                    StrideBytes: 16,
                    ArrayDim: 0,
                    NestedSchemaSymbol: null);
            }
            return null;
        }
        if (StartsWithGeneric(ti, "TSet"))
        {
            string inner = ExtractFirstGenericArgument(ti, "TSet");
            if (IsObjectRefType(inner))
            {
                return new SchemaOpEmit(
                    Kind: SchemaOpKind.SetOfObject,
                    PropertyName: p.Name,
                    StrideBytes: 8,
                    ArrayDim: 0,
                    NestedSchemaSymbol: null);
            }
            return null;
        }
        if (StartsWithGeneric(ti, "TOptional"))
        {
            string inner = ExtractFirstGenericArgument(ti, "TOptional");
            if (IsObjectRefType(inner))
            {
                return new SchemaOpEmit(
                    Kind: SchemaOpKind.OptionalObject,
                    PropertyName: p.Name,
                    StrideBytes: 0,
                    ArrayDim: 0,
                    NestedSchemaSymbol: null);
            }
            return null;
        }

        // ---------- Single-slot (non-container) classification ----------

        // XPtr<T>, XStrongPtr<T>: strong XObject ref -> Object opcode.
        if (StartsWithGeneric(ti, "XPtr") || StartsWithGeneric(ti, "XStrongPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.Object,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // XWeakPtr<T>: weak ref -> WeakObject opcode.
        if (StartsWithGeneric(ti, "XWeakPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.WeakObject,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // XSoftPtr<T>: soft ref -> SoftObject opcode.
        if (StartsWithGeneric(ti, "XSoftPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.SoftObject,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TInterface<I> / FInterfaceProperty.
        if (StartsWithGeneric(ti, "TInterface"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.Interface,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TSubclassOf<T> -> FClassProperty.
        if (StartsWithGeneric(ti, "TSubclassOf"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.ClassProperty,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TSoftClassPtr<T> -> FSoftClassProperty.
        if (StartsWithGeneric(ti, "TSoftClassPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.SoftClass,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TDelegate<S> -> FDelegateProperty.
        if (StartsWithGeneric(ti, "TDelegate"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.Delegate,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TMulticastDelegate<S> -> FMulticastInlineDelegateProperty.
        if (StartsWithGeneric(ti, "TMulticastDelegate"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.MulticastInlineDelegate,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TSparseMulticastDelegate<S> -> FMulticastSparseDelegateProperty.
        if (StartsWithGeneric(ti, "TSparseMulticastDelegate"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.MulticastSparseDelegate,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // Trailing "*" on an identifier rooted at "X" -- treat as raw
        // XObject pointer (the XHT-style `XActor*` form). The emit
        // routes through Object opcode.
        if (ti.EndsWith("*", StringComparison.Ordinal))
        {
            string baseName = ti[..^1].TrimEnd();
            if (LooksLikeXObjectTypeName(baseName))
            {
                return new SchemaOpEmit(
                    Kind: SchemaOpKind.Object,
                    PropertyName: p.Name,
                    StrideBytes: 0,
                    ArrayDim: 0,
                    NestedSchemaSymbol: null);
            }
            return null;
        }

        // Nested FStruct (the "F"-prefixed user-defined struct case).
        //
        // Per spec §7.4.1 emit rules: FStructProperty (inline) ->
        // Struct opcode with NestedSchema set to the nested struct's
        // RefSchema symbol. We emit the symbol name by convention; the
        // resolver-driven path (Stage-B) substitutes the precise schema
        // reference once the nested struct is itself emitted.
        //
        // The convention is: a TypeIdentifier starting with 'F' is
        // assumed an FStruct (XCore-4b naming convention for reflected
        // struct types). The fully-qualified resolver path would
        // narrow further; until then the heuristic is conservative.
        //
        // Non-ref-carrying nested structs (FVector, FRotator) MUST be
        // skipped per spec §7.4.1 ("non-ref nested structs don't emit
        // any opcode at all"). The conservative heuristic emits a
        // Struct opcode pointing at a schema symbol that the linker
        // will resolve to the nested struct's schema; if the nested
        // schema is itself empty (RefSchema == nullptr), the runtime
        // walker no-ops on the Struct opcode (the nested walker
        // observes NumOps==0). The conservative-emit approach is
        // shown valid by spec §7.4.1 emit-time validation: NestedSchema
        // is only required to be non-null for opcodes whose runtime
        // dispatch depends on recursion.
        //
        // Phase 5.g' MVP refinement: the resolver wires the actual
        // FStruct kind (and whether it has any GC refs) at integration
        // time. Until then we suppress the Struct opcode for nested
        // structs whose name matches the pure-POD-math family
        // (FVector / FRotator / FTransform / FMatrix / FQuat / FColor)
        // so the typical case (XCLASS with FVector member) does NOT
        // emit a Struct opcode against a non-ref struct.
        if (LooksLikeFStructTypeName(ti) && !IsKnownNonRefStruct(ti))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.Struct,
                PropertyName: p.Name,
                StrideBytes: 0,
                ArrayDim: 0,
                NestedSchemaSymbol: "&" + SchemaSymbol(ti));
        }

        // Primitives + non-ref types: no opcode emitted.
        return null;
    }

    /// <summary>
    /// Classify the inner-type of a TArray for the schema emit. Returns
    /// the appropriate Array* opcode kind or null when the inner type
    /// carries no XObject refs.
    /// </summary>
    private static SchemaOpEmit? ClassifyArrayInner(string propertyName, string inner)
    {
        inner = inner.Trim();
        if (inner.Length == 0)
        {
            return null;
        }

        // TArray<XPtr<T>> -> ArrayOfObject (stride 8).
        if (StartsWithGeneric(inner, "XPtr") ||
            StartsWithGeneric(inner, "XStrongPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.ArrayOfObject,
                PropertyName: propertyName,
                StrideBytes: 8,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TArray<XWeakPtr<T>> -> StridedArrayOfObject (stride 8).
        if (StartsWithGeneric(inner, "XWeakPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.StridedArrayOfObject,
                PropertyName: propertyName,
                StrideBytes: 8,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TArray<XSoftPtr<T>> -> StridedArrayOfObject (stride 16; soft
        // refs carry a path-based handle slightly larger than the
        // weak-ref shape).
        if (StartsWithGeneric(inner, "XSoftPtr"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.StridedArrayOfObject,
                PropertyName: propertyName,
                StrideBytes: 16,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TArray<TInterface<I>> -> StridedArrayOfObject.
        if (StartsWithGeneric(inner, "TInterface"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.StridedArrayOfObject,
                PropertyName: propertyName,
                StrideBytes: 8,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TArray<TDelegate<S>> -> StridedArrayOfObject.
        if (StartsWithGeneric(inner, "TDelegate") ||
            StartsWithGeneric(inner, "TMulticastDelegate") ||
            StartsWithGeneric(inner, "TSparseMulticastDelegate"))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.StridedArrayOfObject,
                PropertyName: propertyName,
                StrideBytes: 16,
                ArrayDim: 0,
                NestedSchemaSymbol: null);
        }

        // TArray<T*> where T* looks like a raw XObject pointer.
        if (inner.EndsWith("*", StringComparison.Ordinal))
        {
            string baseName = inner[..^1].TrimEnd();
            if (LooksLikeXObjectTypeName(baseName))
            {
                return new SchemaOpEmit(
                    Kind: SchemaOpKind.ArrayOfObject,
                    PropertyName: propertyName,
                    StrideBytes: 8,
                    ArrayDim: 0,
                    NestedSchemaSymbol: null);
            }
            return null;
        }

        // TArray<FStruct> -> ArrayOfStruct (recurse via nested schema).
        if (LooksLikeFStructTypeName(inner) && !IsKnownNonRefStruct(inner))
        {
            return new SchemaOpEmit(
                Kind: SchemaOpKind.ArrayOfStruct,
                PropertyName: propertyName,
                StrideBytes: 0,  // Stage-B fills with sizeof(InnerStruct)
                ArrayDim: 0,
                NestedSchemaSymbol: "&" + SchemaSymbol(inner));
        }

        // TArray<primitive> -> skip (no GC refs).
        return null;
    }

    // ---------- Type-identifier classification helpers ----------

    /// <summary>
    /// True if a TypeIdentifier names a strong / weak / soft XObject
    /// reference type. Used by container-inner classification.
    /// </summary>
    private static bool IsObjectRefType(string typeId)
    {
        typeId = typeId.Trim();
        if (typeId.Length == 0) { return false; }

        if (StartsWithGeneric(typeId, "XPtr")) { return true; }
        if (StartsWithGeneric(typeId, "XStrongPtr")) { return true; }
        if (StartsWithGeneric(typeId, "XWeakPtr")) { return true; }
        if (StartsWithGeneric(typeId, "XSoftPtr")) { return true; }
        if (StartsWithGeneric(typeId, "TInterface")) { return true; }
        if (StartsWithGeneric(typeId, "TSubclassOf")) { return true; }
        if (StartsWithGeneric(typeId, "TSoftClassPtr")) { return true; }
        if (StartsWithGeneric(typeId, "TDelegate")) { return true; }
        if (StartsWithGeneric(typeId, "TMulticastDelegate")) { return true; }
        if (StartsWithGeneric(typeId, "TSparseMulticastDelegate")) { return true; }

        if (typeId.EndsWith("*", StringComparison.Ordinal))
        {
            return LooksLikeXObjectTypeName(typeId[..^1].TrimEnd());
        }
        return false;
    }

    private static bool StartsWithGeneric(string typeId, string generic)
    {
        if (string.IsNullOrEmpty(typeId) || string.IsNullOrEmpty(generic))
        {
            return false;
        }
        // Match "TArray<" / "XPtr<" pattern at the start, ignoring
        // any leading qualification (e.g. "::XCore::TArray<").
        int idx = typeId.LastIndexOf("::", StringComparison.Ordinal);
        string unqual = idx < 0 ? typeId : typeId[(idx + 2)..];
        unqual = unqual.TrimStart();
        return unqual.StartsWith(generic + "<", StringComparison.Ordinal);
    }

    private static string ExtractFirstGenericArgument(string typeId, string generic)
    {
        int open = typeId.IndexOf('<');
        if (open < 0) { return string.Empty; }
        int close = FindMatchingAngle(typeId, open);
        if (close < 0) { return string.Empty; }
        // Find the first comma at depth 0 inside the generic args.
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            char c = typeId[i];
            if (c == '<') { depth++; }
            else if (c == '>') { depth--; }
            else if (c == ',' && depth == 0)
            {
                return typeId[(open + 1)..i].Trim();
            }
        }
        return typeId[(open + 1)..close].Trim();
    }

    private static (string First, string Second) ExtractFirstTwoGenericArguments(string typeId, string generic)
    {
        int open = typeId.IndexOf('<');
        if (open < 0) { return (string.Empty, string.Empty); }
        int close = FindMatchingAngle(typeId, open);
        if (close < 0) { return (string.Empty, string.Empty); }
        int depth = 0;
        int commaIdx = -1;
        for (int i = open + 1; i < close; i++)
        {
            char c = typeId[i];
            if (c == '<') { depth++; }
            else if (c == '>') { depth--; }
            else if (c == ',' && depth == 0)
            {
                commaIdx = i;
                break;
            }
        }
        if (commaIdx < 0)
        {
            return (typeId[(open + 1)..close].Trim(), string.Empty);
        }
        string first = typeId[(open + 1)..commaIdx].Trim();
        string second = typeId[(commaIdx + 1)..close].Trim();
        return (first, second);
    }

    private static int FindMatchingAngle(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') { depth++; }
            else if (c == '>')
            {
                depth--;
                if (depth == 0) { return i; }
            }
        }
        return -1;
    }

    /// <summary>
    /// True if a bare identifier (no trailing "*", no template
    /// brackets) looks like an XObject-derived type. The naming
    /// convention is "X"-prefixed (XActor, XComponent, etc.) plus a
    /// few well-known engine types.
    /// </summary>
    private static bool LooksLikeXObjectTypeName(string typeId)
    {
        if (string.IsNullOrEmpty(typeId)) { return false; }
        // Strip "::"-qualifier prefix.
        int idx = typeId.LastIndexOf("::", StringComparison.Ordinal);
        string unqual = idx < 0 ? typeId : typeId[(idx + 2)..];
        unqual = unqual.Trim();
        if (unqual.Length == 0) { return false; }

        // XPact naming: 'A' (XActor-derived) and 'X' (XObject-derived
        // engine class). 'U' is the UE-style convention historically.
        char first = unqual[0];
        return first == 'X' || first == 'A' || first == 'U';
    }

    /// <summary>
    /// True if a bare identifier (no trailing "*") looks like a
    /// reflected struct type ("F"-prefixed XCore-4b convention).
    /// </summary>
    private static bool LooksLikeFStructTypeName(string typeId)
    {
        if (string.IsNullOrEmpty(typeId)) { return false; }
        int idx = typeId.LastIndexOf("::", StringComparison.Ordinal);
        string unqual = idx < 0 ? typeId : typeId[(idx + 2)..];
        unqual = unqual.Trim();
        if (unqual.Length == 0) { return false; }
        return unqual[0] == 'F';
    }

    /// <summary>
    /// Known pure-POD math structs that carry no XObject references.
    /// The emit skips them to avoid emitting Struct opcodes against
    /// non-ref nested structs (per spec §7.4.1: "non-ref nested
    /// structs don't emit any opcode at all").
    /// </summary>
    private static bool IsKnownNonRefStruct(string typeId)
    {
        int idx = typeId.LastIndexOf("::", StringComparison.Ordinal);
        string unqual = idx < 0 ? typeId : typeId[(idx + 2)..];
        unqual = unqual.Trim();
        return unqual is
            "FVector" or "FVector2D" or "FVector4" or
            "FRotator" or
            "FTransform" or
            "FMatrix" or
            "FQuat" or
            "FColor" or "FLinearColor" or
            "FPlane" or
            "FBox" or "FBox2D" or
            "FSphere" or
            "FIntPoint" or "FIntVector" or "FIntRect" or
            "FName" or
            "FString" or "FText" or
            "FGuid" or
            "FTimespan" or "FDateTime";
    }

    private static string SanitizeForCSymbol(string s)
    {
        StringBuilder sb = new(s.Length);
        foreach (char c in s)
        {
            bool isAlnum =
                (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9');
            sb.Append(isAlnum ? c : '_');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Schema-vector opcode kinds (mirror of the runtime
/// <c>EXObjectRefSchemaOp</c> enumeration; XCoreXObject Rev 4 §7.4
/// 22 active opcodes per Rev 3 FIX-H-R2-3).
/// </summary>
/// <remarks>
/// The values match the runtime enum values byte-for-byte; this is
/// the load-bearing invariant for the emitter writing the byte
/// representation into the .gen.cpp .rodata array.
/// </remarks>
public enum SchemaOpKind : byte
{
    /// <summary>End-of-schema sentinel. XHT-emit appends one Terminator at the tail of every schema.</summary>
    Terminator              = 0,

    /// <summary>Single FObjectProperty slot (raw XObject* / XPtr).</summary>
    Object                  = 1,

    /// <summary>Single FWeakObjectProperty slot.</summary>
    WeakObject              = 2,

    /// <summary>Single FSoftObjectProperty slot.</summary>
    SoftObject              = 3,

    /// <summary>TArray of XPtr / raw XObject* (stride 8).</summary>
    ArrayOfObject           = 4,

    /// <summary>TArray of FStruct (recurse via NestedSchema).</summary>
    ArrayOfStruct           = 5,

    /// <summary>Strided array (weak / soft / interface / delegate variants).</summary>
    StridedArrayOfObject    = 6,

    /// <summary>TMap with XObject ref in key or value (or both).</summary>
    MapOfObject_KeyValue    = 7,

    /// <summary>TSet of XPtr / raw XObject*.</summary>
    SetOfObject             = 8,

    /// <summary>Inline FStruct member (recurse via NestedSchema).</summary>
    Struct                  = 9,

    /// <summary>FFieldPathProperty (post-MVP).</summary>
    FieldPath               = 10,

    /// <summary>TArray of FFieldPath (post-MVP).</summary>
    FieldPathArray          = 11,

    /// <summary>TOptional&lt;XPtr&gt;: Value slot at Offset + 8.</summary>
    OptionalObject          = 12,

    /// <summary>Variant value (FDynamicallyTypedValueProperty; post-MVP).</summary>
    DynamicallyTypedValue   = 13,

    /// <summary>FClass-level AddReferencedObjects callback.</summary>
    ARO                     = 14,

    /// <summary>Deprecated legacy ARO slot.</summary>
    SlowARO                 = 15,

    /// <summary>Per-member AddReferencedObjects.</summary>
    MemberARO               = 16,

    /// <summary>FInterfaceProperty (single slot).</summary>
    Interface               = 17,

    /// <summary>FClassProperty (single FClass* meta-class ref).</summary>
    ClassProperty           = 18,

    /// <summary>FSoftClassProperty (single soft FClass* ref).</summary>
    SoftClass               = 19,

    /// <summary>FDelegateProperty (single bound UObject target).</summary>
    Delegate                = 20,

    /// <summary>FMulticastInlineDelegateProperty.</summary>
    MulticastInlineDelegate = 21,

    /// <summary>FMulticastSparseDelegateProperty.</summary>
    MulticastSparseDelegate = 22,
}

/// <summary>
/// One opcode-emit record produced by the classifier (one row of the
/// emitted Ops array per spec §7.4.1 emit-rules table).
/// </summary>
/// <param name="Kind">Opcode kind (mirrors the runtime <c>EXObjectRefSchemaOp</c>).</param>
/// <param name="PropertyName">The property whose <c>offsetof(...)</c> drives the opcode's Offset field.</param>
/// <param name="StrideBytes">Element stride for container opcodes; 0 for single-slot opcodes.</param>
/// <param name="ArrayDim">In-place array dimension (e.g. <c>XPROPERTY(MyType[N])</c>); 0 otherwise.</param>
/// <param name="NestedSchemaSymbol">C++ identifier of the nested schema (<c>"&amp;s_FFoo_RefSchema"</c>); null for non-recursing opcodes.</param>
public readonly record struct SchemaOpEmit(
    SchemaOpKind Kind,
    string PropertyName,
    int StrideBytes,
    int ArrayDim,
    string? NestedSchemaSymbol);
