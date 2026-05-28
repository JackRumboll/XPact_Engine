// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XObjectKey.h -- 8-byte stable XObject identity handle
// (XCoreXObject Rev 4 §6.4 + §11.1 XPACT_XOBJECTKEY_LAYOUT_TAG).
// =====================================================================
//
// XCoreXObject Rev 4 Section 6.4 ("XObjectKey (stable identity across
// hot-reload)") + Contract Rev 13.9 addendum tag
// `XPACT_XOBJECTKEY_LAYOUT_TAG`:
//
//   "XObjectKey-v1: 8 bytes; InternalIndex@0 (int32), SerialNumber@4
//    (uint32); ABI-compatible with XWeakPtr; alignof = 4"
//
// PURPOSE: the {InternalIndex, SerialNumber} 8-byte handle used for
// STABLE IDENTITY rather than weak-resolve semantics. XObjectKey is
// ABI-compatible with XWeakPtr (identical byte layout) but the two are
// NOT type-interchangeable: each is used at distinct call sites with
// distinct semantics (per spec §6.4 trailing prose).
//
// IDENTITY SEMANTICS (per spec §6.4 + UE-VER-3 / FIX-A-HIGH-10):
//
//   XObjectKey is the XPact analogue of UE's FObjectKey (ObjectKey.h:
//   18-60). The captured {InternalIndex, SerialNumber} survives a
//   hot-reload class replacement because the rebind protocol (spec
//   §6.4) preserves the SerialNumber across in-place class swaps.
//
//   The two key differences from XWeakPtr (whose layout matches):
//
//     * XObjectKey is intended for stable identity in maps, networking
//       protocols, editor selection, undo records.
//     * XObjectKey's Resolve() returns the rebound instance after a
//       hot-reload class swap (which does NOT bump SerialNumber).
//       XWeakPtr's Get() likewise resolves to the same address (same
//       bytewise behaviour at Phase 5.c; the divergence emerges when
//       XLiveCoding ships the rebind protocol).
//
//   When XObjectKey returns nullptr: only when the slot was released
//   AND re-acquired by a different object (which DOES bump the
//   SerialNumber). A hot-reload class replacement does NOT release the
//   slot; it rebinds the FClass pointer on the existing entry.
//
// HASH SEMANTICS (per spec §6.4 + Rev 1 HIGH-1):
//
//   GetTypeHash(XObjectKey) computes a 64-bit hash via XXH3-64 over the
//   8-byte handle so XObjectKey can be a TMap key (XMap, TMap, TSet).
//   Per spec §1.3 cross-arch determinism invariant + FIX-A-CRIT-2:
//   sim-path TUs MUST NOT depend on the hash value nor on the
//   iteration order of containers keyed on XObjectKey. The
//   XPACT_CHECK_SL guard on GetTypeHash enforces this at the read
//   site (Debug/Dev; the guard hook is in place for the future
//   ::XCore::HAL::IsSimPathTU runtime probe, matching the Phase 5.a
//   pattern on XObject::GetSerialNumber).
//
// HOT-RELOAD: NO virtual methods (matches every other XObject-side
// handle type). Trivially copyable + trivially destructible (POD-like;
// memcpy-safe in TArrays, std::bit_cast-safe to uint64).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XReflectionRuntime.h"   // XPACT_XOBJECTKEY_LAYOUT_TAG (tag literal)

#include <bit>                    // std::bit_cast
#include <cstddef>                // offsetof
#include <cstdint>
#include <type_traits>            // is_trivially_copyable_v etc.

namespace XCore
{
    // Forward declarations. XObjectKey references XObject as a const
    // pointer in its constructor + Resolve return type; FXObjectArray is
    // referenced from the .cpp body's Resolve implementation. The
    // forward declarations keep this header's include surface lean
    // (sim-path TUs that include XObjectKey.h for the type alone do
    // NOT pay for FXObjectArray.h / XObject.h transitively).
    class XObject;
    class FXObjectArray;

    // -----------------------------------------------------------------
    // XObjectKey -- 8-byte stable identity handle.
    //
    // alignas(4) -- the XPACT_XOBJECTKEY_LAYOUT_TAG documents alignof =
    // 4 (matches XWeakPtr; both handles use {int32, uint32} layout).
    // The struct's natural alignment is 4 on every supported platform
    // (the int32 + uint32 pair); alignas(4) makes the contract
    // explicit.
    //
    // The struct is NOT a class so the trivially-copyable contract is
    // unambiguous (the public field layout matches the spec body).
    // -----------------------------------------------------------------
    struct alignas(4) XObjectKey
    {
        // =============================================================
        // ABI-locked data members (8 bytes; per spec §6.4 + §11.1 tag).
        // =============================================================

