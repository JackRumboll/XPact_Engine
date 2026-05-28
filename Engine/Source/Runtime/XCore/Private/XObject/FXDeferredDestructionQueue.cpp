// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXDeferredDestructionQueue.cpp -- Phase 5.h two-phase destruction
// queue body (XCoreXObject Rev 4 §2.5 + §4.2 step 7).
// =====================================================================
//
// Produces FinishDestroy dispatch + slot release for objects whose
// BeginDestroy has been called by the Phase 5.h sweep phase.
//
// =====================================================================

#include "XObject/FXDeferredDestructionQueue.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FPlatformTime.h"
#include "Macros/XAssertionMacros.h"
#include "Reflection/FClass.h"
#include "XObject/EObjectFlags.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/XObject.h"

#include <cstring>

namespace XCore
{

FXDeferredDestructionQueue& FXDeferredDestructionQueue::Get() noexcept
{
    static FXDeferredDestructionQueue s_instance;
    return s_instance;
}

FXDeferredDestructionQueue::FXDeferredDestructionQueue() noexcept
    : m_entries(nullptr)
    , m_count(0)
    , m_capacity(0)
    , m_lastPassFinishDestroyCount(0)
    , m_lastPassTimeoutForcedCount(0)
    , m_lastPassDurationUs(0)
    , m_lock()
{
}

FXDeferredDestructionQueue::~FXDeferredDestructionQueue() noexcept
{
    if (m_entries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(m_entries);
        m_entries = nullptr;
    }
}

// =====================================================================
// GrowIfNeededUnderLock -- mirror of FXSweepCandidateQueue grower.
// =====================================================================
void FXDeferredDestructionQueue::GrowIfNeededUnderLock(
    ::std::size_t RequiredSlots) noexcept
{
    const ::std::size_t Needed = m_count + RequiredSlots;
    if (m_capacity >= Needed)
    {
        return;
    }
    ::std::size_t NewCapacity = (m_capacity == 0)
        ? kFXDeferredDestructionInitialCapacity
        : m_capacity;
    while (NewCapacity < Needed)
    {
        NewCapacity *= 2;
    }
    FEntry* const NewEntries = static_cast<FEntry*>(
        ::XCore::HAL::FMemory::MallocOrAbort(
            NewCapacity * sizeof(FEntry),
            alignof(FEntry),
            ::XCore::HAL::FMemTag::Reflection));
    if (m_entries != nullptr)
    {
        ::std::memcpy(NewEntries, m_entries, m_count * sizeof(FEntry));
        ::XCore::HAL::FMemory::Free(m_entries);
    }
    m_entries  = NewEntries;
    m_capacity = NewCapacity;
}

// =====================================================================
// EnqueueAfterBeginDestroy -- producer (sweep phase).
// =====================================================================
void FXDeferredDestructionQueue::EnqueueAfterBeginDestroy(
    ::std::int32_t InternalIndex) noexcept
{
    if (InternalIndex <= 0)
    {
        // Defence-in-depth no-op for the null sentinel; the sweep
        // never produces index 0 (the sweep candidate enumeration
        // starts at index 1).
        return;
    }

    const double NowSeconds = ::XCore::HAL::FPlatformTime::Seconds();

    ::XCore::HAL::FScopedLock Lock(m_lock);
    GrowIfNeededUnderLock(1);
    m_entries[m_count].InternalIndex    = InternalIndex;
    m_entries[m_count]._pad              = 0;
    m_entries[m_count].FirstSeenSeconds = NowSeconds;
    ++m_count;
}

// =====================================================================
// DrainOnePassWithBudget -- consumer (sim-tick or post-sweep).
//
// Implementation strategy:
//   1. Acquire lock; take ownership of the current entry buffer
//      (replace with nullptr/0/0). Release lock.
//   2. Walk the snapshot. For each entry: dispatch
//      IsReadyForFinishDestroy under NO lock (avoids re-entry hazards
//      with sibling subsystems). If ready (or timeout): dispatch
//      FinishDestroy + release the cell + ReleaseSlot.
//   3. Entries not yet ready are accumulated into a re-enqueue buffer.
//   4. After the walk (or budget exhausted), acquire lock; splice the
//      re-enqueue buffer + any UNVISITED entries back onto the queue
//      head.
//
// This avoids holding our lock across lifecycle dispatch (which may
// take other subsystem locks). The "swap-out then re-splice" pattern
// is the standard lock-order-safe drain shape.
// =====================================================================
::std::size_t FXDeferredDestructionQueue::DrainOnePassWithBudget(
    ::std::int64_t BudgetUs) noexcept
{
    const double PassStartSeconds = ::XCore::HAL::FPlatformTime::Seconds();

    // ----- Step 1: take ownership of the queue snapshot. -----
    FEntry*        Snapshot         = nullptr;
    ::std::size_t  SnapshotCount    = 0;
    ::std::size_t  SnapshotCapacity = 0;
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        Snapshot         = m_entries;
        SnapshotCount    = m_count;
        SnapshotCapacity = m_capacity;
        m_entries  = nullptr;
        m_count    = 0;
        m_capacity = 0;
    }

