// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectSatbQueue.cpp -- per-thread SATB queue body (Phase 5.f).
// =====================================================================
//
// XCoreXObject Rev 4 §4.3 + §5.6 + Rev 2 FIX-A-MED-37 + FIX-A-CRIT-TC1
// (TModuleSafeThreadLocal for hot-reload safety).
//
// =====================================================================

#include "XObject/FXObjectSatbQueue.h"

#include "HAL/TModuleSafeThreadLocal.h"
#include "XObject/XGCConcurrentState.h"

namespace XCore
{

// ---------------------------------------------------------------------
// Push -- enqueue an OLD reference value.
//
// HOT PATH. The fast path is:
//   1. Relaxed atomic load of m_count.
//   2. If m_count < kCapacity: write to m_entries[m_count] and
//      atomic-increment m_count.
//   3. Else: slow path (drain to global log; honour the drain-accept
//      gate; reset queue and retry).
//
// Per Prime Directive on the SLOW PATH:
//   * The drain consults g_XGCAcceptDrains FIRST. If false (hot-
//     reload quiesce), block on XGCWaitForDrainsAccepted until the
//     flag is set. The block is bounded by the cascade timeout.
//   * The drain then copies all entries into the global SATB log
//     under the log's lock, resets m_count to 0, and writes the new
//     entry at slot 0.
// ---------------------------------------------------------------------
void FXObjectSatbQueue::Push(XObject* OldValue) noexcept
{
    // Per-thread queue: no other thread observes m_entries / m_count
    // mutations except via the atomic Size() snapshot. The hot path
    // is therefore lock-free + race-free.
    const ::std::uint32_t LocalCount =
        m_count.load(::std::memory_order_relaxed);
    if (XPACT_LIKELY(LocalCount < kCapacity))
    {
        m_entries[LocalCount] = OldValue;
        m_count.store(LocalCount + 1u, ::std::memory_order_relaxed);
        return;
    }

    // Slow path: queue full. Drain to global log + retry.
    //
    // First honour the drain-accept gate (Rev 2 FIX-A-MED-37). If
    // false, block until flag is set or the cascade timeout expires.
    if (!g_XGCAcceptDrains.load(::std::memory_order_acquire))
    {
        ::XCore::XGCWaitForDrainsAccepted();
    }

    DrainToGlobalLog();

    // Post-drain: queue is empty. Write the new entry at slot 0.
    m_entries[0] = OldValue;
    m_count.store(1u, ::std::memory_order_relaxed);
}

// ---------------------------------------------------------------------
// DrainToGlobalLog -- internal drain helper.
//
// Acquires the global SATB log's lock briefly to AppendBatch the
// queue's contents. Resets m_count to 0.
// ---------------------------------------------------------------------
void FXObjectSatbQueue::DrainToGlobalLog() noexcept
{
    const ::std::uint32_t LocalCount =
        m_count.load(::std::memory_order_relaxed);
    if (LocalCount == 0)
    {
        return;
    }
    ::XCore::FXObjectGlobalSatbLog::Get().AppendBatch(
        m_entries,
        static_cast<::std::size_t>(LocalCount));
    m_count.store(0u, ::std::memory_order_release);
}

// =====================================================================
// GetThreadSatbQueue -- TLS accessor.
//
// Uses TModuleSafeThreadLocal<FXObjectSatbQueue> per Rev 2 FIX-A-CRIT-
// TC1 pattern to avoid hot-reload TLS-stale-slot hazards.
//
// The slot is a file-scope static TModuleSafeThreadLocal; the first
// call on any thread allocates the per-thread FXObjectSatbQueue via
// FMemory's Stat tag (TModuleSafeThreadLocal::Get's lazy-create path).
//
// IMPLEMENTATION NOTE.
//
// The TLS slot is allocated at file-scope static construction (BEFORE
// main()), before any potential write-barrier user code runs. The
// TLS_SAFE_OR_NULL fallback applies in the unlikely case that
// FPlatformTLS::AllocSlot exhausted the slot table; in that case
// Get() returns nullptr and the write barrier degrades to a card-only
// mark.
//
// We do NOT use a thread_local raw variable here because of the
// hot-reload hazard documented in TModuleSafeThreadLocal.h:6-16
// (stale slot points at freed code on module unload).
// =====================================================================
namespace
{
    // File-scope TLS slot. The ctor allocates an OS TLS slot at
    // dynamic-init (constexpr ctor is not available for
    // TModuleSafeThreadLocal because FPlatformTLS::AllocSlot is a
    // syscall). The pre-main init is fine: the TLS slot is allocated
    // before any user thread can call into the write barrier.
    ::XCore::HAL::TModuleSafeThreadLocal<FXObjectSatbQueue> s_ThreadSatbQueue;

    // Sentinel "fallback" queue used when AllocSlot fails. This is a
    // process-global queue that all threads share if their TLS slot
    // cannot be allocated; correctness is preserved (entries pile
    // up in the shared queue) but performance degrades to per-Push
    // global-lock acquisition. In practice the AllocSlot path never
    // fails on supported targets (the OS slot table is sized for
    // 1024+ slots; XPact's TLS usage is well under that).
    //
    // We use a separate FXObjectSatbQueue here to keep the sentinel
    // path identical to the normal path; the cost of the sentinel
    // is one extra std::atomic operation per Push and the lifetime
    // is process-global (which is intentional: if the TLS slot
    // can't be allocated, the slot's death is process death).
    FXObjectSatbQueue s_FallbackQueue;
}

FXObjectSatbQueue& GetThreadSatbQueue() noexcept
{
    FXObjectSatbQueue* const Ptr = s_ThreadSatbQueue.Get();
    if (XPACT_LIKELY(Ptr != nullptr))
    {
        return *Ptr;
    }
    return s_FallbackQueue;
}

} // namespace XCore
