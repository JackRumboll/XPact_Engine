// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatRegistry.cpp -- frame-merge implementation (Section 10.5).
// =====================================================================
//
// XCore-4a Rev 3, Section 10.5 (Threading model) + fix Rev 3 M1
// (hot-path lazy-init lifecycle).
//
// Implements:
//   * FStatRegistry::MergeFromAllThreads  -- the per-frame aggregate.
//   * FStatRegistry::Snapshot             -- copy current tree to Out.
//   * FStatRegistry::__Initialize         -- perfect-hash table build.
//   * FStatSnapshot                       -- snapshot value type.
//
// PHASE 1F SCOPE:
//   * MergeFromAllThreads sweeps the calling thread's shard + the
//     thread-exit overflow queue. The multi-thread sweep (every
//     live thread's shard) is wired in Phase 1g once FThreadRegistry
//     ships.
//   * The perfect-hash side table build at __Initialize is a Phase
//     1f stub; the XBT-emitted generated header pipeline ships in
//     Phase 1g.
//
// =====================================================================

#include "HAL/FStatRegistry.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/FStatShard.h"
#include "HAL/FRWLock.h"
#include "Macros/XPactMacros.h"

#include <cstring>
#include <new>

namespace XCore::Stat
{

    // -----------------------------------------------------------------
    // Global tree state.
    //
    // The global tree is a hash-keyed table of (Hash, count) pairs
    // accumulated across all threads. Phase 1f uses a simple linear-
    // grow array; Phase 1g grows to the perfect-hash O(1) accessor.
    //
    // Threading: the table is mutated only by MergeFromAllThreads
    // which runs on a sync point (no concurrent XSTAT_INC). The
    // Snapshot accessor takes the shared RWLock for read.
    // -----------------------------------------------------------------
    struct FStatRegistryState
    {
        // Linear-grow array of accumulated (Hash, count) pairs.
        FStatSnapshot::FEntry* Entries;
        ::int32 Count;
        ::int32 Capacity;

        // Cycle counter parallel array (cycles per stat).
        ::uint64* Cycles;

        // RWLock for Snapshot reads vs MergeFromAllThreads writes.
        ::XCore::HAL::FRWLock Lock;

        FStatRegistryState() noexcept
            : Entries(nullptr)
            , Count(0)
            , Capacity(0)
            , Cycles(nullptr)
        {}

        ~FStatRegistryState() noexcept
        {
            if (Entries != nullptr)
            {
                ::XCore::HAL::FMemory::Free(Entries);
                Entries = nullptr;
            }
            if (Cycles != nullptr)
            {
                ::XCore::HAL::FMemory::Free(Cycles);
                Cycles = nullptr;
            }
        }

        // -------------------------------------------------------------
        // FindOrAdd -- locate the entry for Hash; insert if absent.
        //
        // Linear scan in Phase 1f (the table size is bounded by the
        // stat-name population, expected <= 10K). Phase 1g swaps in
        // the perfect-hash O(1) lookup.
        //
        // Returns a reference to the entry's count field, ready for
        // accumulation.
        // -------------------------------------------------------------
        ::int64* FindOrAdd(::uint64 Hash) noexcept
        {
            for (::int32 i = 0; i < Count; ++i)
            {
                if (Entries[i].Hash == Hash)
                {
                    return &Entries[i].Count;
                }
            }
            // Not found; grow the array (linear doubling).
            if (Count == Capacity)
            {
                const ::int32 NewCap = Capacity == 0 ? 64 : Capacity * 2;
                void* NewEntries = ::XCore::HAL::FMemory::MallocOrAbort(
                    sizeof(FStatSnapshot::FEntry) * NewCap,
                    alignof(FStatSnapshot::FEntry),
                    ::XCore::HAL::FMemTag::Stat);
                void* NewCycles = ::XCore::HAL::FMemory::MallocOrAbort(
                    sizeof(::uint64) * NewCap,
                    alignof(::uint64),
                    ::XCore::HAL::FMemTag::Stat);
                if (Entries != nullptr)
                {
                    ::std::memcpy(NewEntries, Entries, sizeof(FStatSnapshot::FEntry) * Count);
                    ::std::memcpy(NewCycles, Cycles, sizeof(::uint64) * Count);
                    ::XCore::HAL::FMemory::Free(Entries);
                    ::XCore::HAL::FMemory::Free(Cycles);
                }
                // Zero the new tail of the cycles array so the
                // CyclesForHash accessor returns 0 for not-yet-set
                // entries.
                Entries = static_cast<FStatSnapshot::FEntry*>(NewEntries);
                Cycles  = static_cast<::uint64*>(NewCycles);
                ::std::memset(Cycles + Count, 0, sizeof(::uint64) * (NewCap - Count));
                Capacity = NewCap;
            }
            Entries[Count].Hash  = Hash;
            Entries[Count].Count = 0;
            Cycles[Count]        = 0;
            ++Count;
            return &Entries[Count - 1].Count;
        }

