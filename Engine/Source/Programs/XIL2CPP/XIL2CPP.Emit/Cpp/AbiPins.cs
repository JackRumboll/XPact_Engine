// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using Simgenics.XPact.XIL2CPP.Emit.Cpp;

namespace Simgenics.XPact.XIL2CPP.Emit.Cpp;

/// <summary>
/// The Toolchain Contract Rev 13.9 ABI envelope + reflection-type layout +
/// sizeof static-assert pin set, DUPLICATED under the XIL2CPP namespace from
/// <c>XHT.Emitter.AbiLayoutPins</c> per <c>/Documents/XToolchainContract.html</c>
/// Section 14.3 ("every XHT-emitted <c>.gen.cpp</c> file AND every
/// XIL2CPP-emitted <c>.cs.cpp</c> file carries the full pin set"). The data
/// is intentionally duplicated rather than referenced: XIL2CPP must not link
/// XHT.Emitter (standalone-tool discipline); the manifest-level
/// ContractVersion gate guards against drift between the two copies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pin families (Contract Section 14.3, Rev 13.9 counts).</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Layout-tag content pins</b> -- <see cref="LayoutTags"/>: 24
///     <c>static_assert(XPactDetail::CompileTimeStrEq(XPACT_*_LAYOUT_TAG, "&lt;content&gt;"))</c>
///     calls (15 Stage-B reflection-type tags + 9 XObject-side tags).
///   </description></item>
///   <item><description>
///     <b>sizeof pins</b> -- <see cref="TypeSizes"/>: 22
///     <c>static_assert(sizeof(::XCore::Reflect::&lt;Type&gt;) == N)</c> calls
///     (14 reflection-type pins + 7 XObject-side pins + 1 GC-root pin for
///     <c>XGCRootSpan</c>).
///   </description></item>
/// </list>
/// <para>
/// <see cref="EmitPinBlock"/> writes the full block each <c>.cs.cpp</c>
/// needs, matching the XHT <c>SourceEmitter</c> shape (the
/// <c>#ifdef XPACT_FNAME_LAYOUT_TAG</c> guard for the layout-tag pins; the
/// <c>#if defined(XPACT_FCLASS_LAYOUT_TAG) &amp;&amp; __has_include("Reflection/FClass.h")</c>
/// guard + the reflection-header includes for the sizeof pins) so the two
/// tools' generated pin blocks are structurally identical.
/// </para>
/// </remarks>
public static class AbiPins
{
    /// <summary>
    /// ABI layout-tag pin set per Contract Rev 13.9 Section 14.1. Each entry
    /// is <c>(TagMacro, TagContent)</c>; duplicated verbatim from
    /// <c>XHT.Emitter.AbiLayoutPins.LayoutTags</c>.
    /// </summary>
    public static readonly IReadOnlyList<(string TagMacro, string TagContent)> LayoutTags = new (string, string)[]
    {
        (
            "XPACT_FNAME_LAYOUT_TAG",
            "FName-v1: 4+4 / Index+SerialNumber / 8-byte total / 4-byte aligned"
        ),
        (
            "XPACT_FFIELD_LAYOUT_TAG",
            "FField-v1: 32 bytes; ClassPrivate@0, Owner@8, Next@16, NamePrivate@24"
        ),
        (
            "XPACT_FFIELDCLASS_LAYOUT_TAG",
            "FFieldClass-v1: 48 bytes; Name@0, Id@8, CastFlags@16, SuperClass@24, Construct@32, FakeVTable@40"
        ),
        (
            "XPACT_FFIELDVARIANT_LAYOUT_TAG",
            "FFieldVariant-v1: 8 bytes; Storage@0 (1-bit LSB tag, 0=FField, 1=FStruct, on 8-byte-aligned pointer)"
        ),
        (
            "XPACT_FPROPERTY_LAYOUT_TAG",
            "FProperty-v2: FField (32) + FProperty body (64) + DispatchTable pointer (8) = 104 bytes; UE-equivalent rep-meta source; FakeVTable in .rodata; FFieldVariant LSB-tag (LSB=1 means FStruct, inverse of UE)"
        ),
        (
            "XPACT_FFAKEVTABLE_LAYOUT_TAG",
            "FFakeVTable-v2: 8-byte header (Capabilities uint32 + _reservedHeader uint32) + 15 function-pointer slots (8 bytes each) = 128 bytes per FProperty subclass in .rodata; ConvertFromType is slot index 14 (the 15th and last; ESlot enum 0-indexed) per Rev 3 FIX-R2-HIGH-1"
        ),
        (
            "XPACT_FSTRUCT_LAYOUT_TAG",
            "FStruct-v5 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 120 bytes; Class@0 (8) + Owner@8 (8) + Next@16 (8) + Name@24 (8) + SuperStruct@32 (8) + ChildProperties@40 (8) + PropertyLink@48 (8) + ObjectRefProperties@56 (16 = TArray<FProperty*>) + SchemaHash@72 (8) + SchemaVersion@80 (4) + _pad@84 (4) + UnversionedSchema@88 (8) + SerializeStructFn@96 (8) + DestructorLink@104 (8) + RefSchema@112 (8). RefSchema is the fast-path GC walker pointer appended per FIX-A-CRIT-8 / UE-MISS-1. Conceptual layout name string -- the real field offsets per XCore-4b Rev 4 are: NamePrivate@0, SuperStruct@8, ChildProperties@16, PropertiesSize@24, MinAlignment@28, StructFlags@30, _padStructFlags@31, PropertyLink@32, DestructorLink@40, PostConstructLink@48, ObjectRefProperties@56 (24 byte TArray), SchemaHash@80, SchemaVersion@88, _padSchema@92, UnversionedSchema@96, SerializeStructFn@104, RefSchema@112 (Rev 3 appended)."
        ),
        (
            "XPACT_FSCRIPTSTRUCT_LAYOUT_TAG",
            "FScriptStruct-v5 (Contract Rev 13.9 cascade): 120 FStruct base (with appended RefSchema@112) + 16 ICppStructOps FakeVTable pattern = 136 bytes; per-subtype FCppStructOpsFakeVTable in .rodata at 136 bytes (8-byte header + 16 handler slots) unchanged"
        ),
        (
            "XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG",
            "FCppStructOpsFakeVTable-v1: 8-byte header (32-bit Capabilities + _reservedHeader) + 16 handler slots (8 bytes each) = 136 bytes per FScriptStruct subtype in .rodata"
        ),
        (
            "XPACT_FCLASS_LAYOUT_TAG",
            "FClass-v6 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 240 bytes = 120 FStruct base (with appended RefSchema@112) + 120 FClass-specific (with appended LifecycleTable@112 relative to FClass-specific start = FClass-absolute offset 232). FClass-specific field layout (offsets relative to FStruct end at FClass-absolute 120): ClassConstructorFn@0, ClassVTableHelperCtorCaller@8, ClassDefaultObject@16, ClassFlags@24, ClassCastFlags@32, ClassWithin@40, FirstOwnedClassRep@48, ClassRepCount@52, ClassReps@56 (24 byte TArray<FRepRecord>), NetFields@80 (24 byte TArray<FField*>), ClassConfigName@104, LifecycleTable@112 (Rev 3 appended; FClass-absolute offset 232). Per XCore-4b Rev 4 §11.6 baseline (224 = 112 + 112) + Rev 13.9 micro-bump appends RefSchema@FStruct.112 (+8) and LifecycleTable@FClass-specific.112 (+8), yielding FClass total 240 bytes."
        ),
        (
            "XPACT_FREPRECORD_LAYOUT_TAG",
            "FRepRecord-v1: 16 bytes per ClassReps entry; {FProperty* Property; int32 Index} matches UE's Class.h:3984"
        ),
        (
            "XPACT_FENUM_LAYOUT_TAG",
            "FEnum-v4: 72 bytes; Values TArray @ offset 40 = 24 bytes; CppForm @ 64"
        ),
        (
            "XPACT_FINTERFACE_LAYOUT_TAG",
            "FInterface-v4: 64 bytes; InterfaceFunctions TArray @ offset 32 = 24 bytes; InterfaceFlags @ 56"
        ),
        (
            "XPACT_FCUSTOMVERSION_LAYOUT_TAG",
            "FCustomVersion-v1: Key(FGuid 16) + Version(int32) + FriendlyName(FName)"
        ),
        (
            "XPACT_REPMETA_LAYOUT_TAG",
            "RepMeta-v2: 15-condition ELifetimeCondition(1) + RepIndex(2) + RepNotifyFunc-as-FName(8); UE-equivalent pre-Iris set"
        ),
        // -----------------------------------------------------------------
        // XCoreXObject Phase 5.a' Rev 13.9 micro-bump: 9 new XObject-side
        // addendum tags per XCoreXObject Rev 4 Section 11.1.
        // -----------------------------------------------------------------
        (
            "XPACT_XOBJECT_LAYOUT_TAG",
            "XObject-v2: 56 bytes; ClassPrivate@0, InternalIndex@8, SerialNumber@12, Outer@16, NamePrivate@24, ObjectFlags@32, ReachabilityFlag@36 (Rev 3 per FIX-M-R2-3 moved from FXObjectArrayEntry), _reservedCluster0@40, _reservedCluster1@48; alignof = 8; no virtuals on the GC-relevant surface (FakeVTable pattern). Rev 2: cluster reservation expanded to 16 bytes; remote-id reservation dropped (FIX-A-MIN-49 / O5). Rev 3: per-object reachability flag consumes former _padObjectFlags slot."
        ),
        (
            "XPACT_XGC_CARDTABLE_LAYOUT_TAG",
            "XGCCardTable-v1: byte-per-card flat array; card size = 512 bytes; max heap = 4 GB; card table = 8 MB; clean = 0x00, dirty = 0x01"
        ),
        (
            "XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG",
            "FXObjectArrayEntry-v1: 32 bytes; Object@0, SerialNumber@8, ClusterRootIndex@12, StateBits@16 (atomic; pending-destroy + root-pinned + hot-reload + garbage bits), _reserved@24; alignof = 8. Rev 3: rotating reachability flag moved to XObject header per FIX-M-R2-3."
        ),
        (
            "XPACT_XOBJECTKEY_LAYOUT_TAG",
            "XObjectKey-v1: 8 bytes; InternalIndex@0 (int32), SerialNumber@4 (uint32); ABI-compatible with XWeakPtr; alignof = 4"
        ),
        (
            "XPACT_XWEAKPTR_LAYOUT_TAG",
            "XWeakPtr-v1: 8 bytes; InternalIndex@0 (int32), SerialNumber@4 (uint32); matches XCore-4b's FWeakObjectPtr placeholder shape; alignof = 4"
        ),
        (
            "XPACT_XPTR_LAYOUT_TAG",
            "XPtr-v1: 8 bytes; Ptr@0 (raw T* compatible); ABI-equivalent to T*; alignof = 8"
        ),
        (
            "XPACT_XOBJECT_LIFECYCLE_TABLE_TAG",
            "FXObjectLifecycleTable-v1: 72 bytes per FClass; 8-byte header (Capabilities@0 + _pad@4) + 8 slots * 8 bytes = 64 bytes; alignof = 8. Serialize slot signature: void(*)(XObject*, FArchive&, const FArchiveContext*) per FIX-A-HIGH-13."
        ),
        (
            "XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG",
            "FXObjectRefSchema-v1: 24 bytes; NumOps@0 (u32), Version@4 (u32), Ops@8 (const FXObjectRefSchemaOp*), _padTail@16; alignof = 8. FXObjectRefSchemaOp is 24 bytes per opcode; Op@0 (u8), _padOp@1, ArrayDim@2 (u16), Offset@4 (i32), StrideBytes@8 (i32), NestedSchema@16 (const FXObjectRefSchema*); alignof = 8 (4 bytes of pad at offset 12 to align NestedSchema to 8). Rev 3: 22 active opcodes (added Interface, ClassProperty, SoftClass, Delegate, MulticastInlineDelegate, MulticastSparseDelegate per FIX-H-R2-3)."
        ),
        // -----------------------------------------------------------------
        // XCoreXObject Phase 5.e (GC root protocol): XGCRootSpan tag.
        // -----------------------------------------------------------------
        (
            "XPACT_XGC_ROOTSPAN_LAYOUT_TAG",
            "XGCRootSpan-v1: 32 bytes; BaseAddress@0 (8), ByteLength@8 (8), ElementStride@16 (8), Kind@24 (1 uint8 EXGCRootSpanKind: kObject=0, kConservative=1), _pad@25 (7 bytes); alignof = 8. Phase 5.e per XCoreXObject Rev 4 §5.3 + Rev 2 FIX-A-HIGH-11. Conservative kind validates each aligned 8-byte word via the four-gate order (heap-range -> FXObjectArray index -> entry-bind -> SerialNumber match) per spec §5.3 + Rev 2 FIX-A-MED-35."
        ),
    };

