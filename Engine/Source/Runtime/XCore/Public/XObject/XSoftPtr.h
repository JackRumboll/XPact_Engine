// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XSoftPtr.h -- path-based soft reference (XCoreXObject Rev 4 §6.3).
// =====================================================================
//
// XCoreXObject Rev 4 Section 6.3 ("XSoftPtr<T> (soft path-based
// reference)"):
//
//   "XSoftPtr<T> is the soft pointer: a path-based reference that
//    resolves on demand. The path is an FSoftObjectPath-shape from
//    XCore-4b's FSoftObjectProperty. It does NOT register as a GC root
//    (the pointee can be unloaded; resolution loads on demand)."
//
// USE CASE (per spec §6.3):
//
//   "Scenario authoring. A scenario references an XActor by path; the
//    path is serialized; the actor itself is not loaded until the
//    scenario unloads or until the user explicitly resolves the
//    reference."
//
// ABI POSTURE (per spec §6.3):
//
//   XSoftPtr is NOT in the ABI-lock set. Its size is variable (it
//   carries an FSoftObjectPath whose System-5 full impl is path-string-
//   shape). At Phase 5.c the FSoftObjectPath is an 8-byte placeholder
//   (XCore-4b Phase 4b.4b shipped the placeholder for FSoftObjectProperty
//   payload compat); XSoftPtr<T> at Phase 5.c is therefore:
//
//     * 8 bytes for the FSoftObjectPath placeholder
//     * 8 bytes for a CachedRef XWeakPtr<T> (lazy resolution cache)
//     = 16 bytes total
//
//   When System 5 ships the full FSoftObjectPath (per the FSoftObjectPath
//   header's TODO comment), XSoftPtr's footprint will grow proportionally;
//   the API surface here is stable across that swap.
//
// RESOLUTION (per spec §6.3 + the deferred-asset-registry note):
//
//   Phase 5.c ships the SHAPE: the struct layout + the API surface +
//   the cached-weak-ref pattern. The actual path-to-XObject resolution
//   requires XAssetRegistry + async load + path resolution infrastructure
//   that lands at Layer 9+. Get() at Phase 5.c:
//
//     * If CachedRef is valid (the asset has been resolved at least
//       once and the underlying XObject is still alive), return
//       CachedRef.Get().
//     * Otherwise route through FSoftObjectPath::ResolveSyncToXObject
//       which is a Phase 5.c stub returning nullptr until Layer 9
//       wires the real resolver.
//
// HOT-RELOAD: NO virtual methods. The struct is trivially destructible
// (CachedRef is XWeakPtr which is trivially destructible; Path is
// FSoftObjectPath which is trivially destructible). Non-trivially
// COPYABLE because the XWeakPtr CachedRef has non-trivial copy
// semantics? Actually XWeakPtr IS trivially copyable; FSoftObjectPath
// IS trivially copyable; so XSoftPtr IS trivially copyable at Phase
// 5.c. When the full FSoftObjectPath (with path-string heap allocation)
// ships, XSoftPtr will become non-trivially-copyable.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/XObject.h"               // T must derive from XObject
#include "XObject/XWeakPtr.h"               // CachedRef storage
#include "Reflection/FSoftObjectPath.h"     // FSoftObjectPath placeholder

#include <cstddef>                          // offsetof
#include <cstdint>
#include <type_traits>                      // is_base_of_v

namespace XCore
{
    // -----------------------------------------------------------------
    // XSoftPtr<T> -- path-based soft reference with a lazy XWeakPtr
    // resolution cache.
    //
    // alignas(8) -- matches FSoftObjectPath's 8-byte alignment. The
    // overall size is 16 bytes at Phase 5.c (8 Path + 8 CachedRef).
    //
    // Template parameter T MUST derive from XObject.
    // -----------------------------------------------------------------
    template <typename T>
    class alignas(8) XSoftPtr
    {
        static_assert(::std::is_base_of_v<XObject, T>,
                      "XSoftPtr<T> requires T : XObject.");

    public:
        // =============================================================
        // Data members (16 bytes at Phase 5.c).
        //
        // Path stores the path payload (8-byte placeholder; System 5
        // will replace with a path-string handle).
        //
        // CachedRef is mutable so Get() can populate it on first resolve
        // even when called on a const XSoftPtr (the resolve cache IS a
        // logically-const operation; the user's intent is "read the
        // pointee" not "mutate the soft-ptr").
        // =============================================================

        // 8-byte FSoftObjectPath placeholder. Bit-identical to
        // FSoftObjectProperty's payload so reflection-runtime code can
        // read soft-property slots as XSoftPtr<T> directly (modulo the
        // CachedRef which is XSoftPtr-specific and NOT part of the
        // property payload).
        ::XCore::Reflect::FSoftObjectPath Path;       // @0  +8

