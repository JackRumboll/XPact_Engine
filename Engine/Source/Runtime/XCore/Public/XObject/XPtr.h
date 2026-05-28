// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XPtr.h -- 8-byte strong typed pointer (XCoreXObject Rev 4 §6.1).
// =====================================================================
//
// XCoreXObject Rev 4 Section 6.1 ("XPtr<T> (strong reference)") +
// Contract Rev 13.9 addendum tag `XPACT_XPTR_LAYOUT_TAG`:
//
//   "XPtr-v1: 8 bytes; Ptr@0 (raw T* compatible); ABI-equivalent to T*;
//    alignof = 8"
//
// PURPOSE: the strong typed pointer to an XObject. XPtr<T> is a thin
// wrapper around T* with the ABI invariant that the 8 bytes at offset
// 0 are bit-identical to a raw T*. This is LOAD-BEARING for the
// reflection runtime's FObjectProperty payload contract: every
// FObjectProperty stores its value as a raw XObject* at the property's
// offset; consumers (XPropertyEditor, XSerialization, XNetworking) read
// the slot via `*reinterpret_cast<XObject**>(instance + property.Offset)`.
// An XPtr<T> in the same slot must read identically -- the only legal
// implementation is a thin Ptr-only wrapper.
//
// ROOTING SEMANTICS (per spec §6.1):
//
//   XPtr<T> does NOT register as a GC root. Rooting is determined by
//   the container the XPtr lives in:
//
//     * An XPtr in an XCLASS field is rooted at GC time via the
//       FStruct.ObjectRefProperties walk (Phase 5.f) and / or the
//       schema-vector fast-path (Phase 5.g').
//     * An XPtr in a TArray whose container registers an XGCRootSpan
//       (Phase 5.e) is rooted via the span scan.
//     * An XPtr in a stack frame is rooted ONLY if the XIL2CPP stack-
//       map (System 6) enumerates it. Plain C++ stack-local XPtrs are
//       NOT rooted; the pointee may be collected if no other reference
//       path exists.
//
//   For self-rooting strong references in third-party C++ / editor
//   tooling / async I/O work items: use XStrongPtr<T> (spec §6.5) which
//   increments the per-FXObjectArrayEntry refcount.
//
// DEREF SEMANTICS (per spec §6.1):
//
//   Dereference is a RAW LOAD; no SerialNumber check. The pointee is
//   alive by contract because the XPtr held a strong reference path
//   through its container. Construction from a raw T* is checked in
//   Dev (the T* must be a valid live XObject); Shipping trusts the
//   caller. Use XWeakPtr<T> for the weak-resolve-with-serial-check
//   semantic.
//
// HOT-RELOAD: NO virtual methods. Trivially copyable + trivially
// destructible (the raw T* layout makes XPtr<T> a POD-like type;
// memcpy-safe in TArrays).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/XObject.h"          // T must derive from XObject (static_assert)
#include "XReflectionRuntime.h"       // XPACT_XPTR_LAYOUT_TAG (tag literal)

#include <cstddef>                    // offsetof
#include <cstdint>
#include <type_traits>                // is_base_of_v, is_trivially_copyable_v

namespace XCore
{
    // -----------------------------------------------------------------
    // XPtr<T> -- 8-byte strong typed pointer.
    //
    // alignas(8) -- the XPACT_XPTR_LAYOUT_TAG documents alignof = 8
    // (matches a raw T* on every supported platform). alignas(8) makes
    // the contract explicit and ensures the static_asserts below pin
    // the exact ABI shape.
    //
    // Template parameter T MUST derive from XObject. The static_assert
    // is at the class scope so the friendly diagnostic fires before any
    // member-body compilation surfaces a less obvious error.
    // -----------------------------------------------------------------
    template <typename T>
    class alignas(8) XPtr
    {
        static_assert(::std::is_base_of_v<XObject, T>,
                      "XPtr<T> requires T : XObject. The handle is a "
                      "strong typed pointer specifically for GC-managed "
                      "objects; for non-XObject pointer payloads, use a "
                      "different smart-pointer type.");

    public:
        // =============================================================
        // ABI-locked data member (8 bytes; per spec §6.1 + §11.1 tag).
        //
        // Single field at offset 0; bit-identical to a raw T*. The
        // FObjectProperty payload contract reads this slot via
        // reinterpret_cast<XObject**> so the wrapper MUST be the only
        // member + at offset 0.
        // =============================================================