        // FXObjectArray slot index. 0 is the null sentinel (per
        // FXObjectArray::kFXObjectArrayNullIndex); the array never
        // assigns index 0 to a real object.
        ::int32   InternalIndex;     // @0  +4

        // The SerialNumber captured at construction time. Compared
        // against the FXObjectArrayEntry's current SerialNumber at
        // Resolve(); a mismatch indicates the slot has been reused for
        // a different object since this key was captured.
        ::uint32  SerialNumber;      // @4  +4

        // =============================================================
        // Construction.
        //
        // Defaults to the null sentinel ({0, 0}). The from-XObject*
        // ctor captures the live identity tuple; nullptr maps to the
        // null sentinel symmetrically.
        // =============================================================

        // Default ctor: null sentinel. constexpr so XObjectKey can sit
        // by-value inside constinit storage (e.g., XHT-emitted reflection
        // metadata keyed by XObjectKey).
        constexpr XObjectKey() noexcept
            : InternalIndex(0)
            , SerialNumber(0)
        {
        }

        // Nullptr ctor: explicit conversion from nullptr_t for ergonomic
        // call sites (`XObjectKey k = nullptr;` and `k == nullptr`).
        constexpr XObjectKey(::std::nullptr_t) noexcept
            : InternalIndex(0)
            , SerialNumber(0)
        {
        }

        // From-XObject ctor: captures {GetInternalIndex(),
        // GetSerialNumber()} of the live XObject. Body in the .cpp
        // because reading GetSerialNumber() touches an XPACT_CHECK_SL
        // guard hook that requires the full XObject definition (not the
        // forward declaration we hold here).
        //
        // NOTE: this ctor reads XObject::SerialNumber which is the
        // SIMPATH-FORBIDDEN field (FIX-A-CRIT-2). The body therefore
        // routes through XObject::GetSerialNumber so the guard fires at
        // the canonical site. The ctor itself is NOT consteval because
        // XObject's runtime state isn't constexpr-readable.
        explicit XObjectKey(const XObject* Object) noexcept;

        // Trivially-copyable + trivially-destructible: the byte layout
        // is plain {int32, uint32}; the defaults are bitwise. Mark
        // explicit so the contract is auditable + the static_asserts
        // below pin the traits.
        constexpr XObjectKey(const XObjectKey&) noexcept            = default;
        constexpr XObjectKey(XObjectKey&&) noexcept                 = default;
        constexpr XObjectKey& operator=(const XObjectKey&) noexcept = default;
        constexpr XObjectKey& operator=(XObjectKey&&) noexcept      = default;
        ~XObjectKey() noexcept                                      = default;

        // =============================================================
        // Predicates.
        // =============================================================

        // IsNull: structurally null (the captured InternalIndex is 0).
        // NOT the same as IsValid: a NON-null key may still resolve to
        // nullptr if the slot has been reused.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return InternalIndex == 0;
        }

        // IsValid: probes the FXObjectArray to confirm the captured
        // {InternalIndex, SerialNumber} still maps to a live XObject.
        // Returns false on:
        //   * IsNull() (the null sentinel)
        //   * InternalIndex out of committed FXObjectArray range
        //   * Captured SerialNumber != entry's current SerialNumber
        //     (slot was reused for a different object)
        //   * Entry's Object pointer is nullptr (transient between
        //     FXObjectArray::ReserveSlot + BindObject)
        //
        // Implementation routes through Resolve() != nullptr for a
        // single-source-of-truth.
        [[nodiscard]] bool IsValid() const noexcept;

        // =============================================================
        // Resolution.
        //
        // Resolve: returns the bound XObject* iff the slot is live AND
        // the captured SerialNumber matches the entry's. Returns nullptr
        // otherwise (per IsValid contract above).
        //
        // The Resolve path acquires the FXObjectArray's SHARED lock for
        // the entry probe (per FXObjectArray::GetObjectAtIndex SHARED-
        // lock discipline at spec §3.3). Per-call cost: one read-lock
        // acquire + one indexed load + two compares; ~5-10 ns on
        // contemporary hardware.
        // =============================================================

        // Resolve to the live XObject. Returns nullptr per the
        // semantics above.
        [[nodiscard]] XObject* Resolve() const noexcept;

