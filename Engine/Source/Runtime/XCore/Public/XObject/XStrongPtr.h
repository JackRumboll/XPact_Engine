// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XStrongPtr.h -- 8-byte self-rooting strong pointer
// (XCoreXObject Rev 4 §6.5 + Rev 2 FIX-A-MIN-38 / ALT-HANDLE-2).
// =====================================================================
//
// XCoreXObject Rev 4 Section 6.5 ("XStrongPtr<T>"):
//
//   "XStrongPtr<T> is the non-container strong hold equivalent to UE's
//    TStrongObjectPtr (UE-VER-4 / FIX-A-HIGH-11). It uses the
//    FXObjectArrayEntry's refcount slot (8-byte StateBits has a refcount
//    sub-field; per-entry refcount is incremented at construction and
//    decremented at destruction). The GC walk treats objects with non-
//    zero refcount as root-pinned for the duration of the hold."
//
// PURPOSE: SELF-ROOTING strong reference. Unlike XPtr (which depends on
// its container to provide GC rooting), XStrongPtr keeps the pointee
// alive across GC cycles by bumping a per-FXObjectArrayEntry refcount
// field. The collector treats entries with refcount > 0 as root-pinned
// (per spec §6.5 trailing prose); the pointee survives any number of
// GC cycles as long as at least one XStrongPtr holds the reference.
//
// USE CASES (per spec §6.5):
//
//   * Editor tooling holding a reference to an in-flight asset edit.
//   * Async I/O work items capturing the target XObject by handle.
//   * Third-party C++ code that cannot use XPtr (XPtr requires a
//     container that registers an XGCRootSpan; XStrongPtr is the
//     "I have a stack variable / class member outside reflection /
//     std::shared_ptr-like usage" hold).
//
// ABI SHAPE (per spec §6.5 code body):
//
//   "T* Ptr" -- a single 8-byte raw pointer, ABI-equivalent to XPtr.
//   The static_assert at the bottom locks sizeof == 8.
//
// REFCOUNT MECHANICS (per spec §6.5 + FXObjectArray API):
//
//   * Constructor from T*: FXObjectArray::AddRef(InternalIndex)
//     increments the refcount sub-field in FXObjectArrayEntry::StateBits.
//   * Copy ctor / copy-assign: AddRef on the source's index.
//   * Move ctor / move-assign: NO refcount change (transfer of
//     ownership; source becomes null without ReleaseRef).
//   * Destructor / Reset(): FXObjectArray::ReleaseRef(InternalIndex).
//
//   The atomic CAS-loop discipline in FXObjectArray::AddRef /
//   ReleaseRef preserves the refcount under concurrent mutation. The
//   bit layout of StateBits is documented at FXObjectArray.h.
//
// HOT-RELOAD: NO virtual methods. Trivially destructible? NO -- the
// destructor calls FXObjectArray::ReleaseRef. XStrongPtr is the ONLY
// handle type in the XObject family that is NOT trivially destructible
// (and the only one whose copy / move surface is non-default).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/XObject.h"          // T must derive from XObject
#include "XObject/FXObjectArray.h"    // FXObjectArray::AddRef / ReleaseRef

#include <cstddef>                    // offsetof
#include <cstdint>
#include <type_traits>                // is_base_of_v, is_polymorphic_v

namespace XCore
{
    // -----------------------------------------------------------------
    // XStrongPtr<T> -- 8-byte self-rooting strong typed pointer.
    //
    // alignas(8) -- matches a raw T* on every supported platform.
    //
    // Template parameter T MUST derive from XObject (the refcount path
    // routes through FXObjectArray which only handles XObject-family
    // pointers).
    //
    // NO XPACT_*_LAYOUT_TAG defined in §11.1: XStrongPtr ships at Phase
    // 5.c without a dedicated layout-tag string literal (the §11.3
    // static_assert pin is sufficient -- the spec did not allocate a
    // tag string because XStrongPtr's ABI is identical to XPtr's and
    // no separate addendum tag is required to lock the byte layout).
    // The Contract Rev 13.9 addendum tags lock XPtr / XWeakPtr /
    // XObjectKey explicitly; XStrongPtr's layout is implicitly locked
    // by the XPACT_VERIFY_XOBJECT_LAYOUT macro.
    // -----------------------------------------------------------------
    template <typename T>
    class alignas(8) XStrongPtr
    {
        static_assert(::std::is_base_of_v<XObject, T>,
                      "XStrongPtr<T> requires T : XObject. The handle's "
                      "refcount path routes through FXObjectArray which "
                      "only operates on XObject-family pointers.");