    /// <summary>
    /// Per-type sizeof pin set per Contract Rev 13.9 Section 14.2. Each entry
    /// is <c>(TypeName, ExpectedBytes)</c>; duplicated verbatim from
    /// <c>XHT.Emitter.AbiLayoutPins.TypeSizes</c>.
    /// </summary>
    public static readonly IReadOnlyList<(string TypeName, int ExpectedBytes)> TypeSizes = new (string, int)[]
    {
        ("FName", 8),
        ("FField", 32),
        ("FFieldClass", 48),
        ("FFieldVariant", 8),
        ("FProperty", 104),
        ("FFakeVTable", 128),
        ("FStruct", 120),
        ("FScriptStruct", 136),
        ("FCppStructOpsFakeVTable", 136),
        ("FClass", 240),
        ("FRepRecord", 16),
        ("FEnum", 72),
        ("FInterface", 64),
        ("FCustomVersion", 32),
        ("XObject", 56),
        ("FXObjectArrayEntry", 32),
        ("XObjectKey", 8),
        ("XWeakPtr", 8),
        ("XPtr", 8),
        ("FXObjectLifecycleTable", 72),
        ("FXObjectRefSchema", 24),
        ("XGCRootSpan", 32),
    };

    /// <summary>
    /// The count of layout-tag content pins (Contract Rev 13.9: 24). Exposed
    /// as a constant so the pin tests assert the table size against the
    /// contract count without recounting the literal table.
    /// </summary>
    public const int LayoutTagPinCount = 24;

