// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.cpp -- the process-singleton global object table
// (XCoreXObject Rev 4 §3.3). Phase 5.b body.
// =====================================================================

#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

#include "HAL/FMemory.h"
#include "HAL/FPlatformMemory.h"
#include "HAL/FPlatformMisc.h"
#include "Macros/XAssertionMacros.h"

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <new>

namespace XCore
{

    // =================================================================
    // Singleton.
    //
    // The function-local-static initialiser runs at first call. The
    // ctor reserves the full 256 MB VM range and commits the first
    // 1 MB chunk; failure aborts.
    // =================================================================
    FXObjectArray& FXObjectArray::Get() noexcept
    {
        static FXObjectArray s_instance;
        return s_instance;
    }

    // =================================================================
    // ctor / dtor.
    // =================================================================
    FXObjectArray::FXObjectArray() noexcept
        : m_entries(nullptr)
        , m_committedCount(0)
        , m_bumpHead(0)
        , m_numLive(0)
        , m_freeList(nullptr)
        , m_freeListCount(0)
        , m_freeListCapacity(0)
        , m_lock()
    {
        // ----- Step 1: reserve the VM range. -----
        //
        // 256 MB VM reservation is essentially free on 64-bit OSes
        // (page-table-entries only). Backing physical RAM materialises
        // only on commit.
        void* ReservedBase = ::XCore::HAL::FPlatformMemory::ReserveVirtual(
            kFXObjectArrayReservedBytes);
        if (ReservedBase == nullptr)
        {
            // Cannot recover; the engine cannot start without the
            // global object table. Abort with a diagnostic.
            ::XCore::HAL::AbortWithMessage(
                "FXObjectArray: failed to reserve 256 MB VM range. "
                "Cannot bootstrap the XObject heap.",
                __FILE__, __LINE__);
        }

        m_entries = reinterpret_cast<FXObjectArrayEntry*>(ReservedBase);

        // ----- Step 2: commit the first 1 MB chunk. -----
        //
        // 32 768 entries. Enough for the engine bootstrap + the
        // earliest XObject allocations (CDOs, package singletons).
        const bool bCommitOk = ::XCore::HAL::FPlatformMemory::CommitVirtual(
            ReservedBase, kFXObjectArrayCommitBytes);
        if (!bCommitOk)
        {
            ::XCore::HAL::AbortWithMessage(
                "FXObjectArray: failed to commit first 1 MB chunk of the "
                "256 MB reserved VM range. Cannot bootstrap the XObject "
                "heap.",
                __FILE__, __LINE__);
        }

        // ----- Step 3: zero-initialise the committed chunk. -----
        //
        // The OS guarantees zero-fill on commit (Win32 VirtualAlloc +
        // Linux MAP_ANONYMOUS both zero pages), so this is technically
        // redundant. Belt-and-braces explicit Memzero documents the
        // invariant + ensures the placement-new'd FXObjectArrayEntry
        // values are observed zero by every reader regardless of
        // platform.
        ::XCore::HAL::FPlatformMemory::Memzero(
            ReservedBase, kFXObjectArrayCommitBytes);

        // Construct each entry via placement-new so the std::atomic
        // members are in a well-defined state. The Memzero above
        // already zero-bits them which IS the same value-representation
        // as the constexpr default ctor, but placement-new is the
        // strictly-defined entry into the object lifetime.
        for (::int32 Index = 0; Index < kFXObjectArrayEntriesPerCommit; ++Index)
        {
            ::new (&m_entries[Index]) FXObjectArrayEntry();
        }

        // ----- Step 4: reserve index 0 as the null sentinel. -----
        //
        // The slot's Object is already nullptr (zero-fill) and the
        // SerialNumber is 0. We bump m_bumpHead past index 0 so the
        // first AllocateEntry returns index 1 or higher; FreeEntry on
        // index 0 is rejected by the bounds check.
        m_bumpHead = 1;

        m_committedCount.store(
            kFXObjectArrayEntriesPerCommit,
            ::std::memory_order_release);

        // m_numLive stays 0; the null sentinel is NOT counted as a
        // live entry.
    }

    FXObjectArray::~FXObjectArray() noexcept
    {
        if (m_freeList != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_freeList);
            m_freeList         = nullptr;
            m_freeListCount    = 0;
            m_freeListCapacity = 0;
        }

