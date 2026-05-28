// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XWeakPtr.h -- 8-byte weak typed pointer (XCoreXObject Rev 4 §6.2).
// =====================================================================
//
// XCoreXObject Rev 4 Section 6.2 ("XWeakPtr<T> (weak reference)") +
// Contract Rev 13.9 addendum tag `XPACT_XWEAKPTR_LAYOUT_TAG`:
//
//   "XWeakPtr-v1: 8 bytes; InternalIndex@0 (int32), SerialNumber@4
//    (uint32); matches XCore-4b's FWeakObjectPtr placeholder shape;
//    alignof = 4"
//
// PURPOSE: a non-owning typed reference to an XObject that does NOT
// keep the pointee alive and resolves to nullptr if the pointee has
// been collected. The 8-byte handle is {InternalIndex, SerialNumber};
// same layout as XObjectKey but distinct semantics + API surface.
//
// NON-ROOTING (per spec §6.2):
//
//   XWeakPtr does NOT participate in GC root traversal -- neither self-
//   rooted nor property-scan-rooted. The collector ignores XWeakPtr-
//   shaped slots during reachability analysis; the pointee can be
//   collected even when an XWeakPtr exists.
//
//   This is intentional: XWeakPtr is the canonical XPact shape for
//   observer / subscriber / cache-of-last-resort patterns where the
//   reference holder explicitly does NOT want to extend lifetime.
//
// DEREF SEMANTICS (per spec §6.2):
//
//   Get() returns nullptr if the SerialNumber check fails (slot was
//   reused since capture). The cost is ~5-10 ns per call: one indexed
//   load + two compares (under the FXObjectArray SHARED lock per the
//   spec §3.3 read discipline).
//
//   IsValid() is a faster predicate (no full pointer return; same
//   underlying probe).
//
// ABI COMPATIBILITY (per spec §6.2):
//
//   The 8-byte layout matches XCore-4b's FWeakObjectPtr placeholder
//   shape, so FWeakObjectProperty's payload IS bit-identical to an
//   XWeakPtr. This is the load-bearing reflection-runtime invariant:
//   downstream consumers (XSerialization, XNetworking) can read an
//   FWeakObjectProperty slot as an XWeakPtr<XObject> directly.
//
// HOT-RELOAD: NO virtual methods. Trivially copyable + trivially
// destructible.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/XObject.h"          // T must derive from XObject (static_assert)
#include "XObject/FXObjectArray.h"    // Get().GetObjectAtIndex in inline Get()
#include "XReflectionRuntime.h"       // XPACT_XWEAKPTR_LAYOUT_TAG (tag literal)

#include <bit>                        // std::bit_cast
#include <cstddef>                    // offsetof
#include <cstdint>
#include <type_traits>                // is_base_of_v, is_trivially_copyable_v

namespace XCore
{
    // -----------------------------------------------------------------
    // XWeakPtr<T> -- 8-byte weak typed pointer.
    //
    // alignas(4) -- the XPACT_XWEAKPTR_LAYOUT_TAG documents alignof = 4
    // (matches XObjectKey; both use {int32, uint32} layout).
    //
    // Template parameter T MUST derive from XObject. The static_assert
    // is at the class scope so the friendly diagnostic fires before any
    // member-body compilation surfaces a less obvious error.
    // -----------------------------------------------------------------
    template <typename T>
    class alignas(4) XWeakPtr
    {
        static_assert(::std::is_base_of_v<XObject, T>,
                      "XWeakPtr<T> requires T : XObject. For non-XObject "
                      "weak references, use std::weak_ptr or a different "
                      "smart-pointer type.");

    public:
        // =============================================================
        // ABI-locked data members (8 bytes; per spec §6.2 + §11.1 tag).
        //
        // The byte layout EXACTLY matches XObjectKey + XCore-4b's
        // FWeakObjectPtr placeholder so the three are bit-compatible
        // (but NOT type-interchangeable; each carries distinct
        // semantics).
        // =============================================================

        // FXObjectArray slot index. 0 is the null sentinel.
        ::int32   InternalIndex;     // @0  +4

        // The SerialNumber captured at construction time. The Get() /
        // IsValid() probe compares against the FXObjectArrayEntry's
        // current SerialNumber; a mismatch means the slot was reused
        // for a different object since the weak handle was captured.
        ::uint32  SerialNumber;      // @4  +4

        // =============================================================
        // Construction.
        // =============================================================

        constexpr XWeakPtr() noexcept
            : InternalIndex(0)
            , SerialNumber(0)
        {
        }