        // Cached resolution. XWeakPtr<T> is 8 bytes; lazily populated
        // on first Get() / Resolve() call. The XWeakPtr's IsValid()
        // semantics handle the case where the asset was loaded, the
        // cache populated, then the asset unloaded (XWeakPtr resolves
        // to nullptr on the SerialNumber check; XSoftPtr re-resolves
        // from Path on the next Get()).
        //
        // Mutable so the resolve cache works on const XSoftPtr<T>
        // instances (the cache mutation IS logically transparent to
        // the caller observing the resolved pointee).
        mutable XWeakPtr<T>                CachedRef;  // @8  +8

        // =============================================================
        // Construction.
        // =============================================================

        // Default ctor: null path + null cached ref.
        constexpr XSoftPtr() noexcept
            : Path()
            , CachedRef()
        {
        }

        constexpr XSoftPtr(::std::nullptr_t) noexcept
            : Path()
            , CachedRef()
        {
        }

        // From FSoftObjectPath: captures the path; CachedRef stays
        // null (lazy resolve on first Get()).
        explicit constexpr XSoftPtr(const ::XCore::Reflect::FSoftObjectPath& InPath) noexcept
            : Path(InPath)
            , CachedRef()
        {
        }

        // From T*: constructs a path from the object's full name AND
        // populates CachedRef with a weak reference to the object. The
        // first Get() call returns the pointee directly via CachedRef
        // without re-resolving the path (zero-cost if the object stays
        // alive).
        //
        // At Phase 5.c the path-from-XObject construction is a stub
        // (the path is left null because the full FSoftObjectPath impl
        // doesn't exist yet); CachedRef carries the actual reference
        // for the early-load case. When Layer 9 ships the asset path
        // emitter, this ctor's Path-assignment will materialise the
        // real path string.
        explicit XSoftPtr(T* InPtr) noexcept
            : Path()
            , CachedRef(InPtr)
        {
            // TODO(Layer 9 asset registry): emit a real
            // FSoftObjectPath here once the asset path emitter ships.
            // The current Phase 5.c posture is "path null + cache
            // valid" for the early-load case; the lazy-resolve path
            // covers the deserialise-then-load case where the cache
            // starts empty.
        }

        // Trivially destructible + trivially copyable at Phase 5.c
        // (FSoftObjectPath placeholder + XWeakPtr are both trivially
        // copy/destruct).
        constexpr XSoftPtr(const XSoftPtr&) noexcept            = default;
        constexpr XSoftPtr(XSoftPtr&&) noexcept                 = default;
        constexpr XSoftPtr& operator=(const XSoftPtr&) noexcept = default;
        constexpr XSoftPtr& operator=(XSoftPtr&&) noexcept      = default;
        ~XSoftPtr() noexcept                                    = default;

        // =============================================================
        // Predicates.
        // =============================================================

        // IsNull: structurally null (no path AND no cached ref). This
        // is stricter than just `Path.IsNull()` -- the from-T* ctor
        // populates CachedRef WITHOUT a path; a soft-ptr constructed
        // that way is NOT null even though its Path is.
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool IsNull() const noexcept
        {
            return Path.IsNull() && CachedRef.IsNull();
        }

        // IsValid: probes whether the soft-ptr can currently produce a
        // live XObject. Cheaper than Get() in the common case (no
        // copy of the resolved pointer; just the predicate).
        [[nodiscard]] XPACT_FORCEINLINE bool IsValid() const noexcept
        {
            if (CachedRef.IsValid())
            {
                return true;
            }
            // The path-resolve probe routes through Get() which itself
            // populates the cache; the cost is the same regardless.
            return Get() != nullptr;
        }

        // =============================================================
        // Access.
        // =============================================================

        // GetPath: read-only access to the stored FSoftObjectPath.
        // The result is by-const-ref so the caller can avoid a copy
        // when only inspecting the path.
        [[nodiscard]] XPACT_FORCEINLINE constexpr const ::XCore::Reflect::FSoftObjectPath& GetPath() const noexcept
        {
            return Path;
        }

        // Get: lazy resolve.
        //
        //   * If CachedRef is valid, return CachedRef.Get() (single
        //     FXObjectArray lookup; ~5-10 ns).
        //   * Otherwise route through the path-resolve stub. If the
        //     stub returns non-null (Layer 9 + asset registry path),
        //     populate CachedRef and return the resolved pointer.
        //   * Otherwise return nullptr (the path could not be
        //     resolved; the asset is not loaded).
        //
        // Inline because the FXObjectArray::GetObjectAtIndex call (via
        // XWeakPtr::Get) is already a single non-inline lookup; the
        // wrapping logic is trivial branchy code.
        //
        // NOTE: const-method that mutates `mutable CachedRef`; this IS
        // a documented design: the resolve cache is logically
        // transparent to the caller observing the resolved pointee.
        [[nodiscard]] XPACT_FORCEINLINE T* Get() const noexcept
        {
            // Fast path: cache hit.
            if (T* Cached = CachedRef.Get(); Cached != nullptr)
            {
                return Cached;
            }

            // Slow path: re-resolve via the path stub.
            T* Resolved = ResolveSyncToXObject_Internal();
            if (Resolved != nullptr)
            {
                CachedRef = XWeakPtr<T>(Resolved);
            }
            return Resolved;
        }

