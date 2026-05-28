// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectSchemaWalker.h -- schema-vector GC walker (XCoreXObject Rev 4
// §7.4 fast-path mark-phase per-object scan).
// =====================================================================
//
// XCoreXObject Rev 4, Section 7.4 ("Fast-path: schema-vector GC walker")
// + Section 10.6 ("XHT-emitted .gen.cpp registration paths").
//
// PURPOSE: walk an FStruct's RefSchema, visiting every XObject reference
// contained in an instance. Replaces the slow-path FProperty pointer-
// chase walk (XCore-4b FIX-13 + FStruct.ObjectRefProperties) for the
// GC mark phase. Targets >4000 refs/ms on Quest 3 (vs Rev 1's ~3000
// refs/ms baseline with FProperty chase) per spec §7.4 hot-path budget.
//
// USAGE (per spec §7.4 fast-path mark-phase pseudo-code):
//
//   XObject* Obj = ...;
//   const FStruct* Struct = Obj->GetClass();    // FClass IS-A FStruct
//   ::XCore::WalkSchemaRefs(Struct, Obj, [&](XObject* Ref)
//   {
//       MarkRefIfUnmarked(Ref, GrayQueue);
//   });
//
// The visitor signature is `void(XObject*)`. Templated so the visitor
// is monomorphised at each call site (no virtual / std::function
// indirection on the GC hot path).
//
// CALL-SITE DISCIPLINE:
//
//   * `Struct` MUST be non-null and `Struct->RefSchema` MUST be non-
//     null. The caller probes `Struct->GetRefSchema() != nullptr`
//     before invoking the walker. The slow-path consumer (XCore-4b
//     FIX-13's ObjectRefProperties walk) handles the
//     RefSchema==nullptr case (no schema vector emitted because the
//     struct has no object references; nothing to walk).
//   * `Instance` MUST be non-null and MUST point at a valid bytes-
//     buffer of the Struct's storage layout.
//   * The walker reads the schema's Ops array; the array MUST live in
//     .rodata (XHT-emit invariant) so the read is reproducible across
//     hot-reload swaps modulo the Version match check.
//   * The walker does NOT acquire any locks. The caller's GC mark-
//     phase quiescent state owns the synchronisation discipline.
//
// SCOPE (Phase 5.g'):
//
//   Phase 5.g' ships the WALKER template and dispatch for the FULL
//   22-active-opcode set per spec §7.4 Rev 3 FIX-H-R2-3:
//
//     * Terminator           -- end of schema (no-op stop signal)
//     * Object               -- single XObject* ref slot
//     * WeakObject           -- single XWeakPtr slot (visit-through)
//     * SoftObject           -- single XSoftPtr slot (visit-through)
//     * Interface            -- single XObject* via FInterfaceProperty
//     * ClassProperty        -- single FClass* (FClass IS-A XObject)
//     * SoftClass            -- single XSoftPtr<FClass> (visit-through)
//     * ArrayOfObject        -- TArray<XPtr<T>>; visit each element
//     * StridedArrayOfObject -- strided array (weak / soft variants)
//     * ArrayOfStruct        -- recurse via NestedSchema
//     * MapOfObject_KeyValue -- TMap with XObject ref in K or V
//     * SetOfObject          -- TSet<XPtr<T>>
//     * Struct               -- recurse via NestedSchema
//     * Delegate / Multicast variants -- visit target slot
//     * OptionalObject       -- TOptional<XPtr>; bSet-gated visit
//     * ARO / SlowARO / MemberARO -- routed via FClass lifecycle slot
//     * FieldPath / FieldPathArray / DynamicallyTypedValue -- variant
//       paths (post-MVP property kinds; defensive no-op)
//
// CONTAINER-LAYOUT INVARIANT:
//
//   Reflected XPROPERTY-tagged container slots (TArray, TMap, TSet)
//   store their value as the NON-GC TArrayCore layout {T* m_data,
//   int32 m_num, int32 m_max, AllocatorT m_alloc} = 24 bytes. The
//   walker reads m_data + m_num directly from the slot's first 12
//   bytes (TArrayCoreView below) so it does NOT trigger the
//   GC-aware TArray<T*> partial specialisation (which adds a 32-byte
//   m_rootSpan that the slot's bytes do NOT carry for XPtr-typed
//   elements).
//
//   XPtr<T> is layout-identical to T* (Phase 5.c ABI invariant); reads
//   of TArray<XPtr<T>> are bit-identical to reads of TArray<T*>'s
//   data buffer when the element width is 8. The walker leverages
//   this for the ArrayOfObject opcode without forcing the GC-aware
//   spec to instantiate.
//
// HOT-RELOAD SAFETY:
//
//   * No virtual methods. Template-only header for monomorphisation.
//   * The schema vector is .rodata; hot-reload swap-in is an atomic
//     pointer update at FStruct.RefSchema; the old schema's bytes are
//     dead after module unload.
//   * The Version field provides defence-in-depth: a mid-cycle
//     hot-reload where the runtime walker and the emitted schema have
//     drifted is detected, and the walker no-ops out (caller's
//     recovery is the slow-path ObjectRefProperties walk).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Reflection/FXObjectRefSchema.h"