        if (m_entries != nullptr)
        {
            // Decommit + release the VM range. The OS reclaims the
            // physical RAM (decommit) AND the virtual address space
            // (release).
            ::XCore::HAL::FPlatformMemory::ReleaseVirtual(
                m_entries, kFXObjectArrayReservedBytes);
            m_entries = nullptr;
        }

        m_bumpHead = 0;
        m_committedCount.store(0, ::std::memory_order_release);
        m_numLive.store(0, ::std::memory_order_release);
    }

    // =================================================================
    // __ResetForTests -- destructive reset to bootstrap state.
    //
    // The test harness uses this between tests to start each test
    // case with a clean array. Per the header docstring, NOT for
    // production callers.
    // =================================================================
    void FXObjectArray::__ResetForTests() noexcept
    {
        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        // Reset every entry in the currently-committed region. Walk
        // explicitly rather than memzero so the std::atomic stores go
        // through the documented release-store path (memzero on an
        // atomic field is technically UB on platforms with lock-byte-
        // backed atomics, even though all our supported platforms
        // have lock-free 64-bit atomics).
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        for (::int32 Index = 0; Index < LocalCapacity; ++Index)
        {
            m_entries[Index].Object           = nullptr;
            m_entries[Index].SerialNumber     = 0;
            m_entries[Index].ClusterRootIndex = 0;
            m_entries[Index].StateBits.store(0, ::std::memory_order_release);
            m_entries[Index]._reserved        = 0;
        }

        m_bumpHead = 1;  // re-reserve index 0 as null sentinel
        m_numLive.store(0, ::std::memory_order_release);

        m_freeListCount = 0;
        // Leave m_freeList capacity allocated for reuse.
    }

    // =================================================================
    // CommitNextChunkUnderLock -- grow the committed region by one
    // 1 MB chunk.
    //
    // Pre-condition: m_lock held exclusively.
    // Aborts if the next chunk would exceed kFXObjectArrayMaxEntries
    // (the 256 MB reservation cap; reached only at 8M+ XObjects).
    // =================================================================
    void FXObjectArray::CommitNextChunkUnderLock() noexcept
    {
        const ::int32 OldCapacity = m_committedCount.load(::std::memory_order_relaxed);
        if (OldCapacity >= kFXObjectArrayMaxEntries)
        {
            ::XCore::HAL::AbortWithMessage(
                "FXObjectArray: live XObject count reached the 256 MB "
                "reservation cap. Grow the cap via "
                "kFXObjectArrayReservedBytes if this is genuine; "
                "otherwise audit XObject lifetime / leak tracker.",
                __FILE__, __LINE__);
        }

        const ::SIZE_T OffsetBytes =
            static_cast<::SIZE_T>(OldCapacity) * sizeof(FXObjectArrayEntry);
        void* CommitBase =
            reinterpret_cast<char*>(m_entries) + OffsetBytes;

        const bool bCommitOk = ::XCore::HAL::FPlatformMemory::CommitVirtual(
            CommitBase, kFXObjectArrayCommitBytes);
        if (!bCommitOk)
        {
            ::XCore::HAL::AbortWithMessage(
                "FXObjectArray: failed to commit additional 1 MB chunk "
                "during grow. AvailableVirtual exhausted?",
                __FILE__, __LINE__);
        }

        // Zero-fill + placement-new every entry in the newly-committed
        // chunk. The OS already zero-filled the pages (Win32
        // VirtualAlloc/MEM_COMMIT + Linux mprotect-PROT_READ|WRITE
        // both observe zero-filled pages on first access), so the
        // explicit Memzero is belt-and-braces but the placement-new
        // is required to enter the object lifetime.
        ::XCore::HAL::FPlatformMemory::Memzero(
            CommitBase, kFXObjectArrayCommitBytes);
        FXObjectArrayEntry* const NewBase =
            reinterpret_cast<FXObjectArrayEntry*>(CommitBase);
        for (::int32 i = 0; i < kFXObjectArrayEntriesPerCommit; ++i)
        {
            ::new (&NewBase[i]) FXObjectArrayEntry();
        }

        // Release-store the new capacity so SHARED readers using
        // `Capacity()` see the updated bound after the entries are
        // valid.
        m_committedCount.store(
            OldCapacity + kFXObjectArrayEntriesPerCommit,
            ::std::memory_order_release);
    }

    // =================================================================
    // GrowFreeListUnderLock -- double the free-list capacity.
    //
    // Pre-condition: m_lock held exclusively.
    // =================================================================
    void FXObjectArray::GrowFreeListUnderLock() noexcept
    {
        const ::int32 NewCapacity =
            (m_freeListCapacity == 0) ? 64 : (m_freeListCapacity * 2);
        const ::SIZE_T NewSizeBytes =
            static_cast<::SIZE_T>(NewCapacity) * sizeof(::int32);
        const ::SIZE_T OldSizeBytes =
            static_cast<::SIZE_T>(m_freeListCapacity) * sizeof(::int32);

        ::int32* NewBuf = static_cast<::int32*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                NewSizeBytes,
                /*Align=*/ alignof(::int32),
                ::XCore::HAL::FMemTag::XObject));

        if (m_freeList != nullptr)
        {
            ::XCore::HAL::FPlatformMemory::Memcpy(
                NewBuf, m_freeList, OldSizeBytes);
            ::XCore::HAL::FMemory::Free(m_freeList);
        }

        m_freeList         = NewBuf;
        m_freeListCapacity = NewCapacity;
    }

    // =================================================================
    // ReserveSlot -- step 1 of the two-step NewObject hot path.
    //
    // Returns the assigned int32 InternalIndex AND captures the
    // SerialNumber via OutSerialNumber. The caller fills the XObject
    // header (in particular XObject::SerialNumber) to match this value
    // BEFORE calling BindObject so the entry is consistent at every
    // observable moment from the GC's perspective.
    // =================================================================
    ::int32 FXObjectArray::ReserveSlot(::uint32* OutSerialNumber) noexcept
    {
        XPACT_CHECK(OutSerialNumber != nullptr);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        ::int32 Index;
        if (m_freeListCount > 0)
        {
            // Pop the most recent freed slot (LIFO; cache-warm).
            --m_freeListCount;
            Index = m_freeList[m_freeListCount];
            // SerialNumber was already bumped at FreeEntry time; we
            // do NOT bump it again on reuse (the bump-at-free ensures
            // every weak-handle captured BEFORE the free returns nullptr
            // on its next deref; bumping again at reuse would only
            // change the cardinality of the bumps, not the
            // correctness).
            *OutSerialNumber = m_entries[Index].SerialNumber;
        }
        else
        {
            // Consume the next bump-allocated slot. Grow the committed
            // region if we hit the boundary.
            Index = m_bumpHead;
            const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_relaxed);
            if (Index >= LocalCapacity)
            {
                CommitNextChunkUnderLock();
            }
            ++m_bumpHead;

            // Fresh slot. SerialNumber starts at 1 (NOT 0; the
            // null-sentinel SerialNumber is 0, so any real slot
            // starting at 1 lets the consumer distinguish "this is
            // index 0 / null" from "this is a freshly-allocated real
            // slot").
            m_entries[Index].SerialNumber = 1;
            *OutSerialNumber              = 1;
        }

        // Object stays nullptr until BindObject runs. The GC sweep
        // tolerates this: a slot with Object == nullptr is treated as
        // free + non-marked.
        m_entries[Index].Object = nullptr;

        return Index;
    }

    // =================================================================
    // BindObject -- step 2 of the two-step NewObject hot path.
    // =================================================================
    void FXObjectArray::BindObject(::int32 InternalIndex, XObject* Object) noexcept
    {
        XPACT_CHECK(InternalIndex > 0);  // 0 is the null sentinel
        XPACT_CHECK(Object        != nullptr);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        XPACT_CHECK(InternalIndex < m_committedCount.load(::std::memory_order_relaxed));
        // Bind. The slot's SerialNumber was set by ReserveSlot; the
        // XObject's SerialNumber should already match (caller's
        // responsibility).
        m_entries[InternalIndex].Object = Object;
        m_numLive.fetch_add(1, ::std::memory_order_acq_rel);
    }

    // =================================================================
    // AllocateEntry -- one-shot variant.
    //
    // Functionally equivalent to ReserveSlot + BindObject in one
    // lock-acquire; for callers that already have a fully-populated
    // XObject* and want a single-call API. ForObject MAY be nullptr
    // (mirrors ReserveSlot's no-bind semantics) -- the slot is
    // allocated without a back-pointer.
    // =================================================================
    ::int32 FXObjectArray::AllocateEntry(XObject* ForObject) noexcept
    {
        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        ::int32 Index;
        if (m_freeListCount > 0)
        {
            --m_freeListCount;
            Index = m_freeList[m_freeListCount];
        }
        else
        {
            Index = m_bumpHead;
            const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_relaxed);
            if (Index >= LocalCapacity)
            {
                CommitNextChunkUnderLock();
            }
            ++m_bumpHead;

            // Fresh slot starts at SerialNumber 1.
            m_entries[Index].SerialNumber = 1;
        }

        m_entries[Index].Object = ForObject;

        if (ForObject != nullptr)
        {
            m_numLive.fetch_add(1, ::std::memory_order_acq_rel);
        }

        return Index;
    }

    // =================================================================
    // FreeEntry -- mark a slot free; bump SerialNumber.
    //
    // The SerialNumber bump is the load-bearing weak-pointer
    // invalidation event: every XWeakPtr / XObjectKey captured BEFORE
    // the FreeEntry returns nullptr on its next deref.
    // =================================================================
    void FXObjectArray::FreeEntry(::int32 InternalIndex) noexcept
    {
        XPACT_CHECK(InternalIndex > 0);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        XPACT_CHECK(InternalIndex < m_committedCount.load(::std::memory_order_relaxed));

        // Bump SerialNumber. Wrap-around at 2^32 is acceptable per
        // spec §3.3 trailing prose (4 billion slot reuses is
        // unreachable within a process lifetime).
        const bool bWasLive = (m_entries[InternalIndex].Object != nullptr);
        ++m_entries[InternalIndex].SerialNumber;
        // SerialNumber 0 is reserved for the null sentinel; on wrap
        // skip past it.
        if (m_entries[InternalIndex].SerialNumber == 0)
        {
            m_entries[InternalIndex].SerialNumber = 1;
        }
        m_entries[InternalIndex].Object = nullptr;

        if (bWasLive)
        {
            m_numLive.fetch_sub(1, ::std::memory_order_acq_rel);
        }

        // Push onto the LIFO free list.
        if (m_freeListCount >= m_freeListCapacity)
        {
            GrowFreeListUnderLock();
        }
        m_freeList[m_freeListCount] = InternalIndex;
        ++m_freeListCount;
    }

    // =================================================================
    // GetObjectAtIndex -- weak-ptr deref entry point.
    // =================================================================
    XObject* FXObjectArray::GetObjectAtIndex(
        ::int32 InternalIndex,
        ::uint32 ExpectedSerialNumber) const noexcept
    {
        // The null sentinel ALWAYS resolves to nullptr.
        if (InternalIndex <= 0)
        {
            return nullptr;
        }

        ::XCore::HAL::FScopedReadLock ReadLock(m_lock);

        const ::int32 LocalCapacity =
            m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return nullptr;
        }

        // Serial check. Mismatch = the slot was reused since the
        // weak handle was captured.
        if (m_entries[InternalIndex].SerialNumber != ExpectedSerialNumber)
        {
            return nullptr;
        }

        // The Object pointer may still be nullptr in the brief window
        // between ReserveSlot and BindObject; return as observed.
        return m_entries[InternalIndex].Object;
    }

    // =================================================================
    // GetObjectAtIndexUnchecked -- known-live fast path.
    //
    // No lock; the GC mark sweep runs under its own safe-point
    // protocol per spec §5.5 which guarantees no concurrent
    // FreeEntry / grow during the sweep.
    // =================================================================
    XObject* FXObjectArray::GetObjectAtIndexUnchecked(
        ::int32 InternalIndex) const noexcept
    {
        // Even the unchecked path honours the null sentinel + range
        // check via XPACT_CHECK in Debug/Dev (zero-cost in Shipping).
        XPACT_CHECK(InternalIndex > 0);
        XPACT_CHECK(InternalIndex < m_committedCount.load(::std::memory_order_acquire));
        return m_entries[InternalIndex].Object;
    }

    // =================================================================
    // AddRef -- XStrongPtr-side refcount increment (XCoreXObject Rev 4
    // §3.3 + §6.5; Phase 5.c).
    //
    // CAS-loop atomic increment of the refcount sub-field in
    // FXObjectArrayEntry::StateBits (bits 32..55). The CAS preserves
    // every other bit in the 64-bit word (the GC-flag bits in the low
    // half, the future remote-handle bits in the high byte) -- a
    // straight fetch_add on the whole word would also work, but the
    // CAS loop lets us guard against the 24-bit overflow case cleanly.
    //
    // No-op for the null sentinel (InternalIndex == 0). The Dev / Debug
    // range check fires for genuinely-out-of-range indices.
    // =================================================================
    void FXObjectArray::AddRef(::int32 InternalIndex) noexcept
    {
        // Null sentinel: no-op. XStrongPtr<T>(nullptr) short-circuits
        // before reaching here; this is defence-in-depth for any
        // caller that arrives with index 0 (e.g., a freshly-zeroed
        // XStrongPtr being destroyed).
        if (InternalIndex == 0)
        {
            return;
        }

        XPACT_CHECK(InternalIndex > 0);
        XPACT_CHECK(InternalIndex < m_committedCount.load(::std::memory_order_acquire));

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;

        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            const ::std::uint64_t CurrentCount =
                (Old & kFXObjectArrayRefCountMask) >> kFXObjectArrayRefCountShift;

            // Overflow guard (Dev/Debug). The 24-bit ceiling is
            // 16 777 215; realistic workloads stay below 1k. Aborting
            // here surfaces a real bug at the call site (XStrongPtr
            // copy-cascade gone wrong, async I/O queue not draining,
            // etc.). In Shipping the XPACT_CHECK is compiled out and
            // the CAS loop saturates at the max.
            XPACT_CHECK(CurrentCount < kFXObjectArrayRefCountMax);

            if (CurrentCount >= kFXObjectArrayRefCountMax)
            {
                // Shipping-mode saturation: refuse to wrap. The
                // resulting underflow-on-ReleaseRef would be a far
                // worse failure mode (free-while-referenced; UAF).
                return;
            }

            const ::std::uint64_t New = Old + kFXObjectArrayRefCountUnit;

            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return;
            }
            // CAS failed; Old has been re-loaded with the freshest
            // value; retry. Spurious-failure-safe.
        }
    }

    // =================================================================
    // ReleaseRef -- XStrongPtr-side refcount decrement.
    //
    // CAS-loop atomic decrement. The mirror of AddRef. Underflow is a
    // bug (unbalanced ReleaseRef); aborts in Dev / Debug, saturates at
    // zero in Shipping.
    // =================================================================
    void FXObjectArray::ReleaseRef(::int32 InternalIndex) noexcept
    {
        if (InternalIndex == 0)
        {
            return;
        }

        XPACT_CHECK(InternalIndex > 0);
        XPACT_CHECK(InternalIndex < m_committedCount.load(::std::memory_order_acquire));

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;

        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            const ::std::uint64_t CurrentCount =
                (Old & kFXObjectArrayRefCountMask) >> kFXObjectArrayRefCountShift;

            // Underflow guard. ReleaseRef when refcount == 0 indicates
            // unbalanced ReleaseRef / AddRef (a real bug at the call
            // site). The XPACT_CHECK fires in Dev / Debug.
            XPACT_CHECK(CurrentCount > 0);

            if (CurrentCount == 0)
            {
                // Shipping-mode saturation: refuse to wrap to
                // 16 777 215.
                return;
            }

            const ::std::uint64_t New = Old - kFXObjectArrayRefCountUnit;

            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return;
            }
        }
    }

    // =================================================================
    // GetRefCount -- diagnostic / test read of the refcount sub-field.
    //
    // Atomic load + bit extract. No lock. The result is a snapshot
    // valid at the load moment; production code should NOT depend on
    // the value being stable past the call.
    // =================================================================
    ::uint32 FXObjectArray::GetRefCount(::int32 InternalIndex) const noexcept
    {
        if (InternalIndex <= 0)
        {
            return 0;
        }
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return 0;
        }
        const ::std::uint64_t State =
            m_entries[InternalIndex].StateBits.load(::std::memory_order_acquire);
        return static_cast<::uint32>(
            (State & kFXObjectArrayRefCountMask) >> kFXObjectArrayRefCountShift);
    }

} // namespace XCore
