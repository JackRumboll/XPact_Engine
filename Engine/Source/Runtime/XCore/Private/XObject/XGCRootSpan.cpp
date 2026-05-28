// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRootSpan.cpp -- XGCRootSpanRegistry body (Phase 5.e).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5.3. Phase 5.e ships the process-singleton
// root-span registry + the per-Conservative-span four-gate validator
// dispatch internal helper.
//
// IMPLEMENTATION CHOICES:
//
//   * Dense slot table (handle == index). Slots are NEVER relocated
//     so a held handle remains valid for the span's registration
//     lifetime. Capacity doubles via FMemory::Realloc when the bump
//     head reaches m_slotCapacity (rare; typical projects stay under
//     ~500 spans per spec §4.8).
//
//   * Free-list LIFO for slot reuse: RemoveSpan pushes the freed
//     index onto the chain so the next AddSpan reuses it. The chain
//     head + per-slot next pointer are stored in a parallel
//     m_freeListNext array to keep FSlot trivially-copyable (Phase
//     5.e ABI lock).
//
//   * Atomic m_activeCount for lock-free GetSpanCount. Bumped on
//     AddSpan; decremented on RemoveSpan. memory_order_acq_rel ensures
//     the visible count is consistent with the slot Active bit.
//
//   * Iteration acquires SHARED lock. The ValidateConservative-
//     CandidateInternal dispatcher acquires its own SHARED locks on
//     FXObjectAllocator + FXObjectArray (both recursive-acquirable
//     so the nesting is safe).
//
// =====================================================================

#include "XObject/XGCRootSpan.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <new>

namespace XCore
{

    // =================================================================
    // Singleton accessor.
    //
    // Magic-static (C++11 guarantees thread-safe initialisation of
    // function-local statics). The ctor pre-allocates the slot table
    // at kXGCRootSpanInitialCapacity entries; if FMemory::Malloc
    // fails the standard FMemory abort path fires.
    // =================================================================
    XGCRootSpanRegistry& XGCRootSpanRegistry::Get() noexcept
    {
        static XGCRootSpanRegistry s_instance;
        return s_instance;
    }

