// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectLifecycleTable.h -- 72-byte per-FClass lifecycle dispatch
// table (XCoreXObject Rev 4 §2.4 + Contract Rev 13.9 addendum tag
// XPACT_XOBJECT_LIFECYCLE_TABLE_TAG).
// =====================================================================
//
// XCoreXObject Rev 4 Section 2.4 ("XObject lifecycle dispatch
// (FakeVTable pattern extended to XObject)") + Contract Rev 13.9
// `XPACT_XOBJECT_LIFECYCLE_TABLE_TAG`:
//
//   "FXObjectLifecycleTable-v1: 72 bytes per FClass; 8-byte header
//    (Capabilities@0 + _pad@4) + 8 slots * 8 bytes = 64 bytes;
//    alignof = 8. Serialize slot signature:
//    void(*)(XObject*, FArchive&, const FArchiveContext*) per
//    FIX-A-HIGH-13."
//
// PURPOSE: per-FClass FakeVTable that holds lifecycle function-pointer
// dispatch slots. UE's UObject has ~30 virtual methods (PostInitProperties,
// BeginDestroy, FinishDestroy, IsReadyForFinishDestroy, Serialize,
// PostLoad, PreSave, ...); each is a hot-reload risk because a patched
// DLL whose user-class overrides a virtual must be rebuilt against the
// exact same vtable layout. XPact's pattern moves the dispatch into
// .rodata-resident function-pointer tables emitted per-FClass by XHT,
// so a patched DLL may change a user-class's behaviour without rewriting
// any vtable -- there isn't one to rewrite.
//
// HOT-RELOAD COMMITMENT: NO virtual methods on this struct. The table
// IS a POD layout of 8-byte function-pointer slots + an 8-byte header.
// Patched DLLs publish a NEW FXObjectLifecycleTable in their own .rodata
// (the per-FClass slot in FClass::LifecycleTable is rebound during the
// hot-reload cascade; the old table's lifetime ends with the unloaded
// DLL). Per spec §2.4 + §9.3.
//
// CAPABILITIES BITMASK (per spec §2.4):
//
//   The first 32-bit word of the table is a bitmask: bit N set iff
//   Slots[N] is non-null. The runtime dispatcher reads the bitmask
//   FIRST and short-circuits the call if the bit is clear. This avoids
//   the null-check on the function pointer in the hot path (the
//   Capabilities load is one 4-byte read; the slot load is an 8-byte
//   read + indirect call, which we want to skip if the bit is clear).
//
//   Bits 0..7 are the 8 active lifecycle slots; bits 8..31 are
//   reserved for future hooks. The bitmask MUST be consistent with the
//   actual slot non-nullness (an emit that sets a slot but doesn't set
//   the bit is a bug; an emit that sets the bit but leaves the slot
//   null is a bug). XHT's emit path is the single source of truth.
//
// SERIALIZE SLOT SIGNATURE (per Rev 2 FIX-A-HIGH-13):
//
//   void(*Serialize)(XObject*, FArchive&, const void* ArchiveContext);
//
//   The third argument `const void* ArchiveContext` is the XSerialization
//   forward-compatibility indirection. XSerialization is the strong-
//   symbol provider of the Serialize implementation; the FArchiveContext
//   layout is XSerialization's concern. XCoreXObject's lifecycle table
//   carries the pointer typed as `const void*` so XSerialization can
//   extend the context type without breaking the table ABI. Per spec
//   §2.4 trailing prose: "This is the standard void*-style forward-
//   compat pattern: the type's identity is fixed (FArchiveContext); its
//   layout is XSerialization's concern."
//
// FArchive (per Rev 2 FIX-A-HIGH-13): FArchive lands in XSerialization
// (Layer 9; post-XCoreXObject). We forward-declare it in the
// XCore::Reflect namespace; the function-pointer signature uses the
// forward declaration only (no dereference happens in XCoreXObject's
// own dispatch path; XSerialization owns the full FArchive type when
// its Serialize implementation is invoked through this slot).
//
// FOOTPRINT (per spec §2.4 + spec §1.4 prose):
//
//   72 bytes per FClass * ~10k typical classes = ~720 KB module
//   aggregate. The table is in .rodata (shared, demand-paged); the
//   resident-set cost is bounded by hot-class working set.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/XObject.h"             // XObject* in slot signatures
#include "Reflection/FName.h"            // FName in ConvertFromType slot

