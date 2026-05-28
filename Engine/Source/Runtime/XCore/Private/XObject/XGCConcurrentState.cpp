// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCConcurrentState.cpp -- global concurrent-mark flag + global SATB
// log + drain-accept wait primitive (Phase 5.f).
// =====================================================================
//
// XCoreXObject Rev 4 §4.3 + §4.7 + §5.6 + Rev 2 FIX-A-MED-37.
//
// =====================================================================

#include "XObject/XGCConcurrentState.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FPlatformProcess.h"

#include <cstring>      // std::memcpy

namespace XCore
{

// ---------------------------------------------------------------------
// Global atomics. Definitions for the externs declared in
// XGCConcurrentState.h.
//
// Both initialise to their documented defaults at value-init time
// (which precedes ALL dynamic-init in the program per [basic.start.
// static]/2). The atomic<bool>'s default-construct value is
// implementation-defined per C++17, but every supported toolchain
// value-initialises to false; we explicitly initialise to be defensive.
//
// g_XGCAcceptDrains starts at TRUE: drains are permitted by default;
// only the hot-reload quiesce path clears the flag.
// ---------------------------------------------------------------------
::std::atomic<bool> g_XGCIsConcurrentMarkActive{false};
::std::atomic<bool> g_XGCAcceptDrains{true};

// =====================================================================
// FXObjectGlobalSatbLog -- body.
// =====================================================================

// Magic-static singleton accessor.
FXObjectGlobalSatbLog& FXObjectGlobalSatbLog::Get() noexcept
{
    static FXObjectGlobalSatbLog s_instance;
    return s_instance;
}

// Initial capacity for the entry buffer. Chosen so the first
// AppendBatch (256 entries from a per-thread queue) fits without a
// grow. Subsequent grows double capacity.
namespace
{
    constexpr ::std::size_t kInitialCapacity = 256;
}

FXObjectGlobalSatbLog::FXObjectGlobalSatbLog() noexcept
    : m_entries(nullptr)
    , m_count(0)
    , m_capacity(0)
    , m_lock()
{
}

FXObjectGlobalSatbLog::~FXObjectGlobalSatbLog() noexcept
{
    if (m_entries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(m_entries);
        m_entries = nullptr;
    }
}

// ---------------------------------------------------------------------
// GrowIfNeededUnderLock -- ensure m_capacity - m_count >=
// RequiredSlots.
//
// Caller holds m_lock exclusively. Doubling growth keeps amortised
// O(1) cost per push.
// ---------------------------------------------------------------------
void FXObjectGlobalSatbLog::GrowIfNeededUnderLock(
    ::std::size_t RequiredSlots) noexcept
{
    const ::std::size_t Needed = m_count + RequiredSlots;
    if (m_capacity >= Needed)
    {
        return;
    }
    ::std::size_t NewCapacity = (m_capacity == 0)
        ? kInitialCapacity
        : m_capacity;
    while (NewCapacity < Needed)
    {
        NewCapacity *= 2;
    }
    // Allocate fresh buffer. This is under-lock per the lock-discipline
    // waiver: the SATB log's grow is rare (the buffer doubles per
    // grow; after ~16 grows the buffer is 16k entries = 128 KB) and
    // it's reflection-runtime metadata, not on the per-store hot path.
    // The waiver mirrors FXObjectArray::GrowFreeListUnderLock.
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

// ---------------------------------------------------------------------
// AppendBatch -- copy N entries from a per-thread queue into the log.
// ---------------------------------------------------------------------
void FXObjectGlobalSatbLog::AppendBatch(
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

// ---------------------------------------------------------------------
// Size -- snapshot of the current count. Uses the lock briefly to
// guarantee a coherent uint64 read (the count can be modified
// concurrently by AppendBatch; a torn read is unlikely on 64-bit
// platforms but the lock costs nothing meaningful here).
// ---------------------------------------------------------------------
::std::size_t FXObjectGlobalSatbLog::Size() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count;
}

// ---------------------------------------------------------------------
// Test-only reset. Frees the buffer; returns to freshly-constructed.
// ---------------------------------------------------------------------
void FXObjectGlobalSatbLog::__ResetForTests() noexcept
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
// XGCWaitForDrainsAccepted -- spin-yield until g_XGCAcceptDrains is
// true.
//
// Per Rev 2 FIX-A-MED-37: the wait is bounded by §11 acceptance
// criterion (e) cascade hot-patch timeout (<120 s nominal / <150 s
// kill). We do NOT enforce the timeout at this primitive (the spin
// is engineer-station-only; production paths never enter this code
// because g_XGCAcceptDrains is permanently true outside hot-reload).
//
// The wait is a graded yield:
//   * First 1024 iterations: spin (PauseInstruction).
//   * Then yield: FPlatformProcess::Yield() until the flag clears.
//
// This is a simple primitive. Phase 5.j may upgrade it to a
// condition-variable-backed wait if hot-reload latency measurements
// indicate the spin path is meaningful.
// =====================================================================
void XGCWaitForDrainsAccepted() noexcept
{
    // Fast path: flag already set.
    if (g_XGCAcceptDrains.load(::std::memory_order_acquire))
    {
        return;
    }
    // Short spin.
    for (int I = 0; I < 1024; ++I)
    {
        if (g_XGCAcceptDrains.load(::std::memory_order_acquire))
        {
            return;
        }
    }
    // Graded yield. Sleep(0) gives up the time slice to a same-priority
    // ready thread on the same core; Sleep(1) waits ~1ms.
    while (!g_XGCAcceptDrains.load(::std::memory_order_acquire))
    {
        ::XCore::HAL::FPlatformProcess::Sleep(0.001f);
    }
}

} // namespace XCore
