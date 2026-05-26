// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FWeakObjectPtr.h -- 8-byte weak-XObject-pointer placeholder
// (XCore-4b Phase 4b.4b).
// =====================================================================
//
// FWeakObjectPtr is the GC-aware weak reference to an XObject (the
// XPact equivalent of UE's TWeakObjectPtr). The full implementation
// (XObject* + serial number; checks against the GC index for liveness)
// lands at XCoreXObject (System 5).
//
// PLACEHOLDER POSTURE:
//
// XCore-4b Phase 4b.4b ships an 8-byte placeholder value type so:
//
//   * FWeakObjectProperty can declare its payload at offset 104
//     by-value (the storage slot is FWeakObjectPtr, not a raw pointer
//     -- UE's FWeakObjectProperty uses a TWeakObjectPtr-shaped slot
//     and XPact mirrors that for ABI parity).
//   * sizeof(FWeakObjectProperty) == 112 (104 FProperty + 8 payload)
//     is locked at Phase 4b.4b per §11.2.
//   * The bit-exact storage shape is preserved when System 5 lands the
//     full impl; the 8-byte slot can be re-interpreted as the full
//     impl's layout once that type exists.
//
// UE COMPATIBILITY NOTE:
//
// UE's TWeakObjectPtr is structurally a `{int32 ObjectIndex; int32
// ObjectSerialNumber;}` pair (8 bytes total). XPact's FWeakObjectPtr
// will mirror this shape at System 5 landing. Phase 4b.4b's
// placeholder reserves the 8-byte slot via the same `uint64 Storage`
// idiom used for FSoftObjectPath -- the storage interpretation is
// opaque until System 5.
//
// LAYOUT INVARIANT (locked):
//
//   * sizeof(FWeakObjectPtr) == 8 bytes
//   * alignof(FWeakObjectPtr) == 8 (matches the 8-byte slot at offset 104)
//   * Trivially copyable + trivially destructible (constinit-friendly;
//     placement-new-friendly; memcpy-safe within reflection containers).
//
// TODO(System 5): replace the opaque uint64 Storage with the actual
// FWeakObjectPtr impl (typically {int32 ObjectIndex; int32
// SerialNumber} per UE's TWeakObjectPtr layout, or an equivalent
// XPact-specific encoding tied to the XObject index pool). Keep the
// 8-byte total + 8-byte alignment invariant so FWeakObjectProperty's
// layout is preserved across the swap.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <cstddef>
#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FWeakObjectPtr -- 8-byte placeholder weak-XObject-pointer.
    //
    // alignas(8) so the 8-byte slot at FWeakObjectProperty offset 104
    // is naturally aligned. The Storage field is initialised to zero
    // (the "no target" sentinel; structurally a null weak ref).
    // -----------------------------------------------------------------
    struct alignas(8) FWeakObjectPtr
    {
        // 8-byte storage. The internal interpretation lands at System 5
        // (full FWeakObjectPtr impl); Phase 4b.4b treats it as opaque.
        ::uint64 Storage;

        // -------------------------------------------------------------
        // Construction.
        //
        // The default ctor zero-initialises Storage. constexpr so
        // constinit arrays of FWeakObjectPtr storage can be declared
        // and the type can sit by-value inside FWeakObjectProperty's
        // constinit payload.
        // -------------------------------------------------------------
        constexpr FWeakObjectPtr() noexcept
            : Storage(0)
        {
        }

        // Defaulted copy / move. POD; defaulted operations are bitwise.
        constexpr FWeakObjectPtr(const FWeakObjectPtr&) noexcept            = default;
        constexpr FWeakObjectPtr(FWeakObjectPtr&&) noexcept                 = default;
        constexpr FWeakObjectPtr& operator=(const FWeakObjectPtr&) noexcept = default;
        constexpr FWeakObjectPtr& operator=(FWeakObjectPtr&&) noexcept      = default;
        ~FWeakObjectPtr() noexcept                                          = default;

        // -------------------------------------------------------------
        // Equality. Byte-level compare on the 8-byte Storage field.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const FWeakObjectPtr& Other) const noexcept
        {
            return Storage == Other.Storage;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const FWeakObjectPtr& Other) const noexcept
        {
            return Storage != Other.Storage;
        }

        // -------------------------------------------------------------
        // IsNull -- predicate matching the "no target" sentinel.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return Storage == 0;
        }
    };

    // ABI lock.
    static_assert(sizeof(FWeakObjectPtr)  == 8,
                  "FWeakObjectPtr ABI lock: 8 bytes (System 5 full-impl "
                  "must preserve sizeof so FWeakObjectProperty layout is "
                  "stable across the swap; matches UE TWeakObjectPtr's "
                  "{int32 ObjectIndex; int32 SerialNumber} 8-byte shape)");
    static_assert(alignof(FWeakObjectPtr) == 8,
                  "FWeakObjectPtr ABI lock: 8-byte alignment");
    static_assert(::std::is_standard_layout_v<FWeakObjectPtr>,
                  "FWeakObjectPtr must be standard layout (placement-new "
                  "+ memcpy-safe inside FWeakObjectProperty payload)");
    static_assert(::std::is_trivially_copyable_v<FWeakObjectPtr>,
                  "FWeakObjectPtr must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FWeakObjectPtr>,
                  "FWeakObjectPtr must be trivially destructible");

} // namespace XCore::Reflect