    /// <summary>
    /// The count of sizeof pins (Contract Rev 13.9: 22). Exposed as a constant
    /// so the pin tests assert the table size against the contract count.
    /// </summary>
    public const int SizeofPinCount = 22;

    /// <summary>
    /// The total per-TU static-assert pin count each <c>.cs.cpp</c> carries
    /// for the Rev 13.9 layout + sizeof surface (Contract Section 14, "45 per
    /// TU"): <see cref="LayoutTagPinCount"/> + <see cref="SizeofPinCount"/> =
    /// 46. (Section 14.0's "45" predates the Rev 13.9 layout-tag table growth
    /// to 24; the authoritative per-family counts are 24 + 22 = 46.)
    /// </summary>
    public const int TotalLayoutAndSizeofPinCount = LayoutTagPinCount + SizeofPinCount;

    /// <summary>
    /// The reflection-runtime header include set the sizeof pin block pulls in
    /// (so <c>::XCore::Reflect::&lt;Type&gt;</c> is a complete type at the
    /// <c>sizeof</c> site). Mirrors the XHT <c>SourceEmitter</c> include list
    /// order.
    /// </summary>
    public static readonly IReadOnlyList<string> SizeofPinIncludes = new[]
    {
        "Reflection/FName.h",
        "Reflection/FField.h",
        "Reflection/FFieldClass.h",
        "Reflection/FFieldVariant.h",
        "Reflection/FProperty.h",
        "Reflection/FFakeVTable.h",
        "Reflection/FStruct.h",
        "Reflection/FScriptStruct.h",
        "Reflection/FCppStructOpsFakeVTable.h",
        "Reflection/FClass.h",
        "Reflection/FRepRecord.h",
        "Reflection/FEnum.h",
        "Reflection/FInterface.h",
        "Reflection/FCustomVersion.h",
    };