        // Cycle counter accessor parallel to FindOrAdd. Always returns
        // a valid pointer (the cycles array is sized to match Entries).
        ::uint64* FindOrAddCycles(::uint64 Hash) noexcept
        {
            for (::int32 i = 0; i < Count; ++i)
            {
                if (Entries[i].Hash == Hash)
                {
                    return &Cycles[i];
                }
            }
            // Not found; trigger FindOrAdd to create the entry, then
            // re-resolve the cycles slot.
            FindOrAdd(Hash);
            return &Cycles[Count - 1];
        }
    };

    // The singleton state holder.
    static FStatRegistryState& GetRegistryState() noexcept
    {
        static FStatRegistryState Instance;
        return Instance;
    }

    // -----------------------------------------------------------------
    // FStatRegistry::MergeFromAllThreads
    //
    // Per Section 10.5 ordering: exit-overflow queue first; live-
    // thread shards second; cycle counters accumulated alongside.
    //
    // Phase 1f sweeps only the calling thread's shard (the
    // FThreadRegistry that lets Merge iterate every live thread
    // lands in Phase 1g). The exit-overflow queue is fully drained
    // because the Treiber stack is process-wide.
    // -----------------------------------------------------------------
    void FStatRegistry::MergeFromAllThreads() noexcept
    {
        FStatRegistryState& State = GetRegistryState();
        ::XCore::HAL::FScopedWriteLock L(State.Lock);

        // Step 1: drain the thread-exit overflow queue.
        while (FStatExitOverflowEntry* Entry = __PopThreadExitEntry())
        {
            *State.FindOrAdd(Entry->Hash) += Entry->Count;
            if (Entry->Cycles != 0)
            {
                *State.FindOrAddCycles(Entry->Hash) += Entry->Cycles;
            }
            ::XCore::HAL::FMemory::Free(Entry);
        }

        // Step 2: walk the calling thread's shard.
        FStatShard* Shard = __GetCurrentThreadShard();
        if (Shard != nullptr)
        {
            FStatSlot* Slots = Shard->__GetSlots();
            const ::int32 N = Shard->__GetSlotCount();

            for (::int32 i = 0; i < N; ++i)
            {
                FStatSlot& Slot = Slots[i];
                if (Slot.Hash == 0)
                {
                    continue;
                }
                if (Slot.Count != 0)
                {
                    *State.FindOrAdd(Slot.Hash) += Slot.Count;
                    Slot.Count = 0;   // reset for next frame's delta
                }
                if (Slot.Padding != 0)
                {
                    *State.FindOrAddCycles(Slot.Hash) += Slot.Padding;
                    Slot.Padding = 0;
                }
            }

            // Overflow entries.
            const FStatSlot* OverflowEntries = Shard->__GetOverflowEntries();
            const ::int32 OverflowCount = Shard->__GetOverflowCount();
            for (::int32 i = 0; i < OverflowCount; ++i)
            {
                const FStatSlot& Slot = OverflowEntries[i];
                if (Slot.Hash == 0)
                {
                    continue;
                }
                if (Slot.Count != 0)
                {
                    *State.FindOrAdd(Slot.Hash) += Slot.Count;
                }
                if (Slot.Padding != 0)
                {
                    *State.FindOrAddCycles(Slot.Hash) += Slot.Padding;
                }
            }
            // Reset overflow post-aggregate.
            Shard->__ResetOverflow();
        }

        // TODO(Phase 1g): iterate every live thread's shard via
        // FThreadRegistry. The calling thread's shard is the only
        // shard swept in Phase 1f.
    }