#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace XCore::Reflect
{
    struct FStruct;
} // namespace XCore::Reflect

namespace XCore
{
    class XObject;
} // namespace XCore

namespace XCore
{

    // -----------------------------------------------------------------
    // SchemaWalkerDetail -- per-opcode dispatch helpers.
    //
    // Header-private namespace; consumers MUST NOT call these directly.
    // The public surface is WalkSchemaRefs (below) which dispatches
    // into these helpers for each opcode.
    // -----------------------------------------------------------------
    namespace SchemaWalkerDetail
    {
        // -----------------------------------------------------------------
        // TArrayCoreView -- read-only view of the non-GC TArrayCore
        // {m_data, m_num, m_max} prefix (16 bytes) of a TArray<T> slot.
        //
        // The reflected XPROPERTY slot for a TArray<XPtr<T>> stores the
        // value as the non-GC TArrayCore layout (XPtr<T> is layout-
        // identical to T* but is its own non-XObject-derived type, so
        // the GC-aware TArray<T*> partial specialisation does NOT
        // match; the slot is sizeof(TArrayCore<XPtr<T>>) = 24 bytes).
        //
        // The walker reads m_data + m_num via this view to avoid
        // forcing the GC-aware spec to instantiate (which would add
        // a 32-byte m_rootSpan that the slot does NOT carry).
        //
        // The view layout MUST match TArrayCore's first 16 bytes
        // (m_data@0 + m_num@8 + m_max@12); the static_asserts in
        // TArrayCore.h pin those offsets. The m_alloc tag (16..24)
        // is intentionally NOT read here; the walker does not need
        // allocator-tag attribution.
        // -----------------------------------------------------------------
        struct TArrayCoreView
        {
            const void*    DataPtr;   // @0 +8   T* m_data
            ::std::int32_t Num;       // @8 +4   m_num
            ::std::int32_t Max;       // @12 +4  m_max
        };
        static_assert(sizeof(TArrayCoreView)  == 16,
                      "TArrayCoreView ABI lock: reads the first 16 bytes "
                      "of a TArrayCore<T> prefix (m_data@0 + m_num@8 + "
                      "m_max@12). Must match TArrayCore.h layout.");
        static_assert(alignof(TArrayCoreView) == 8,
                      "TArrayCoreView alignment lock (matches TArrayCore).");