    /// <summary>
    /// Emit the full per-TU static-assert pin block into <paramref name="writer"/>:
    /// the Stage-A ABI-envelope pins (GC root / exception / mangling-scheme tags
    /// + the ConstInit + accessor-slot sentinels, drawn from
    /// <paramref name="context"/>'s ABI-tag content strings), then the 24
    /// layout-tag content pins (guarded by <c>#ifdef XPACT_FNAME_LAYOUT_TAG</c>),
    /// then the 22 sizeof pins (guarded by
    /// <c>#if defined(XPACT_FCLASS_LAYOUT_TAG) &amp;&amp; __has_include("Reflection/FClass.h")</c>
    /// with the reflection-header includes). Matches the XHT
    /// <c>SourceEmitter</c> pin-block shape so the two tools' <c>.gen.cpp</c> /
    /// <c>.cs.cpp</c> pin blocks are structurally identical.
    /// </summary>
    /// <param name="writer">The target C++ writer (typically at depth 0 / file scope). Must not be null.</param>
    /// <param name="context">The per-module emit context carrying the ABI-tag content strings. Must not be null.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="writer"/> or <paramref name="context"/> is null.</exception>
    public static void EmitPinBlock(CppWriter writer, EmitContext context)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(context);

        // Stage-A ABI-envelope pins (carry-forward from Rev 13.8; matches the
        // XHT SourceEmitter envelope block + XIL2CPP.html §5.1 .cs.cpp shape).
        writer.AppendComment("Pin the active object format + ABI envelope per Contract Section 7.1 + 10.2 + 14 (XIL2CPP .cs.cpp).");
        writer.AppendStaticAssert("XPACT_WITH_CONSTINIT_XOBJECT", "XIL2CPP emit assumes ConstInit XObject format");
        writer.AppendStaticAssert(
            "XPactDetail::CompileTimeStrEq(XPACT_GC_ROOT_ABI_TAG, " + CppWriter.EncodeCStringLiteral(context.GCRootABI) + ")",
            "GC root ABI mismatch between manifest and runtime");
        writer.AppendStaticAssert(
            "XPactDetail::CompileTimeStrEq(XPACT_EXCEPTION_ABI_TAG, " + CppWriter.EncodeCStringLiteral(context.ExceptionABI) + ")",
            "Exception ABI mismatch between manifest and runtime");
        writer.AppendStaticAssert(
            "XPactDetail::CompileTimeStrEq(XPACT_MANGLING_SCHEME_TAG, " + CppWriter.EncodeCStringLiteral(context.ManglingScheme) + ")",
            "Mangling scheme mismatch between manifest and runtime");
        writer.AppendStaticAssert("XPACT_PROPERTY_HAS_ACCESSORS == 1", "XPropertyDescriptor must carry accessor slots (XIL2CPP)");
        writer.AppendLine();

