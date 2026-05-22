// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TArray.h -- user-facing growable contiguous array (Section 5.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (Public API) + Section 5.4 (GC interaction,
// locked decisions 7 and 9) + Section 5.5 (UE divergences) + dependency-
// graph step 7.
//
// TArray<T> is the user-facing growable array. Two flavors:
//
//   1. Non-GC specialization (default for value types: int, float,
//      structs, FName, etc.). Aliases Detail::TArrayCore<T, AllocatorT>
//      verbatim. Copy + move + Add + Emplace + RemoveAt + RemoveAtSwap
//      + Reserve + Reset + At + operator[]. See TArrayCore.h for the
//      buffer-management primitive.
//
//   2. GC-aware partial specialization (TArray<T*, AllocatorT> where
//      T : XCore::Reflect::XObject). Embeds an XGCRootSpan, registers
//      it at construction, updates on every resize, unregisters at
//      destruction. Move-only (per Contract Rev 13.7 Section 3.4 M1).
//      Stores invoke XGC_WriteBarrier BEFORE the slot write (per fix
//      C-4 pre-store ordering).
//
// PHASE 1C CONTRACT FOR THE GC-AWARE SPECIALIZATION.
//
// The GC-aware specialization is gated on `requires
// std::derived_from<T, XCore::Reflect::XObject>`. XObject ships in
// XCore-4b (master plan Section 4 Step 5); Phase 1c has no concrete
// XObject type yet. The specialization is therefore compiled but
// uninstantiable from end-user code -- no T satisfies the
// std::derived_from constraint until XCore-4b's XObject type lands.
//
// This is the engineering-principles-correct design rather than the
// shortcut alternatives:
//   * Stubbing XObject in XCore-4a would create a layering inversion
//     (XCore-4a depending on XCore-4b's domain abstraction).
//   * Skipping the specialization entirely would defer the contract
//     handoff to XCore-4b and risk an API break when XObject lands.
//   * Defining the specialization with `requires false` would compile
//     trivially but communicate nothing about the intended contract.
//
// The chosen design: the specialization is well-formed source code,
// references XCore::Reflect::XObject as a forward-declared class, and
// is selected only when std::derived_from<T, XObject> evaluates true.
// XCore-4b's first revision delivers `class XObject` in namespace
// XCore::Reflect; on that revision, TArray<XActor*> immediately
// resolves to the GC-aware specialization with zero source changes.
//
// CONTRACT HANDOFF NOTE (read me when shipping XCore-4b).
//
// When XCore-4b lands:
//   1. The `class XObject;` forward declaration in this file is
//      satisfied by XCore-4b's full XObject definition.
//   2. Any user TArray<XActor*> instantiation selects the GC-aware
//      specialization automatically.
//   3. The XGC_RegisterRootSpan / XGC_UpdateRootSpan /
//      XGC_UnregisterRootSpan / XGC_WriteBarrier extern "C" symbols
//      (declared in GC/XGCDeclarations.h) are now backed by the
//      XCore-4b card-table-marking implementation rather than the
//      Phase 1c no-op stubs.
// No edits to this header should be required.
//
// =====================================================================

#include "Containers/TArrayCore.h"
#include "Containers/DefaultAllocator.h"
#include "GC/XGCDeclarations.h"          // XGCRootSpan, XGCRootKind, XGC_* extern "C"
#include "Macros/XPactMacros.h"           // X_DECLARE_GC_AWARE_CONTAINER
#include "Macros/XCoreFwd.h"              // XCore::Reflect::FName forward

#include <concepts>                       // std::derived_from
#include <type_traits>                    // std::is_pointer_v, std::remove_pointer_t

// =====================================================================
// XCore::Reflect::XObject forward declaration.
//
// The actual class ships in XCore-4b (master plan Step 5). The forward
// declaration here lets the GC-aware partial specialization's
// std::derived_from<T, XObject> constraint be well-formed without
// XCore-4a depending on XCore-4b. The forward-declaration pattern is
// the same one used for FName (XCoreFwd.h Section 11.8) and FArchive
// (HAL/FMemory.h Section 4.1).
//
// IMPORTANT: this is a CLASS forward declaration, not the FName-style
// STRUCT forward declaration with a layout-lock. XObject's layout is
// XCore-4b's concern; XCore-4a only needs the type name to be
// declarable as a base class in the std::derived_from constraint.
// =====================================================================