        // Reset: clears both Path and CachedRef.
        XPACT_FORCEINLINE void Reset() noexcept
        {
            Path      = ::XCore::Reflect::FSoftObjectPath{};
            CachedRef = XWeakPtr<T>{};
        }

        // =============================================================
        // Equality.
        //
        // Two XSoftPtrs are equal iff their Paths are equal. The
        // CachedRef is a resolution artefact and NOT part of the
        // identity surface (two soft-ptrs to the same asset may have
        // different cache states at any moment).
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator==(const XSoftPtr& Other) const noexcept
        {
            return Path == Other.Path;
        }
        [[nodiscard]] XPACT_FORCEINLINE constexpr bool operator!=(const XSoftPtr& Other) const noexcept
        {
            return Path != Other.Path;
        }

    private:
        // =============================================================
        // ResolveSyncToXObject_Internal -- the path-to-XObject stub.
        //
        // Phase 5.c posture: this is a STUB returning nullptr until
        // Layer 9 ships the asset-registry / async-load infrastructure
        // that can resolve an FSoftObjectPath to a live XObject.
        //
        // The stub is per-template-instantiation static-inline so:
        //   * Every XSoftPtr<T> instantiation gets its own stub copy
        //     (linker COMDAT-dedups). This avoids forcing a
        //     XSoftPtr.cpp anchor TU.
        //   * The stub body is trivially inlinable (returns nullptr)
        //     so the compiler can elide the dead-store of "Resolved
        //     = nullptr; CachedRef = XWeakPtr(nullptr);" at every Get()
        //     call site.
        //
        // When Layer 9 ships the real resolver, this stub is replaced
        // with a non-inline call into the asset-registry's
        // XCore::Asset::TryResolveSoft path.
        //
        // TODO(Layer 9 asset registry): replace this stub with the
        // real synchronous-resolve path. The signature must remain
        // (T*) noexcept so the call site here is stable.
        // =============================================================
        [[nodiscard]] XPACT_FORCEINLINE T* ResolveSyncToXObject_Internal() const noexcept
        {
            // Phase 5.c stub: no asset-registry to consult.
            //
            // The Path's IsNull() short-circuit avoids any work for
            // soft-ptrs whose only reference path is the CachedRef
            // (the from-T* ctor case).
            (void)Path;
            return nullptr;
        }
    };

    // ---------------------------------------------------------------------
    // ABI checks (XSoftPtr is NOT in the §11.1 Contract-locked tag set;
    // its size is variable. The Phase 5.c sizeof IS 16 bytes given the
    // placeholder FSoftObjectPath; the static_assert below documents
    // that invariant for the current snapshot. When the full
    // FSoftObjectPath ships, this assertion will need to be updated
    // alongside it.
    // ---------------------------------------------------------------------
    static_assert(sizeof(XSoftPtr<XObject>) == 16,
                  "XSoftPtr<XObject> Phase 5.c sizeof lock: 16 bytes = "
                  "8 FSoftObjectPath placeholder + 8 CachedRef "
                  "XWeakPtr<XObject>. NOT part of the ABI-lock set per "
                  "spec §6.3 (variable-size); the sizeof grows when "
                  "FSoftObjectPath's System-5 full impl ships.");
    static_assert(alignof(XSoftPtr<XObject>) == 8,
                  "XSoftPtr<XObject> alignment: 8 bytes (matches "
                  "FSoftObjectPath alignof).");

    static_assert(offsetof(XSoftPtr<XObject>, Path)      == 0,
                  "XSoftPtr<XObject>: Path at offset 0.");
    static_assert(offsetof(XSoftPtr<XObject>, CachedRef) == 8,
                  "XSoftPtr<XObject>: CachedRef at offset 8.");

    // Trait locks. At Phase 5.c XSoftPtr IS trivially copyable +
    // trivially destructible because both component types are. The
    // sizeof + trait surface will shift when the full FSoftObjectPath
    // ships.
    static_assert(::std::is_trivially_copyable_v<XSoftPtr<XObject>>,
                  "XSoftPtr<XObject> Phase 5.c trait lock: trivially "
                  "copyable (FSoftObjectPath placeholder + XWeakPtr "
                  "both trivially copyable). This relaxes when System 5 "
                  "ships the full FSoftObjectPath.");
    static_assert(::std::is_trivially_destructible_v<XSoftPtr<XObject>>,
                  "XSoftPtr<XObject> Phase 5.c trait lock: trivially "
                  "destructible.");

} // namespace XCore