#include <cstddef>          // offsetof
#include <cstdint>
#include <type_traits>      // is_standard_layout

// Forward declarations.
//
// FArchive ships in XSerialization (Layer 9; post-XCoreXObject). The
// Serialize slot's signature names it as a reference parameter; the
// forward declaration is sufficient because XCoreXObject's own
// dispatch path never dereferences the FArchive (the strong-symbol
// implementation provided by XSerialization owns the full type at
// the moment of invocation).
//
// Per Rev 2 FIX-A-HIGH-13 + spec §2.4: the third argument to Serialize
// is typed as `const void* ArchiveContext` so the FArchiveContext
// type (also XSerialization-owned) can evolve without disturbing the
// table ABI. The forward declaration is the conservative posture; if
// a future revision pulls FArchive into XCoreXObject's own surface,
// the typedef can be relaxed without ABI churn.
namespace XCore::Reflect { class FArchive; }

// =====================================================================
// NAMESPACE PLACEMENT (per spec §2.4 + XObject.h Phase 5.a + FClass.h
// Phase 5.a' forward declarations):
//
// FXObjectLifecycleTable lives in `XCore::Reflect` (matches the
// forward-decls in XObject.h:132 and FClass.h:215 + 364). The
// type semantically belongs with the FClass FakeVTable pattern --
// FClass references it as a member pointer slot; the dispatcher in
// XCoreXObject reads through that pointer.
//
// The slot signatures use `::XCore::XObject*` (the XObject base lives
// in `XCore`). The cross-namespace reference is well-formed because
// XCore-4b's Reflection types routinely point at XCore::XObject (e.g.,
// FObjectProperty's payload).
// =====================================================================

namespace XCore::Reflect
{