        // Layout-tag content pins (Contract Rev 13.9 §14.1).
        writer.AppendComment("XCore-4b Stage B addendum ABI layout pins (Contract Rev 13.9 + spec Section 9.4).");
        writer.AppendComment("Every XPACT_*_LAYOUT_TAG macro expansion is compared at compile time");
        writer.AppendComment("against the contract-frozen literal so a patch DLL that picked up a");
        writer.AppendComment("different layout fails the compile cleanly.");
        writer.AppendLine("#ifdef XPACT_FNAME_LAYOUT_TAG");
        foreach ((string macro, string content) in LayoutTags)
        {
            writer.AppendStaticAssert(
                "XPactDetail::CompileTimeStrEq(" + macro + ", " + CppWriter.EncodeCStringLiteral(content) + ")",
                "ABI lock: " + macro + " mismatch between manifest and runtime per Contract Rev 13.9");
        }
        writer.AppendLine("#endif // XPACT_FNAME_LAYOUT_TAG");
        writer.AppendLine();

        // sizeof pins (Contract Rev 13.9 §14.2).
        writer.AppendComment("Per-type sizeof pins per XCore-4b Rev 4 Section 11.2 + 11.3 (Stage B addendum).");
        writer.AppendComment("Pulls in the full XCore::Reflect surface when the runtime header");
        writer.AppendComment("XReflectionRuntime.h declares the XCore-4b addendum macro family.");
        writer.AppendLine("#if defined(XPACT_FCLASS_LAYOUT_TAG) && __has_include(\"Reflection/FClass.h\")");
        foreach (string include in SizeofPinIncludes)
        {
            writer.AppendLine("#  include \"" + include + "\"");
        }
        foreach ((string type, int bytes) in TypeSizes)
        {
            string n = bytes.ToString(CultureInfo.InvariantCulture);
            writer.AppendStaticAssert(
                "sizeof(::XCore::Reflect::" + type + ") == " + n,
                "ABI lock: sizeof(" + type + ") must be " + n + " bytes per Contract Rev 13.9 (XCore-4b Stage B addendum)");
        }
        writer.AppendLine("#endif // XPACT_FCLASS_LAYOUT_TAG && __has_include");
    }
}