        constexpr XWeakPtr(::std::nullptr_t) noexcept
            : InternalIndex(0)
            , SerialNumber(0)
        {
        }

        // From-T* ctor: captures {GetInternalIndex(), GetSerialNumber()}
        // of the live XObject. nullptr maps to the null sentinel.
        //
        // The ctor reads XObject::SerialNumber directly (rather than
        // through XObject::GetSerialNumber()) to avoid the
        // SIMPATH-FORBIDDEN guard on the accessor: an XWeakPtr capture
        // IS the legitimate non-sim-path read site (the spec FIX-A-
        // CRIT-2 invariant is about sim-path-TU reads, not non-sim-path
        // captures). The direct field read here is identical to the
        // FXObjectArrayEntry's bookkeeping path which also reads the
        // field directly (FXObjectArray.cpp::FreeEntry bumps it).
        //
        // EXPLICIT to prevent silent T*-to-XWeakPtr conversions.
        explicit XPACT_FORCEINLINE XWeakPtr(T* InPtr) noexcept
            : InternalIndex(InPtr != nullptr ? InPtr->InternalIndex : 0)
            , SerialNumber(InPtr != nullptr  ? InPtr->SerialNumber  : 0u)
        {
        }

        // Bytewise-copy ctor from raw {int32, uint32} pair (used by
        // utility code and tests). Marked explicit so direct {int32,
        // uint32}-from-pair conversions don't happen accidentally.
        explicit XPACT_FORCEINLINE constexpr XWeakPtr(::int32 InIndex, ::uint32 InSerial) noexcept
            : InternalIndex(InIndex)
            , SerialNumber(InSerial)
        {
        }

        // Trivially-copyable + trivially-destructible.
        constexpr XWeakPtr(const XWeakPtr&) noexcept            = default;
        constexpr XWeakPtr(XWeakPtr&&) noexcept                 = default;
        constexpr XWeakPtr& operator=(const XWeakPtr&) noexcept = default;
        constexpr XWeakPtr& operator=(XWeakPtr&&) noexcept      = default;
        ~XWeakPtr() noexcept                                    = default;

        // Assignment from T* (ergonomic; rebinds the captured tuple).
        XPACT_FORCEINLINE XWeakPtr& operator=(T* InPtr) noexcept
        {
            InternalIndex = (InPtr != nullptr ? InPtr->InternalIndex : 0);
            SerialNumber  = (InPtr != nullptr ? InPtr->SerialNumber  : 0u);
            return *this;
        }

        XPACT_FORCEINLINE XWeakPtr& operator=(::std::nullptr_t) noexcept
        {
            InternalIndex = 0;
            SerialNumber  = 0;
            return *this;
        }

        // =============================================================
        // Predicates.
        // =============================================================

        // IsExplicitlyNull: structurally null. Cheap (single int32
        // compare; matches spec §6.2 prose).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsExplicitlyNull() const noexcept
        {
            return InternalIndex == 0;
        }

        // IsNull: alias for IsExplicitlyNull (the XWeakPtr-canonical
        // surface uses IsExplicitlyNull per spec §6.2; we expose IsNull
        // as a sibling alias for consistency with XObjectKey / XPtr).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return InternalIndex == 0;
        }

        // IsValid: probes the FXObjectArray to confirm the captured
        // {InternalIndex, SerialNumber} still maps to a live XObject.
        // Returns false on the same conditions as Get() returns nullptr.
        //
        // Inline because the FXObjectArray::GetObjectAtIndex call is
        // already a single non-inline lookup; wrapping it in a function-
        // call layer would add unnecessary overhead at hot call sites.
        [[nodiscard]] XPACT_FORCEINLINE bool IsValid() const noexcept
        {
            if (InternalIndex == 0)
            {
                return false;
            }
            return FXObjectArray::Get().GetObjectAtIndex(InternalIndex, SerialNumber) != nullptr;
        }