    // -----------------------------------------------------------------
    // FStatRegistry::Snapshot -- copy the current tree to Out.
    //
    // Takes the shared RWLock; the global tree may not be written
    // during the snapshot (writes happen only inside
    // MergeFromAllThreads, which takes the exclusive lock).
    // -----------------------------------------------------------------
    void FStatRegistry::Snapshot(FStatSnapshot& Out) noexcept
    {
        FStatRegistryState& State = GetRegistryState();
        ::XCore::HAL::FScopedReadLock L(State.Lock);

        Out.__Reset();
        for (::int32 i = 0; i < State.Count; ++i)
        {
            Out.__AddEntry(State.Entries[i].Hash, State.Entries[i].Count);
        }
    }

    // -----------------------------------------------------------------
    // FStatRegistry::__Initialize
    //
    // Phase 1f stub: the perfect-hash table is built from a generated
    // header that XBT emits during the XSTAT_DECL scan. The pipeline
    // ships in Phase 1g; Phase 1f's body is a no-op.
    //
    // The hot path remains correct without the perfect-hash table
    // (per fix Rev 3 M1): writes always go to the open-addressing
    // slot in the per-thread shard; reads go through Snapshot which
    // walks the global tree's linear array.
    // -----------------------------------------------------------------
    void FStatRegistry::__Initialize() noexcept
    {
        // TODO(Phase 1g): consume the XBT-generated perfect-hash
        // header here. Until then, the global-tree linear array
        // suffices for the unit-test surface.
    }

    // -----------------------------------------------------------------
    // FStatSnapshot
    // -----------------------------------------------------------------

    FStatSnapshot::FStatSnapshot() noexcept
        : m_entries(nullptr)
        , m_count(0)
        , m_capacity(0)
    {
    }

    FStatSnapshot::~FStatSnapshot() noexcept
    {
        if (m_entries != nullptr)
        {
            ::XCore::HAL::FMemory::Free(m_entries);
            m_entries = nullptr;
        }
    }

    ::int64 FStatSnapshot::Lookup(::uint64 Hash) const noexcept
    {
        for (::int32 i = 0; i < m_count; ++i)
        {
            if (m_entries[i].Hash == Hash)
            {
                return m_entries[i].Count;
            }
        }
        return 0;
    }

    ::int32 FStatSnapshot::GetEntryCount() const noexcept
    {
        return m_count;
    }

    const FStatSnapshot::FEntry& FStatSnapshot::GetEntry(::int32 Index) const noexcept
    {
        XPACT_CHECK(Index >= 0 && Index < m_count);
        return m_entries[Index];
    }

    void FStatSnapshot::__AddEntry(::uint64 Hash, ::int64 Count) noexcept
    {
        if (m_count == m_capacity)
        {
            const ::int32 NewCap = m_capacity == 0 ? 64 : m_capacity * 2;
            void* NewEntries = ::XCore::HAL::FMemory::MallocOrAbort(
                sizeof(FEntry) * NewCap,
                alignof(FEntry),
                ::XCore::HAL::FMemTag::Stat);
            if (m_entries != nullptr)
            {
                ::std::memcpy(NewEntries, m_entries, sizeof(FEntry) * m_count);
                ::XCore::HAL::FMemory::Free(m_entries);
            }
            m_entries = static_cast<FEntry*>(NewEntries);
            m_capacity = NewCap;
        }
        m_entries[m_count].Hash  = Hash;
        m_entries[m_count].Count = Count;
        ++m_count;
    }

    void FStatSnapshot::__Reset() noexcept
    {
        m_count = 0;
    }

} // namespace XCore::Stat