namespace XCore::Reflect
{
    class XObject;
}

namespace XCore
{
    // -----------------------------------------------------------------
    // TArray<T, AllocatorT> -- the primary non-GC specialization.
    //
    // Section 5.1 wording: copy + move + Add + Emplace + RemoveAt +
    // RemoveAtSwap + Reserve + Reset + At + operator[] + GetData +
    // begin/end. The non-GC TArray IS Detail::TArrayCore<T, AllocatorT>
    // -- a using-alias would suffice in principle, but a class derivation
    // is clearer in error messages and lets future Phase-1d additions
    // (e.g., TArray-specific algorithms that don't belong on the
    // primitive) land here naturally.
    //
    // Per Section 5.1 the public TArray copy semantics depend on T:
    //   * Non-GC T (value types, non-XObject pointers, structs):
    //     copy-allowed (delegates to TArrayCore which is copyable).
    //   * GC-aware T (XObject pointers, TWeakObjectPtr<T>):
    //     move-only (the partial specialization below deletes copy).
    //
    // The non-GC TArray inherits TArrayCore's full surface; it does not
    // override anything. The class derivation is purely for the contract
    // handoff (a future Phase-1d addition like TArray::Sort would land
    // as a member of this class without disturbing the primitive).
    // -----------------------------------------------------------------

    template<typename T, typename AllocatorT>
    class TArray : public ::XCore::Detail::TArrayCore<T, AllocatorT>
    {
    public:
        using BaseType      = ::XCore::Detail::TArrayCore<T, AllocatorT>;
        using ElementType   = typename BaseType::ElementType;
        using AllocatorType = typename BaseType::AllocatorType;
        using SizeType      = typename BaseType::SizeType;

        // Inherit all constructors from the primitive. The base class's
        // default ctor, allocator-taking ctor, copy ctor, copy-assign,
        // move ctor, move-assign, destructor all do the right thing
        // for non-GC T.
        using BaseType::BaseType;

        // The base class methods are visible through public inheritance;
        // no further redeclaration needed. The class body exists so a
        // future Phase-1d TArray-specific method (Sort, Filter, etc.)
        // can land here without disturbing TArrayCore.
    };

    // =================================================================
    // GC-aware partial specialization (Section 5.4, locked decision 7).
    // =================================================================
    //
    // TArray<T*, AllocatorT> where T : XCore::Reflect::XObject is
    // GC-aware: every mutation invokes the XGC_WriteBarrier; every
    // resize updates the embedded XGCRootSpan; the destructor
    // unregisters the span before freeing the buffer.
    //
    // The specialization is selected via a `requires
    // std::derived_from<T, XCore::Reflect::XObject>` constraint on the
    // partial specialization's primary template, so only XObject-
    // derived pointer instantiations match. A `TArray<int*>` (non-
    // XObject pointer) goes to the non-GC primary template and behaves
    // like any other value-type TArray (without barriers or root span).
    //
    // The specialization is move-only (Contract Section 3.4 M1).
    // Copy ctor and copy-assign are deleted; move ctor and move-assign
    // transfer the span ownership atomically with respect to the
    // collector (the new container re-registers; the old container
    // updates its span's count to 0 and unregisters).
    //
    // Section 5.4 construction protocol (N6):
    //   1. Initialize m_rootSpan = { nullptr, sizeof(void*), 0, Strong, 0 }.
    //   2. Call XGC_RegisterRootSpan(&m_rootSpan).
    //   3. The buffer is allocated lazily on first Add/Reserve. The
    //      first allocation triggers an XGC_UpdateRootSpan with the
    //      newly-allocated base + the requested count.
    //
    // Section 5.4 resize protocol:
    //   * Every TArrayCore::ReserveAtLeast / __GrowByOne that reallocs
    //     the buffer must be followed by an XGC_UpdateRootSpan
    //     reflecting the new base + count.
    //   * The current Phase-1c implementation routes through Add /
    //     Emplace which trigger __GrowByOne; the partial specialization
    //     wraps the inherited base methods to inject the update.
    //
    // Section 5.4 destruction protocol:
    //   * XGC_UnregisterRootSpan FIRST (so the collector cannot observe
    //     a stale span pointing to the about-to-be-freed buffer).
    //   * Then free the buffer (inherited base destructor handles this).
    //
    // Section 5.4 write-barrier protocol (fix C-4 pre-store ordering):
    //   * XGC_WriteBarrier(&slot, newValue) is called BEFORE the slot
    //     store. The pre-store ordering is the contract.
    //   * The non-mutating accessors (operator[] read, At read) do not
    //     invoke the barrier; only the mutating overloads do.
    //
    // X_DECLARE_GC_AWARE_CONTAINER injects the hot-reload-frozen
    // sentinel + the sizeof(m_rootSpan) == 32 layout assert
    // (Section 13.1 macro definition).
    // =================================================================