    if (SnapshotCount == 0)
    {
        // Nothing to do. Restore the (empty) snapshot ownership.
        if (Snapshot != nullptr)
        {
            ::XCore::HAL::FScopedLock Lock(m_lock);
            // The queue may have grown via a concurrent
            // EnqueueAfterBeginDestroy between our snapshot and the
            // re-lock; do NOT free if it has. The MVP discipline (one
            // marker thread, one sim-tick thread, never overlapping) makes
            // this a structurally-impossible race, but the defence is
            // cheap.
            if (m_entries == nullptr)
            {
                m_entries  = Snapshot;
                m_capacity = SnapshotCapacity;
            }
            else
            {
                ::XCore::HAL::FMemory::Free(Snapshot);
            }
        }
        m_lastPassFinishDestroyCount = 0;
        m_lastPassTimeoutForcedCount = 0;
        m_lastPassDurationUs         = 0;
        return 0;
    }

    // ----- Step 2: walk the snapshot. -----
    FXObjectArray&     Array     = FXObjectArray::Get();
    FXObjectAllocator& Allocator = FXObjectAllocator::Get();

    ::std::size_t Finalised        = 0;
    ::std::size_t TimeoutForced    = 0;
    ::std::size_t I                = 0;
    bool          BudgetExhausted  = false;

    // Re-enqueue accumulator (entries that didn't fire this pass).
    // We piggyback on the snapshot buffer by writing the not-ready
    // entries forward from index 0 (the snapshot is reused as the
    // re-enqueue buffer). This is safe because we process entries
    // in order and write to indices <= the current read pointer.
    ::std::size_t WriteIndex = 0;