        T* Ptr;                                       // @0  +8

        // =============================================================
        // Construction.
        //
        // Default ctor: null pointer. constexpr so XPtr<T> can sit by-
        // value inside constinit storage (e.g., XHT-emitted reflection
        // metadata).
        //
        // From-T* ctor: Dev-checks the pointer is a live XObject; in
        // Shipping the check is compiled out and the pointer is
        // trusted. Per spec §6.1 ABI prose.
        // =============================================================

        constexpr XPtr() noexcept
            : Ptr(nullptr)
        {
        }

        constexpr XPtr(::std::nullptr_t) noexcept
            : Ptr(nullptr)
        {
        }

        // From-T* ctor. EXPLICIT to prevent silent T*-to-XPtr conversions
        // at unintended call sites (mirrors UE's TObjectPtr discipline).
        //
        // The Dev check (`XPACT_CHECK(InPtr == nullptr || InPtr->
        // IsValidLowLevel())`) is REDUCED to a simple non-null + class-
        // pointer probe at Phase 5.c: IsValidLowLevel currently ships
        // the no-array subset (full FXObjectArray cross-check lands in
        // future phases). The body is constexpr-friendly when InPtr is
        // a constant expression; XPACT_CHECK is a runtime check so the
        // constructor is constexpr only on the nullptr branch.
        //
        // NOTE: the ctor cannot be consteval because it accepts non-
        // constexpr T* values (typical call sites pass runtime-
        // allocated XObjects).
        explicit XPACT_FORCEINLINE XPtr(T* InPtr) noexcept
            : Ptr(InPtr)
        {
            // Dev / Debug check: a non-null pointer must point at a
            // structurally-valid XObject (ClassPrivate non-null, etc.).
            // Compiles out in Shipping. The IsValidLowLevel check is
            // routed through XObject because IsValidLowLevel itself is
            // the documented entry point for this exact predicate.
            XPACT_CHECK(InPtr == nullptr
                        || static_cast<const XObject*>(InPtr)->IsValidLowLevel());
        }

        // Trivially-copyable + trivially-destructible: the byte layout
        // is plain T*; the defaults are bitwise.
        constexpr XPtr(const XPtr&) noexcept            = default;
        constexpr XPtr(XPtr&&) noexcept                 = default;
        constexpr XPtr& operator=(const XPtr&) noexcept = default;
        constexpr XPtr& operator=(XPtr&&) noexcept      = default;
        ~XPtr() noexcept                                = default;

        // Assignment from a raw T* (ergonomic call sites). Dev-checks
        // the pointer per the from-T* ctor's contract.
        XPACT_FORCEINLINE XPtr& operator=(T* InPtr) noexcept
        {
            XPACT_CHECK(InPtr == nullptr
                        || static_cast<const XObject*>(InPtr)->IsValidLowLevel());
            Ptr = InPtr;
            return *this;
        }

        XPACT_FORCEINLINE XPtr& operator=(::std::nullptr_t) noexcept
        {
            Ptr = nullptr;
            return *this;
        }

        // =============================================================
        // Predicates.
        // =============================================================

        // IsNull: structurally null. Cheap (single pointer compare).
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return Ptr == nullptr;
        }

        // IsValid: alias for !IsNull at Phase 5.c. The semantic is
        // "the XPtr points at a usable XObject". A more aggressive
        // check would route through XObject::IsValidLowLevel but the
        // contract per spec §6.1 ("Dereference is a raw load; no
        // SerialNumber check; the pointee is alive by contract") makes
        // the simple null check the canonical implementation -- XPtr's
        // entire value proposition is the ZERO-overhead deref.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsValid() const noexcept
        {
            return Ptr != nullptr;
        }

        // =============================================================
        // Access.
        //
        // Get: returns the raw T*. The single load that EVERY GC-walk
        // / property-edit / serialisation consumer uses.
        //
        // operator-> / operator*: ergonomic deref. Asserts non-null in
        // Dev; raw deref in Shipping.
        //
        // operator bool: alias for IsValid (matches std::unique_ptr /
        // UE TObjectPtr surface).
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr T* Get() const noexcept
        {
            return Ptr;
        }

        [[nodiscard]] XPACT_FORCEINLINE T* operator->() const noexcept
        {
            XPACT_CHECK(Ptr != nullptr);
            return Ptr;
        }