    template<typename T, typename AllocatorT>
        requires ::std::derived_from<T, ::XCore::Reflect::XObject>
    class TArray<T*, AllocatorT>
    {
    public:
        using ElementType   = T*;
        using AllocatorType = AllocatorT;
        using SizeType      = ::int32;

        // -------------------------------------------------------------
        // Default ctor (Section 5.4 N6 construction protocol).
        //
        // The order matters:
        //   1. Initialize the inner primitive (empty buffer, m_num = 0).
        //   2. Initialize the span with count = 0 (no slots yet).
        //   3. Register the span. The collector may now observe the
        //      span; with count = 0 the mark walk is a no-op.
        //
        // The construction is exception-safe by virtue of the count = 0
        // initial value: even if a future allocation throws (which it
        // does not under the abort-on-OOM contract; included for
        // belt-and-braces correctness), the destructor's
        // XGC_UnregisterRootSpan + buffer-free are clean teardowns
        // (the buffer is null and count == 0).
        // -------------------------------------------------------------
        TArray() noexcept
            : m_core()
            , m_rootSpan{ nullptr, sizeof(void*), 0, ::XGC::XGCRootKind::Strong, { 0, 0, 0 }, 0 }
        {
            ::XGC::XGC_RegisterRootSpan(&m_rootSpan);
        }

        // -------------------------------------------------------------
        // Allocator-taking ctor: allows per-container FMemTag override.
        // -------------------------------------------------------------
        explicit TArray(const AllocatorT& InAlloc) noexcept
            : m_core(InAlloc)
            , m_rootSpan{ nullptr, sizeof(void*), 0, ::XGC::XGCRootKind::Strong, { 0, 0, 0 }, 0 }
        {
            ::XGC::XGC_RegisterRootSpan(&m_rootSpan);
        }

        // -------------------------------------------------------------
        // Destructor (Section 5.4 destruction protocol).
        //
        // The order matters:
        //   1. XGC_UnregisterRootSpan FIRST (the collector cannot
        //      observe a stale span pointing at the about-to-be-freed
        //      buffer).
        //   2. The inner primitive's destructor frees the buffer
        //      (inherited automatically).
        // -------------------------------------------------------------
        ~TArray() noexcept
        {
            ::XGC::XGC_UnregisterRootSpan(&m_rootSpan);
            // m_core's destructor runs after this; it frees the buffer.
        }

        // -------------------------------------------------------------
        // Copy ctor / copy-assign deleted (Contract Section 3.4 M1
        // / locked decision 9).
        // -------------------------------------------------------------
        TArray(const TArray&)            = delete;
        TArray& operator=(const TArray&) = delete;

        // -------------------------------------------------------------
        // Move ctor (Section 5.4 move protocol).
        //
        // Transfers span ownership: the new container's span is
        // registered with the moved-from buffer; the old container's
        // span is updated to count = 0 (its destructor will
        // unregister it cleanly).
        //
        // Engineering note: we cannot transfer the span IN PLACE
        // because each XGCRootSpan is registered by its byte-address
        // with the collector; the collector's tracking would see the
        // span at the new container's address as a NEW registration
        // separate from the old. Therefore the protocol is:
        //   1. Move the buffer state (m_core) from Other.
        //   2. Register THIS container's span with the moved-in buffer.
        //   3. Zero Other's span (its destructor will then unregister
        //      an empty span as a clean no-op).
        //
        // The "atomic with respect to the collector" wording in Section
        // 5.4 is honoured by Contract Section 3.6 (the collector and
        // span update are mutually serialised via the XGC ABI; XCore-4a
        // is not responsible for the synchronisation primitive itself).
        // -------------------------------------------------------------
        TArray(TArray&& Other) noexcept
            : m_core(::std::move(Other.m_core))
            , m_rootSpan{ nullptr, sizeof(void*), 0, ::XGC::XGCRootKind::Strong, { 0, 0, 0 }, 0 }
        {
            // The buffer is now owned by `this`. Register this span
            // with the moved-in buffer.
            ::XGC::XGC_RegisterRootSpan(&m_rootSpan);
            ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                      reinterpret_cast<void**>(m_core.GetData()),
                                      static_cast<::SIZE_T>(m_core.Num()));

