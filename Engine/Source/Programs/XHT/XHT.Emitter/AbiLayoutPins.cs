// Copyright Simgenics. All Rights Reserved.

using System.Collections.Generic;

namespace Simgenics.XPact.XHT.Emitter;

/// <summary>
/// XCore-4b Stage B addendum (Toolchain Contract Rev 13.8) + XCoreXObject
/// Phase 5.a' Rev 13.9 micro-bump ABI layout pin surface. Mirrors XBT's
/// <c>ContractSurface.AbiLayoutTags</c> + <c>ContractSurface.AbiTypeSizes</c>
/// tables; the two sides MUST agree byte-for-byte or the manifest-level
/// <c>ContractVersion</c> rotation will surface diagnostic <c>XHT002</c>
/// at read time.
/// </summary>
/// <remarks>
/// <para>
/// Per XCore-4b Rev 4 Section 9.4 ("Per-DLL static_assert pins") +
/// Section 11.6 ("ContractVersion bump"): every XHT-emitted
/// <c>.gen.cpp</c> file carries
/// <c>static_assert(XPactDetail::CompileTimeStrEq(...))</c> calls that
/// compare the runtime header's <c>XPACT_*_LAYOUT_TAG</c> macro
/// expansion against the contract-frozen string. A patch DLL compiled
/// against a different ABI fails to link with a clean compile-time
/// error.
/// </para>
/// <para>
/// The companion <see cref="TypeSizes"/> table emits
/// <c>static_assert(sizeof(...) == N)</c> calls so a developer who
/// adds a member to (e.g.) <c>FProperty.h</c> without bumping Contract
/// Rev 13.8 sees the compile-time mismatch at every <c>.gen.cpp</c> TU
/// in the project. The runtime headers themselves also carry these
/// asserts, but the per-TU repetition catches "the runtime header
/// changed but the consumer module wasn't recompiled" (Live Coding
/// patch DLL scenario).
/// </para>
/// <para>
/// These two tables are duplicated in
/// <c>XBT.Manifest/ContractSurface.cs</c>; the duplication is
/// intentional (XHT does not link XBT.Manifest at runtime per XHT.html
/// Section 25.2 item 1 standalone-tool discipline). The
/// <c>ContractVersion</c> mismatch detection (XbtManifestReader)
/// guards against drift.
/// </para>
/// </remarks>
public static class AbiLayoutPins
{
    /// <summary>
    /// ABI layout-tag pin set per XCore-4b Rev 4 Section 11.6. Each
    /// entry is (TagMacro, TagContent); the runtime header
    /// <c>XReflectionRuntime.h</c> defines each <c>TagMacro</c> as a
    /// <c>#define</c> producing the literal <c>TagContent</c> string.
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
            // XCore-4b Subagent A FIX-A7: tag wording corrected to reflect
            // actual field decomposition (FField 32 + FProperty body 64 +
            // DispatchTable 8 = 104). Prior "96 base + 8 DispatchTable"
            // misframed the 96 as monolithic "base". Three sources
            // (XReflectionRuntime.h, ContractSurface.cs, this file) MUST
            // stay byte-identical.
            "XPACT_FPROPERTY_LAYOUT_TAG",
            "FProperty-v2: FField (32) + FProperty body (64) + DispatchTable pointer (8) = 104 bytes; UE-equivalent rep-meta source; FakeVTable in .rodata; FFieldVariant LSB-tag (LSB=1 means FStruct, inverse of UE)"
        ),
        (
            "XPACT_FFAKEVTABLE_LAYOUT_TAG",
            "FFakeVTable-v2: 8-byte header (Capabilities uint32 + _reservedHeader uint32) + 15 function-pointer slots (8 bytes each) = 128 bytes per FProperty subclass in .rodata; ConvertFromType is slot index 14 (the 15th and last; ESlot enum 0-indexed) per Rev 3 FIX-R2-HIGH-1"
        ),
        (
            // XCoreXObject Phase 5.a' Rev 13.9 micro-bump (per XCoreXObject
            // Rev 4 §11.1 + §11.2): FStruct-v4 -> FStruct-v5 with RefSchema
            // appended at offset 112; sizeof 112 -> 120. Three sources
            // (XReflectionRuntime.h, ContractSurface.cs, this file) MUST
            // stay byte-identical.
            "XPACT_FSTRUCT_LAYOUT_TAG",
            "FStruct-v5 (Contract Rev 13.9 extension via XCoreXObject Rev 3): 120 bytes; Class@0 (8) + Owner@8 (8) + Next@16 (8) + Name@24 (8) + SuperStruct@32 (8) + ChildProperties@40 (8) + PropertyLink@48 (8) + ObjectRefProperties@56 (16 = TArray<FProperty*>) + SchemaHash@72 (8) + SchemaVersion@80 (4) + _pad@84 (4) + UnversionedSchema@88 (8) + SerializeStructFn@96 (8) + DestructorLink@104 (8) + RefSchema@112 (8). RefSchema is the fast-path GC walker pointer appended per FIX-A-CRIT-8 / UE-MISS-1. Conceptual layout name string -- the real field offsets per XCore-4b Rev 4 are: NamePrivate@0, SuperStruct@8, ChildProperties@16, PropertiesSize@24, MinAlignment@28, StructFlags@30, _padStructFlags@31, PropertyLink@32, DestructorLink@40, PostConstructLink@48, ObjectRefProperties@56 (24 byte TArray), SchemaHash@80, SchemaVersion@88, _padSchema@92, UnversionedSchema@96, SerializeStructFn@104, RefSchema@112 (Rev 3 appended)."
        ),
        (
            // XCoreXObject Phase 5.a' Rev 13.9 cascade: FScriptStruct-v4
            // -> FScriptStruct-v5 (FStruct base grew +8 via RefSchema
            // appendage; body unchanged).
            "XPACT_FSCRIPTSTRUCT_LAYOUT_TAG",
            "FScriptStruct-v5 (Contract Rev 13.9 cascade): 120 FStruct base (with appended RefSchema@112) + 16 ICppStructOps FakeVTable pattern = 136 bytes; per-subtype FCppStructOpsFakeVTable in .rodata at 136 bytes (8-byte header + 16 handler slots) unchanged"
        ),
        (
            "XPACT_FCPPSTRUCTOPSFAKEVTABLE_LAYOUT_TAG",
            "FCppStructOpsFakeVTable-v1: 8-byte header (32-bit Capabilities + _reservedHeader) + 16 handler slots (8 bytes each) = 136 bytes per FScriptStruct subtype in .rodata"
        ),
        (
            // XCoreXObject Phase 5.a' Rev 13.9 micro-bump (per XCoreXObject
            // Rev 4 §11.1 + §11.2): FClass-v4 -> FClass-v6 with
            // LifecycleTable appended at FClass-absolute offset 232;
            // sizeof 224 -> 240 (+16 net = +8 FStruct.RefSchema cascade +
            // +8 FClass-specific.LifecycleTable). Note: v5 was skipped to
            // align the version int with the Rev 3 spec's §11.1 tag
            // table publication (FIX-N-R2-1).
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
        // addendum tags per XCoreXObject Rev 4 §11.1.
        //
        // These tags pin the byte layouts of the XObject-side ABI surface
        // that XCoreXObject (System 5; not yet shipped at Phase 5.a') will
        // introduce. Added at Phase 5.a' (Contract prerequisite) so the
        // per-DLL static_assert pins XHT emits can verify against the
        // frozen layouts before XCoreXObject's runtime types land.
        //
        // Until XCoreXObject ships, the XObject / FXObjectArrayEntry /
        // XObjectKey / XWeakPtr / XPtr / FXObjectLifecycleTable /
        // FXObjectRefSchema / XGCCardTable types do NOT exist as concrete
        // C++ types in the runtime headers; the tags are pure string-
        // literal pins for the Contract canonicalization + the XHT-emit
        // static_assert(CompileTimeStrEq(...)) calls.
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
    };

    /// <summary>
    /// Per-type sizeof pin set per XCore-4b Rev 4 Section 11.2 / 11.3
    /// tables. Each entry is (TypeName, ExpectedBytes); the emitter
    /// produces one <c>static_assert(sizeof(TypeName) == ExpectedBytes,
    /// "...")</c> line per entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TypeName values are bare (unqualified) since the .gen.cpp
    /// <c>#include "Reflection/F*.h"</c> brings the reflection-runtime
    /// types into the <c>XCore::Reflect</c> namespace via the headers'
    /// own namespace blocks; the emit uses
    /// <c>XCore::Reflect::TypeName</c> to avoid ambiguity with any
    /// user-side type that happens to share a name.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<(string TypeName, int ExpectedBytes)> TypeSizes = new (string, int)[]
    {
        ("FName", 8),
        ("FField", 32),
        ("FFieldClass", 48),
        ("FFieldVariant", 8),
        ("FProperty", 104),
        ("FFakeVTable", 128),
        // XCoreXObject Phase 5.a' Rev 13.9 micro-bump: FStruct 112 -> 120
        // (RefSchema appended), FClass 224 -> 240 (FStruct cascade +8 +
        // LifecycleTable +8), FScriptStruct 128 -> 136 (FStruct cascade +8).
        ("FStruct", 120),
        ("FScriptStruct", 136),
        ("FCppStructOpsFakeVTable", 136),
        ("FClass", 240),
        ("FRepRecord", 16),
        ("FEnum", 72),
        ("FInterface", 64),
        ("FCustomVersion", 32),
        // XCoreXObject Phase 5.a' Rev 13.9 micro-bump: 7 new XObject-
        // side type sizes (XObject heap surface; lands fully when
        // XCoreXObject ships). Until then these are pure pins for the
        // Contract canonicalization + XHT-emit static_asserts.
        ("XObject", 56),
        ("FXObjectArrayEntry", 32),
        ("XObjectKey", 8),
        ("XWeakPtr", 8),
        ("XPtr", 8),
        ("FXObjectLifecycleTable", 72),
        ("FXObjectRefSchema", 24),
    };
}
