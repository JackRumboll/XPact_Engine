// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectRefSchema.h -- packed opcode vector for the schema-vector GC
// walker (XCoreXObject Rev 4 §7.4 + §11.1 + Contract Rev 13.9 tag
// XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG).
// =====================================================================
//
// XCoreXObject Rev 4, Section 7.4 ("Fast-path: schema-vector GC walker")
// + Section 7.4.1 ("Schema-vector emit rules") + Section 11.1 layout
// table row for XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG:
//
//   "FXObjectRefSchema-v1: 24 bytes; NumOps@0 (u32), Version@4 (u32),
//    Ops@8 (const FXObjectRefSchemaOp*), _padTail@16; alignof = 8.
//    FXObjectRefSchemaOp is 24 bytes per opcode; Op@0 (u8), _padOp@1,
//    ArrayDim@2 (u16), Offset@4 (i32), StrideBytes@8 (i32),
//    NestedSchema@16 (const FXObjectRefSchema*); alignof = 8 (4 bytes
//    of pad at offset 12 to align NestedSchema to 8). Rev 3: 22 active
//    opcodes (added Interface, ClassProperty, SoftClass, Delegate,
//    MulticastInlineDelegate, MulticastSparseDelegate per FIX-H-R2-3)."
//
// PURPOSE: FXObjectRefSchema is the cache-coherent fast-path replacement
// for the FProperty pointer-chase walk over an FStruct's
// ObjectRefProperties (the slow-path inspection surface populated by
// XCore-4b FIX-13 + Phase 4b.5 FStruct::Link). The pointer-chase walk
// suffers ~3000 refs/ms on Quest 3 (Cortex-A78); the schema vector
// pushes throughput to >4000 refs/ms by reading offset + stride + kind
// from a contiguous .rodata array.
//
// The schema vector is emitted by XHT at compile time per FClass (one
// FXObjectRefSchemaOp per reference-carrying FProperty, terminated by
// a Terminator opcode). The FStruct's RefSchema pointer (offset 112;
// Phase 5.a' Rev 13.9 micro-bump) references the .rodata-resident
// schema; the GC mark phase walks the opcode vector once per object,
// dispatching per opcode kind.
//
// FStruct.ObjectRefProperties (the dense TArray; XCore-4b FIX-13)
// REMAINS as the slow-path inspection surface (XEditor.PropertyInspector,
// debugger introspection, leak-tracker). Both fields exist; they serve
// different consumers per spec §7.3 vs §7.4 split.
//
// =====================================================================
// LAYOUT (per Contract Rev 13.9 XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG):
//
//   struct alignas(8) FXObjectRefSchemaOp {
//       EXObjectRefSchemaOp Op;          //  0  +1   uint8 opcode
//       uint8_t             _padOp;      //  1  +1
//       uint16_t            ArrayDim;    //  2  +2   in-place array dim
//       int32_t             Offset;      //  4  +4   byte offset within parent
//       int32_t             StrideBytes; //  8  +4   stride for container opcodes
//       uint32_t            _padAlign;   // 12  +4   align NestedSchema to 8
//       const FXObjectRefSchema* NestedSchema;   // 16 +8
//   };
//   static_assert(sizeof(FXObjectRefSchemaOp) == 24);
//   static_assert(alignof(FXObjectRefSchemaOp) == 8);
//
//   struct alignas(8) FXObjectRefSchema {
//       uint32_t                   NumOps;    //  0  +4
//       uint32_t                   Version;   //  4  +4
//       const FXObjectRefSchemaOp* Ops;       //  8  +8   pointer to .rodata array
//       uint64_t                   _padTail;  // 16  +8
//   };
//   static_assert(sizeof(FXObjectRefSchema) == 24);
//   static_assert(alignof(FXObjectRefSchema) == 8);
//
// HOT-RELOAD SAFETY (per Phase 5.a-5.f discipline):
//
//   * No virtual methods.
//   * Trivially-copyable + trivially-destructible.
//   * Standard-layout (single base; POD members; the only "fat" member
//     is the const-pointer to the opcode array which is itself a POD).
//   * Both types live in .rodata when emitted by XHT (constinit) so
//     hot-reload swap-in is atomic at the FStruct.RefSchema pointer
//     level (the new module's .rodata schema is the new pointee; the
//     old module's pointee is dead after the unload).
//
// EMIT DISCIPLINE (per spec §7.4.1 + §10.6):
//
//   XHT walks each FClass / FStruct's reflected properties at compile
//   time, filtering on object-reference-carrying FProperty subclasses,
//   and emits one FXObjectRefSchemaOp per ref-carrying property. The
//   emit terminates with EXObjectRefSchemaOp::Terminator (a sentinel
//   the runtime walker uses as the end-of-schema marker; consistent
//   with the NumOps count for redundant safety).
//
//   For FStructProperty (inline nested struct), XHT emits a Struct
//   opcode whose NestedSchema points at the nested FStruct's RefSchema
//   (which is itself emitted as a separate .rodata vector). For
//   container-of-struct (FArrayProperty<FStructProperty> etc.), the
//   container opcode (ArrayOfStruct, MapOfObject_KeyValue,
//   SetOfObject struct variant) carries the nested struct's RefSchema
//   as the NestedSchema pointer.
//
//   The full emit-rules table is in spec §7.4.1; the C# side mirror
//   lives in Engine/Source/Programs/XHT/XHT.Emitter/SchemaVectorEmitter.cs
//   (Phase 5.g').
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "XReflectionRuntime.h"     // XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG

#include <cstddef>                  // offsetof
#include <cstdint>
#include <type_traits>              // is_trivially_*, is_standard_layout

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // EXObjectRefSchemaOp -- the opcode-kind enumeration (Rev 3 22
    // active opcodes per FIX-H-R2-3).
    //
    // Underlying type uint8_t (matches Op@0 1-byte slot per layout tag).
    // Values are sequential integers 0..22; bit-masks for capability
    // flags are a separate constexpr surface (see kObjectOp / etc.
    // below) and are NOT identical to the opcode values.
    //
    // Per spec §7.4: Terminator (0) is the sentinel marking end of
    // schema; the runtime walker probes for it as a redundant
    // verification against the NumOps count. Object (1) ...
    // MulticastSparseDelegate (22) are the active opcodes.
    //
    // The high bit value (_Reserved255) is reserved per spec as a
    // hot-reload-canary slot; XHT MUST NOT emit it.
    // -----------------------------------------------------------------
    enum class EXObjectRefSchemaOp : ::std::uint8_t
    {
        // Terminator (end of schema)
        Terminator              = 0,

        // Single-reference opcodes (primitive ref slot)
        Object                  = 1,   // FObjectProperty
        WeakObject              = 2,   // FWeakObjectProperty
        SoftObject              = 3,   // FSoftObjectProperty

        // Container opcodes
        ArrayOfObject           = 4,   // TArray<XPtr<T>>; stride = sizeof(XPtr)
        ArrayOfStruct           = 5,   // TArray<FStruct>; NestedSchema set
        StridedArrayOfObject    = 6,   // strided array (weak / soft variants)
        MapOfObject_KeyValue    = 7,   // TMap<K, V>; K or V is XObject ref
        SetOfObject             = 8,   // TSet<XPtr<T>>

        // Nested struct
        Struct                  = 9,   // FStructProperty (inline); NestedSchema set

        // FieldPath family (post-MVP; Rev 3 reserved)
        FieldPath               = 10,
        FieldPathArray          = 11,

        // Optional / variant
        OptionalObject          = 12,  // FOptionalProperty wrapping XObject ref
        DynamicallyTypedValue   = 13,

        // AddReferencedObjects callbacks (FClass lifecycle slot)
        ARO                     = 14,
        SlowARO                 = 15,
        MemberARO               = 16,

        // Rev 3 additions (FIX-H-R2-3): full coverage of XCore-4b
        // Rev 4's 28 MVP FProperty subclasses (the 22 active ops + 5
        // reserved post-MVP not yet emitted, +1 sentinel = 28).
        Interface               = 17,  // FInterfaceProperty
        ClassProperty           = 18,  // FClassProperty
        SoftClass               = 19,  // FSoftClassProperty
        Delegate                = 20,  // FDelegateProperty
        MulticastInlineDelegate = 21,  // FMulticastInlineDelegateProperty
        MulticastSparseDelegate = 22,  // FMulticastSparseDelegateProperty

        // High bit reserved (hot-reload canary; XHT MUST NOT emit).
        _Reserved255            = 255,
    };

    // -----------------------------------------------------------------
    // Active-opcode count (Rev 3 per FIX-H-R2-3).
    //
    // The "active" opcodes are values 1..22 (Object ..
    // MulticastSparseDelegate). Terminator (0) and _Reserved255 are
    // sentinels; they are NOT counted as active.
    //
    // Mirrors the spec §11.1 "22 active opcodes" assertion and the
    // FXObjectRefSchemaOp.Tests/OpcodeEnumeration test pin.
    // -----------------------------------------------------------------
    inline constexpr ::std::uint32_t kEXObjectRefSchemaOpActiveCount = 22u;

    // -----------------------------------------------------------------
    // Per-opcode capability-bit constants (Rev 3 per FIX-H-R2-3).
    //
    // The capability mask lives ALONGSIDE FXObjectRefSchema (carried by
    // FClass at the .rodata aggregator level; future phases may expose
    // an accessor). Each opcode kind owns one bit so a "does this
    // schema contain any X-kind ref?" query is a single AND test.
    //
    // The BIT POSITIONS match the opcode VALUES (kObjectOp = 1ULL <<
    // EXObjectRefSchemaOp::Object). Spec §7.4 lists kObjectOp = 1ULL <<
    // 1 etc.; we encode the relationship explicitly via the underlying-
    // type cast so a future opcode value change rotates the bits in
    // lockstep.
    //
    // The bits are uint64_t to leave headroom for future opcodes
    // (current 22 fits in 32 bits; 64 bits accommodates the post-MVP
    // expansion without an ABI break).
    // -----------------------------------------------------------------
    inline constexpr ::std::uint64_t kObjectOp                  = 1ULL <<  1;
    inline constexpr ::std::uint64_t kWeakObjectOp              = 1ULL <<  2;
    inline constexpr ::std::uint64_t kSoftObjectOp              = 1ULL <<  3;
    inline constexpr ::std::uint64_t kArrayOfObjectOp           = 1ULL <<  4;
    inline constexpr ::std::uint64_t kArrayOfStructOp           = 1ULL <<  5;
    inline constexpr ::std::uint64_t kStridedArrayOfObjectOp    = 1ULL <<  6;
    inline constexpr ::std::uint64_t kMapOfObjectKeyValueOp     = 1ULL <<  7;
    inline constexpr ::std::uint64_t kSetOfObjectOp             = 1ULL <<  8;
    inline constexpr ::std::uint64_t kStructOp                  = 1ULL <<  9;
    inline constexpr ::std::uint64_t kFieldPathOp               = 1ULL << 10;
    inline constexpr ::std::uint64_t kFieldPathArrayOp          = 1ULL << 11;
    inline constexpr ::std::uint64_t kOptionalObjectOp          = 1ULL << 12;
    inline constexpr ::std::uint64_t kDynamicallyTypedValueOp   = 1ULL << 13;
    inline constexpr ::std::uint64_t kAROOp                     = 1ULL << 14;
    inline constexpr ::std::uint64_t kSlowAROOp                 = 1ULL << 15;
    inline constexpr ::std::uint64_t kMemberAROOp               = 1ULL << 16;
    // Rev 3 additions (FIX-H-R2-3):
    inline constexpr ::std::uint64_t kInterfaceOp               = 1ULL << 17;
    inline constexpr ::std::uint64_t kClassPropertyOp           = 1ULL << 18;
    inline constexpr ::std::uint64_t kSoftClassOp               = 1ULL << 19;
    inline constexpr ::std::uint64_t kDelegateOp                = 1ULL << 20;
    inline constexpr ::std::uint64_t kMulticastInlineOp         = 1ULL << 21;
    inline constexpr ::std::uint64_t kMulticastSparseOp         = 1ULL << 22;

    // Forward declaration so FXObjectRefSchemaOp can reference the
    // header type via the NestedSchema pointer.
    struct FXObjectRefSchema;

    // -----------------------------------------------------------------
    // FXObjectRefSchemaOp -- 24-byte per-opcode descriptor (per
    // Contract Rev 13.9 XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG).
    //
    // Each opcode encodes one reference-carrying FProperty as
    // (kind, offset, stride, array-dim, nested-schema). The kind
    // dispatches in the GC walker; offset + stride locate the slot
    // in the owning struct's instance bytes; nested-schema recurses
    // into nested FStruct properties.
    //
    // Trivially copyable + trivially destructible (POD members; no
    // owning relationships). Standard-layout (single base; all members
    // same access level).
    // -----------------------------------------------------------------
    struct alignas(8) FXObjectRefSchemaOp
    {
        EXObjectRefSchemaOp Op;          //  0  +1   opcode kind
        ::std::uint8_t      _padOp;      //  1  +1   alignment pad
        ::std::uint16_t     ArrayDim;    //  2  +2   in-place array dim
                                          //         (for XPROPERTY(MyType[N]))
        ::std::int32_t      Offset;      //  4  +4   byte offset within
                                          //         owner struct
        ::std::int32_t      StrideBytes; //  8  +4   stride for container ops;
                                          //         0 for single-slot
        ::std::uint32_t     _padAlign;   // 12  +4   align NestedSchema to 8
                                          //         (Rev 3 per FIX-H-R2-5;
                                          //         explicit pad field)
        const FXObjectRefSchema* NestedSchema;   // 16  +8   recursion target
                                                  //         (Struct / ArrayOfStruct
                                                  //          / SetOfStruct / Map-
                                                  //          of-struct opcodes)
    };

    // ABI lock per Contract Rev 13.9 XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG
    // + spec §7.4 trailing static_assert + §11.3
    // XPACT_VERIFY_XOBJECT_LAYOUT macro pin set.
    static_assert(sizeof(FXObjectRefSchemaOp)  == 24,
                  "FXObjectRefSchemaOp ABI lock (Rev 13.9): 24 bytes per "
                  "opcode; Op@0 (1) + _padOp@1 (1) + ArrayDim@2 (2) + "
                  "Offset@4 (4) + StrideBytes@8 (4) + _padAlign@12 (4) + "
                  "NestedSchema@16 (8) = 24 bytes. Per XCoreXObject Rev 4 "
                  "§7.4 + XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG.");
    static_assert(alignof(FXObjectRefSchemaOp) == 8,
                  "FXObjectRefSchemaOp ABI lock: 8-byte alignment per "
                  "XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG (NestedSchema slot "
                  "must align to 8).");

    // Member offset locks (per spec §7.4 + Contract Rev 13.9 layout
    // tag's per-field documentation).
    static_assert(offsetof(FXObjectRefSchemaOp, Op)           ==  0,
                  "FXObjectRefSchemaOp.Op offset lock (u8 at 0).");
    static_assert(offsetof(FXObjectRefSchemaOp, _padOp)       ==  1,
                  "FXObjectRefSchemaOp._padOp offset lock (1 byte pad).");
    static_assert(offsetof(FXObjectRefSchemaOp, ArrayDim)     ==  2,
                  "FXObjectRefSchemaOp.ArrayDim offset lock (u16 at 2).");
    static_assert(offsetof(FXObjectRefSchemaOp, Offset)       ==  4,
                  "FXObjectRefSchemaOp.Offset offset lock (i32 at 4).");
    static_assert(offsetof(FXObjectRefSchemaOp, StrideBytes)  ==  8,
                  "FXObjectRefSchemaOp.StrideBytes offset lock (i32 at 8).");
    static_assert(offsetof(FXObjectRefSchemaOp, _padAlign)    == 12,
                  "FXObjectRefSchemaOp._padAlign offset lock (Rev 3 per "
                  "FIX-H-R2-5; explicit alignment pad).");
    static_assert(offsetof(FXObjectRefSchemaOp, NestedSchema) == 16,
                  "FXObjectRefSchemaOp.NestedSchema offset lock "
                  "(8-byte pointer at 16; 4 bytes of pad at offset 12 "
                  "to align to 8).");

    // Trait locks. FXObjectRefSchemaOp MUST be trivially copyable +
    // trivially destructible + standard-layout so:
    //   * It can be emitted as constinit in .rodata (XHT-emit).
    //   * Containers + memcpy/memmove invariants hold.
    //   * offsetof() is well-defined (single base; POD members; same
    //     access level for every member).
    static_assert(::std::is_trivially_copyable_v<FXObjectRefSchemaOp>,
                  "FXObjectRefSchemaOp must be trivially copyable "
                  "(.rodata constinit + memcpy invariants).");
    static_assert(::std::is_trivially_destructible_v<FXObjectRefSchemaOp>,
                  "FXObjectRefSchemaOp must be trivially destructible.");
    static_assert(::std::is_standard_layout_v<FXObjectRefSchemaOp>,
                  "FXObjectRefSchemaOp must be standard-layout "
                  "(offsetof correctness).");

    // -----------------------------------------------------------------
    // FXObjectRefSchema -- 24-byte schema header pointing at the
    // .rodata-resident opcode array (per Contract Rev 13.9
    // XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG).
    //
    // NumOps counts every opcode INCLUDING the Terminator sentinel
    // (spec §7.4 emit prose: "appends a single Terminator opcode at
    // the end"). The runtime walker iterates [0..NumOps) and probes
    // for Terminator as a defensive check; both stops the walk
    // (NumOps reached AND Terminator opcode encountered are equivalent
    // signals).
    //
    // Version is bumped on layout changes (e.g., new opcode kinds
    // added to EXObjectRefSchemaOp); the runtime checks the Version
    // field against the build-time constant before walking. A
    // mismatch indicates a hot-reload mid-cycle where the emit-time
    // schema and the runtime walker have drifted; the runtime falls
    // back to the slow-path ObjectRefProperties walk in that case.
    //
    // Ops is a const pointer to the .rodata-resident opcode array.
    // The schema and the opcodes are emitted as separate constinit
    // entities at XHT-emit time so the Ops pointer can be initialised
    // with the address of the named opcode array.
    //
    // _padTail explicitly occupies the trailing 8 bytes (16..23) so
    // the layout is 24 bytes total per XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG.
    // Reserving the slot leaves headroom for a future capability mask
    // or version-2 sub-version field without an ABI break.
    // -----------------------------------------------------------------
    struct alignas(8) FXObjectRefSchema
    {
        ::std::uint32_t            NumOps;     //  0  +4   opcode count
                                                //         (incl. Terminator)
        ::std::uint32_t            Version;    //  4  +4   schema-format version
        const FXObjectRefSchemaOp* Ops;        //  8  +8   .rodata opcode array
        ::std::uint64_t            _padTail;   // 16  +8   reserved
    };

    // ABI lock per Contract Rev 13.9 XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG
    // + spec §7.4 trailing static_assert + §11.3
    // XPACT_VERIFY_XOBJECT_LAYOUT macro pin set.
    static_assert(sizeof(FXObjectRefSchema)  == 24,
                  "FXObjectRefSchema ABI lock (Rev 13.9): 24 bytes "
                  "(NumOps@0 + Version@4 + Ops@8 + _padTail@16) per "
                  "XCoreXObject Rev 4 §7.4 + "
                  "XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG.");
    static_assert(alignof(FXObjectRefSchema) == 8,
                  "FXObjectRefSchema ABI lock: 8-byte alignment per "
                  "XPACT_FXOBJECTREFSCHEMA_LAYOUT_TAG.");

    // Member offset locks (per spec §7.4 + Contract Rev 13.9 layout
    // tag's per-field documentation).
    static_assert(offsetof(FXObjectRefSchema, NumOps)   ==  0,
                  "FXObjectRefSchema.NumOps offset lock (u32 at 0).");
    static_assert(offsetof(FXObjectRefSchema, Version)  ==  4,
                  "FXObjectRefSchema.Version offset lock (u32 at 4).");
    static_assert(offsetof(FXObjectRefSchema, Ops)      ==  8,
                  "FXObjectRefSchema.Ops offset lock (pointer at 8).");
    static_assert(offsetof(FXObjectRefSchema, _padTail) == 16,
                  "FXObjectRefSchema._padTail offset lock (8 bytes at 16).");

    // Trait locks. FXObjectRefSchema MUST be trivially copyable +
    // trivially destructible + standard-layout so:
    //   * It can be emitted as constinit in .rodata (XHT-emit).
    //   * offsetof() is well-defined.
    //   * Containers + memcpy/memmove invariants hold.
    static_assert(::std::is_trivially_copyable_v<FXObjectRefSchema>,
                  "FXObjectRefSchema must be trivially copyable "
                  "(.rodata constinit + memcpy invariants).");
    static_assert(::std::is_trivially_destructible_v<FXObjectRefSchema>,
                  "FXObjectRefSchema must be trivially destructible.");
    static_assert(::std::is_standard_layout_v<FXObjectRefSchema>,
                  "FXObjectRefSchema must be standard-layout "
                  "(offsetof correctness).");

    // Current schema-format version. Bumped on opcode-set changes
    // (e.g., a new opcode kind added to EXObjectRefSchemaOp).
    //
    // Rev 3 (per FIX-H-R2-3): 22 active opcodes; version 1.
    inline constexpr ::std::uint32_t kFXObjectRefSchemaCurrentVersion = 1u;

} // namespace XCore::Reflect
