// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FNamePool.cpp -- the FName intern table implementation (XCore-4b §4.2-4.7).
// =====================================================================
//
// XCore-4b Rev 3, Section 4.2 ("Intern-table design"), Section 4.3
// ("Shared-read fast path"), Section 4.6 ("Persistence layout"),
// Section 4.7 ("Hot-reload across DLL patch").
//
// Implementation notes:
//
//   * 256 shards. Selection: high 8 bits of XXH3-64(bytes, len, seed=0).
//   * Per shard: FRWLock + open-addressing slot table + entry-pointer
//     table + linked list of 64 KB blocks.
//   * NAME_None reserved at shard 0 / shard-local id 0 at Init time.
//   * Entries are bump-allocated from the tail 64 KB block; full
//     blocks become read-only; pointers are stable for the lifetime
//     of the process.
//   * Numbered-name suffixes live entirely in FName.SerialNumber;
//     they do NOT consume intern-table entries (per §4.4).
//
// =====================================================================

#include "Reflection/FNamePool.h"

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "HAL/XInitPhase.h"
#include "Hash/FXxh3.h"

#include <cstring>     // std::memcpy, std::memcmp, std::strlen
#include <new>         // placement new

namespace XCore::Reflect
{

// =====================================================================
// Singleton storage. Constinit-initialised so the pool is live from
// PreStaticInit (matching FMemory::__Init's bootstrap-phase posture).
// =====================================================================

// The pool singleton lives inside Get() as a function-local static. The
// FRWLock ctors are not constexpr (they call platform SRWLock /
// pthread_rwlock_init) so we cannot use XCONSTINIT. C++11 guarantees
// thread-safe initialisation of function-local statics (the so-called
// "magic statics"); the first call to Get() initialises the pool
// instance under an implicit acquire-release fence, and subsequent calls
// observe the initialised instance with no lock cost.
//
// First-use of Intern() also lazily runs Init() under a separate atomic
// flag + shard 0 exclusive lock (see Intern() body). The two layers are
// orthogonal:
//   * "magic static" guards the FNamePool ctor (FRWLock ctors etc.)
//   * Init() flag + double-checked locking guards the NAME_None
//     sentinel reservation + slot-table allocation.

// =====================================================================
// FNamePool member implementations.
// =====================================================================

FNamePool& FNamePool::Get() noexcept
{
    // Function-local static; C++11 guarantees thread-safe init.
    static FNamePool s_pool;
    return s_pool;
}

FNamePool::~FNamePool() noexcept
{
    // The function-local-static singleton is destroyed at process exit;
    // we leak the 64 KB blocks intentionally to avoid touching FMemory
    // after its own __Shutdown has run (the OS reclaims process memory
    // and the leaks are unobservable). If a future test harness drives
    // explicit Shutdown then re-Init cycles it should call Shutdown()
    // explicitly between cycles.
}

// ---------------------------------------------------------------------
// Init -- reserve the NAME_None sentinel.
// ---------------------------------------------------------------------
void FNamePool::Init() noexcept
{
    if (m_isInitialised.load(::std::memory_order_acquire))
    {
        return;
    }

    // Reserve the NAME_None sentinel in shard 0 / shard-local id 0.
    // The "None" bytes are interned verbatim so ToString() of NAME_None
    // produces "None" via the same code path as any other FName (no
    // special-case branch).
    constexpr const char kNoneStr[]   = "None";
    constexpr ::int32    kNoneByteLen = 4;  // length of "None"

    FNamePoolShard& Shard0 = m_shards[0];
    {
        // Serialise concurrent first-touch Init() callers via shard 0's
        // exclusive lock. Double-check inside the critical section so
        // a racing thread that wins the lock acquire skips the work.
        ::XCore::HAL::FScopedWriteLock Lock(Shard0.Lock);
        if (m_isInitialised.load(::std::memory_order_relaxed))
        {
            return;
        }

        // Force the NAME_None entry to land at shard-local id 0 by
        // allocating directly. We deliberately bypass Intern() to keep
        // the sentinel reservation atomic and unambiguous.
        FNameEntry* NoneEntry = AllocateEntryInPool(Shard0, /*EntryId=*/0, kNoneStr, kNoneByteLen);

        // Slot-table insertion is also direct so a future
        // FName("None") call hits the sentinel.
        if (Shard0.SlotCapacity == 0)
        {
            // First-touch slot table allocation.
            const ::SIZE_T Bytes = kInitialSlotCapacity * sizeof(FNamePoolSlot);
            Shard0.Slots = static_cast<FNamePoolSlot*>(
                ::XCore::HAL::FMemory::MallocOrAbort(Bytes, alignof(FNamePoolSlot), ::XCore::HAL::FMemTag::Reflection));
            for (::SIZE_T I = 0; I < kInitialSlotCapacity; ++I)
            {
                Shard0.Slots[I].ByteHash     = 0;
                Shard0.Slots[I].ShardLocalId = FNamePoolSlot::kEmptySlot;
                Shard0.Slots[I]._pad         = 0;
            }
            Shard0.SlotCapacity = kInitialSlotCapacity;
            Shard0.SlotCount    = 0;
        }

        // Insert the "None" -> entry id 0 mapping into the slot table.
        const ::uint64 NoneHash = ::XCore::Hash::FXxh3::Hash64(kNoneStr, kNoneByteLen, /*Seed=*/0);
        ::SIZE_T   SlotIdx   = NoneHash & (Shard0.SlotCapacity - 1);
        const ::SIZE_T Mask  = Shard0.SlotCapacity - 1;
        while (Shard0.Slots[SlotIdx].ShardLocalId != FNamePoolSlot::kEmptySlot)
        {
            SlotIdx = (SlotIdx + 1) & Mask;
        }
        Shard0.Slots[SlotIdx].ByteHash     = NoneHash;
        Shard0.Slots[SlotIdx].ShardLocalId = 0;
        ++Shard0.SlotCount;

        // Record the reserved entry pointer at shard-local id 0.
        if (Shard0.EntryCapacity == 0)
        {
            const ::SIZE_T InitialCap = 16;
            const ::SIZE_T Bytes      = InitialCap * sizeof(const FNameEntry*);
            Shard0.EntryPointers = static_cast<const FNameEntry**>(
                ::XCore::HAL::FMemory::MallocOrAbort(Bytes, alignof(const FNameEntry*), ::XCore::HAL::FMemTag::Reflection));
            for (::SIZE_T I = 0; I < InitialCap; ++I)
            {
                Shard0.EntryPointers[I] = nullptr;
            }
            Shard0.EntryCapacity = InitialCap;
        }
        Shard0.EntryPointers[0]    = NoneEntry;
        Shard0.NextShardLocalId    = 1;  // shard 0's id 0 is taken; next allocation lands at id 1
        m_noneEntry                = NoneEntry;
    }

    // Release-store so any thread that observes m_isInitialised == true
    // has visibility of every prior write inside this Init body.
    m_isInitialised.store(true, ::std::memory_order_release);
}

// ---------------------------------------------------------------------
// Shutdown -- release all shard storage.
// ---------------------------------------------------------------------
void FNamePool::Shutdown() noexcept
{
    if (!m_isInitialised.load(::std::memory_order_acquire))
    {
        return;
    }

    for (::SIZE_T S = 0; S < kShardCount; ++S)
    {
        FNamePoolShard& Shard = m_shards[S];
        ::XCore::HAL::FScopedWriteLock Lock(Shard.Lock);

        // Free all 64 KB blocks.
        FNamePoolBlock* Block = Shard.HeadBlock;
        while (Block != nullptr)
        {
            FNamePoolBlock* Next = Block->Next;
            ::XCore::HAL::FMemory::Free(Block);
            Block = Next;
        }
        Shard.HeadBlock = nullptr;
        Shard.TailBlock = nullptr;

        if (Shard.Slots != nullptr)
        {
            ::XCore::HAL::FMemory::Free(Shard.Slots);
            Shard.Slots        = nullptr;
            Shard.SlotCapacity = 0;
            Shard.SlotCount    = 0;
        }

        if (Shard.EntryPointers != nullptr)
        {
            ::XCore::HAL::FMemory::Free(Shard.EntryPointers);
            Shard.EntryPointers   = nullptr;
            Shard.EntryCapacity   = 0;
            Shard.NextShardLocalId = 0;
        }
    }

    m_noneEntry = nullptr;
    m_isInitialised.store(false, ::std::memory_order_release);
}

// ---------------------------------------------------------------------
// Intern -- the core lookup/insert path.
//
// Implements the shared-read fast path + exclusive-write slow path
// per spec §4.3 (Rev 2 renamed "Shared-read" from Rev 1's "Lock-free").
// ---------------------------------------------------------------------
::uint32 FNamePool::Intern(const char* Utf8, ::int32 ByteLen) noexcept
{
    // Lazy bootstrap. Per spec §3 cyclic-risk-resolution: "the first FName
    // ever created (NAME_None == ID 0) is a static constexpr sentinel that
    // requires no heap allocation; subsequent FName creations require
    // EInitPhase::PostStaticInit (the allocator is fully live by then)."
    //
    // To support both engine bootstrap (explicit __InitFNamePool at
    // PostStaticInit) and stand-alone test harnesses (where each test
    // exe runs FMemory::__Init then constructs FNames directly), we
    // lazily initialise the pool the first time Intern is called. The
    // lazy path runs under a per-pool acquire-release flag; the cost
    // on the steady-state hot path is one acquire-load.
    if (!m_isInitialised.load(::std::memory_order_acquire))
    {
        Init();
    }

    // Empty input is NAME_None per §4.3 (FromString fast path).
    if (ByteLen <= 0 || Utf8 == nullptr)
    {
        return kNoneIndex;
    }

    // Reject oversize names per §4.2 max 1024 bytes.
    XPACT_CHECK(static_cast<::SIZE_T>(ByteLen) <= kFNameMaxLength);

    // 1. Bytewise hash (XCore-4a scalar XXH3; fixed seed = 0 per §4.5).
    const ::uint64 Hash = ::XCore::Hash::FXxh3::Hash64(Utf8, static_cast<::SIZE_T>(ByteLen), /*Seed=*/0);

    // 2. Shard selection from the high 8 bits of the byte hash.
    const ::uint8 ShardId = GetShardIdFromHash(Hash);
    FNamePoolShard& Shard = m_shards[ShardId];

    // 3. Shared-lock read: try to find existing entry.
    {
        ::XCore::HAL::FScopedReadLock ReadLock(Shard.Lock);
        const ::uint32 ExistingId = TryFindEntry(Shard, Hash, Utf8, ByteLen);
        if (ExistingId != FNamePoolSlot::kEmptySlot)
        {
            return MakeIndex(ShardId, ExistingId);
        }
    } // ReadLock released here

    // 4. Exclusive-lock write path: race-checked insert.
    {
        ::XCore::HAL::FScopedWriteLock WriteLock(Shard.Lock);

        // Re-probe under the exclusive lock: a racing writer may have
        // inserted the same bytes between read-unlock and write-lock.
        const ::uint32 RaceCheckId = TryFindEntry(Shard, Hash, Utf8, ByteLen);
        if (RaceCheckId != FNamePoolSlot::kEmptySlot)
        {
            return MakeIndex(ShardId, RaceCheckId);
        }

        // Insert.
        const ::uint32 NewId = InsertEntry(Shard, ShardId, Hash, Utf8, ByteLen);
        return MakeIndex(ShardId, NewId);
    }
}

// ---------------------------------------------------------------------
// FindEntry -- resolve an Index to its FNameEntry*.
// ---------------------------------------------------------------------
const FNameEntry* FNamePool::FindEntry(::uint32 Index) noexcept
{
    // Lazy init: callers that look up NAME_None before any Intern() ran
    // must still receive the sentinel "None" entry. Without this, a
    // default-constructed FName's ToString() would return nullptr.
    if (!m_isInitialised.load(::std::memory_order_acquire))
    {
        Init();
    }

    if (Index == kNoneIndex)
    {
        return m_noneEntry;
    }

    const ::uint8  ShardId      = GetShardIdFromIndex(Index);
    const ::uint32 ShardLocalId = GetShardLocalIdFromIndex(Index);

    FNamePoolShard& Shard = m_shards[ShardId];

    // Reads of EntryPointers under a shared lock. The pointer array
    // grows under exclusive lock but every entry, once written, is
    // immutable for the process lifetime; thus a shared-lock read
    // observes a stable pointer value.
    ::XCore::HAL::FScopedReadLock ReadLock(Shard.Lock);
    if (ShardLocalId < Shard.NextShardLocalId &&
        Shard.EntryPointers != nullptr)
    {
        return Shard.EntryPointers[ShardLocalId];
    }
    return nullptr;
}

bool FNamePool::IsValidIndex(::uint32 Index) noexcept
{
    if (Index == kNoneIndex)
    {
        return true;  // NAME_None is always valid
    }
    return FindEntry(Index) != nullptr;
}

// ---------------------------------------------------------------------
// TryFindEntry -- probe the slot table for an existing matching entry.
// Caller must hold Shard.Lock (shared or exclusive).
//
// Returns the shard-local entry id on hit, or FNamePoolSlot::kEmptySlot
// on miss.
// ---------------------------------------------------------------------
::uint32 FNamePool::TryFindEntry(FNamePoolShard& Shard,
                                 ::uint64 ByteHash,
                                 const char* Utf8,
                                 ::int32 ByteLen) const noexcept
{
    if (Shard.SlotCapacity == 0)
    {
        return FNamePoolSlot::kEmptySlot;
    }

    const ::SIZE_T Mask    = Shard.SlotCapacity - 1;
    ::SIZE_T       SlotIdx = static_cast<::SIZE_T>(ByteHash) & Mask;

    // Linear probe. Termination is guaranteed by the load-factor cap
    // (we grow before the table fills up, so there is always at least
    // one empty slot).
    for (::SIZE_T Probes = 0; Probes < Shard.SlotCapacity; ++Probes)
    {
        const FNamePoolSlot& Slot = Shard.Slots[SlotIdx];

        if (Slot.ShardLocalId == FNamePoolSlot::kEmptySlot)
        {
            // Empty slot terminates the probe -- a matching entry would
            // have been placed here OR earlier.
            return FNamePoolSlot::kEmptySlot;
        }

        // Hash matches: do a byte-level compare to confirm.
        if (Slot.ByteHash == ByteHash)
        {
            const FNameEntry* Entry = Shard.EntryPointers[Slot.ShardLocalId];
            if (Entry != nullptr &&
                Entry->GetLength() == static_cast<::uint16>(ByteLen) &&
                std::memcmp(Entry->GetBytes(), Utf8, static_cast<::SIZE_T>(ByteLen)) == 0)
            {
                return Slot.ShardLocalId;
            }
            // Hash collision: continue probing.
        }

        SlotIdx = (SlotIdx + 1) & Mask;
    }

    return FNamePoolSlot::kEmptySlot;
}

// ---------------------------------------------------------------------
// InsertEntry -- create a new entry and slot-table mapping.
// Caller must hold Shard.Lock in EXCLUSIVE mode.
// ---------------------------------------------------------------------
::uint32 FNamePool::InsertEntry(FNamePoolShard& Shard,
                                ::uint8 /*ShardId*/,
                                ::uint64 ByteHash,
                                const char* Utf8,
                                ::int32 ByteLen) noexcept
{
    // Lazy slot-table allocation.
    if (Shard.SlotCapacity == 0)
    {
        const ::SIZE_T Bytes = kInitialSlotCapacity * sizeof(FNamePoolSlot);
        Shard.Slots = static_cast<FNamePoolSlot*>(
            ::XCore::HAL::FMemory::MallocOrAbort(Bytes, alignof(FNamePoolSlot), ::XCore::HAL::FMemTag::Reflection));
        for (::SIZE_T I = 0; I < kInitialSlotCapacity; ++I)
        {
            Shard.Slots[I].ByteHash     = 0;
            Shard.Slots[I].ShardLocalId = FNamePoolSlot::kEmptySlot;
            Shard.Slots[I]._pad         = 0;
        }
        Shard.SlotCapacity = kInitialSlotCapacity;
        Shard.SlotCount    = 0;
    }

    // Check load factor; grow before insert.
    if ((Shard.SlotCount + 1) * kSlotLoadDenominator >= Shard.SlotCapacity * kSlotLoadNumerator)
    {
        GrowSlotTable(Shard);
    }

    // Reserve a fresh shard-local id.
    XPACT_CHECK(Shard.NextShardLocalId < kMaxShardLocalEntries);
    const ::uint32 NewId = Shard.NextShardLocalId++;

    // Allocate the entry record in the block pool.
    FNameEntry* NewEntry = AllocateEntryInPool(Shard, NewId, Utf8, ByteLen);

    // Grow entry-pointer table if needed.
    if (NewId >= Shard.EntryCapacity)
    {
        GrowEntryPointers(Shard);
    }
    Shard.EntryPointers[NewId] = NewEntry;

    // Insert into the slot table via linear probe.
    const ::SIZE_T Mask    = Shard.SlotCapacity - 1;
    ::SIZE_T       SlotIdx = static_cast<::SIZE_T>(ByteHash) & Mask;
    while (Shard.Slots[SlotIdx].ShardLocalId != FNamePoolSlot::kEmptySlot)
    {
        SlotIdx = (SlotIdx + 1) & Mask;
    }
    Shard.Slots[SlotIdx].ByteHash     = ByteHash;
    Shard.Slots[SlotIdx].ShardLocalId = NewId;
    ++Shard.SlotCount;

    return NewId;
}

// ---------------------------------------------------------------------
// AllocateEntryInPool -- bump-allocate an entry record from the tail
// 64 KB block; allocate a fresh block if the tail does not have room.
// ---------------------------------------------------------------------
FNameEntry* FNamePool::AllocateEntryInPool(FNamePoolShard& Shard,
                                           ::uint32 EntryId,
                                           const char* Utf8,
                                           ::int32 ByteLen) noexcept
{
    const ::SIZE_T EntrySize = ComputeFNameEntryAllocSize(static_cast<::SIZE_T>(ByteLen));
    XPACT_CHECK(EntrySize <= FNamePoolBlock::ArenaCapacity());

    // If the tail block does not have room, allocate a fresh block.
    if (Shard.TailBlock == nullptr ||
        Shard.TailBlock->UsedBytes + EntrySize > FNamePoolBlock::ArenaCapacity())
    {
        // Allocate raw block. The 64 KB block alignment matches the
        // FNameEntry 8-byte alignment requirement.
        void* Raw = ::XCore::HAL::FMemory::MallocOrAbort(
            kBlockSize, alignof(FNamePoolBlock), ::XCore::HAL::FMemTag::Reflection);

        FNamePoolBlock* NewBlock = static_cast<FNamePoolBlock*>(Raw);
        NewBlock->Next      = nullptr;
        NewBlock->UsedBytes = 0;
        NewBlock->_pad      = 0;

        if (Shard.TailBlock == nullptr)
        {
            // First-ever block in this shard.
            Shard.HeadBlock = NewBlock;
            Shard.TailBlock = NewBlock;
        }
        else
        {
            Shard.TailBlock->Next = NewBlock;
            Shard.TailBlock       = NewBlock;
        }
    }

    // Bump-allocate from the tail block.
    FNamePoolBlock* Block = Shard.TailBlock;
    unsigned char*  Cursor = Block->Arena + Block->UsedBytes;
    Block->UsedBytes += EntrySize;

    // Place the entry header + payload at the cursor.
    auto* Entry = reinterpret_cast<FNameEntry*>(Cursor);
    Entry->Header.EntryId = EntryId;
    Entry->Header.Length  = static_cast<::uint16>(ByteLen);

    // Copy the UTF-8 payload + write the trailing NUL terminator.
    std::memcpy(&Entry->Bytes[0], Utf8, static_cast<::SIZE_T>(ByteLen));
    Entry->Bytes[ByteLen] = '\0';

    return Entry;
}

// ---------------------------------------------------------------------
// GrowSlotTable -- double the shard's slot capacity and rehash.
// Caller must hold Shard.Lock in EXCLUSIVE mode.
// ---------------------------------------------------------------------
void FNamePool::GrowSlotTable(FNamePoolShard& Shard) noexcept
{
    const ::SIZE_T OldCapacity = Shard.SlotCapacity;
    const ::SIZE_T NewCapacity = OldCapacity == 0 ? kInitialSlotCapacity : OldCapacity * 2;
    XPACT_CHECK((NewCapacity & (NewCapacity - 1)) == 0);

    const ::SIZE_T Bytes  = NewCapacity * sizeof(FNamePoolSlot);
    auto* NewSlots = static_cast<FNamePoolSlot*>(
        ::XCore::HAL::FMemory::MallocOrAbort(Bytes, alignof(FNamePoolSlot), ::XCore::HAL::FMemTag::Reflection));
    for (::SIZE_T I = 0; I < NewCapacity; ++I)
    {
        NewSlots[I].ByteHash     = 0;
        NewSlots[I].ShardLocalId = FNamePoolSlot::kEmptySlot;
        NewSlots[I]._pad         = 0;
    }

    // Rehash every existing slot into the larger table.
    const ::SIZE_T NewMask = NewCapacity - 1;
    for (::SIZE_T OldIdx = 0; OldIdx < OldCapacity; ++OldIdx)
    {
        const FNamePoolSlot& Old = Shard.Slots[OldIdx];
        if (Old.ShardLocalId == FNamePoolSlot::kEmptySlot)
        {
            continue;
        }

        ::SIZE_T NewIdx = static_cast<::SIZE_T>(Old.ByteHash) & NewMask;
        while (NewSlots[NewIdx].ShardLocalId != FNamePoolSlot::kEmptySlot)
        {
            NewIdx = (NewIdx + 1) & NewMask;
        }
        NewSlots[NewIdx].ByteHash     = Old.ByteHash;
        NewSlots[NewIdx].ShardLocalId = Old.ShardLocalId;
    }

    // Swap in the new table and free the old.
    if (Shard.Slots != nullptr)
    {
        ::XCore::HAL::FMemory::Free(Shard.Slots);
    }
    Shard.Slots        = NewSlots;
    Shard.SlotCapacity = NewCapacity;
    // SlotCount is unchanged (same number of live entries).
}

// ---------------------------------------------------------------------
// GrowEntryPointers -- enlarge the shard's entry-pointer array.
// Caller must hold Shard.Lock in EXCLUSIVE mode.
// ---------------------------------------------------------------------
void FNamePool::GrowEntryPointers(FNamePoolShard& Shard) noexcept
{
    const ::SIZE_T OldCapacity = Shard.EntryCapacity;
    const ::SIZE_T NewCapacity = OldCapacity == 0 ? 16 : OldCapacity * 2;

    const ::SIZE_T NewBytes = NewCapacity * sizeof(const FNameEntry*);
    auto* NewPtrs = static_cast<const FNameEntry**>(
        ::XCore::HAL::FMemory::MallocOrAbort(NewBytes, alignof(const FNameEntry*), ::XCore::HAL::FMemTag::Reflection));

    // Copy existing pointers.
    for (::SIZE_T I = 0; I < OldCapacity; ++I)
    {
        NewPtrs[I] = Shard.EntryPointers[I];
    }
    // Zero the new tail.
    for (::SIZE_T I = OldCapacity; I < NewCapacity; ++I)
    {
        NewPtrs[I] = nullptr;
    }

    if (Shard.EntryPointers != nullptr)
    {
        ::XCore::HAL::FMemory::Free(Shard.EntryPointers);
    }
    Shard.EntryPointers = NewPtrs;
    Shard.EntryCapacity = NewCapacity;
}

// =====================================================================
// Public Init / Shutdown hooks (declared in Reflection/FName.h).
// =====================================================================

void __InitFNamePool() noexcept
{
    FNamePool::Get().Init();
}

void __ShutdownFNamePool() noexcept
{
    FNamePool::Get().Shutdown();
}

} // namespace XCore::Reflect