        // =============================================================
        // Resolution.
        //
        // Get: returns the bound T* iff the SerialNumber check passes.
        // Returns nullptr on:
        //   * IsExplicitlyNull()
        //   * InternalIndex out of committed FXObjectArray range
        //   * SerialNumber mismatch (slot was reused)
        //   * The bound object has EObjectFlags::BeginDestroyed set
        //     (the FXObjectArray's GetObjectAtIndex already filters
        //     pre-bind nullptr; the BeginDestroyed check below filters
        //     in-destruction objects per spec §6.2 body code)
        //
        // The Resolve path routes through FXObjectArray::GetObjectAtIndex
        // (SHARED-lock probe; one indexed load + two compares). The
        // static_cast<T*> at the tail recovers the typed pointer; this
        // IS safe because XWeakPtr<T> was constructed from a T*, so the
        // stored InternalIndex always points at a slot bound to a
        // T-or-derived XObject.
        //
        // BeginDestroyed filter (per spec §6.2 reference impl): objects
        // whose destructor is in flight return nullptr from Get() even
        // before the slot is freed. This avoids races where the
        // collector has marked the object for destruction but the slot
        // hasn't been released yet -- a small but real window.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE T* Get() const noexcept
        {
            if (InternalIndex == 0)
            {
                return nullptr;
            }
            XObject* Resolved = FXObjectArray::Get().GetObjectAtIndex(InternalIndex, SerialNumber);
            if (Resolved == nullptr)
            {
                return nullptr;
            }
            if (Resolved->HasAnyFlags(EObjectFlags::BeginDestroyed))
            {
                return nullptr;
            }
            // The XWeakPtr<T> was constructed from a T* (or assigned
            // from one); the stored slot must therefore hold a T-or-
            // derived XObject. static_cast is correct (no runtime
            // type-check needed; the type discipline is enforced at
            // construction time).
            return static_cast<T*>(Resolved);
        }

        // =============================================================
        // Equality.
        //
        // Per Rev 1 HIGH-1 + the FName precedent: bytewise equality via
        // std::bit_cast<uint64_t>. XWeakPtr equality compares the
        // CAPTURED tuple, NOT the resolved pointer; two XWeakPtrs are
        // equal iff they were captured from the same slot at the same
        // SerialNumber.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const XWeakPtr& Other) const noexcept
        {
            return ::std::bit_cast<::std::uint64_t>(*this)
                == ::std::bit_cast<::std::uint64_t>(Other);
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const XWeakPtr& Other) const noexcept
        {
            return !(*this == Other);
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(::std::nullptr_t) const noexcept
        {
            return InternalIndex == 0;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(::std::nullptr_t) const noexcept
        {
            return InternalIndex != 0;
        }
    };

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.1 tag XPACT_XWEAKPTR_LAYOUT_TAG
    // + §11.3 XPACT_VERIFY_XOBJECT_LAYOUT macro pin).
    //
    // The XWeakPtr<XObject> instantiation is the canonical pin. Any T
    // derived from XObject produces the same byte layout (the storage
    // is always {int32, uint32}).
    // ---------------------------------------------------------------------
    static_assert(sizeof(XWeakPtr<XObject>)  == 8,
                  "XWeakPtr<XObject> ABI lock: 8 bytes (matches "
                  "FWeakObjectPtr placeholder) per XCoreXObject Rev 4 "
                  "§6.2 + Contract Rev 13.9 XPACT_XWEAKPTR_LAYOUT_TAG.");
    static_assert(alignof(XWeakPtr<XObject>) == 4,
                  "XWeakPtr<XObject> ABI lock: 4-byte alignment per "
                  "XPACT_XWEAKPTR_LAYOUT_TAG.");

    static_assert(offsetof(XWeakPtr<XObject>, InternalIndex) == 0,
                  "XWeakPtr<XObject> ABI lock: InternalIndex at offset 0.");
    static_assert(offsetof(XWeakPtr<XObject>, SerialNumber)  == 4,
                  "XWeakPtr<XObject> ABI lock: SerialNumber at offset 4.");

    static_assert(sizeof(XWeakPtr<XObject>::InternalIndex) == 4,
                  "XWeakPtr<XObject> ABI lock: InternalIndex is int32 (4 bytes).");
    static_assert(sizeof(XWeakPtr<XObject>::SerialNumber)  == 4,
                  "XWeakPtr<XObject> ABI lock: SerialNumber is uint32 (4 bytes).");

    static_assert(::std::is_trivially_copyable_v<XWeakPtr<XObject>>,
                  "XWeakPtr<XObject> must be trivially copyable "
                  "(FWeakObjectProperty payload + bit_cast + container "
                  "memcpy invariants).");
    static_assert(::std::is_trivially_destructible_v<XWeakPtr<XObject>>,
                  "XWeakPtr<XObject> must be trivially destructible.");
    static_assert(::std::is_standard_layout_v<XWeakPtr<XObject>>,
                  "XWeakPtr<XObject> must be standard layout "
                  "(reinterpret_cast-from-FWeakObjectPtr invariant).");

} // namespace XCore