    // -----------------------------------------------------------------
    // EXObjectLifecycleSlot -- the 8 slot indices (per spec §2.4).
    //
    // Per spec the table is "8 slots * 8 bytes = 64 bytes" (plus the
    // 8-byte header for 72 total). The slot indices are enumerated
    // here as an enum class for typed access; the slot table itself
    // is an array of 8 type-erased `void(*)(void)` pointers cast at
    // dispatch time to the per-slot signature.
    //
    // SLOT TABLE (per spec §2.4 + Rev 2 FIX-A-HIGH-13):
    //
    //   * PostInitProperties      -- void(*)(XObject*)
    //                                fires after NewObject construction
    //                                + default-value copy. Spec §3.5 +
    //                                §8.4 (the FXObjectInitializer
    //                                destructor is the canonical trigger).
    //   * BeginDestroy            -- void(*)(XObject*)
    //                                first-phase destruction; instance
    //                                is still mostly valid. Spec §2.5.
    //   * IsReadyForFinishDestroy -- bool(*)(const XObject*)
    //                                returns true when FinishDestroy is
    //                                safe to call (the deferred-
    //                                destruction queue polls this).
    //   * FinishDestroy           -- void(*)(XObject*)
    //                                second-phase destruction; instance
    //                                bytes are about to be freed.
    //   * Serialize               -- void(*)(XObject*, FArchive&, const void*)
    //                                XSerialization's Serialize-the-
    //                                object path (Rev 2 FIX-A-HIGH-13
    //                                third arg is FArchiveContext void*
    //                                for forward-compat).
    //   * AddReferencedObjects    -- void(*)(XObject*, void*)
    //                                explicit GC root-walk hook for
    //                                native-only types that hold XObject
    //                                pointers in non-reflected slots
    //                                (editor caches, runtime pools).
    //                                The second arg is the gray-queue
    //                                pointer; XCoreXObject Phase 5.h+
    //                                ships the type. Phase 5.d ships
    //                                the slot with a void* placeholder
    //                                second-arg per the forward-decl
    //                                discipline.
    //   * PostLoad                -- void(*)(XObject*)
    //                                post-deserialize; runs after the
    //                                entire object graph is loaded.
    //   * ConvertFromType         -- void(*)(XObject*, FName, const void*, size_t)
    //                                schema-migration hook: when an
    //                                old-format property is read by the
    //                                loader and the type has changed,
    //                                this hook converts the OldData
    //                                bytes (of size OldSize, named by
    //                                OldTypeName) into the new layout.
    //                                XPact-specific design choice
    //                                (vs UE's separate ConvertFromType
    //                                that lives on FProperty). Spec
    //                                §2.4 + §10.6 trailing prose.
    //
    //   Phase 5.d ships PreSave conceptually under the "future hooks"
    //   bit 8+ slot range; the spec §2.4 names PreSave as slot 7 but
    //   Rev 2 FIX-A-HIGH-13 and the user dispatch wording on Phase 5.d
    //   reorder it so the SerializeFn slot is slot 4 and ConvertFromType
    //   is slot 7. We follow the dispatch wording for the slot ordering
    //   (the Capabilities-bit assignments stay consistent so XHT-emit
    //   sees a stable contract).
    //
    // EXTENSION HOOK: bits 8..31 of Capabilities are RESERVED for
    // future lifecycle hooks. The slot table itself is FIXED at 8 slots
    // per the spec §2.4 + Contract Rev 13.9 72-byte ABI lock; future
    // hooks added to the Capabilities bit range MUST be backed by
    // out-of-band storage (not in this 64-byte slot array) until a
    // future Contract revision expands the slot count.
    // -----------------------------------------------------------------
    enum class EXObjectLifecycleSlot : ::std::uint32_t
    {
        PostInitProperties        = 0,
        BeginDestroy              = 1,
        IsReadyForFinishDestroy   = 2,
        FinishDestroy             = 3,
        AddReferencedObjects      = 4,
        Serialize                 = 5,
        PostLoad                  = 6,
        ConvertFromType           = 7,
        Count                     = 8,
    };

    static_assert(static_cast<::std::uint32_t>(EXObjectLifecycleSlot::Count) == 8,
                  "EXObjectLifecycleSlot count lock: 8 slots per spec §2.4. "
                  "The 72-byte table ABI is 8 header + 8 slots * 8 bytes; "
                  "changing the slot count breaks XPACT_XOBJECT_LIFECYCLE_TABLE_TAG.");

    // -----------------------------------------------------------------
    // EXObjectLifecycleCapability -- bit positions for the Capabilities
    // bitmask (per spec §2.4).
    //
    // bit N set iff Slots[N] is non-null. The runtime dispatcher uses
    // this to short-circuit dispatch on slots the user-class didn't
    // implement. The bit positions match the EXObjectLifecycleSlot
    // values (bit N corresponds to slot N).
    //
    // Bits 8..31 RESERVED for future hooks per spec §2.4 trailing
    // prose. New engine lifecycle hooks should consume from this range
    // FIRST (preserving the spec-defined slot 0..7 assignments).
    // -----------------------------------------------------------------
    enum class EXObjectLifecycleCapability : ::std::uint32_t
    {
        None                       = 0,

        HasPostInitProperties      = 1u << 0,
        HasBeginDestroy            = 1u << 1,
        HasIsReadyForFinishDestroy = 1u << 2,
        HasFinishDestroy           = 1u << 3,
        HasAddReferencedObjects    = 1u << 4,
        HasSerialize               = 1u << 5,
        HasPostLoad                = 1u << 6,
        HasConvertFromType         = 1u << 7,

        // Bits 8..31: RESERVED for future hooks. Consumers MUST NOT
        // depend on these bits being clear; XHT-emit may begin
        // populating them in a future Contract revision.
    };

