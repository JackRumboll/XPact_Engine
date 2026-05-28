// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXGrayQueue.cpp -- per-thread gray queue + global overflow list
// bodies (Phase 5.g).
// =====================================================================
//
// XCoreXObject Rev 4 §4.2 + §4.4. Implementation patterns:
//   * Per-thread queue: LIFO bounded buffer; wait-free fast path.
//   * Global overflow list: lock-protected heap-grown vector.
//   * TLS access via TModuleSafeThreadLocal (mirrors FXObjectSatbQueue).
//
// =====================================================================

#include "XObject/FXGrayQueue.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/TModuleSafeThreadLocal.h"
#include "XObject/XObject.h"

#include <algorithm>
#include <cstring>

namespace XCore
{

// =====================================================================
// FXGrayQueue -- per-thread body.
// =====================================================================

// ---------------------------------------------------------------------
// Push -- enqueue. Hot path is wait-free (relaxed atomic). Overflow
// spills the FULL buffer to the global list and resets m_count to 0.
// ---------------------------------------------------------------------
void FXGrayQueue::Push(XObject* Object) noexcept
{
    if (XPACT_UNLIKELY(Object == nullptr))
    {
        // Defensive: the mark visitor pre-filters nullptr; double-check
        // is cheap and prevents a stale nullptr from polluting the
        // queue.
        return;
    }

    const ::std::uint32_t LocalCount =
        m_count.load(::std::memory_order_relaxed);

    if (XPACT_LIKELY(LocalCount < kCapacity))
    {
        m_entries[LocalCount] = Object;
        const ::std::uint32_t NewCount = LocalCount + 1u;
        m_count.store(NewCount, ::std::memory_order_relaxed);

        // Peak tracking: update m_peakCount if NewCount exceeds the
        // current peak. Compare-and-swap loop; relaxed since the peak
        // is diagnostic-only.
        ::std::uint32_t Peak =
            m_peakCount.load(::std::memory_order_relaxed);
        while (NewCount > Peak)
        {
            if (m_peakCount.compare_exchange_weak(
                    Peak,
                    NewCount,
                    ::std::memory_order_relaxed,
                    ::std::memory_order_relaxed))
            {
                break;
            }
        }
        return;
    }

    // Overflow: spill the FULL buffer to the global overflow list.
    // The producer-side spill copies the buffer's bytes; the global
    // list owns the entries from that point. Reset m_count to 0 then
    // write the new entry at slot 0.
    FXGrayOverflowList::Get().AppendBatch(m_entries, kCapacity);
    m_entries[0] = Object;
    m_count.store(1u, ::std::memory_order_relaxed);

    // Peak: post-spill the local count is back to 1. The peak captured
    // the kCapacity before the spill so the peak-load reflects the
    // maximum TLS occupancy across the cycle.
    ::std::uint32_t Peak =
        m_peakCount.load(::std::memory_order_relaxed);
    while (kCapacity > Peak)
    {
        if (m_peakCount.compare_exchange_weak(
                Peak,
                kCapacity,
                ::std::memory_order_relaxed,
                ::std::memory_order_relaxed))
        {
            break;
        }
    }
}

// ---------------------------------------------------------------------
// Pop -- LIFO dequeue. When the TLS buffer is empty, refill from the
// global overflow list. Returns nullptr only when both are empty.
// ---------------------------------------------------------------------
XObject* FXGrayQueue::Pop() noexcept
{
    ::std::uint32_t LocalCount =
        m_count.load(::std::memory_order_relaxed);

    if (XPACT_LIKELY(LocalCount > 0u))
    {
        const ::std::uint32_t NewCount = LocalCount - 1u;
        XObject* const Object = m_entries[NewCount];
        m_count.store(NewCount, ::std::memory_order_relaxed);
        return Object;
    }

    // TLS empty: refill from global overflow list. Pull up to kCapacity
    // entries.
    const ::std::size_t Pulled =
        FXGrayOverflowList::Get().PullBatch(m_entries, kCapacity);

    if (Pulled == 0)
    {
        return nullptr;
    }

    // Refilled. Pop the last (most-recently-stored) entry.
    LocalCount = static_cast<::std::uint32_t>(Pulled);
    const ::std::uint32_t NewCount = LocalCount - 1u;
    XObject* const Object = m_entries[NewCount];
    m_count.store(NewCount, ::std::memory_order_relaxed);
    return Object;
}

// ---------------------------------------------------------------------
// IsEmpty -- snapshot. TLS empty AND global overflow empty.
// ---------------------------------------------------------------------
bool FXGrayQueue::IsEmpty() const noexcept
{
    if (m_count.load(::std::memory_order_acquire) != 0u)
    {
        return false;
    }
    return FXGrayOverflowList::Get().IsEmpty();
}

// ---------------------------------------------------------------------
// ResetForCycle -- clear queue state at GC cycle start.
// ---------------------------------------------------------------------
void FXGrayQueue::ResetForCycle() noexcept
{
    m_count.store(0u, ::std::memory_order_release);
    m_peakCount.store(0u, ::std::memory_order_release);
}

// =====================================================================
// FXGrayOverflowList -- global aggregator body.
// =====================================================================

FXGrayOverflowList& FXGrayOverflowList::Get() noexcept
{
    static FXGrayOverflowList s_instance;
    return s_instance;
}

namespace
{
    // Initial capacity matches FXObjectGlobalSatbLog; 256 entries
    // is the first overflow spill size from a single TLS queue.
    constexpr ::std::size_t kOverflowInitialCapacity = 256;
}

FXGrayOverflowList::FXGrayOverflowList() noexcept
    : m_entries(nullptr)
    , m_count(0)
    , m_capacity(0)
    , m_lock()
{
}

FXGrayOverflowList::~FXGrayOverflowList() noexcept
{
    if (m_entries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(m_entries);
        m_entries = nullptr;
    }
}

void FXGrayOverflowList::GrowIfNeededUnderLock(
    ::std::size_t RequiredSlots) noexcept
{
    const ::std::size_t Needed = m_count + RequiredSlots;
    if (m_capacity >= Needed)
    {
        return;
    }
    ::std::size_t NewCapacity = (m_capacity == 0)
        ? kOverflowInitialCapacity
        : m_capacity;
    while (NewCapacity < Needed)
    {
        NewCapacity *= 2;
    }
    XObject** const NewEntries = static_cast<XObject**>(
        ::XCore::HAL::FMemory::MallocOrAbort(
            NewCapacity * sizeof(XObject*),
            alignof(XObject*),
            ::XCore::HAL::FMemTag::Reflection));
    if (m_entries != nullptr)
    {
        ::std::memcpy(NewEntries, m_entries, m_count * sizeof(XObject*));
        ::XCore::HAL::FMemory::Free(m_entries);
    }
    m_entries  = NewEntries;
    m_capacity = NewCapacity;
}

void FXGrayOverflowList::AppendBatch(
    XObject* const* Entries,
    ::std::size_t   Count) noexcept
{
    if (Count == 0)
    {
        return;
    }
    ::XCore::HAL::FScopedLock Lock(m_lock);
    GrowIfNeededUnderLock(Count);
    ::std::memcpy(m_entries + m_count, Entries, Count * sizeof(XObject*));
    m_count += Count;
}

::std::size_t FXGrayOverflowList::PullBatch(
    XObject**     OutEntries,
    ::std::size_t MaxCount) noexcept
{
    if (MaxCount == 0 || OutEntries == nullptr)
    {
        return 0;
    }
    ::XCore::HAL::FScopedLock Lock(m_lock);
    if (m_count == 0)
    {
        return 0;
    }
    const ::std::size_t ToPull = (m_count < MaxCount) ? m_count : MaxCount;
    // LIFO pull from the tail. We copy the tail block into the caller's
    // buffer; the caller then Pops from the LAST slot of its TLS
    // buffer, which is the most-recently-spilled entry.
    const ::std::size_t SrcStart = m_count - ToPull;
    ::std::memcpy(OutEntries, m_entries + SrcStart, ToPull * sizeof(XObject*));
    m_count -= ToPull;
    return ToPull;
}

::std::size_t FXGrayOverflowList::Size() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count;
}

