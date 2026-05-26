// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FSoftObjectPath.h -- 8-byte soft-object-path placeholder
// (XCore-4b Phase 4b.4b; FIX-3).
// =====================================================================
//
// FSoftObjectPath is the asset-reference handle used by
// FSoftObjectProperty / FSoftClassProperty (XCore-4b §5.5; Rev 2 MVP
// addition per FIX-3). The full implementation (string-typed path with
// asset-system resolution; XScenario preset reference; etc.) lands at
// XCoreXObject (System 5).
//
// PLACEHOLDER POSTURE:
//
// XCore-4b Phase 4b.4b ships an 8-byte placeholder value type so:
//
//   * FSoftObjectProperty / FSoftClassProperty can declare their
//     payload by-value at offset 104 without an additional
//     forward-declaration boundary.
//   * sizeof(FSoftObjectProperty) == 112 (104 FProperty + 8
//     FSoftObjectPath) is locked at Phase 4b.4b per §11.2.
//   * The bit-exact storage shape is preserved when System 5 lands the
//     full impl (asset path string handle + resolution cache); the
//     8-byte slot can be re-interpreted as a `const FSoftObjectPathInternal*`
//     once that type exists.
//
// Per spec §11.2: PropertyClass payload @ offset 104 is 8 bytes; for
// FSoftObjectProperty the row reads "(extends FProperty:0..103)
// PropertyClass@104" -- 8 bytes regardless of whether the payload is
// FClass* (Phase 4b.4b) or FSoftObjectPath (future System 5).
//
// LAYOUT INVARIANT (locked):
//
//   * sizeof(FSoftObjectPath) == 8 bytes
//   * alignof(FSoftObjectPath) == 8 (matches the 8-byte slot at offset 104)
//   * Trivially copyable + trivially destructible (constinit-friendly;
//     placement-new-friendly; memcpy-safe within reflection containers).
//
// TODO(System 5): replace the opaque uint64 Storage with the actual
// FSoftObjectPath impl (typically an FName-handle pair for the asset
// path + the sub-object path within the asset). Keep the 8-byte
// total + 8-byte alignment invariant so FSoftObjectProperty's layout
// is preserved across the swap.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <cstddef>
#include <type_traits>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // FSoftObjectPath -- 8-byte placeholder asset-path value type.
    //
    // alignas(8) so the 8-byte slot at FSoftObjectProperty offset 104
    // is naturally aligned. The Storage field is initialised to zero
    // (the "no path" sentinel; structurally a null handle).
    // -----------------------------------------------------------------
    struct alignas(8) FSoftObjectPath
    {
        // 8-byte storage. The internal interpretation lands at System 5
        // (full FSoftObjectPath impl); Phase 4b.4b treats it as opaque.
        ::uint64 Storage;

        // -------------------------------------------------------------
        // Construction.
        //
        // The default ctor zero-initialises Storage. constexpr so
        // constinit arrays of FSoftObjectPath storage can be declared
        // and the type can sit by-value inside FSoftObjectProperty's
        // constinit payload.
        // -------------------------------------------------------------
        constexpr FSoftObjectPath() noexcept
            : Storage(0)
        {
        }

        // Defaulted copy / move. POD; defaulted operations are bitwise.
        constexpr FSoftObjectPath(const FSoftObjectPath&) noexcept            = default;
        constexpr FSoftObjectPath(FSoftObjectPath&&) noexcept                 = default;
        constexpr FSoftObjectPath& operator=(const FSoftObjectPath&) noexcept = default;
        constexpr FSoftObjectPath& operator=(FSoftObjectPath&&) noexcept      = default;
        ~FSoftObjectPath() noexcept                                           = default;

        // -------------------------------------------------------------
        // Equality. Byte-level compare on the 8-byte Storage field.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const FSoftObjectPath& Other) const noexcept
        {
            return Storage == Other.Storage;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const FSoftObjectPath& Other) const noexcept
        {
            return Storage != Other.Storage;
        }

        // -------------------------------------------------------------
        // IsNull -- predicate matching the "no path" sentinel.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return Storage == 0;
        }
    };

    // ABI lock.
    static_assert(sizeof(FSoftObjectPath)  == 8,
                  "FSoftObjectPath ABI lock: 8 bytes (System 5 full-impl "
                  "must preserve sizeof so FSoftObjectProperty layout is "
                  "stable across the swap)");
    static_assert(alignof(FSoftObjectPath) == 8,
                  "FSoftObjectPath ABI lock: 8-byte alignment");
    static_assert(::std::is_standard_layout_v<FSoftObjectPath>,
                  "FSoftObjectPath must be standard layout (placement-new "
                  "+ memcpy-safe inside FSoftObjectProperty payload)");
    static_assert(::std::is_trivially_copyable_v<FSoftObjectPath>,
                  "FSoftObjectPath must be trivially copyable");
    static_assert(::std::is_trivially_destructible_v<FSoftObjectPath>,
                  "FSoftObjectPath must be trivially destructible");

} // namespace XCore::Reflect