            // Zero Other's span. Its destructor will see count == 0 and
            // unregister cleanly.
            ::XGC::XGC_UpdateRootSpan(&Other.m_rootSpan, nullptr, 0);
        }

        // -------------------------------------------------------------
        // Move-assign (Section 5.4 move protocol; mirrors move ctor).
        // -------------------------------------------------------------
        TArray& operator=(TArray&& Other) noexcept
        {
            if (this != &Other)
            {
                // Unregister this container's existing span (its buffer
                // is about to be replaced via m_core's move-assign).
                ::XGC::XGC_UpdateRootSpan(&m_rootSpan, nullptr, 0);
                // m_core's move-assign frees its own old buffer and
                // takes ownership of Other's buffer.
                m_core = ::std::move(Other.m_core);

                // Re-publish the new buffer to the collector.
                ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                          reinterpret_cast<void**>(m_core.GetData()),
                                          static_cast<::SIZE_T>(m_core.Num()));

                // Zero Other's span.
                ::XGC::XGC_UpdateRootSpan(&Other.m_rootSpan, nullptr, 0);
            }
            return *this;
        }

        // =================================================================
        // Accessors. Match the non-GC TArray surface.
        // =================================================================

        [[nodiscard]] XPACT_FORCEINLINE ::int32 Num() const noexcept
        {
            return m_core.Num();
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 Max() const noexcept
        {
            return m_core.Max();
        }

        [[nodiscard]] XPACT_FORCEINLINE bool IsEmpty() const noexcept
        {
            return m_core.IsEmpty();
        }

        [[nodiscard]] XPACT_FORCEINLINE bool IsValidIndex(::int32 Index) const noexcept
        {
            return m_core.IsValidIndex(Index);
        }

        [[nodiscard]] XPACT_FORCEINLINE T** GetData() noexcept
        {
            return reinterpret_cast<T**>(m_core.GetData());
        }

        [[nodiscard]] XPACT_FORCEINLINE T* const* GetData() const noexcept
        {
            return reinterpret_cast<T* const*>(m_core.GetData());
        }

        // -------------------------------------------------------------
        // operator[] read accessors. NO write barrier (read-only).
        //
        // The non-const overload returns T* by VALUE, not T*& -- the
        // assignment overload below (set-by-index) is the only path
        // that mutates a slot, and it threads the write barrier
        // properly. Returning T*& would let callers bypass the barrier
        // via `arr[i] = newPtr`, which is the very anti-pattern
        // Section 5.4 forbids.
        //
        // For "I want to mutate slot i" callers should use the
        // Set(int32, T*) method which invokes the barrier correctly.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE T* operator[](::int32 Index) const noexcept
        {
            return m_core[Index];
        }

        // -------------------------------------------------------------
        // At -- always-checked indexed access; returns Result-wrapped.
        // Read-only; no barrier.
        // -------------------------------------------------------------
        [[nodiscard]] ::XCore::Result<::std::reference_wrapper<T* const>, ::XCore::FBoundsError> At(::int32 Index) const noexcept
        {
            // Delegate to the const At overload of m_core. The cast
            // through reference_wrapper<T*> -> reference_wrapper<T* const>
            // is well-defined (we are referring to the underlying T*).
            auto Inner = m_core.At(Index);
            if (!Inner.has_value())
            {
                return ::XCore::Unexpected(Inner.error());
            }
            return ::std::reference_wrapper<T* const>(Inner.value().get());
        }

        // -------------------------------------------------------------
        // Set(Index, NewValue) -- the GC-aware index-assignment path.
        //
        // Section 5.4 fix C-4 pre-store ordering:
        //   XGC_WriteBarrier(&slot, NewValue);   // mark card first
        //   slot = NewValue;                     // then store
        // -------------------------------------------------------------
        void Set(::int32 Index, T* NewValue) noexcept
        {
            XPACT_CHECK(m_core.IsValidIndex(Index));
            void** Slot = reinterpret_cast<void**>(m_core.GetData() + Index);
            ::XGC::XGC_WriteBarrier(Slot, static_cast<void*>(NewValue));
            *Slot = static_cast<void*>(NewValue);
        }

        // =================================================================
        // Mutators. Each call into the inner primitive's mutator is
        // followed by an XGC_UpdateRootSpan reflecting the new
        // buffer + count. Writes are barrier-guarded (fix C-4).
        // =================================================================

        // -------------------------------------------------------------
        // Add -- store a pointer into a new tail slot.
        //
        // Per fix C-4 pre-store ordering, the barrier is invoked
        // BEFORE the slot is written. The protocol:
        //   1. Grow if needed via TArrayCore::__GrowByOne (which
        //      reallocates the buffer but does NOT bump m_num).
        //   2. If the grow reallocated, publish the new buffer base to
        //      the collector via XGC_UpdateRootSpan BEFORE writing into
        //      it -- this ensures the collector sees the freshly-
        //      allocated buffer (with count == old count, so it scans
        //      the existing live elements at their new addresses) for
        //      the duration between the realloc and the slot write.
        //   3. Compute &slot at the post-grow position.
        //   4. Invoke barrier on &slot. Note: per Section 5.4 the
        //      barrier guards every store, even fresh ones -- the
        //      contract is uniform.
        //   5. Raw-write NewValue into &slot.
        //   6. Bump m_num via __BumpNumUninit (no placement new; the
        //      slot has already been initialized via the raw store
        //      in step 5).
        //   7. Final XGC_UpdateRootSpan publishing the new count.
        //
        // Returns the index of the new element.
        // -------------------------------------------------------------
        ::int32 Add(T* NewValue) noexcept
        {
            // Grow if needed. __GrowByOne reallocates when m_num ==
            // m_max; the new buffer base + capacity become visible
            // immediately after the call. Crucially, __GrowByOne does
            // NOT bump m_num -- the count is still the OLD count.
            if (m_core.Num() >= m_core.Max())
            {
                m_core.__GrowByOne();
                // The buffer base may have moved. Publish the new base
                // to the collector with the existing element count so
                // the collector can scan the live elements at their
                // new addresses if a mark intervenes here.
                ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                          reinterpret_cast<void**>(m_core.GetData()),
                                          static_cast<::SIZE_T>(m_core.Num()));
            }

            // Compute the new slot's address (the tail slot, currently
            // uninitialized raw memory at index Num()).
            const ::int32 NewIndex = m_core.Num();
            void** Slot = reinterpret_cast<void**>(m_core.GetData() + NewIndex);

            // Pre-store barrier (fix C-4 pre-store ordering).
            ::XGC::XGC_WriteBarrier(Slot, static_cast<void*>(NewValue));

            // Raw store. T* is trivially-constructible; this is
            // equivalent to placement new(slot) T*(NewValue).
            *Slot = static_cast<void*>(NewValue);

            // Bump m_num without re-writing the slot.
            m_core.__BumpNumUninit();

            // Final span update publishing the new count to the
            // collector.
            ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                      reinterpret_cast<void**>(m_core.GetData()),
                                      static_cast<::SIZE_T>(m_core.Num()));

            return NewIndex;
        }

        // -------------------------------------------------------------
        // Emplace -- move-add. Identical semantics to Add for T*
        // (pointers are trivially-movable); kept for surface symmetry.
        // -------------------------------------------------------------
        ::int32 Emplace(T* NewValue) noexcept
        {
            return Add(NewValue);
        }

        // -------------------------------------------------------------
        // RemoveAt -- delegate to the inner primitive; update the span
        // post-removal.
        //
        // TODO(Phase 1d, after XCore-4b card-table lands): the inner
        // memmove-based shift moves pointers between slots without
        // invoking XGC_WriteBarrier per slot. The Phase 1c no-op
        // barrier stub means this is observationally fine today; once
        // the real card-table-marking barrier ships in XCore-4b, the
        // contract demands a "barrier-on-range" notification covering
        // the [Index, OldNum-2] swept range. The cleanest path is to
        // add a XGC_WriteBarrierRange(void** RangeStart, size_t RangeLen)
        // extern "C" hook to the XGC ABI; for Phase 1c this remains a
        // known and documented gap.
        // -------------------------------------------------------------
        void RemoveAt(::int32 Index) noexcept
        {
            m_core.RemoveAt(Index);
            ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                      reinterpret_cast<void**>(m_core.GetData()),
                                      static_cast<::SIZE_T>(m_core.Num()));
        }

        // -------------------------------------------------------------
        // RemoveAtSwap -- delegate; update span post-removal.
        //
        // The single-slot move (last->Index) is one pointer overwrite.
        // Same TODO as RemoveAt above for the barrier-on-range hook
        // (here a single-slot WriteBarrier on &m_core[Index] would
        // suffice, but the inner TArrayCore::RemoveAtSwap currently
        // does the move without barrier hooks).
        // -------------------------------------------------------------
        void RemoveAtSwap(::int32 Index) noexcept
        {
            m_core.RemoveAtSwap(Index);
            ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                      reinterpret_cast<void**>(m_core.GetData()),
                                      static_cast<::SIZE_T>(m_core.Num()));
        }

        // -------------------------------------------------------------
        // Reserve -- delegate; update span post-grow.
        // -------------------------------------------------------------
        void Reserve(::int32 NewMax)
        {
            m_core.Reserve(NewMax);
            ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                      reinterpret_cast<void**>(m_core.GetData()),
                                      static_cast<::SIZE_T>(m_core.Num()));
        }

        // -------------------------------------------------------------
        // Reset -- delegate; update span post-clear.
        // -------------------------------------------------------------
        void Reset(::int32 NewCapacityHint = 0) noexcept
        {
            m_core.Reset(NewCapacityHint);
            ::XGC::XGC_UpdateRootSpan(&m_rootSpan,
                                      reinterpret_cast<void**>(m_core.GetData()),
                                      static_cast<::SIZE_T>(m_core.Num()));
        }

        // =================================================================
        // Iteration. Returns raw T** pointers (typed); the collector
        // walks via the registered span, not via these iterators.
        // =================================================================

        [[nodiscard]] XPACT_FORCEINLINE T** begin() noexcept
        {
            return reinterpret_cast<T**>(m_core.begin());
        }

        [[nodiscard]] XPACT_FORCEINLINE T** end() noexcept
        {
            return reinterpret_cast<T**>(m_core.end());
        }

        [[nodiscard]] XPACT_FORCEINLINE T* const* begin() const noexcept
        {
            return reinterpret_cast<T* const*>(m_core.begin());
        }

        [[nodiscard]] XPACT_FORCEINLINE T* const* end() const noexcept
        {
            return reinterpret_cast<T* const*>(m_core.end());
        }

    private:
        // The inner non-GC primitive holds the buffer + count + cap +
        // allocator. We delegate every buffer operation to it; the
        // GC-aware specialization layers the root-span protocol on top.
        //
        // The inner primitive is instantiated against T* (the pointer
        // type), so its internal storage layout is "array of T*" which
        // matches the collector's stride contract (sizeof(void*) per
        // slot).
        ::XCore::Detail::TArrayCore<T*, AllocatorT> m_core;

        // The embedded root span. Layout-locked to 32 bytes per fix
        // M-12; the X_DECLARE_GC_AWARE_CONTAINER macro asserts this.
        ::XGC::XGCRootSpan m_rootSpan;

    public:
        // Hot-reload-frozen sentinel + 32-byte layout assert (Section
        // 13.1 X_DECLARE_GC_AWARE_CONTAINER). XLiveCoding's patch-
        // validation pass needs this readable from outside the class
        // (the sentinel is checked at patch time without instantiating
        // the container). The X_DECLARE_GC_AWARE_CONTAINER macro
        // produces a static constexpr bool + static_assert; both are
        // type-level constructs that benefit from public visibility
        // for diagnostic introspection.
        X_DECLARE_GC_AWARE_CONTAINER(TArray);
    };

} // namespace XCore