    for (; I < SnapshotCount; ++I)
    {
        const FEntry& Entry = Snapshot[I];

        // Per-entry processing under NO lock. The XObject is
        // structurally alive (the slot has not yet been released; the
        // SerialNumber is the one captured at enqueue).
        XObject* Object = Array.GetObjectAtIndexUnchecked(Entry.InternalIndex);
        if (Object == nullptr)
        {
            // The slot was released by another path (test harness
            // synchronous FreeEntry, or a duplicate enqueue raced).
            // Drop without dispatch.
            continue;
        }

        // ----- Probe IsReadyForFinishDestroy via the lifecycle table.
        // The slot may be NULL (capability bit clear): per spec §2.8
        // the default is TRUE.
        bool ReadyToFinalise = true;
        const ::XCore::Reflect::FClass* const Class = Object->GetClass();
        if (Class != nullptr)
        {
            const ::XCore::Reflect::FXObjectLifecycleTable* const Table =
                Class->GetLifecycleTable();
            if (Table != nullptr &&
                Table->HasSlot(
                    ::XCore::Reflect::EXObjectLifecycleSlot::IsReadyForFinishDestroy))
            {
                auto* const Fn = Table->GetSlot<
                    ::XCore::Reflect::EXObjectLifecycleSlot::IsReadyForFinishDestroy>();
                if (Fn != nullptr)
                {
                    ReadyToFinalise = Fn(Object);
                }
            }
        }

        // ----- Timeout check. Per Prime Directive: 1 second of wall
        // time across drain passes forces FinishDestroy regardless.
        const double NowSeconds = ::XCore::HAL::FPlatformTime::Seconds();
        const double Elapsed = NowSeconds - Entry.FirstSeenSeconds;
        const bool TimedOut = Elapsed >= kFXDeferredDestructionTimeoutSeconds;
        if (!ReadyToFinalise && TimedOut)
        {
            ReadyToFinalise = true;
            ++TimeoutForced;
        }

        if (!ReadyToFinalise)
        {
            // Re-enqueue at WriteIndex (compaction). Preserves the
            // original FirstSeenSeconds so the timeout window is
            // continuous across drain passes.
            Snapshot[WriteIndex] = Entry;
            ++WriteIndex;
        }
        else
        {
            // ----- Finalise. Dispatch FinishDestroy via the table.
            if (Class != nullptr)
            {
                const ::XCore::Reflect::FXObjectLifecycleTable* const Table =
                    Class->GetLifecycleTable();
                if (Table != nullptr &&
                    Table->HasSlot(
                        ::XCore::Reflect::EXObjectLifecycleSlot::FinishDestroy))
                {
                    auto* const Fn = Table->GetSlot<
                        ::XCore::Reflect::EXObjectLifecycleSlot::FinishDestroy>();
                    if (Fn != nullptr)
                    {
                        Fn(Object);
                    }
                }
            }

            // Set EObjectFlags::FinishDestroyed on the XObject so any
            // diagnostic / probe code can observe the transition before
            // the cell is reclaimed.
            Object->SetFlags(EObjectFlags::FinishDestroyed);

            // ----- Release the FXObjectAllocator cell.
            //
            // The cell's first bytes are the XObject header; the cell
            // width is the size-class width owned by the FClass. The
            // Phase 5.b Deallocate accepts the raw cell pointer (the
            // XObject* IS the cell pointer; XObject is the first member
            // of the cell at offset 0).
            //
            // RATIONALE for ordering (Deallocate BEFORE ReleaseSlot):
            //   * The Deallocate path uses the cell pointer to locate
            //     its owning slab; the FXObjectAllocator does NOT
            //     consult the FXObjectArray for this. The slot can be
            //     released either before or after Deallocate without
            //     affecting Deallocate's correctness.
            //   * Releasing the slot AFTER Deallocate avoids a brief
            //     window where the slot is on the free list but the
            //     cell is still allocated (a freshly-allocated XObject
            //     could land at the same slot index but a different
            //     cell). The ordering chosen ensures the cell+slot
            //     transition is atomic-as-observed: from outside, the
            //     XObject either exists fully or is fully gone.
            Allocator.Deallocate(static_cast<void*>(Object));

            // ----- Release the FXObjectArray slot.
            //
            // ReleaseSlot bumps SerialNumber so every in-flight
            // XWeakPtr to this index returns nullptr on its next deref.
            // The PendingDestroy / Garbage / RootPinned / HotReload
            // mirror bits are cleared by ReleaseSlot's body.
            Array.ReleaseSlot(Entry.InternalIndex);

            ++Finalised;
        }

        // Budget check (per Rev 2 design decision O9).
        const double NowAfterEntry = ::XCore::HAL::FPlatformTime::Seconds();
        const double ElapsedPassSeconds = NowAfterEntry - PassStartSeconds;
        const ::std::int64_t ElapsedPassUs = static_cast<::std::int64_t>(
            ElapsedPassSeconds * 1'000'000.0);
        if (ElapsedPassUs >= BudgetUs)
        {
            // Budget exhausted. Stop processing; the remaining entries
            // (I + 1 .. SnapshotCount - 1) need to be carried forward.
            BudgetExhausted = true;
            ++I;  // advance past the just-processed entry
            break;
        }
    }