    public:
        // =============================================================
        // ABI-locked data member (8 bytes; per spec §6.5 code body).
        //
        // Single field at offset 0; raw T*. Matches XPtr's storage but
        // with DIFFERENT lifecycle semantics (the destructor releases
        // the refcount; XPtr's destructor is trivial).
        // =============================================================

        T* Ptr;                                       // @0  +8

        // =============================================================
        // Construction.
        //
        // Default ctor: null pointer; NO refcount touch. constexpr so
        // XStrongPtr can sit in constinit storage as a null sentinel
        // (the refcount only fires when a real T* is assigned).
        //
        // From-T* ctor: captures Ptr; bumps refcount on the target's
        // FXObjectArrayEntry. nullptr maps to a null sentinel + no
        // refcount activity.
        // =============================================================

        constexpr XStrongPtr() noexcept
            : Ptr(nullptr)
        {
        }

        constexpr XStrongPtr(::std::nullptr_t) noexcept
            : Ptr(nullptr)
        {
        }

        // From-T* ctor. EXPLICIT to prevent silent T*-to-XStrongPtr
        // conversions (mirrors UE TStrongObjectPtr discipline).
        //
        // The Dev check + the AddRef happen inline; the refcount is
        // bumped only for non-null InPtr.
        //
        // Body is non-inline (in XObjectHandles.cpp) because the
        // AddRef path touches FXObjectArray's lock; inlining the call
        // into every site would defeat the diagnostic clarity of
        // having a single non-inline entry point + a single non-inline
        // exit point.
        explicit XStrongPtr(T* InPtr) noexcept;

        // =============================================================
        // Copy ctor / copy-assign: AddRef on the source's slot.
        //
        // The Ptr is copied; the refcount is incremented. The two
        // XStrongPtrs now share ownership of the keep-alive on the
        // target.
        // =============================================================

        XStrongPtr(const XStrongPtr& Other) noexcept;
        XStrongPtr& operator=(const XStrongPtr& Other) noexcept;

        // =============================================================
        // Move ctor / move-assign: ownership transfer; NO refcount
        // change.
        //
        // The source's Ptr is set to nullptr WITHOUT calling
        // ReleaseRef; the destination takes over the existing
        // refcount. Net effect: refcount is unchanged across the move.
        //
        // The body is non-inline (in XObjectHandles.cpp) for symmetry
        // with the copy ctor; the actual instruction count would
        // happily inline, but the diagnostic-clarity-via-single-entry-
        // point rule applies.
        // =============================================================

        XStrongPtr(XStrongPtr&& Other) noexcept;
        XStrongPtr& operator=(XStrongPtr&& Other) noexcept;

        // =============================================================
        // Destructor: ReleaseRef.
        //
        // For a non-null Ptr, decrements the refcount on the target's
        // FXObjectArrayEntry. If the refcount drops to zero, the
        // collector's next pass may reclaim the slot (this is the
        // "release pinning" semantic).
        //
        // The destructor is NOT trivial -- XStrongPtr is the only
        // XObject-family handle that touches refcount state at
        // destruction. The static_assert below documents this in the
        // trait surface.
        // =============================================================

        ~XStrongPtr() noexcept;

        // =============================================================
        // Reset / assignment.
        // =============================================================

        // Reset to a null pointer (or to a new T*). The existing Ptr's
        // refcount is decremented; the new Ptr's refcount (if non-null)
        // is incremented. Symmetric with std::shared_ptr::reset.
        void Reset(T* InPtr = nullptr) noexcept;

        // From-T* assignment (ergonomic call sites; routes through
        // Reset to share the same release-then-bump implementation).
        XPACT_FORCEINLINE XStrongPtr& operator=(T* InPtr) noexcept
        {
            Reset(InPtr);
            return *this;
        }