bool FXGrayOverflowList::IsEmpty() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count == 0;
}

void FXGrayOverflowList::__ResetForTests() noexcept
{
    XObject** OldEntries = nullptr;
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        OldEntries = m_entries;
        m_entries  = nullptr;
        m_count    = 0;
        m_capacity = 0;
    }
    if (OldEntries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(OldEntries);
    }
}

// =====================================================================
// GetThreadGrayQueue -- TLS accessor.
//
// Mirrors GetThreadSatbQueue (Phase 5.f) pattern. The TLS slot is
// allocated at file-scope static construction (pre-main); first call
// on any thread lazy-creates the FXGrayQueue.
// =====================================================================
namespace
{
    ::XCore::HAL::TModuleSafeThreadLocal<FXGrayQueue> s_ThreadGrayQueue;

    // Fallback queue used when AllocSlot fails. Same posture as
    // FXObjectSatbQueue's s_FallbackQueue: process-global; correctness
    // preserved; performance degrades to contention-on-Push for any
    // thread that lost its TLS slot. In practice the TLS allocation
    // never fails on supported targets.
    FXGrayQueue s_FallbackGrayQueue;
}

FXGrayQueue& GetThreadGrayQueue() noexcept
{
    FXGrayQueue* const Ptr = s_ThreadGrayQueue.Get();
    if (XPACT_LIKELY(Ptr != nullptr))
    {
        return *Ptr;
    }
    return s_FallbackGrayQueue;
}

} // namespace XCore