        [[nodiscard]] XPACT_FORCEINLINE T& operator*() const noexcept
        {
            XPACT_CHECK(Ptr != nullptr);
            return *Ptr;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr explicit operator bool() const noexcept
        {
            return Ptr != nullptr;
        }

        // Implicit conversion to T* is INTENTIONALLY OMITTED. The
        // explicit Get() forces call sites to acknowledge they are
        // crossing the smart-pointer boundary -- matches UE TObjectPtr's
        // discipline (Get() instead of implicit T*).

        // =============================================================
        // Comparison.
        //
        // The XPtr storage IS the pointer; pointer-equality is
        // bit-identical to T*-equality. Defaults to constexpr per the
        // POD-like nature of the storage.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const XPtr& Other) const noexcept
        {
            return Ptr == Other.Ptr;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const XPtr& Other) const noexcept
        {
            return Ptr != Other.Ptr;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const T* Other) const noexcept
        {
            return Ptr == Other;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const T* Other) const noexcept
        {
            return Ptr != Other;
        }

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(::std::nullptr_t) const noexcept
        {
            return Ptr == nullptr;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(::std::nullptr_t) const noexcept
        {
            return Ptr != nullptr;
        }
    };

    // ---------------------------------------------------------------------
    // Reverse comparison (T* on the lhs). Ergonomic call sites:
    //   if (RawPtr == MyXPtr) { ... }
    // ---------------------------------------------------------------------
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const T* Lhs, const XPtr<T>& Rhs) noexcept
    {
        return Lhs == Rhs.Get();
    }
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const T* Lhs, const XPtr<T>& Rhs) noexcept
    {
        return Lhs != Rhs.Get();
    }
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(::std::nullptr_t, const XPtr<T>& Rhs) noexcept
    {
        return Rhs.IsNull();
    }
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(::std::nullptr_t, const XPtr<T>& Rhs) noexcept
    {
        return !Rhs.IsNull();
    }

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.1 tag XPACT_XPTR_LAYOUT_TAG
    // + §11.3 XPACT_VERIFY_XOBJECT_LAYOUT macro pin).
    //
    // The XPtr<XObject> instantiation is the canonical pin (the spec's
    // sizeof / alignof / offsetof contract). Any T derived from XObject
    // produces the same byte layout (the only member is a T* which is
    // always 8 bytes on 64-bit hosts), so the XObject specialisation
    // is sufficient to lock the family.
    //
    // Any byte-layout change breaks:
    //   * The FObjectProperty payload reinterpret_cast<XObject**>
    //     contract (spec §6.1 trailing prose).
    //   * Every GC-walk site that reads an XPtr as a raw pointer.
    //   * XHT-emitted reflection metadata that emits XPtr<T> slots
    //     as constinit data.
    // ---------------------------------------------------------------------
    static_assert(sizeof(XPtr<XObject>)  == 8,
                  "XPtr<XObject> ABI lock: 8 bytes (raw-T*-compatible) "
                  "per XCoreXObject Rev 4 §6.1 + Contract Rev 13.9 "
                  "XPACT_XPTR_LAYOUT_TAG.");
    static_assert(alignof(XPtr<XObject>) == 8,
                  "XPtr<XObject> ABI lock: 8-byte alignment per "
                  "XPACT_XPTR_LAYOUT_TAG.");
    static_assert(offsetof(XPtr<XObject>, Ptr) == 0,
                  "XPtr<XObject> ABI lock: Ptr at offset 0 (the slot is "
                  "reinterpret_cast-compatible with XObject**).");
    static_assert(sizeof(XPtr<XObject>::Ptr) == 8,
                  "XPtr<XObject> ABI lock: Ptr is an 8-byte raw pointer.");

    // Trait locks. XPtr MUST be trivially copyable + trivially
    // destructible so:
    //   * Containers can memcpy without invoking copy-constructors.
    //   * The FObjectProperty payload can read the slot as a raw
    //     XObject* via reinterpret_cast.
    //   * TArray storage uses the trivial-relocate fast path.
    static_assert(::std::is_trivially_copyable_v<XPtr<XObject>>,
                  "XPtr<XObject> must be trivially copyable (raw-T*-"
                  "compatibility + container memcpy invariants).");
    static_assert(::std::is_trivially_destructible_v<XPtr<XObject>>,
                  "XPtr<XObject> must be trivially destructible.");
    static_assert(::std::is_standard_layout_v<XPtr<XObject>>,
                  "XPtr<XObject> must be standard layout (reinterpret_"
                  "cast-from-XObject** + offsetof invariants).");

} // namespace XCore
