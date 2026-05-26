// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FFakeVTable.h -- per-FProperty-subclass dispatch table (XCore-4b §5.4 +
// §5.4.1 + §11.2; FIX-2 + FIX-R2-MAJ-1 + FIX-R2-HIGH-1).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.4 ("FProperty FakeVTable dispatch") +
// Section 5.4.1 ("Fixed-table dispatch rationale") + Section 11.2 byte
// layout row `FFakeVTable: 128 per FProperty subclass in .rodata`.
//
// FFakeVTable is the per-FProperty-subclass dispatch table that replaces
// UE's per-instance C++ vtable. Each FProperty subclass has exactly one
// FFakeVTable constinit instance in `.rodata`; every FProperty instance
// carries an 8-byte pointer to its subclass's table at offset 96.
//
// HOT-RELOAD SAFETY (§5.4):
//
//   UE's FProperty has 25+ virtual methods (UnrealType.h:178). Every
//   subclass overrides several. The hot-reload risk is significant: any
//   patch DLL whose FProperty subclass overrides a virtual method must
//   be recompiled against the exact same vtable layout the runtime
//   expects, OR the patch DLL's vtable bytes must be slotted into the
//   runtime's known vtable layout. UE's pattern frequently breaks.
//
//   XPact's pattern eliminates the vtable. Each FProperty subclass's
//   FFakeVTable lives in .rodata and is named by the FFieldClass's
//   FakeVTable slot; a patch DLL compiled against the same
//   XPACT_FFAKEVTABLE_LAYOUT_TAG produces a binary-compatible table.
//
// FIXED-TABLE DISPATCH (§5.4.1):
//
//   The table is a FIXED 15-slot array (Rev 3 FIX-R2-MAJ-1 +
//   FIX-R2-HIGH-1). UE's analogous TStructOpsFakeVTable
//   (Class.h:1655-1721) uses a VARIABLE-LENGTH form with popcount-decode
//   at every dispatch site. Rev 3 deliberately diverges per Prime
//   Directive ("Use the thing that is correct not the thing that is
//   easier to implement"):
//
//     * Per-call dispatch speed dominates at our scale. Every
//       replication frame, every GC scan, every serialization round-
//       trip dispatches through these slots tens to hundreds of
//       thousands of times. The fixed table is a single indexed load
//       (`vtable->Slots[ESlot::Identical]`) -- one instruction.
//     * The footprint cost is bounded and small (28 subclasses x 128
//       bytes = ~3.5 KB total module-wide).
//     * Compile-time slot enumeration is readable (`ESlot::Identical`
//       vs `SlotPointerForCapability(vtable, CAP_HasIdentical)`).
//     * Sparse nulls are explicit and queryable via the `Capabilities`
//       bitmask.
//     * Variable-length popcount-encoding has bugs in UE history;
//       the fixed-table form is structurally immune.
//
// LAYOUT (locked at 128 bytes per Contract Rev 13.8 §11.2):
//
//   struct alignas(8) FFakeVTable {
//       uint32 Capabilities;        //  0  +4   bit N set => slot N populated
//       uint32 _reservedHeader;     //  4  +4   pad to 8-byte slot boundary
//       void(*Slots[15])(void);     //  8  +120 fifteen 8-byte function pointers
//   };
//
//   sizeof(FFakeVTable) == 128 = 8-byte header + 15 * 8 = 128.
//
// XPACT_FFAKEVTABLE_LAYOUT_TAG (Rev 3 §11.6):
//   "FFakeVTable-v2: 8-byte header (Capabilities uint32 + _reservedHeader
//    uint32) + 15 function-pointer slots (8 bytes each) = 128 bytes per
//    FProperty subclass in .rodata; ConvertFromType is slot index 14 (the
//    15th and last; ESlot enum 0-indexed)"
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <cstddef>      // offsetof
#include <type_traits>  // is_standard_layout etc.

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // ESlot -- compile-time enumeration of the 15 dispatch slots.
    //
    // The slot ordering is LOCKED at the Stage B addendum (Rev 3 §11.6
    // XPACT_FFAKEVTABLE_LAYOUT_TAG); changing the order ABI-breaks every
    // FFakeVTable constinit instance engine-wide.
    //
    // The values are 0-indexed; ESlot::Count is the sentinel (NOT a
    // real slot). The static_assert below pins Count == 15.
    //
    // Per §5.4 slot table (Rev 3 added ConvertFromType per FIX-R2-HIGH-1):
    //   0  GetValue              -- FProperty::GetValue_InContainer
    //   1  SetValue              -- FProperty::SetValue_InContainer
    //   2  CopySingleValue       -- FProperty::CopySingleValue
    //   3  CopyCompleteValue     -- FProperty::CopyCompleteValue
    //   4  InitializeValue       -- FProperty::InitializeValue
    //   5  DestroyValue          -- FProperty::DestroyValue
    //   6  Identical             -- FProperty::Identical
    //   7  SerializeItem         -- FProperty::SerializeItem
    //   8  NetSerializeItem      -- FProperty::NetSerializeItem
    //   9  ContainsObjectReference -- FProperty::ContainsObjectReference
    //  10  ExportText            -- FProperty::ExportText_Internal
    //  11  ImportText            -- FProperty::ImportText_Internal
    //  12  GetValueTypeHash      -- FProperty::GetValueTypeHash
    //  13  AppendToSchemaHash    -- FProperty::AppendSchemaHash
    //  14  ConvertFromType       -- FProperty::ConvertFromType (Rev 3 new)
    // -----------------------------------------------------------------
    enum class ESlot : ::uint32
    {
        GetValue                = 0,
        SetValue                = 1,
        CopySingleValue         = 2,
        CopyCompleteValue       = 3,
        InitializeValue         = 4,
        DestroyValue            = 5,
        Identical               = 6,
        SerializeItem           = 7,
        NetSerializeItem        = 8,
        ContainsObjectReference = 9,
        ExportText              = 10,
        ImportText              = 11,
        GetValueTypeHash        = 12,
        AppendToSchemaHash      = 13,
        ConvertFromType         = 14,   // Rev 3 added per FIX-R2-HIGH-1

        Count                   = 15,   // sentinel; NOT a real slot
    };

    static_assert(static_cast<::uint32>(ESlot::Count) == 15,
                  "FFakeVTable slot count locked at 15 (Rev 3; "
                  "GetValue..ConvertFromType inclusive)");

    // -----------------------------------------------------------------
    // FFakeVTable -- the 128-byte per-FProperty-subclass dispatch table.
    //
    // Per spec §5.4: alignas(8). NO virtual methods (hot-reload safety).
    //
    // The Capabilities bitmask declares which Slots are populated; bit N
    // set means Slots[N] is non-null. Callers SHOULD probe via
    // `HasSlot(ESlot::X)` before calling through `Slots[X]` to avoid
    // null-deref on unsupported operations.
    //
    // The function pointers are stored as `void(*)(void)` (a type-erased
    // function pointer) and cast at the call site via `GetSlot<T>()`.
    // The type erasure is necessary because the 15 slots have 15 distinct
    // signatures (no common base); a union of function-pointer types
    // would not be standard-layout. `void(*)(void)` is the canonical
    // type-erased shape used by every engine that ships a FakeVTable
    // pattern.
    // -----------------------------------------------------------------
    struct alignas(8) FFakeVTable
    {
        ::uint32 Capabilities;       //  0  +4   bit N set => Slots[N] is non-null
        ::uint32 _reservedHeader;    //  4  +4   pad to 8-byte boundary
        void   (*Slots[15])(void);   //  8  +120 fifteen 8-byte function pointers

        // -------------------------------------------------------------
        // HasSlot -- predicate over the Capabilities bitmask.
        //
        // Returns true if the slot is populated. Callers MUST check
        // this before dispatching through `Slots[X]` because some
        // operations (e.g., NetSerializeItem on a non-networked
        // property, ConvertFromType on a subclass without legacy
        // migration paths) are deliberately nullptr.
        //
        // O(1); branch-free (single AND + compare).
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool HasSlot(ESlot Slot) const noexcept
        {
            const ::uint32 Bit = ::uint32(1) << static_cast<::uint32>(Slot);
            return (Capabilities & Bit) != 0;
        }

        // -------------------------------------------------------------
        // GetSlot<T> -- typed accessor.
        //
        // Returns the function pointer at Slots[Slot] cast to the
        // caller-supplied signature `T`. Returns nullptr if the slot
        // is not populated (per Capabilities bitmask).
        //
        // `T` MUST be a function-pointer type (e.g.,
        // `void(*)(void* Dst, const void* Src)` for the CopySingleValue
        // signature). The cast goes through `reinterpret_cast`; the
        // caller bears the responsibility for matching the actual
        // signature stored at the slot. The ESlot enum's documented
        // signature in this header IS the contract.
        //
        // Usage example:
        //
        //   using FCopySingleValueFn = void(*)(void* Dst, const void* Src);
        //   if (auto* Fn = vtable->GetSlot<FCopySingleValueFn>(ESlot::CopySingleValue)) {
        //       Fn(DestBuffer, SrcBuffer);
        //   }
        //
        // The XPACT_CHECK guards against out-of-range Slot values
        // (Slot >= ESlot::Count); in Shipping the check compiles out.
        // -------------------------------------------------------------
        template <typename T>
        [[nodiscard]] XPACT_FORCEINLINE T GetSlot(ESlot Slot) const noexcept
        {
            XPACT_CHECK(static_cast<::uint32>(Slot) < static_cast<::uint32>(ESlot::Count));
            if (!HasSlot(Slot))
            {
                return nullptr;
            }
            return reinterpret_cast<T>(Slots[static_cast<::uint32>(Slot)]);
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (Contract Rev 13.8 §11.2). The static_asserts here ARE the
    // ABI contract. Any layout change breaks every FFakeVTable constinit
    // instance engine-wide.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FFakeVTable) == 128,
                  "FFakeVTable ABI lock: must be exactly 128 bytes "
                  "(8-byte header + 15 slots * 8 bytes = 128; "
                  "Rev 3 FIX-R2-MAJ-1 + FIX-R2-HIGH-1). See XCore-4b §11.2.");
    static_assert(alignof(FFakeVTable) == 8,
                  "FFakeVTable ABI lock: 8-byte alignment per §5.4 alignas(8)");

    // Member offsets locked per §11.2.
    static_assert(offsetof(FFakeVTable, Capabilities)    == 0,
                  "FFakeVTable ABI lock: Capabilities at offset 0");
    static_assert(offsetof(FFakeVTable, _reservedHeader) == 4,
                  "FFakeVTable ABI lock: _reservedHeader at offset 4");
    static_assert(offsetof(FFakeVTable, Slots)           == 8,
                  "FFakeVTable ABI lock: Slots[15] starts at offset 8 "
                  "(immediately after the 8-byte header)");
    static_assert(sizeof(FFakeVTable::Slots) == 15 * 8,
                  "FFakeVTable ABI lock: Slots[15] is 15 * 8 = 120 bytes "
                  "(15 function pointers each 8 bytes)");

    // Type traits.
    static_assert(::std::is_standard_layout_v<FFakeVTable>,
                  "FFakeVTable must be standard layout (offsetof must be "
                  "well-defined; constinit-friendly aggregate initialisation)");
    static_assert(::std::is_trivially_copyable_v<FFakeVTable>,
                  "FFakeVTable must be trivially copyable (no virtual methods)");
    static_assert(::std::is_trivially_destructible_v<FFakeVTable>,
                  "FFakeVTable must be trivially destructible (constinit + "
                  ".rodata residency requires no per-instance teardown)");

    // -----------------------------------------------------------------
    // Capability-bitmask helpers.
    //
    // CapabilityBit(Slot) returns the single-bit pattern for Slot's
    // position in the Capabilities mask. Used by constinit FFakeVTable
    // initialisers to compose the Capabilities mask from the populated
    // slots without hand-counting bit positions.
    //
    // CapabilityMask(Slot, Slot, ...) ORs multiple slot bits together
    // (used by static-init: see the FBoolProperty registration example
    // in §5.4 of the spec).
    // -----------------------------------------------------------------

    [[nodiscard]] XPACT_FORCEINLINE constexpr ::uint32 CapabilityBit(ESlot Slot) noexcept
    {
        return ::uint32(1) << static_cast<::uint32>(Slot);
    }

} // namespace XCore::Reflect