    // ----- Step 3: re-enqueue the not-ready entries + the budget-
    // exhausted tail. -----
    //
    // The not-ready entries occupy [0 .. WriteIndex). The budget-
    // exhausted tail (if any) occupies [I .. SnapshotCount).
    //
    // We copy the tail forward to immediately follow the not-ready
    // entries; the compacted [0 .. WriteIndex + (SnapshotCount - I))
    // range is the new queue contents.
    if (BudgetExhausted && I < SnapshotCount)
    {
        const ::std::size_t TailCount = SnapshotCount - I;
        if (WriteIndex != I)
        {
            // Use memmove because the source + destination ranges may
            // overlap.
            ::std::memmove(
                Snapshot + WriteIndex,
                Snapshot + I,
                TailCount * sizeof(FEntry));
        }
        WriteIndex += TailCount;
    }

    // ----- Step 4: restore the snapshot ownership, OR splice if a new
    // batch was enqueued concurrently. -----
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);

        if (m_entries == nullptr)
        {
            // No concurrent enqueue; restore the (now-compacted)
            // snapshot as the queue.
            m_entries  = Snapshot;
            m_count    = WriteIndex;
            m_capacity = SnapshotCapacity;
        }
        else
        {
            // A concurrent EnqueueAfterBeginDestroy ran (structurally
            // impossible in the MVP discipline; defence-in-depth). The
            // new buffer m_entries[0..m_count) contains the concurrent
            // additions. Splice: copy our compacted snapshot to the
            // front of a fresh buffer, then append the concurrent
            // additions.
            const ::std::size_t Total = WriteIndex + m_count;
            ::std::size_t NewCapacity =
                (m_capacity > 0) ? m_capacity : kFXDeferredDestructionInitialCapacity;
            while (NewCapacity < Total)
            {
                NewCapacity *= 2;
            }
            FEntry* const NewBuf = static_cast<FEntry*>(
                ::XCore::HAL::FMemory::MallocOrAbort(
                    NewCapacity * sizeof(FEntry),
                    alignof(FEntry),
                    ::XCore::HAL::FMemTag::Reflection));
            ::std::memcpy(NewBuf, Snapshot, WriteIndex * sizeof(FEntry));
            ::std::memcpy(NewBuf + WriteIndex, m_entries, m_count * sizeof(FEntry));
            ::XCore::HAL::FMemory::Free(Snapshot);
            ::XCore::HAL::FMemory::Free(m_entries);
            m_entries  = NewBuf;
            m_count    = Total;
            m_capacity = NewCapacity;
        }
    }

    // ----- Step 5: update diagnostics. -----
    const double PassEndSeconds = ::XCore::HAL::FPlatformTime::Seconds();
    const ::std::int64_t PassDurationUs = static_cast<::std::int64_t>(
        (PassEndSeconds - PassStartSeconds) * 1'000'000.0);

    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        m_lastPassFinishDestroyCount = Finalised;
        m_lastPassTimeoutForcedCount = TimeoutForced;
        m_lastPassDurationUs         = PassDurationUs;
    }

    return Finalised;
}

// =====================================================================
// Size / IsEmpty / diagnostics accessors.
// =====================================================================
::std::size_t FXDeferredDestructionQueue::Size() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count;
}

bool FXDeferredDestructionQueue::IsEmpty() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_count == 0;
}

::std::size_t FXDeferredDestructionQueue::GetLastPassFinishDestroyCount() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_lastPassFinishDestroyCount;
}

::std::size_t FXDeferredDestructionQueue::GetLastPassTimeoutForcedCount() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_lastPassTimeoutForcedCount;
}

::std::int64_t FXDeferredDestructionQueue::GetLastPassDurationUs() const noexcept
{
    ::XCore::HAL::FScopedLock Lock(m_lock);
    return m_lastPassDurationUs;
}

// =====================================================================
// __ResetForTests -- drop every queued entry; release the buffer.
// =====================================================================
void FXDeferredDestructionQueue::__ResetForTests() noexcept
{
    FEntry* OldEntries = nullptr;
    {
        ::XCore::HAL::FScopedLock Lock(m_lock);
        OldEntries = m_entries;
        m_entries  = nullptr;
        m_count    = 0;
        m_capacity = 0;
        m_lastPassFinishDestroyCount = 0;
        m_lastPassTimeoutForcedCount = 0;
        m_lastPassDurationUs         = 0;
    }
    if (OldEntries != nullptr)
    {
        ::XCore::HAL::FMemory::Free(OldEntries);
    }
}

} // namespace XCore