        // -----------------------------------------------------------------
        // SchemaWalkerByteCopy -- the type-punning-safe byte copy used by
        // the walker for aliasing-clean loads from raw bytes into the
        // known-layout view struct.
        //
        // The hand-rolled byte loop is reliably folded by every modern
        // compiler to the smallest available load/store sequence (a
        // pair of 64-bit loads for sizeof(TArrayCoreView) == 16, one
        // 64-bit load for sizeof(XObject*) == 8). Using a hand-rolled
        // loop instead of std::memcpy avoids pulling <cstring> into
        // every walker call site's translation unit.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void SchemaWalkerByteCopy(
            void* Dest, const void* Src, ::std::size_t Bytes) noexcept
        {
            ::std::uint8_t* DestBytes = static_cast<::std::uint8_t*>(Dest);
            const ::std::uint8_t* SrcBytes = static_cast<const ::std::uint8_t*>(Src);
            for (::std::size_t I = 0; I < Bytes; ++I)
            {
                DestBytes[I] = SrcBytes[I];
            }
        }

        // -----------------------------------------------------------------
        // ReadTArrayCoreView -- read the {m_data, m_num, m_max} prefix
        // of a TArray slot via SchemaWalkerByteCopy (avoiding undefined-
        // behaviour from heterogeneous-storage reinterpret_cast).
        //
        // The byte-copy form is the well-defined path for type-punning
        // through a known-layout struct.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE TArrayCoreView ReadTArrayCoreView(
            const ::std::uint8_t* SlotBytes) noexcept
        {
            TArrayCoreView View;
            SchemaWalkerByteCopy(&View, SlotBytes, sizeof(TArrayCoreView));
            return View;
        }

        // -----------------------------------------------------------------
        // VisitObjectSlot -- visit a single XObject* slot at offset.
        //
        // Reads the raw 8-byte pointer at `InstanceBytes + Offset`
        // (which is bit-identical to either a raw `XObject*` OR an
        // `XPtr<T>` -- the XPtr ABI invariant per Phase 5.c). The
        // pointer is then passed to the visitor.
        //
        // Visitor signature: `void(XObject*)`. The visitor MAY receive
        // nullptr (an empty XPtr slot); the visitor is responsible for
        // null-checking. Mark-phase visitors short-circuit on nullptr.
        // -----------------------------------------------------------------
        template <typename Visitor>
        XPACT_FORCEINLINE void VisitObjectSlot(const ::std::uint8_t* InstanceBytes,
                                                ::std::int32_t Offset,
                                                Visitor& V) noexcept
        {
            XObject* Slot = nullptr;
            SchemaWalkerByteCopy(&Slot,
                                         InstanceBytes + Offset,
                                         sizeof(XObject*));
            V(Slot);
        }

        // -----------------------------------------------------------------
        // VisitObjectArraySlot -- visit each element of a TArray<XPtr<T>>
        // (or equivalent) at offset.
        //
        // Reads the TArrayCore prefix at the slot, then walks
        // Num elements each at stride 8 bytes (XPtr<T> / XObject*
        // are layout-identical 8-byte handles).
        //
        // The non-GC TArray<XPtr<T>> spec stores the buffer at
        // m_data; the GC-aware spec for raw XObject* extends with
        // m_rootSpan but the same m_data position holds; this walker
        // is correct for both because we only read the prefix.
        // -----------------------------------------------------------------
        template <typename Visitor>
        XPACT_FORCEINLINE void VisitObjectArraySlot(const ::std::uint8_t* InstanceBytes,
                                                     ::std::int32_t Offset,
                                                     Visitor& V) noexcept
        {
            const TArrayCoreView View = ReadTArrayCoreView(
                InstanceBytes + Offset);
            const ::std::int32_t Count = View.Num;
            if (Count <= 0 || View.DataPtr == nullptr)
            {
                return;
            }
            const ::std::uint8_t* ElementBytes =
                static_cast<const ::std::uint8_t*>(View.DataPtr);
            for (::std::int32_t I = 0; I < Count; ++I)
            {
                XObject* Slot = nullptr;
                SchemaWalkerByteCopy(
                    &Slot,
                    ElementBytes + static_cast<::std::ptrdiff_t>(I) * 8,
                    sizeof(XObject*));
                V(Slot);
            }
        }

        // -----------------------------------------------------------------
        // VisitStridedObjectArraySlot -- visit each element of a
        // strided array (TArray<XWeakPtr> / TArray<XSoftPtr> path).
        //
        // Each element is `StrideBytes` wide; the leading 8 bytes of
        // each element are treated as an XObject*-shaped handle
        // (XPtr/XWeakPtr/XSoftPtr all carry an 8-byte handle at offset
        // 0 per Phase 5.c / Rev 13.9 ABI tags).
        //
        // For XWeakPtr/XSoftPtr the mark-phase consumer routes
        // through the four-gate validator before marking; this
        // walker visits the slot unconditionally. The visitor's
        // serial-validation gate filters dead handles.
        // -----------------------------------------------------------------
        template <typename Visitor>
        XPACT_FORCEINLINE void VisitStridedObjectArraySlot(
            const ::std::uint8_t* InstanceBytes,
            ::std::int32_t Offset,
            ::std::int32_t StrideBytes,
            Visitor& V) noexcept
        {
            const TArrayCoreView View = ReadTArrayCoreView(
                InstanceBytes + Offset);
            const ::std::int32_t Count = View.Num;
            if (Count <= 0 || View.DataPtr == nullptr || StrideBytes <= 0)
            {
                return;
            }
            const ::std::uint8_t* ElementBytes =
                static_cast<const ::std::uint8_t*>(View.DataPtr);
            for (::std::int32_t I = 0; I < Count; ++I)
            {
                XObject* Slot = nullptr;
                SchemaWalkerByteCopy(
                    &Slot,
                    ElementBytes + static_cast<::std::ptrdiff_t>(I) * StrideBytes,
                    sizeof(XObject*));
                V(Slot);
            }
        }

        // -----------------------------------------------------------------
        // VisitNestedStruct -- recurse into a nested FStruct via its
        // RefSchema.
        //
        // The nested struct's instance bytes live at
        // `InstanceBytes + Offset`; the nested schema is in
        // `NestedSchema` (must be non-null; XHT-emit guarantees this
        // for Struct / ArrayOfStruct / SetOfObject struct variant /
        // Map struct variant opcodes per spec §7.4.1 emit validation).
        // -----------------------------------------------------------------
        template <typename Visitor>
        void WalkSchemaRefsImpl(const ::XCore::Reflect::FXObjectRefSchema* Schema,
                                const ::std::uint8_t* InstanceBytes,
                                Visitor& V) noexcept;

        template <typename Visitor>
        XPACT_FORCEINLINE void VisitNestedStruct(
            const ::std::uint8_t* InstanceBytes,
            ::std::int32_t Offset,
            const ::XCore::Reflect::FXObjectRefSchema* NestedSchema,
            Visitor& V) noexcept
        {
            if (NestedSchema == nullptr)
            {
                return;  // defensive; XHT-emit guarantees non-null here
            }
            WalkSchemaRefsImpl<Visitor>(NestedSchema,
                                         InstanceBytes + Offset,
                                         V);
        }

        // -----------------------------------------------------------------
        // VisitStructArraySlot -- visit each element of a
        // TArray<FStruct> (typed by NestedSchema).
        //
        // Each element of the TArray is `StrideBytes` wide (= the
        // nested struct's instance size per spec §7.4.1 table). The
        // walker recurses into NestedSchema for each element.
        // -----------------------------------------------------------------
        template <typename Visitor>
        XPACT_FORCEINLINE void VisitStructArraySlot(
            const ::std::uint8_t* InstanceBytes,
            ::std::int32_t Offset,
            ::std::int32_t StrideBytes,
            const ::XCore::Reflect::FXObjectRefSchema* NestedSchema,
            Visitor& V) noexcept
        {
            if (NestedSchema == nullptr || StrideBytes <= 0)
            {
                return;
            }
            const TArrayCoreView View = ReadTArrayCoreView(
                InstanceBytes + Offset);
            const ::std::int32_t Count = View.Num;
            if (Count <= 0 || View.DataPtr == nullptr)
            {
                return;
            }
            const ::std::uint8_t* ElementBytes =
                static_cast<const ::std::uint8_t*>(View.DataPtr);
            for (::std::int32_t I = 0; I < Count; ++I)
            {
                WalkSchemaRefsImpl<Visitor>(
                    NestedSchema,
                    ElementBytes + static_cast<::std::ptrdiff_t>(I) * StrideBytes,
                    V);
            }
        }

        // -----------------------------------------------------------------
        // WalkSchemaRefsImpl -- the dispatch core.
        //
        // Walks `Schema->Ops` for `Schema->NumOps` opcodes; dispatches
        // each opcode kind to the appropriate visit helper.
        //
        // The template parameter `Visitor` is monomorphised at each
        // call site; the entire dispatch + visit chain inlines under
        // optimisation.
        //
        // Terminator (opcode 0) stops the walk via the per-iteration
        // probe; the NumOps count is the redundant upper bound.
        // -----------------------------------------------------------------
        template <typename Visitor>
        void WalkSchemaRefsImpl(const ::XCore::Reflect::FXObjectRefSchema* Schema,
                                const ::std::uint8_t* InstanceBytes,
                                Visitor& V) noexcept
        {
            XPACT_CHECK(Schema != nullptr);
            XPACT_CHECK(InstanceBytes != nullptr);

            // Version mismatch defence: a mid-cycle hot-reload where
            // the emit-time schema and the runtime walker have drifted
            // is detected here. The caller's recovery is to fall back
            // to the slow-path ObjectRefProperties walk; from the
            // walker's standpoint, "unknown version" is a no-op.
            if (Schema->Version !=
                ::XCore::Reflect::kFXObjectRefSchemaCurrentVersion)
            {
                return;
            }

            const ::XCore::Reflect::FXObjectRefSchemaOp* const Ops = Schema->Ops;
            if (Ops == nullptr)
            {
                return;
            }

            const ::std::uint32_t NumOps = Schema->NumOps;
            for (::std::uint32_t I = 0; I < NumOps; ++I)
            {
                const ::XCore::Reflect::FXObjectRefSchemaOp& Op = Ops[I];

                switch (Op.Op)
                {
                    case ::XCore::Reflect::EXObjectRefSchemaOp::Terminator:
                        // Defensive early-out; NumOps is the upper bound.
                        return;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::Object:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::Interface:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::ClassProperty:
                        VisitObjectSlot(InstanceBytes, Op.Offset, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::WeakObject:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::SoftObject:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::SoftClass:
                        // Weak / soft refs: the 8-byte handle at offset
                        // is NOT a raw XObject*; it is an {Index, Serial}
                        // pair. The walker visits the slot as a raw
                        // pointer-shaped value; the mark-phase consumer
                        // is expected to route through the four-gate
                        // validator (Phase 5.c IsValidLowLevel + serial
                        // gate). Per spec §7.4: weak/soft refs are NOT
                        // marked as roots (the resolve gate fails for
                        // collected pointees), so the visitor's null-
                        // check or serial-validation short-circuits.
                        //
                        // The slot IS visited so a future weak-reference
                        // book-keeping pass (e.g., weak-list clear after
                        // sweep) can hook in here.
                        VisitObjectSlot(InstanceBytes, Op.Offset, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::Delegate:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::MulticastInlineDelegate:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::MulticastSparseDelegate:
                        // Delegate slots carry an FObject* target at
                        // offset 0 of the delegate payload (per
                        // XCore-4b §5.5 FDelegateProperty layout). For
                        // the schema walker the first 8 bytes of the
                        // delegate slot ARE the target pointer.
                        //
                        // Multicast variants point at a TArray<FDelegate>
                        // (inline) or a TSet<FDelegate> (sparse); the
                        // schema vector emits one opcode per multicast
                        // slot, and the walker resolves the targets via
                        // the FDelegate's first-8-bytes invariant.
                        //
                        // Phase 5.g' MVP: the multicast variants are
                        // treated as a single-slot visit; the precise
                        // per-target walk is a Phase 5.g consumer
                        // responsibility once the delegate-list types
                        // are wired.
                        VisitObjectSlot(InstanceBytes, Op.Offset, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::ArrayOfObject:
                        VisitObjectArraySlot(InstanceBytes, Op.Offset, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::StridedArrayOfObject:
                        VisitStridedObjectArraySlot(InstanceBytes, Op.Offset,
                                                     Op.StrideBytes, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::ArrayOfStruct:
                        VisitStructArraySlot(InstanceBytes, Op.Offset,
                                              Op.StrideBytes, Op.NestedSchema, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::Struct:
                        VisitNestedStruct(InstanceBytes, Op.Offset,
                                           Op.NestedSchema, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::SetOfObject:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::MapOfObject_KeyValue:
                        // Phase 5.g' MVP: TSet<XPtr> + TMap<K,XPtr>
                        // dispatch via the strided-array walker shape
                        // (the underlying SwissTable storage is a
                        // flat array of {Hash, Key, Value} entries
                        // per XCore-4a §5.1 container layout). The
                        // precise per-element walk is Phase 5.g
                        // consumer responsibility; for the schema
                        // walker MVP, we route through the strided
                        // helper which reads the FIRST 8 bytes of each
                        // entry.
                        //
                        // The MapOfObject_KeyValue + SetOfObject struct
                        // variants (NestedSchema non-null) recurse via
                        // the structured walker; the primitive variant
                        // (NestedSchema nullptr) is the strided-
                        // pointer-only walk.
                        if (Op.NestedSchema != nullptr)
                        {
                            VisitStructArraySlot(InstanceBytes, Op.Offset,
                                                  Op.StrideBytes,
                                                  Op.NestedSchema, V);
                        }
                        else
                        {
                            VisitStridedObjectArraySlot(InstanceBytes,
                                                         Op.Offset,
                                                         Op.StrideBytes, V);
                        }
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::OptionalObject:
                        // TOptional<XObject*>: layout is {bSet (1 byte)
                        // + 7 bytes pad + Value (8 bytes pointer)}.
                        // The Value slot is at offset 8 within the
                        // optional storage; we treat the slot at
                        // Op.Offset+8 as the pointer.
                        //
                        // Phase 5.g' MVP: a bSet=false optional still
                        // visits the slot; the slot is null in that
                        // case and the visitor short-circuits.
                        VisitObjectSlot(InstanceBytes, Op.Offset + 8, V);
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::FieldPath:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::FieldPathArray:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::DynamicallyTypedValue:
                        // Phase 5.g' MVP: not yet wired end-to-end
                        // (these property kinds land post-MVP per
                        // XCore-4b §5.5 reserved bits). The schema
                        // walker reserves the dispatch path; XHT-emit
                        // does NOT emit these opcodes until the
                        // property kinds ship. No-op visit.
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::ARO:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::SlowARO:
                    case ::XCore::Reflect::EXObjectRefSchemaOp::MemberARO:
                        // Phase 5.g' MVP: AddReferencedObjects callbacks
                        // dispatch through the FClass's lifecycle
                        // FakeVTable slot. The walker does NOT carry
                        // the FClass* directly (only an FStruct); a
                        // future enhancement routes ARO opcodes through
                        // a sibling walker entry that takes FClass +
                        // FXObjectInstance and dispatches.
                        //
                        // For the Phase 5.g' MVP we skip; the slow-
                        // path consumer can dispatch ARO via FClass's
                        // lifecycle table directly.
                        break;

                    case ::XCore::Reflect::EXObjectRefSchemaOp::_Reserved255:
                    default:
                        // Hot-reload canary slot or unknown opcode.
                        // Defensive no-op; the runtime does NOT trust
                        // an unknown opcode (a corrupt schema in
                        // memory would surface as an unexpected enum
                        // value; we fall through cleanly).
                        break;
                }
            }
        }

    } // namespace SchemaWalkerDetail

    // -----------------------------------------------------------------
    // WalkSchemaRefs -- public entry point.
    //
    // Walks `Struct->RefSchema` against the bytes of `Instance`,
    // invoking `V(XObject*)` for every XObject reference encountered
    // (including null refs; the visitor null-checks).
    //
    // PRECONDITIONS:
    //   * Struct != nullptr.
    //   * Struct->RefSchema != nullptr is HANDLED (no-op if null since
    //     the struct has no GC-ref properties and there is nothing to
    //     walk).
    //   * Instance != nullptr.
    //
    // POSTCONDITIONS:
    //   * V invoked once per ref-slot in the schema's opcode order.
    //   * No allocations. No locks. No virtual dispatch.
    //
    // The walker is the GC mark-phase fast-path replacement for the
    // FProperty pointer-chase loop (XCore-4b FIX-13's slow-path
    // ObjectRefProperties walk). Per spec §7.4: target 4000+ refs/ms
    // on Quest 3 (Cortex-A78); the per-iteration cost is one indexed
    // load from .rodata + one indirect branch on the opcode kind.
    // -----------------------------------------------------------------
    template <typename Visitor>
    XPACT_FORCEINLINE void WalkSchemaRefs(const ::XCore::Reflect::FStruct* Struct,
                                           const void* Instance,
                                           Visitor V) noexcept;

    // -----------------------------------------------------------------
    // WalkSchemaRefsWithSchema -- entry point for callers that already
    // hold the FXObjectRefSchema directly (avoids the FStruct round-
    // trip when the schema pointer is in hand from a sibling walk).
    //
    // Same preconditions as WalkSchemaRefs except Schema is the input
    // directly (no FStruct accessor indirection).
    // -----------------------------------------------------------------
    template <typename Visitor>
    XPACT_FORCEINLINE void WalkSchemaRefsWithSchema(
        const ::XCore::Reflect::FXObjectRefSchema* Schema,
        const void* Instance,
        Visitor V) noexcept
    {
        XPACT_CHECK(Schema != nullptr);
        XPACT_CHECK(Instance != nullptr);
        SchemaWalkerDetail::WalkSchemaRefsImpl<Visitor>(
            Schema,
            static_cast<const ::std::uint8_t*>(Instance),
            V);
    }

} // namespace XCore

// FStruct's full type needed for GetRefSchema(); include after the
// template forward declarations so the walker body below can access
// the accessor inline.
#include "Reflection/FStruct.h"

namespace XCore
{
    template <typename Visitor>
    XPACT_FORCEINLINE void WalkSchemaRefs(const ::XCore::Reflect::FStruct* Struct,
                                           const void* Instance,
                                           Visitor V) noexcept
    {
        XPACT_CHECK(Struct != nullptr);
        XPACT_CHECK(Instance != nullptr);

        const ::XCore::Reflect::FXObjectRefSchema* Schema = Struct->GetRefSchema();
        if (Schema == nullptr)
        {
            // No schema vector emitted (the struct has no GC-ref
            // properties). Slow-path consumers walk
            // ObjectRefProperties instead.
            return;
        }

        SchemaWalkerDetail::WalkSchemaRefsImpl<Visitor>(
            Schema,
            static_cast<const ::std::uint8_t*>(Instance),
            V);
    }
} // namespace XCore
