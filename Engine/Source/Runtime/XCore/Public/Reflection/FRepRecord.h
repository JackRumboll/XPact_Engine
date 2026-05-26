// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FRepRecord.h -- per-replicated-property record (XCore-4b §6.5 + §11.3;
// FIX-7).
// =====================================================================
//
// XCore-4b Rev 3, Section 6.5 ("RepIndex + FRepRecord") + Section 11.3
// ("FClass / FStruct / ... layouts") row `FRepRecord: 16 per ClassReps
// entry; {FProperty* Property; int32 Index}`.
//
// FRepRecord is a single entry in `FClass::ClassReps` (a
// `TArray<FRepRecord>` populated at FClass::Link time). Per FIX-7, the
// ClassReps shape is `TArray<FRepRecord>` (not `TArray<FProperty*>`) so
// it can represent `XPROPERTY(Replicated) float Stamina[4]` correctly:
// one FRepRecord entry per element of an `ArrayDim > 1` replicated
// property. The `Index` field is the element index within the property's
// fixed-size array (0..ArrayDim-1); for non-array (ArrayDim == 1)
// replicated properties, exactly one FRepRecord with `Index == 0` is
// emitted.
//
// LAYOUT (locked at 16 bytes per Contract Rev 13.8 §11.3):
//
//   struct alignas(8) FRepRecord {
//       FProperty* Property;   //  0  +8   the replicated property
//       int32      Index;      //  8  +4   ArrayDim element index
//       int32      _pad;       // 12  +4   pad to 16-byte alignment
//   };
//
// Total: 16 bytes. Matches UE's `Class.h:3984` `FRepRecord` layout
// exactly (Rev 3 FIX-R2-MED-3 corrected the cite from Rev 2's `:3915`).
//
// HOT-RELOAD SAFETY:
//
//   * Plain trivially-copyable aggregate; no virtuals; no per-instance
//     dispatch surface.
//   * Standard-layout for well-defined offsetof.
//   * Stored by value inside `TArray<FRepRecord>`; the array's growth
//     copies bytewise.
//
// USAGE FROM FCLASS::LINK (Phase 4b.5):
//
//   The link operation walks the FProperty chain (PropertyLink in
//   FStruct) and, for every property whose PropertyFlags include
//   `CPF_Net`, emits `ArrayDim` FRepRecord entries into
//   `FClass::ClassReps`. The position within ClassReps is the
//   property's `RepIndex`. NetIndex is encoded as the ClassReps
//   array position (per FIX-19); no per-FProperty NetIndex field
//   exists.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <cstddef>       // offsetof
#include <type_traits>   // is_trivially_copyable etc.

namespace XCore::Reflect
{
    // Forward declaration. FProperty's full definition lives in
    // Reflection/FProperty.h. FRepRecord stores a pointer-to-FProperty
    // and never dereferences through this header, so the forward
    // declaration is sufficient.
    struct FProperty;

    // -----------------------------------------------------------------
    // FRepRecord -- 16-byte ClassReps entry.
    //
    // The `_pad` field is explicit (not implicit-trailing-pad) so
    // `sizeof(FRepRecord) == 16` is a layout guarantee, not an
    // implementation detail. This matters because XHT's `.gen.cpp`
    // pre-emits `FClass::ClassReps` as constinit data per FIX-R2-LOW-8
    // and the constinit initializer must address every member by
    // designator.
    // -----------------------------------------------------------------
    struct alignas(8) FRepRecord
    {
        FProperty* Property;    //  0  +8   the replicated property descriptor
        ::int32    Index;       //  8  +4   ArrayDim element index (0..ArrayDim-1)
        ::int32    _pad;        // 12  +4   pad to 16-byte alignment

        // Default ctor: nullptr Property + zero Index. Constexpr so
        // constinit arrays of FRepRecord can be brace-initialized
        // verbatim by XHT-emitted code.
        constexpr FRepRecord() noexcept
            : Property(nullptr)
            , Index(0)
            , _pad(0)
        {
        }

        // Explicit ctor for programmatic construction.
        constexpr FRepRecord(FProperty* InProperty, ::int32 InIndex) noexcept
            : Property(InProperty)
            , Index(InIndex)
            , _pad(0)
        {
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.8 §11.3). The static_asserts here ARE
    // the ABI contract; changing any of these positions breaks every
    // FClass::ClassReps constinit emitter site engine-wide.
    // ---------------------------------------------------------------------
    static_assert(sizeof(FRepRecord)  == 16,
                  "FRepRecord ABI lock: must be exactly 16 bytes "
                  "({FProperty* Property; int32 Index; int32 _pad}; "
                  "matches UE Class.h:3984). See XCore-4b §6.5 + §11.3.");
    static_assert(alignof(FRepRecord) == 8,
                  "FRepRecord ABI lock: 8-byte alignment");

    // Per-member offsets locked per §11.3.
    static_assert(offsetof(FRepRecord, Property) == 0,
                  "FRepRecord ABI lock: Property at offset 0");
    static_assert(offsetof(FRepRecord, Index)    == 8,
                  "FRepRecord ABI lock: Index at offset 8");
    static_assert(offsetof(FRepRecord, _pad)     == 12,
                  "FRepRecord ABI lock: _pad at offset 12");

    // Type traits.
    static_assert(::std::is_standard_layout_v<FRepRecord>,
                  "FRepRecord must be standard layout (so offsetof is well-defined; "
                  "all members public + same access + no virtual functions)");
    static_assert(::std::is_trivially_copyable_v<FRepRecord>,
                  "FRepRecord must be trivially copyable (memcpy-able in TArray<FRepRecord>)");
    static_assert(::std::is_trivially_destructible_v<FRepRecord>,
                  "FRepRecord must be trivially destructible (no per-instance teardown)");

} // namespace XCore::Reflect