        // =============================================================
        // Equality.
        //
        // Per Rev 1 HIGH-1 + the FName precedent: bytewise equality via
        // std::bit_cast<uint64_t> for a single 64-bit compare. This
        // is faster than the field-by-field compare on contemporary
        // toolchains (one 64-bit cmp vs two 32-bit cmps + branch) and
        // preserves the strict-aliasing invariant.
        //
        // The constexpr-friendly bit_cast IS available since C++20
        // (P0476R2); all supported toolchains (MSVC 19.30+, Clang 14+,
        // GCC 11+) honour the constant-expression form for trivially-
        // copyable types.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const XObjectKey& Other) const noexcept
        {
            return ::std::bit_cast<::std::uint64_t>(*this)
                == ::std::bit_cast<::std::uint64_t>(Other);
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const XObjectKey& Other) const noexcept
        {
            return !(*this == Other);
        }

        // Nullptr-equality (for ergonomic "if (k == nullptr)" patterns).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(::std::nullptr_t) const noexcept
        {
            return IsNull();
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(::std::nullptr_t) const noexcept
        {
            return !IsNull();
        }
    };

    // ---------------------------------------------------------------------
    // GetTypeHash -- 64-bit hash of an XObjectKey for use in
    // TMap<XObjectKey, V> / TSet<XObjectKey>.
    //
    // SIMPATH-FORBIDDEN READ (per FIX-A-CRIT-2 + spec §1.3 cross-arch
    // determinism invariant). Sim-path TUs MAY NOT depend on the hash
    // value nor on the iteration order of XObjectKey-keyed containers.
    // The XPACT_CHECK_SL guard hook in the body (currently a no-op
    // pending ::XCore::HAL::IsSimPathTU runtime probe; matches Phase
    // 5.a XObject::GetSerialNumber discipline) is the runtime defence-
    // in-depth; the static-analysis sim-path filter at the build level
    // is the primary enforcement.
    //
    // Implementation: XXH3-64 over the 8 raw bytes. We bit_cast to
    // uint64 first so the input is well-defined regardless of host
    // endianness (XXH3 itself is endian-agnostic per its spec, but the
    // bit_cast pins the byte order at the call site as the documented
    // contract).
    //
    // Body in the .cpp so this header does not transitively pull
    // Hash/FXxh3.h into every consumer (XObjectKey is the only
    // identity-comparable handle; hashing is an opt-in operation, not
    // a default-include cost).
    // ---------------------------------------------------------------------
    [[nodiscard]] ::uint64 GetTypeHash(const XObjectKey& Key) noexcept;

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.1 tag
    // XPACT_XOBJECTKEY_LAYOUT_TAG + §11.3 XPACT_VERIFY_XOBJECT_LAYOUT
    // macro pin).
    //
    // Any byte-layout change breaks:
    //   * Every Resolve() implementation (offsets are hard-coded in
    //     the .cpp body's bit_cast / direct-field reads).
    //   * Every TMap<XObjectKey, V> hash + compare.
    //   * Every networking / replication path that serialises
    //     XObjectKey as 8 raw bytes.
    // ---------------------------------------------------------------------
    static_assert(sizeof(XObjectKey)  == 8,
                  "XObjectKey ABI lock: 8 bytes per XCoreXObject Rev 4 "
                  "§6.4 + Contract Rev 13.9 XPACT_XOBJECTKEY_LAYOUT_TAG.");
    static_assert(alignof(XObjectKey) == 4,
                  "XObjectKey ABI lock: 4-byte alignment per "
                  "XPACT_XOBJECTKEY_LAYOUT_TAG.");

    static_assert(offsetof(XObjectKey, InternalIndex) == 0,
                  "XObjectKey ABI lock: InternalIndex at offset 0.");
    static_assert(offsetof(XObjectKey, SerialNumber)  == 4,
                  "XObjectKey ABI lock: SerialNumber at offset 4.");

    static_assert(sizeof(XObjectKey::InternalIndex) == 4,
                  "XObjectKey ABI lock: InternalIndex is int32 (4 bytes).");
    static_assert(sizeof(XObjectKey::SerialNumber)  == 4,
                  "XObjectKey ABI lock: SerialNumber is uint32 (4 bytes).");

    // Trait locks. XObjectKey MUST be trivially copyable + trivially
    // destructible so:
    //   * Containers can memcpy without invoking copy-constructors.
    //   * std::bit_cast<uint64_t>(XObjectKey) is well-defined (the
    //     trivially-copyable trait is bit_cast's pre-condition).
    //   * The TArray storage path can use the trivial-relocate fast
    //     path.
    static_assert(::std::is_trivially_copyable_v<XObjectKey>,
                  "XObjectKey must be trivially copyable (bit_cast + "
                  "container memcpy invariants).");
    static_assert(::std::is_trivially_destructible_v<XObjectKey>,
                  "XObjectKey must be trivially destructible.");
    static_assert(::std::is_standard_layout_v<XObjectKey>,
                  "XObjectKey must be standard layout (offsetof + "
                  "designated-init invariants).");

} // namespace XCore