    // -----------------------------------------------------------------
    // Bitwise operator surface for EXObjectLifecycleCapability.
    //
    // Strongly-typed enum class with constexpr bitwise operators (the
    // same shape as EObjectFlags / EClassFlags / EClassCastFlags).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr EXObjectLifecycleCapability
        operator|(EXObjectLifecycleCapability Lhs, EXObjectLifecycleCapability Rhs) noexcept
    {
        using U = ::std::underlying_type_t<EXObjectLifecycleCapability>;
        return static_cast<EXObjectLifecycleCapability>(
            static_cast<U>(Lhs) | static_cast<U>(Rhs));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr EXObjectLifecycleCapability
        operator&(EXObjectLifecycleCapability Lhs, EXObjectLifecycleCapability Rhs) noexcept
    {
        using U = ::std::underlying_type_t<EXObjectLifecycleCapability>;
        return static_cast<EXObjectLifecycleCapability>(
            static_cast<U>(Lhs) & static_cast<U>(Rhs));
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr EXObjectLifecycleCapability
        operator~(EXObjectLifecycleCapability Value) noexcept
    {
        using U = ::std::underlying_type_t<EXObjectLifecycleCapability>;
        return static_cast<EXObjectLifecycleCapability>(~static_cast<U>(Value));
    }

    XPACT_FORCEINLINE constexpr EXObjectLifecycleCapability&
        operator|=(EXObjectLifecycleCapability& Lhs, EXObjectLifecycleCapability Rhs) noexcept
    {
        Lhs = Lhs | Rhs;
        return Lhs;
    }

    [[nodiscard]] XPACT_FORCEINLINE constexpr ::std::uint32_t
        ToUnderlying(EXObjectLifecycleCapability Value) noexcept
    {
        return static_cast<::std::uint32_t>(Value);
    }

    // -----------------------------------------------------------------
    // Typed function-pointer aliases for each slot (per spec §2.4 +
    // Rev 2 FIX-A-HIGH-13).
    //
    // The slot table stores function pointers as `void(*)(void)`
    // (the smallest correctly-aligned function-pointer type); the
    // dispatcher casts to the per-slot signature at the call site. The
    // typedefs below are the canonical signatures the cast targets.
    //
    // ALL signatures are `noexcept`: XPact's lifecycle hooks MUST NOT
    // throw (the engine has no exception machinery beyond the
    // FXObjectInitializer destructor's no-throw guarantee). Throwing
    // hooks would cascade into std::terminate() through the noexcept
    // signature; HARD diagnostic at the call site.
    // -----------------------------------------------------------------

    using FXObjectPostInitPropertiesFn =
        void(*)(::XCore::XObject* Self) noexcept;

    using FXObjectBeginDestroyFn =
        void(*)(::XCore::XObject* Self) noexcept;

    using FXObjectIsReadyForFinishDestroyFn =
        bool(*)(const ::XCore::XObject* Self) noexcept;

    using FXObjectFinishDestroyFn =
        void(*)(::XCore::XObject* Self) noexcept;

    // AddReferencedObjects: the gray-queue type lands at Phase 5.h+
    // (FXGrayQueue per spec §5). Phase 5.d uses `void*` for the second
    // argument so the signature is stable across the Phase 5.h ship.
    // The cast at the call site (Phase 5.h's collector mark pass) is
    // an explicit reinterpret of `void*` to `FXGrayQueue*`.
    using FXObjectAddReferencedObjectsFn =
        void(*)(::XCore::XObject* Self, void* OutRefs) noexcept;

    // Serialize: third argument is FArchiveContext void* per Rev 2
    // FIX-A-HIGH-13. FArchive ships in XSerialization (Layer 9); the
    // forward declaration is sufficient because XCoreXObject's own
    // dispatch path never instantiates FArchive (only invokes the
    // strong-symbol implementation that XSerialization provides).
    using FXObjectSerializeFn =
        void(*)(::XCore::XObject* Self, ::XCore::Reflect::FArchive& Ar,
                const void* ArchiveContext) noexcept;

    using FXObjectPostLoadFn =
        void(*)(::XCore::XObject* Self) noexcept;

    // ConvertFromType: schema-migration hook. The loader detects an
    // old-format property and invokes this hook with:
    //   * OldTypeName -- the FName of the old type (e.g.,
    //                     FName("XOldComponent"))
    //   * OldData     -- a pointer to the deserialized old-format
    //                     bytes (XSerialization owns the lifetime)
    //   * OldSize     -- size in bytes of OldData
    //
    // The hook reads OldData and writes the converted value into the
    // appropriate field on Self. XPact's design moves the conversion
    // hook from UE's per-FProperty location (UE's ConvertFromType) to
    // the XObject lifecycle table per spec §2.4 -- the per-class
    // conversion logic is easier to author + audit than per-property.
    using FXObjectConvertFromTypeFn =
        void(*)(::XCore::XObject* Self, ::XCore::Reflect::FName OldTypeName,
                const void* OldData, ::SIZE_T OldSize) noexcept;

    // -----------------------------------------------------------------
    // FXObjectLifecycleTable -- 72-byte per-FClass dispatch table.
    //
    // Per spec §2.4 + Contract Rev 13.9 XPACT_XOBJECT_LIFECYCLE_TABLE_TAG:
    //
    //   * Capabilities@0  (4 bytes) -- bit N set iff Slots[N] non-null.
    //   * _padHeader@4    (4 bytes) -- alignment padding.
    //   * Slots[0..7]@8   (64 bytes) -- 8 * 8-byte function pointers.
    //
    // Total: 72 bytes; alignas(8).
    //
    // LAYOUT-LOCKED at Contract Rev 13.9. The table is .rodata-resident
    // (XHT emits a `constinit const FXObjectLifecycleTable` per FClass
    // alongside the .gen.cpp's FClass instance); the per-FClass slot
    // FClass::LifecycleTable points at the table.
    //
    // CONSTRUCTION: XHT-emitted code uses aggregate-initialisation:
    //
    //   constinit const FXObjectLifecycleTable XActor_LifecycleTable = {
    //       /*Capabilities=*/ ToUnderlying(
    //           EXObjectLifecycleCapability::HasPostInitProperties |
    //           EXObjectLifecycleCapability::HasBeginDestroy),
    //       /*_padHeader=*/ 0,
    //       /*Slots=*/ {
    //           reinterpret_cast<FXObjectGenericFn>(&Z_PostInitProperties_XActor),
    //           reinterpret_cast<FXObjectGenericFn>(&Z_BeginDestroy_XActor),
    //           nullptr, nullptr, nullptr, nullptr, nullptr, nullptr,
    //       },
    //   };
    //
    // The Slots array is `FXObjectGenericFn[8]` (typedef below); each
    // entry is cast at the dispatch call site to the per-slot typed
    // signature. The cast from a typed function-pointer to the generic
    // form + back is well-defined per [expr.reinterpret.cast]/6.
    //
    // The FXObjectLifecycleTable IS trivially-copyable (all fields are
    // POD scalars / function pointers) so std::bit_cast or memcpy on
    // the table bytes is well-defined; the destructor is trivial. The
    // type IS standard-layout (no virtual methods, no mixed access
    // control, no multiple inheritance, all members same access).
    // -----------------------------------------------------------------

    // Generic function-pointer type the Slots array stores.
    //
    // `void(*)(void)` is the smallest function-pointer type guaranteed
    // by the standard to round-trip through reinterpret_cast back to
    // the original typed function pointer ([expr.reinterpret.cast]/6).
    // We add a noexcept marker so casting noexcept lifecycle hooks
    // through this type does not lose the noexcept information.
    using FXObjectGenericFn = void(*)() noexcept;

    struct alignas(8) FXObjectLifecycleTable
    {
        // =============================================================
        // Header (offset 0; 8 bytes).
        // =============================================================

        // Bitmask of populated slots (bit N <=> Slots[N] != nullptr).
        // The dispatcher reads this FIRST to short-circuit calls to
        // unimplemented hooks; populated as
        // `ToUnderlying(EXObjectLifecycleCapability::Has*)` OR-composed
        // per the slots emitted.
        //
        // The bitmask MUST be consistent with the actual non-nullness
        // of the Slots array (an emit that sets a slot but doesn't set
        // the bit is a bug; an emit that sets the bit but leaves the
        // slot null is a bug). XHT's emit path is the single source of
        // truth.
        ::std::uint32_t       Capabilities;       //  0  +4

        // 4 bytes of alignment padding so the Slots array lands at
        // offset 8 (8-byte aligned). Always zero. The pin in
        // XPACT_XOBJECT_LIFECYCLE_TABLE_TAG calls this slot `_pad`;
        // here we use the more descriptive `_padHeader` name.
        ::std::uint32_t       _padHeader;         //  4  +4

        // =============================================================
        // Slot table (offset 8; 64 bytes).
        //
        // Eight function pointers, one per EXObjectLifecycleSlot enum
        // value. Each slot is stored as the generic FXObjectGenericFn
        // type; the dispatch call site casts to the per-slot typed
        // signature.
        // =============================================================

        FXObjectGenericFn     Slots[8];           //  8  +64

        // =============================================================
        // HasCapability -- predicate against the Capabilities bitmask.
        //
        // Constexpr + noexcept; the dispatch call site is:
        //
        //   if (Table->HasCapability(EXObjectLifecycleCapability::HasPostInitProperties))
        //   {
        //       auto Fn = reinterpret_cast<FXObjectPostInitPropertiesFn>(
        //           Table->Slots[size_t(EXObjectLifecycleSlot::PostInitProperties)]);
        //       Fn(Self);
        //   }
        //
        // Reads the Capabilities word; bitmask-AND against the queried
        // capability; non-zero result means the bit is set.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool
            HasCapability(EXObjectLifecycleCapability Bit) const noexcept
        {
            return (Capabilities & ToUnderlying(Bit)) != 0u;
        }

        // Convenience HasSlot taking the enum class slot index. The
        // dispatcher form is:
        //
        //   if (Table->HasSlot(EXObjectLifecycleSlot::PostInitProperties)) { ... }
        //
        // Internally translates the slot index to the corresponding
        // capability bit (slot N <=> bit N) and queries the bitmask.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool
            HasSlot(EXObjectLifecycleSlot Slot) const noexcept
        {
            const ::std::uint32_t Bit =
                1u << static_cast<::std::uint32_t>(Slot);
            return (Capabilities & Bit) != 0u;
        }

        // GetSlot<Slot> -- typed slot fetch.
        //
        // Returns the slot's function pointer cast to the per-slot
        // typed signature. Returns nullptr if the slot is unpopulated
        // (the caller MUST check HasSlot or test the returned pointer
        // before calling). The `if constexpr` chain on the slot enum
        // value resolves the slot-to-signature mapping at compile
        // time; the optimiser collapses the chain to a single load +
        // cast at every call site.
        //
        // USAGE:
        //
        //   auto Fn = Table->GetSlot<EXObjectLifecycleSlot::PostInitProperties>();
        //   if (Fn) Fn(Self);
        //
        // The reinterpret_cast is well-defined per
        // [expr.reinterpret.cast]/6 (round-trip through the generic
        // function-pointer type FXObjectGenericFn). The cast preserves
        // the noexcept qualification because FXObjectGenericFn is
        // itself noexcept.
        template <EXObjectLifecycleSlot Slot>
        [[nodiscard]] XPACT_FORCEINLINE auto GetSlot() const noexcept
        {
            if constexpr (Slot == EXObjectLifecycleSlot::PostInitProperties)
            {
                return reinterpret_cast<FXObjectPostInitPropertiesFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::BeginDestroy)
            {
                return reinterpret_cast<FXObjectBeginDestroyFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::IsReadyForFinishDestroy)
            {
                return reinterpret_cast<FXObjectIsReadyForFinishDestroyFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::FinishDestroy)
            {
                return reinterpret_cast<FXObjectFinishDestroyFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::AddReferencedObjects)
            {
                return reinterpret_cast<FXObjectAddReferencedObjectsFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::Serialize)
            {
                return reinterpret_cast<FXObjectSerializeFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::PostLoad)
            {
                return reinterpret_cast<FXObjectPostLoadFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else if constexpr (Slot == EXObjectLifecycleSlot::ConvertFromType)
            {
                return reinterpret_cast<FXObjectConvertFromTypeFn>(
                    Slots[static_cast<::std::size_t>(Slot)]);
            }
            else
            {
                // Slot enum value out of the documented 0..7 range
                // (typically EXObjectLifecycleSlot::Count itself, used
                // as the sentinel). Returns nullptr as a fallback;
                // callers MUST not invoke GetSlot with the sentinel.
                static_assert(static_cast<::std::uint32_t>(Slot)
                              < static_cast<::std::uint32_t>(EXObjectLifecycleSlot::Count),
                              "GetSlot<Slot> called with out-of-range slot value");
                return reinterpret_cast<FXObjectGenericFn>(nullptr);
            }
        }
    };

    // -----------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.1 tag
    // XPACT_XOBJECT_LIFECYCLE_TABLE_TAG + spec §2.4 + §11.3
    // XPACT_VERIFY_XOBJECT_LAYOUT macro pin).
    //
    // The static_asserts here ARE the ABI contract. Any byte-layout
    // change breaks:
    //   * Every XHT-emitted `.gen.cpp` that aggregate-initialises a
    //     constinit FXObjectLifecycleTable.
    //   * Every dispatcher load that reads Capabilities at offset 0.
    //   * Every Slots[N] access (the slot stride is 8 bytes; changing
    //     the alignment would shift every slot).
    // -----------------------------------------------------------------

    static_assert(sizeof(FXObjectLifecycleTable) == 72,
                  "FXObjectLifecycleTable ABI lock (XPACT_XOBJECT_LIFECYCLE_TABLE_TAG): "
                  "72 bytes = 8 header (Capabilities@0 + _padHeader@4) + "
                  "8 slots * 8 bytes = 64. Per XCoreXObject Rev 4 §2.4 + "
                  "Contract Rev 13.9 micro-bump.");

    static_assert(alignof(FXObjectLifecycleTable) == 8,
                  "FXObjectLifecycleTable ABI lock: 8-byte aligned per spec §2.4 "
                  "alignas(8); load-bearing for the Slots[] array alignment.");

    static_assert(offsetof(FXObjectLifecycleTable, Capabilities) == 0,
                  "FXObjectLifecycleTable.Capabilities offset lock (4-byte "
                  "bitmask at offset 0 per XPACT_XOBJECT_LIFECYCLE_TABLE_TAG).");

    static_assert(offsetof(FXObjectLifecycleTable, _padHeader) == 4,
                  "FXObjectLifecycleTable._padHeader offset lock (4 bytes of "
                  "alignment padding at offset 4).");

    static_assert(offsetof(FXObjectLifecycleTable, Slots) == 8,
                  "FXObjectLifecycleTable.Slots offset lock (8-byte function "
                  "pointers starting at offset 8; 8 * 8 = 64 bytes).");

    static_assert(sizeof(FXObjectLifecycleTable::Capabilities) == 4,
                  "FXObjectLifecycleTable.Capabilities field-size lock (uint32).");

    static_assert(sizeof(FXObjectLifecycleTable::_padHeader) == 4,
                  "FXObjectLifecycleTable._padHeader field-size lock (uint32).");

    static_assert(sizeof(FXObjectLifecycleTable::Slots) == 64,
                  "FXObjectLifecycleTable.Slots array-size lock "
                  "(8 slots * 8 bytes = 64).");

    // Trait locks.
    //
    // The table MUST be trivially-copyable + trivially-destructible so
    // .rodata constinit construction is well-defined and slab teardown
    // is free. Standard-layout is preserved (no virtuals, all members
    // public, no multiple inheritance) so the offsetof macros are
    // well-defined on every supported compiler.
    static_assert(::std::is_trivially_copyable_v<FXObjectLifecycleTable>,
                  "FXObjectLifecycleTable must be trivially copyable "
                  "(constinit .rodata initialisation + cross-DLL copy semantics).");
    static_assert(::std::is_trivially_destructible_v<FXObjectLifecycleTable>,
                  "FXObjectLifecycleTable must be trivially destructible "
                  "(.rodata constinit lifetime; no destructor to call at shutdown).");
    static_assert(::std::is_standard_layout_v<FXObjectLifecycleTable>,
                  "FXObjectLifecycleTable must be standard-layout (POD-throughout; "
                  "offsetof correctness across all supported compilers).");
    static_assert(!::std::is_polymorphic_v<FXObjectLifecycleTable>,
                  "FXObjectLifecycleTable must NOT be polymorphic (hot-reload "
                  "commitment: NO virtual methods anywhere on this surface).");

} // namespace XCore::Reflect

// =====================================================================
// XCore namespace re-export aliases (per spec §2.4 +
// FXObjectInitializer.cpp dispatch path).
//
// The dispatch path in XCoreXObject (FXObjectInitializer + the
// future Phase 5.h FXObjectCollector mark loop) reads the
// lifecycle table off the FClass pointer; the consumer's typical
// access pattern is `Class->LifecycleTable->Slots[N]`. The aliases
// below let XCoreXObject consumers spell the type as
// `XCore::FXObjectLifecycleTable` without forcing every consumer to
// reach into `XCore::Reflect`. This is the same re-export pattern
// XCore-4b uses for FName at `XCore::FName` (the canonical type
// lives in XCore::Reflect; the consumer-facing alias is in XCore).
// =====================================================================
namespace XCore
{
    // Re-export of the canonical type.
    using FXObjectLifecycleTable = ::XCore::Reflect::FXObjectLifecycleTable;

    // Re-export of the slot enum + capability bitmask.
    using EXObjectLifecycleSlot       = ::XCore::Reflect::EXObjectLifecycleSlot;
    using EXObjectLifecycleCapability = ::XCore::Reflect::EXObjectLifecycleCapability;

    // Re-export of the typed function-pointer aliases.
    using FXObjectGenericFn                   = ::XCore::Reflect::FXObjectGenericFn;
    using FXObjectPostInitPropertiesFn        = ::XCore::Reflect::FXObjectPostInitPropertiesFn;
    using FXObjectBeginDestroyFn              = ::XCore::Reflect::FXObjectBeginDestroyFn;
    using FXObjectIsReadyForFinishDestroyFn   = ::XCore::Reflect::FXObjectIsReadyForFinishDestroyFn;
    using FXObjectFinishDestroyFn             = ::XCore::Reflect::FXObjectFinishDestroyFn;
    using FXObjectAddReferencedObjectsFn      = ::XCore::Reflect::FXObjectAddReferencedObjectsFn;
    using FXObjectSerializeFn                 = ::XCore::Reflect::FXObjectSerializeFn;
    using FXObjectPostLoadFn                  = ::XCore::Reflect::FXObjectPostLoadFn;
    using FXObjectConvertFromTypeFn           = ::XCore::Reflect::FXObjectConvertFromTypeFn;

    // Re-export of the ToUnderlying overload (capability bitmask -> uint32).
    //
    // Note: XCore namespace already has a ToUnderlying(EObjectFlags) from
    // EObjectFlags.h; the overload set is extended with the capability
    // variant via a free function here. We use a regular function (not
    // `using ::XCore::Reflect::ToUnderlying`) so the overload resolution
    // doesn't ambiguate with the EObjectFlags overload.
    [[nodiscard]] XPACT_FORCEINLINE constexpr ::std::uint32_t
        ToUnderlying(EXObjectLifecycleCapability Value) noexcept
    {
        return ::XCore::Reflect::ToUnderlying(Value);
    }
} // namespace XCore
