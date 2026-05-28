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

    // =================================================================
    // SetRootPin -- XGCRoot::AddRoot dispatch site (XCoreXObject Rev 4
    // §5.1 / §5.2; Phase 5.e).
    //
    // Atomic OR with kFXObjectArrayRootPinnedBit. The CAS loop is the
    // canonical bit-set pattern: load Old, mask the target bit, build
    // New, CAS. Returns true iff the bit transitioned from clear to set
    // on this call.
    //
    // INDEX 0 (NULL SENTINEL) + out-of-range indices are no-ops returning
    // false; defence-in-depth for callers that arrive with a freshly-
    // zeroed XObject or an unregistered InternalIndex.
    //
    // CONCURRENCY: lock-free CAS on the entry's StateBits word. Race-
    // safe against:
    //   * Concurrent ClearRootPin on the same entry (the bit ends in
    //     one consistent state per atomic ordering rules).
    //   * Concurrent AddRef / ReleaseRef on the same entry (these touch
    //     bits 32..55; the OR operation's read-modify-write CAS preserves
    //     both halves of the 64-bit word).
    //   * Concurrent SetRootPin on the same entry from another caller
    //     (the second caller observes the bit already set + returns
    //     false).
    // =================================================================
    bool FXObjectArray::SetRootPin(::int32 InternalIndex) noexcept
    {
        if (InternalIndex <= 0)
        {
            return false;
        }
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return false;
        }

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;
        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            // If the bit is already set, this is a no-op idempotent
            // call: return false (we did NOT transition; the caller
            // observes "already pinned").
            if ((Old & kFXObjectArrayRootPinnedBit) != 0u)
            {
                return false;
            }
            const ::std::uint64_t New = Old | kFXObjectArrayRootPinnedBit;
            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return true;
            }
            // CAS failed: Old has been re-loaded with the freshest
            // value; the loop re-checks the bit state. Spurious-
            // failure-safe.
        }
    }

    // =================================================================
    // ClearRootPin -- XGCRoot::RemoveRoot dispatch site (Phase 5.e).
    //
    // Atomic AND with ~kFXObjectArrayRootPinnedBit. Returns true iff
    // the bit transitioned from set to clear on this call. Mirror of
    // SetRootPin.
    // =================================================================
    bool FXObjectArray::ClearRootPin(::int32 InternalIndex) noexcept
    {
        if (InternalIndex <= 0)
        {
            return false;
        }
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return false;
        }

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;
        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            // Already clear: idempotent no-op; return false (we did
            // NOT transition).
            if ((Old & kFXObjectArrayRootPinnedBit) == 0u)
            {
                return false;
            }
            const ::std::uint64_t New = Old & ~kFXObjectArrayRootPinnedBit;
            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return true;
            }
        }
    }

    // =================================================================
    // IsRootPinned -- SHARED-lock-acquired read of the kRootPinnedBit
    // (Phase 5.e).
    //
    // The lock is required to make the index-range check + the atomic
    // load coherent with a concurrent grow. The Unchecked variant
    // below is the hot-path entry point for callers already under the
    // shared lock (e.g., the ForEachObject visitor body).
    // =================================================================
    bool FXObjectArray::IsRootPinned(::int32 InternalIndex) const noexcept
    {
        if (InternalIndex <= 0)
        {
            return false;
        }
        ::XCore::HAL::FScopedReadLock ReadLock(m_lock);
        if (InternalIndex >= m_committedCount.load(::std::memory_order_acquire))
        {
            return false;
        }
        const ::std::uint64_t State =
            m_entries[InternalIndex].StateBits.load(::std::memory_order_acquire);
        return (State & kFXObjectArrayRootPinnedBit) != 0u;
    }

    // =================================================================
    // IsRootPinnedUnchecked -- lock-free read (Phase 5.e).
    //
    // Pre-condition: the caller has established happens-before ordering
    // against the array's grow (typically by already holding m_lock in
    // SHARED mode -- this is the ForEachRoot visitor body's context).
    // UB on out-of-range index.
    //
    // The Unchecked variant is the hot-path entry point for GC mark
    // root iteration (per XGCRoot::ForEachRoot template body).
    // =================================================================
    bool FXObjectArray::IsRootPinnedUnchecked(::int32 InternalIndex) const noexcept
    {
        const ::std::uint64_t State =
            m_entries[InternalIndex].StateBits.load(::std::memory_order_acquire);
        return (State & kFXObjectArrayRootPinnedBit) != 0u;
    }

    // =================================================================
    // SetPendingDestroyBit -- atomic CAS set of kFXObjectArrayPendingDestroyBit
    // (XCoreXObject Rev 4 §4.2 step 6; Phase 5.h sweep consumer surface).
    //
    // Mirror of the kRootPinnedBit SetRootPin CAS shape. Returns true
    // iff the bit transitioned from clear to set on this call.
    // =================================================================
    bool FXObjectArray::SetPendingDestroyBit(::int32 InternalIndex) noexcept
    {
        if (InternalIndex <= 0)
        {
            return false;
        }
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return false;
        }

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;
        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            if ((Old & kFXObjectArrayPendingDestroyBit) != 0u)
            {
                return false;
            }
            const ::std::uint64_t New = Old | kFXObjectArrayPendingDestroyBit;
            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return true;
            }
        }
    }

    // =================================================================
    // ClearPendingDestroyBit -- atomic CAS clear of the bit.
    //
    // Returns true iff the bit transitioned from set to clear. Used by
    // ReleaseSlot when the deferred-destruction queue finalises and
    // the slot returns to the free list.
    // =================================================================
    bool FXObjectArray::ClearPendingDestroyBit(::int32 InternalIndex) noexcept
    {
        if (InternalIndex <= 0)
        {
            return false;
        }
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return false;
        }

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;
        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            if ((Old & kFXObjectArrayPendingDestroyBit) == 0u)
            {
                return false;
            }
            const ::std::uint64_t New = Old & ~kFXObjectArrayPendingDestroyBit;
            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return true;
            }
        }
    }

    // =================================================================
    // IsPendingDestroyUnchecked -- lock-free read of kPendingDestroyBit.
    //
    // Mirrors the IsRootPinnedUnchecked posture: caller has established
    // happens-before ordering against the array's grow (sweep runs post-
    // mark with the structure quiescent). UB on out-of-range index.
    // =================================================================
    bool FXObjectArray::IsPendingDestroyUnchecked(::int32 InternalIndex) const noexcept
    {
        const ::std::uint64_t State =
            m_entries[InternalIndex].StateBits.load(::std::memory_order_acquire);
        return (State & kFXObjectArrayPendingDestroyBit) != 0u;
    }

    // =================================================================
    // SetGarbageBit -- atomic CAS set of kFXObjectArrayGarbageBit.
    //
    // Mirrors the EObjectFlags::MarkedAsGarbage transition. Per FIX-A-
    // HIGH-19: the sweep's kEliminateGarbageRefs pass reads this bit
    // first (one-load probe) before consulting the XObject's
    // ObjectFlags, so the per-candidate scan stays in the FXObjectArrayEntry
    // cache line.
    //
    // Phase 5.h sets this bit at sweep enqueue time (the sweep is the
    // sole writer of this bit on the GC side; XObject::MarkAsGarbage
    // independently sets EObjectFlags::MarkedAsGarbage on the XObject
    // header, and the next sweep observes that flag and propagates
    // here).
    // =================================================================
    bool FXObjectArray::SetGarbageBit(::int32 InternalIndex) noexcept
    {
        if (InternalIndex <= 0)
        {
            return false;
        }
        const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
        if (InternalIndex >= LocalCapacity)
        {
            return false;
        }

        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;
        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            if ((Old & kFXObjectArrayGarbageBit) != 0u)
            {
                return false;
            }
            const ::std::uint64_t New = Old | kFXObjectArrayGarbageBit;
            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                return true;
            }
        }
    }

    // =================================================================
    // IsGarbageUnchecked -- lock-free read of kGarbageBit.
    // =================================================================
    bool FXObjectArray::IsGarbageUnchecked(::int32 InternalIndex) const noexcept
    {
        const ::std::uint64_t State =
            m_entries[InternalIndex].StateBits.load(::std::memory_order_acquire);
        return (State & kFXObjectArrayGarbageBit) != 0u;
    }

    // =================================================================
    // GetStateBits -- diagnostic / test snapshot of the full word.
    //
    // SHARED-lock-acquired (to coherently bound the index-range check
    // against a concurrent grow). Returns 0 for the null sentinel + for
    // out-of-range indices.
    // =================================================================
    ::std::uint64_t FXObjectArray::GetStateBits(::int32 InternalIndex) const noexcept
    {
        if (InternalIndex <= 0)
        {
            return 0u;
        }
        ::XCore::HAL::FScopedReadLock ReadLock(m_lock);
        if (InternalIndex >= m_committedCount.load(::std::memory_order_acquire))
        {
            return 0u;
        }
        return m_entries[InternalIndex].StateBits.load(::std::memory_order_acquire);
    }

    // =================================================================
    // ReleaseSlot -- Phase 5.h sweep-side slot return (XCoreXObject Rev
    // 4 §4.2 step 7).
    //
    // Called by the FXDeferredDestructionQueue's drain pass after
    // FinishDestroy has completed for the slot's prior bound object.
    // The slot is returned to the LIFO free list; SerialNumber is
    // bumped so any in-flight XWeakPtr deref returns nullptr.
    //
    // The body is structurally identical to FreeEntry (which has been
    // shipping since Phase 5.b) -- they perform the same operation. We
    // keep both names because:
    //
    //   * FreeEntry is the Phase 5.b synchronous-destroy entry point
    //     (legacy callers that don't route through the GC sweep,
    //     primarily the test harness).
    //
    //   * ReleaseSlot is the spec-canonical sweep-side entry point per
    //     §4.2 step 7 + §11.5. The Phase 5.h sweep + the deferred-
    //     destruction queue's drain pass call this rather than FreeEntry
    //     so audits + telemetry can distinguish "GC reclaimed this slot"
    //     from "synchronous destroy".
    //
    // The implementation forwards to the FreeEntry mechanic, additionally
    // clearing the PendingDestroy + Garbage bits (the entry-state bits
    // pertain to the prior bound object; once the slot is on the free
    // list those bits MUST be clear so the next AllocateEntry observes
    // a freshly-zero entry state). FreeEntry alone does NOT clear those
    // bits (Phase 5.b never set them).
    //
    // EXCLUSIVE lock acquired.
    // =================================================================
    void FXObjectArray::ReleaseSlot(::int32 InternalIndex) noexcept
    {
        XPACT_CHECK(InternalIndex > 0);

        ::XCore::HAL::FScopedWriteLock WriteLock(m_lock);

        XPACT_CHECK(InternalIndex < m_committedCount.load(::std::memory_order_relaxed));

        // Bump SerialNumber (mirror of FreeEntry's invariant).
        const bool bWasLive = (m_entries[InternalIndex].Object != nullptr);
        ++m_entries[InternalIndex].SerialNumber;
        if (m_entries[InternalIndex].SerialNumber == 0)
        {
            m_entries[InternalIndex].SerialNumber = 1;
        }
        m_entries[InternalIndex].Object = nullptr;

        // Clear entry-state bits that pertained to the prior bound
        // object. The refcount sub-field MUST be zero at this point
        // (the sweep only enqueues objects with refcount == 0; we do
        // not zero it defensively here so a misuse surfaces as a
        // distinct symptom rather than silently corrupting the
        // refcount discipline).
        ::std::atomic<::std::uint64_t>& StateBits = m_entries[InternalIndex].StateBits;
        constexpr ::std::uint64_t kEntryStateClearMask =
            kFXObjectArrayPendingDestroyBit |
            kFXObjectArrayGarbageBit        |
            kFXObjectArrayRootPinnedBit     |
            kFXObjectArrayHotReloadInProgress;
        ::std::uint64_t Old = StateBits.load(::std::memory_order_relaxed);
        for (;;)
        {
            const ::std::uint64_t New = Old & ~kEntryStateClearMask;
            if (Old == New)
            {
                break;
            }
            if (StateBits.compare_exchange_weak(
                    Old,
                    New,
                    ::std::memory_order_acq_rel,
                    ::std::memory_order_acquire))
            {
                break;
            }
        }

        if (bWasLive)
        {
            m_numLive.fetch_sub(1, ::std::memory_order_acq_rel);
        }

        // Push onto LIFO free list (mirror of FreeEntry).
        if (m_freeListCount >= m_freeListCapacity)
        {
            GrowFreeListUnderLock();
        }
        m_freeList[m_freeListCount] = InternalIndex;
        ++m_freeListCount;
    }

} // namespace XCore