        XPACT_FORCEINLINE XStrongPtr& operator=(::std::nullptr_t) noexcept
        {
            Reset(nullptr);
            return *this;
        }

        // =============================================================
        // Predicates + Access.
        //
        // Mirrors XPtr's surface. IsValid and IsNull return whether
        // the stored Ptr is non-null; the contract is "the pointee is
        // alive by refcount; no SerialNumber check needed".
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return Ptr == nullptr;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsValid() const noexcept
        {
            return Ptr != nullptr;
        }

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

        // =============================================================
        // Comparison.
        //
        // Same as XPtr: pointer-equality on the stored Ptr.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const XStrongPtr& Other) const noexcept
        {
            return Ptr == Other.Ptr;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const XStrongPtr& Other) const noexcept
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

    // =================================================================
    // Template body definitions.
    //
    // The bodies live INLINE-in-the-header (rather than in a .cpp) so
    // the compiler can monomorphise per-T. The actual AddRef /
    // ReleaseRef calls route through FXObjectArray which itself has
    // non-template bodies in .cpp; the per-T XStrongPtr instantiation
    // just forwards Ptr->InternalIndex into those.
    //
    // The body operates on the InternalIndex extracted via the public
    // XObject::InternalIndex field; that field is public per the spec
    // §2.2 documented surface (and the FXObjectArray's own bookkeeping
    // path reads it directly).
    // =================================================================

    template <typename T>
    XPACT_FORCEINLINE XStrongPtr<T>::XStrongPtr(T* InPtr) noexcept
        : Ptr(InPtr)
    {
        if (InPtr != nullptr)
        {
            XPACT_CHECK(static_cast<const XObject*>(InPtr)->IsValidLowLevel());
            FXObjectArray::Get().AddRef(InPtr->InternalIndex);
        }
    }

    template <typename T>
    XPACT_FORCEINLINE XStrongPtr<T>::XStrongPtr(const XStrongPtr<T>& Other) noexcept
        : Ptr(Other.Ptr)
    {
        if (Ptr != nullptr)
        {
            FXObjectArray::Get().AddRef(Ptr->InternalIndex);
        }
    }

    template <typename T>
    XPACT_FORCEINLINE XStrongPtr<T>& XStrongPtr<T>::operator=(const XStrongPtr<T>& Other) noexcept
    {
        // Self-assignment: net AddRef + ReleaseRef is balanced; we
        // can short-circuit to avoid the lock-acquire pair. Mirrors
        // std::shared_ptr's self-assignment fast path.
        if (this == &Other)
        {
            return *this;
        }

        // Bump the new target's refcount BEFORE releasing the old to
        // preserve correctness if the old and new are the same object
        // (the refcount transitions non-zero -> non-zero without ever
        // touching zero, which would otherwise expose the pointee to
        // GC reclaim between the two operations).
        T* const NewPtr = Other.Ptr;
        if (NewPtr != nullptr)
        {
            FXObjectArray::Get().AddRef(NewPtr->InternalIndex);
        }
        T* const OldPtr = Ptr;
        Ptr = NewPtr;
        if (OldPtr != nullptr)
        {
            FXObjectArray::Get().ReleaseRef(OldPtr->InternalIndex);
        }
        return *this;
    }

    template <typename T>
    XPACT_FORCEINLINE XStrongPtr<T>::XStrongPtr(XStrongPtr<T>&& Other) noexcept
        : Ptr(Other.Ptr)
    {
        // No refcount activity: ownership transfers from Other to
        // *this. Other's Ptr is nulled so its destructor is a no-op.
        Other.Ptr = nullptr;
    }

    template <typename T>
    XPACT_FORCEINLINE XStrongPtr<T>& XStrongPtr<T>::operator=(XStrongPtr<T>&& Other) noexcept
    {
        if (this == &Other)
        {
            return *this;
        }

        // Release the old; take over the source's existing refcount.
        T* const OldPtr = Ptr;
        Ptr             = Other.Ptr;
        Other.Ptr       = nullptr;
        if (OldPtr != nullptr)
        {
            FXObjectArray::Get().ReleaseRef(OldPtr->InternalIndex);
        }
        return *this;
    }