    // =================================================================
    // Ctor / Dtor.
    //
    // Pre-allocate the slot table + per-slot free-list pointer array.
    // The slot table is TArray-like but hand-rolled because Phase 5.e
    // does not pull Containers/TArray.h into the XGCRootSpan header
    // (would force every container that registers a span to include
    // TArray; chicken-and-egg).
    // =================================================================
    XGCRootSpanRegistry::XGCRootSpanRegistry() noexcept
        : m_slots(nullptr)
        , m_freeListNext(nullptr)
        , m_slotCapacity(0)
        , m_bumpHead(0)
        , m_freeListHead(-1)
        , m_activeCount(0)
    {
        // Pre-allocate the initial slot table. Failures abort via the
        // standard FMemory::MallocOrAbort path.
        m_slotCapacity = kXGCRootSpanInitialCapacity;

        // FSlot array.
        m_slots = static_cast<FSlot*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(m_slotCapacity) * sizeof(FSlot),
                alignof(FSlot),
                ::XCore::HAL::FMemTag::Reflection));

        // m_freeListNext array (parallel; per-slot next-free-handle
        // chain).
        m_freeListNext = static_cast<::int32*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(m_slotCapacity) * sizeof(::int32),
                alignof(::int32),
                ::XCore::HAL::FMemTag::Reflection));

        // Zero-init both arrays. FSlot's Span field is zero-default-
        // constructible (constexpr ctor); Active is false; the free-
        // list chain pointers are unused at this point.
        for (::int32 I = 0; I < m_slotCapacity; ++I)
        {
            // Placement-new the FSlot to invoke the XGCRootSpan default
            // ctor (zeroes the Span; sets Active=false).
            new (&m_slots[I]) FSlot{ XGCRootSpan{}, false };
            m_freeListNext[I] = -1;
        }
    }

    XGCRootSpanRegistry::~XGCRootSpanRegistry() noexcept
    {
        if (m_slots != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_slots);
            m_slots = nullptr;
        }
        if (m_freeListNext != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_freeListNext);
            m_freeListNext = nullptr;
        }
    }

    // =================================================================
    // __ResetForTests.
    //
    // Phase 5.e test-suite helper. Drops every active span; resets
    // counters + free-list to the freshly-constructed state. ONLY
    // used by tests; production callers MUST NOT call this.
    // =================================================================
    void XGCRootSpanRegistry::__ResetForTests() noexcept
    {
        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);
        for (::int32 I = 0; I < m_bumpHead; ++I)
        {
            m_slots[I].Active = false;
            m_slots[I].Span   = XGCRootSpan{};
            m_freeListNext[I] = -1;
        }
        m_bumpHead     = 0;
        m_freeListHead = -1;
        m_activeCount.store(0, ::std::memory_order_release);
    }

    // =================================================================
    // AddSpan -- register a span.
    //
    // EXCLUSIVE lock acquired. Validates the span shape (ElementStride
    // == 8; ByteLength % ElementStride == 0; nullptr Base only with
    // ByteLength == 0); rejects malformed spans with the sentinel
    // invalid handle.
    //
    // Slot reuse: if the free list is non-empty, pop the head + reuse
    // the slot. Otherwise bump-allocate from m_bumpHead; if the bump
    // head reaches m_slotCapacity, grow the table.
    // =================================================================
    ::int32 XGCRootSpanRegistry::AddSpan(const XGCRootSpan& Span) noexcept
    {
        // Validate shape BEFORE acquiring the lock (no shared state
        // mutated; pure validation).
        if (Span.ElementStride != sizeof(XObject*))
        {
            // Phase 5.e enforces 8-byte stride. Non-8 strides are
            // reserved for future use; reject with the sentinel.
            return kXGCRootSpanInvalidHandle;
        }
        if (Span.ByteLength != 0 && Span.BaseAddress == nullptr)
        {
            // Non-zero length with nullptr base is malformed; reject.
            return kXGCRootSpanInvalidHandle;
        }
        if (Span.ByteLength % Span.ElementStride != 0)
        {
            // Slot count must be exact (no partial slots at the end).
            return kXGCRootSpanInvalidHandle;
        }

        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        // Slot reuse via free-list.
        ::int32 Handle;
        if (m_freeListHead != -1)
        {
            Handle = m_freeListHead;
            m_freeListHead = m_freeListNext[Handle];
            m_freeListNext[Handle] = -1;
        }
        else
        {
            // Bump-allocate. Grow the table if needed.
            if (m_bumpHead >= m_slotCapacity)
            {
                EnsureCapacityUnderLock();
            }
            Handle = m_bumpHead++;
        }

        m_slots[Handle].Span   = Span;
        m_slots[Handle].Active = true;

        m_activeCount.fetch_add(1, ::std::memory_order_acq_rel);
        return Handle;
    }

    // =================================================================
    // RemoveSpan -- unregister a span.
    //
    // EXCLUSIVE lock acquired. Idempotent on an already-removed
    // handle (no-op). Pushes the slot index onto the free-list LIFO
    // so the next AddSpan reuses it.
    // =================================================================
    void XGCRootSpanRegistry::RemoveSpan(::int32 Handle) noexcept
    {
        if (Handle < 0)
        {
            return;
        }

        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        if (Handle >= m_bumpHead)
        {
            // Invalid handle (never registered).
            return;
        }

        FSlot& Slot = m_slots[Handle];
        if (!Slot.Active)
        {
            // Already removed. Idempotent no-op.
            return;
        }

        Slot.Active = false;
        Slot.Span   = XGCRootSpan{};

        // Push onto the free-list LIFO.
        m_freeListNext[Handle] = m_freeListHead;
        m_freeListHead = Handle;

        m_activeCount.fetch_sub(1, ::std::memory_order_acq_rel);
    }

    // =================================================================
    // GetSpanCount -- atomic snapshot.
    // =================================================================
    ::std::size_t XGCRootSpanRegistry::GetSpanCount() const noexcept
    {
        const ::int32 N = m_activeCount.load(::std::memory_order_acquire);
        return (N < 0) ? 0u : static_cast<::std::size_t>(N);
    }

    // =================================================================
    // GetConservativeSpanCount -- SHARED-lock scan.
    //
    // Walks the active slots counting Kind == kConservative entries.
    // Phase 5.e ships the scan baseline; a future revision may add a
    // dedicated atomic counter bumped on AddSpan when Kind ==
    // kConservative to avoid the scan.
    // =================================================================
    ::std::size_t XGCRootSpanRegistry::GetConservativeSpanCount() const noexcept
    {
        ::XCore::HAL::FScopedReadLock ReadLock(m_lock);
        ::std::size_t Count = 0;
        for (::int32 I = 0; I < m_bumpHead; ++I)
        {
            const FSlot& Slot = m_slots[I];
            if (Slot.Active && Slot.Span.Kind == EXGCRootSpanKind::kConservative)
            {
                ++Count;
            }
        }
        return Count;
    }

    // =================================================================
    // EnsureCapacityUnderLock -- double the slot table.
    //
    // Pre-condition: caller holds m_lock EXCLUSIVE.
    //
    // The slot table is realloced (no in-place reuse; the FMemory
    // realloc path moves the data into a fresh allocation if the
    // current block cannot grow in place). Free-list pointers are
    // index-based (not address-based) so the realloc does not
    // invalidate them.
    //
    // The new (post-grow) slots are zero-initialised. The bump head +
    // free-list-head state are unchanged.
    // =================================================================
    void XGCRootSpanRegistry::EnsureCapacityUnderLock() noexcept
    {
        const ::int32 OldCapacity = m_slotCapacity;
        const ::int32 NewCapacity = (OldCapacity == 0) ? kXGCRootSpanInitialCapacity
                                                       : (OldCapacity * 2);

        // Allocate fresh blocks; copy the old data; free the old
        // blocks. FMemory does not have a Realloc primitive for the
        // size-class-specific tagged path; we hand-roll a malloc /
        // copy / free.
        FSlot* NewSlots = static_cast<FSlot*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(NewCapacity) * sizeof(FSlot),
                alignof(FSlot),
                ::XCore::HAL::FMemTag::Reflection));
        ::int32* NewFreeListNext = static_cast<::int32*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(NewCapacity) * sizeof(::int32),
                alignof(::int32),
                ::XCore::HAL::FMemTag::Reflection));

        // Copy old slots; placement-new the unused tail.
        for (::int32 I = 0; I < OldCapacity; ++I)
        {
            new (&NewSlots[I]) FSlot{ m_slots[I].Span, m_slots[I].Active };
            NewFreeListNext[I] = m_freeListNext[I];
        }
        for (::int32 I = OldCapacity; I < NewCapacity; ++I)
        {
            new (&NewSlots[I]) FSlot{ XGCRootSpan{}, false };
            NewFreeListNext[I] = -1;
        }

        if (m_slots != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_slots);
        }
        if (m_freeListNext != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_freeListNext);
        }
        m_slots         = NewSlots;
        m_freeListNext  = NewFreeListNext;
        m_slotCapacity  = NewCapacity;
    }

    // =================================================================
    // ValidateConservativeCandidateInternal -- four-gate validator.
    //
    // Spec §5.3 invariant: NEVER deref Candidate before gate 1 passes.
    // Phase 5.e implementation:
    //
    //   1. Heap-range: FXObjectAllocator::IsHeapAddress(Candidate).
    //      If false: return nullptr.
    //
    //   2. Index-range: read Candidate->InternalIndex (now safe; gate 1
    //      proved the address is in heap range). Verify
    //      [1, FXObjectArray::Capacity()).
    //      If out-of-range: return nullptr.
    //
    //   3. Entry-bind: read FXObjectArray's entry at InternalIndex;
    //      verify Entry.Object == Candidate.
    //      If mismatch: return nullptr.
    //
    //   4. Serial match: read Candidate->SerialNumber; verify
    //      Entry.SerialNumber == Candidate->SerialNumber.
    //      If mismatch: return nullptr.
    //
    // All four pass: return Candidate (cast to XObject*).
    //
    // PERFORMANCE: see header docstring; ~13-21 cycles typical.
    //
    // THREAD SAFETY: gate 1's IsHeapAddress acquires SHARED lock on
    // FXObjectAllocator; gates 2-4 use FXObjectArray's lock-free
    // capacity load + the per-entry atomic-load surface. Race-safe
    // against concurrent allocate / free / serial-bump.
    //
    // PUBLIC API: this method is the implementation of
    // ::XCore::ValidateConservativeCandidate (the public free
    // function at XGCConservativeValidate.h). Phase 5.e routes the
    // public API through this private member so the registry's
    // per-Conservative-span scan path can call it without an extra
    // indirection.
    // =================================================================
    XObject* XGCRootSpanRegistry::ValidateConservativeCandidateInternal(
        const void* Candidate) const noexcept
    {
        if (Candidate == nullptr)
        {
            return nullptr;
        }

        // -- Gate 1: heap-range check --
        if (!FXObjectAllocator::Get().IsHeapAddress(Candidate))
        {
            return nullptr;
        }

        // Gate 1 passed: Candidate IS within an allocator-owned slab.
        // The XObject header layout (Phase 5.a; locked at Contract Rev
        // 13.9) guarantees ClassPrivate@0, InternalIndex@8,
        // SerialNumber@12. We can NOW read those fields without
        // page-fault risk; gate 2 reads InternalIndex.
        const XObject* PutativeXObject = static_cast<const XObject*>(Candidate);

        // -- Gate 2: FXObjectArray index-range check --
        const ::int32 InternalIndex = PutativeXObject->InternalIndex;
        FXObjectArray& Array = FXObjectArray::Get();
        const ::int32 Capacity = Array.Capacity();
        if (InternalIndex <= 0 || InternalIndex >= Capacity)
        {
            return nullptr;
        }

        // -- Gate 3: entry-bind check --
        // GetObjectAtIndexUnchecked is the lock-free read of
        // Entries[InternalIndex].Object. The atomic happens-before
        // ordering against the Capacity load above is provided by the
        // m_committedCount release-store at the end of
        // CommitNextChunkUnderLock (the array's grow path); we read
        // Capacity with acquire and the entry pointer is stable per
        // the never-relocate invariant.
        XObject* BoundObject = Array.GetObjectAtIndexUnchecked(InternalIndex);
        if (BoundObject == nullptr)
        {
            return nullptr;
        }
        if (BoundObject != PutativeXObject)
        {
            // The entry binds to a different XObject than the
            // candidate. The candidate is either pointing into a
            // freed-then-reused cell (the entry now binds a
            // different XObject) or into a "look-alike" byte
            // sequence within an XObject's body that happens to start
            // with a plausible InternalIndex value.
            return nullptr;
        }

        // -- Gate 4: SerialNumber match check --
        const ::uint32 CandidateSerial = PutativeXObject->SerialNumber;
        // The entry's SerialNumber is in FXObjectArrayEntry.SerialNumber
        // (offset 8 per Phase 5.a). We read it via the public
        // FXObjectArrayEntry surface (the entry pointer is stable; the
        // atomic semantic is on the StateBits word, not on SerialNumber
        // itself -- but the serial is bumped atomically by FreeEntry
        // under the EXCLUSIVE lock, so a SHARED-protected read here
        // sees a consistent value).
        //
        // We don't have a lock-free getter for the entry's
        // SerialNumber; the simplest expression of the gate is to use
        // GetObjectAtIndex with the captured serial which returns
        // nullptr on mismatch:
        XObject* SerialChecked = Array.GetObjectAtIndex(InternalIndex, CandidateSerial);
        if (SerialChecked != PutativeXObject)
        {
            // Serial mismatch (slot was reused since the candidate's
            // SerialNumber was captured) OR GetObjectAtIndex's own
            // checks failed.
            return nullptr;
        }

        // All four gates passed; the candidate IS a live XObject.
        // Cast away const because the caller (the GC mark phase /
        // ValidateConservativeCandidate's public API consumer) needs
        // the mutable XObject* to push onto the gray queue.
        return const_cast<XObject*>(PutativeXObject);
    }

    // =================================================================
    // ValidateConservativeCandidate -- public free function
    // (implementation of the XGCConservativeValidate.h declaration).
    //
    // Routes through XGCRootSpanRegistry's private member so the
    // registry's per-Conservative-span scan path + the public API
    // share the same code.
    //
    // The registry must be initialised (the singleton's first-Get()
    // pre-allocates the slot table); the API works regardless of
    // whether any spans are registered.
    // =================================================================
    XObject* ValidateConservativeCandidate(const void* Candidate) noexcept
    {
        return XGCRootSpanRegistry::Get().ValidateConservativeCandidateInternal(Candidate);
    }

} // namespace XCore