    template <typename T>
    XPACT_FORCEINLINE XStrongPtr<T>::~XStrongPtr() noexcept
    {
        if (Ptr != nullptr)
        {
            FXObjectArray::Get().ReleaseRef(Ptr->InternalIndex);
        }
    }

    template <typename T>
    XPACT_FORCEINLINE void XStrongPtr<T>::Reset(T* InPtr) noexcept
    {
        // Same release-then-bump discipline as operator= for the same
        // exposure-to-GC-reclaim reason.
        if (InPtr != nullptr)
        {
            XPACT_CHECK(static_cast<const XObject*>(InPtr)->IsValidLowLevel());
            FXObjectArray::Get().AddRef(InPtr->InternalIndex);
        }
        T* const OldPtr = Ptr;
        Ptr = InPtr;
        if (OldPtr != nullptr)
        {
            FXObjectArray::Get().ReleaseRef(OldPtr->InternalIndex);
        }
    }

    // ---------------------------------------------------------------------
    // Reverse comparison (T* / nullptr on the lhs).
    // ---------------------------------------------------------------------
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const T* Lhs, const XStrongPtr<T>& Rhs) noexcept
    {
        return Lhs == Rhs.Get();
    }
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const T* Lhs, const XStrongPtr<T>& Rhs) noexcept
    {
        return Lhs != Rhs.Get();
    }
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(::std::nullptr_t, const XStrongPtr<T>& Rhs) noexcept
    {
        return Rhs.IsNull();
    }
    template <typename T>
    [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(::std::nullptr_t, const XStrongPtr<T>& Rhs) noexcept
    {
        return !Rhs.IsNull();
    }

    // ---------------------------------------------------------------------
    // ABI locks (per Contract Rev 13.9 §11.3 XPACT_VERIFY_XOBJECT_LAYOUT
    // macro pin; XStrongPtr does NOT have a dedicated XPACT_*_LAYOUT_TAG
    // in §11.1 -- its layout is the same as a raw T* and the §11.3 macro
    // is sufficient to lock the contract).
    //
    // The XStrongPtr<XObject> instantiation is the canonical pin.
    // ---------------------------------------------------------------------
    static_assert(sizeof(XStrongPtr<XObject>)  == 8,
                  "XStrongPtr<XObject> ABI lock: 8 bytes per XCoreXObject "
                  "Rev 4 §6.5 (Rev 2 FIX-A-MIN-38 / ALT-HANDLE-2). The "
                  "storage is identical to XPtr<T> (raw T*); the lifecycle "
                  "semantics differ.");
    static_assert(alignof(XStrongPtr<XObject>) == 8,
                  "XStrongPtr<XObject> ABI lock: 8-byte alignment.");
    static_assert(offsetof(XStrongPtr<XObject>, Ptr) == 0,
                  "XStrongPtr<XObject> ABI lock: Ptr at offset 0.");
    static_assert(sizeof(XStrongPtr<XObject>::Ptr) == 8,
                  "XStrongPtr<XObject> ABI lock: Ptr is an 8-byte raw pointer.");

    // Trait locks.
    //
    // XStrongPtr is NOT trivially copyable (the copy ctor / copy-assign
    // bump the refcount; the move ctor / move-assign clear the source's
    // Ptr; the destructor calls ReleaseRef). This is the EXPECTED
    // contract -- XStrongPtr is the only handle whose lifecycle has
    // real side-effects.
    //
    // XStrongPtr IS non-polymorphic (no virtual methods) so the
    // hot-reload commitment is preserved (a patched DLL can rebind the
    // FClass without rewriting any XStrongPtr surface).
    static_assert(!::std::is_polymorphic_v<XStrongPtr<XObject>>,
                  "XStrongPtr<XObject> must NOT be polymorphic "
                  "(hot-reload commitment + FakeVTable dispatch pattern).");
    static_assert(!::std::is_trivially_copyable_v<XStrongPtr<XObject>>,
                  "XStrongPtr<XObject> must NOT be trivially copyable "
                  "(the copy ctor + assign bump the refcount; the "
                  "destructor releases). This static_assert documents "
                  "the deliberate non-trivial lifecycle.");

} // namespace XCore
