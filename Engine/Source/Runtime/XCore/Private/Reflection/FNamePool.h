// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FNamePool.h -- the 256-shard sharded FName intern table (XCore-4b §4.2).
// =====================================================================
//
// XCore-4b Rev 3, Section 4.2 + 4.3 + 4.6 + 4.7.
//
// PRIVATE header. The FNamePool is the process-singleton intern table that
// backs every FName allocation. Consumers do NOT include this header; they
// interact via FName's public ctors and accessors (Public/Reflection/FName.h).
//
// SHARDING (§4.2 + §4.6).
//   * 256 shards (per Rev 3 §14 OPEN-1 RECOMMENDED: "256 shards; UE uses
//     1024 by default but XPact's smaller name population ~5k-10k makes
//     256 a better fit").
//   * Shard selection is the high 8 bits of the XXH3-64 byte hash.
//     FName.Index packs `(shard_id << 24) | shard_local_entry_id`.
//   * Each shard owns: its own FRWLock, an open-addressing hash table
//     (slot table) keyed by the 64-bit byte-hash, and a linked-list of
//     64 KB entry blocks (per FIX-25: Quest 3 L2 cache match).
//
// BLOCK POOL (§4.7).
//   * Each shard maintains a linked list of 64 KB blocks. New entries
//     bump-allocate from the tail block; a full block becomes read-only
//     and the shard allocates a fresh 64 KB block from FMemory::Malloc
//     tagged FMemTag::Reflection.
//   * Blocks are never freed for the lifetime of the process (matches
//     UE's discipline; intern-table grows logarithmically in unique
//     name count so cumulative footprint is bounded).
//   * Pointer stability is structural: an entry's address is fixed for
//     the lifetime of the process.
//
// LOST-RACE PROTOCOL (§4.3).
//   * The read path acquires the shard's FRWLock in SHARED mode and
//     probes the slot table. On hit, returns the existing entry's
//     index. The shared lock allows multiple readers concurrent.
//   * On miss, the read lock is released and an exclusive lock is
//     acquired. A second probe runs under the exclusive lock to detect
//     a racing writer that may have inserted the same bytes between
//     the read-lock release and the exclusive-lock acquire. On hit,
//     return the racing-writer's entry. On miss, allocate a new entry
//     and insert it into the slot table.
//
// NAME_None RESERVATION (§3 cyclic-risk).
//   * Shard 0 reserves shard-local entry id 0 for NAME_None at __Init
//     time. The reserved bytes are "None" (4 UTF-8 bytes + NUL) so a
//     `FName(0, 0).ToString()` produces "None" without a heap walk.
//   * No other Index is ever 0; the shard 0 slot table refuses to
//     accept a string that would hash to entry 0 (it always falls
//     through to slot 1+).
//
// SLOT TABLE (open-addressing).
//   * Each shard's slot table is a linear-probed open-addressing
//     hash table indexed by the 64-bit byte hash. Slot capacity
//     starts at 256 and doubles when load factor exceeds 0.75. The
//     slot entries store the 64-bit byte-hash and the 32-bit
//     entry-pool-relative offset (which combined with shard_id forms
//     the FName.Index).
//   * SwissTable-style algorithmic optimisations (control-byte
//     prefetch, 16-byte mirror tail) are NOT used here: the FNamePool
//     surface is allocation-bound (per-name 64 KB block alloc dominates)
//     and the simpler linear-probe shape is easier to audit. If
//     profiling shows the slot probe to be the bottleneck a future
//     revision can swap in TSet/TMap's SwissTable backing.
//
// THREAD SAFETY.
//   * Per-shard FRWLock. Multiple readers per shard; one writer per
//     shard; readers and writers across different shards never block
//     each other.
//   * Lock-order discipline: callers acquire at most one shard lock at
//     a time. No FNamePool method calls another FNamePool method
//     while holding a shard lock (avoids deadlock).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"
#include "HAL/FRWLock.h"
#include "Reflection/FName.h"
#include "Reflection/FNameEntry.h"

#include <atomic>
#include <cstddef>

namespace XCore::Reflect
{
    // -----------------------------------------------------------------
    // Tunables. All compile-time constants so the implementation has
    // zero per-call branch overhead.
    // -----------------------------------------------------------------

    // 256 shards. Selection is hash >> 56 (high 8 bits).
    inline constexpr ::SIZE_T kShardCount = 256;
    static_assert((kShardCount & (kShardCount - 1)) == 0,
                  "kShardCount must be a power of two for high-byte shard selection");

    // The shard id occupies the high 8 bits of FName.Index, leaving 24 bits
    // (16 777 216) for shard-local entry ids. Maximum total intern-table
    // capacity is therefore 256 * 16M = 4.3 billion entries -- the same as
    // FName.Index's uint32 range (by construction).
    inline constexpr ::uint32 kShardIdShift          = 24;
    inline constexpr ::uint32 kShardLocalIdMask      = (::uint32(1) << kShardIdShift) - 1;  // 0x00FF_FFFF
    inline constexpr ::uint32 kMaxShardLocalEntries  = ::uint32(1) << kShardIdShift;        // 16 777 216

    // -----------------------------------------------------------------
    // Saturation warning threshold (Rev 2 FIX-1 / MEDIUM-16).
    //
    // Each shard can hold at most kMaxShardLocalEntries entries. When a
    // shard's NextShardLocalId crosses 80% of the cap (i.e., the shard
    // has consumed 13 421 772 of its 16 777 216 slots), we emit a one-
    // shot Dev/Debug warning so the operator is given a chance to react
    // before reaching the hard XPACT_CHECK abort. The threshold is per-
    // shard; the warning fires AT MOST ONCE per shard for the process
    // lifetime (guarded by the per-shard SaturationWarned flag).
    //
    // Rationale for 80%: the FName intern-table sees logarithmic growth
    // in the unique-name population. 80% gives the operator ~3.3 M slots
    // of headroom (~20% of the cap) to investigate, rotate stale names,
    // or escalate the shard count before saturation hard-aborts the
    // process. A higher threshold (e.g., 95%) leaves too little wall-
    // clock time to react under hot-load conditions.
    //
    // The threshold is compile-time constant so the comparison is a
    // single 32-bit immediate compare; the branch is XPACT_UNLIKELY-
    // marked in the saturation-check site so the predictor learns the
    // fast path.
    // -----------------------------------------------------------------
    inline constexpr ::uint32 kShardSaturationWarnNumerator   = 4;   // 80% = 4/5
    inline constexpr ::uint32 kShardSaturationWarnDenominator = 5;
    inline constexpr ::uint32 kShardSaturationWarnThreshold   =
        static_cast<::uint32>(static_cast<::uint64>(kMaxShardLocalEntries) *
                              kShardSaturationWarnNumerator / kShardSaturationWarnDenominator);
    static_assert(kShardSaturationWarnThreshold == 13'421'772u,
                  "kShardSaturationWarnThreshold must equal 80% of kMaxShardLocalEntries "
                  "(13 421 772). The constant is wired into the saturation diagnostic and "
                  "any divergence would mis-report the threshold the operator was warned at.");

    // 64 KB entry-pool block size (§4.7 / FIX-25).
    inline constexpr ::SIZE_T kBlockSize = 64 * 1024;

    // Initial slot-table capacity per shard. 256 keeps the per-shard
    // table footprint modest while still avoiding immediate resize for
    // the common small-population case. Doubles on each grow.
    inline constexpr ::SIZE_T kInitialSlotCapacity = 256;

    // Load factor that triggers a slot-table grow. 0.75 mirrors the
    // SwissTable / abseil flat_hash_set threshold.
    inline constexpr ::SIZE_T kSlotLoadNumerator   = 3;
    inline constexpr ::SIZE_T kSlotLoadDenominator = 4;

    // -----------------------------------------------------------------
    // FNamePoolBlock -- one 64 KB block in a shard's entry-block list.
    //
    // The block carries a small header at offset 0 followed by the
    // entry-byte arena. Entries bump-allocate from the arena; when the
    // arena's bytes-remaining drops below the next entry's size, the
    // shard allocates a fresh block and chains it.
    //
    // sizeof(FNamePoolBlock) must NOT exceed kBlockSize (the block is
    // the allocation; the header lives at offset 0 of the alloc and
    // entries fill the remainder).
    // -----------------------------------------------------------------
    struct alignas(8) FNamePoolBlock
    {
        FNamePoolBlock* Next;       //  0  +8   next block in the shard's list (null = tail)
        ::SIZE_T        UsedBytes;  //  8  +8   bytes consumed by entries (from offset kHeaderSize)
        ::SIZE_T        _pad;       // 16  +8   pad to 24-byte header

        // Trailing arena spans `kBlockSize - kHeaderSize` bytes.
        static constexpr ::SIZE_T kHeaderSize = 24;
        unsigned char Arena[1];     // 24  +N   actual sizeof at allocation = kBlockSize - kHeaderSize

        [[nodiscard]] static constexpr ::SIZE_T ArenaCapacity() noexcept
        {
            return kBlockSize - kHeaderSize;
        }
    };

    static_assert(FNamePoolBlock::kHeaderSize == offsetof(FNamePoolBlock, Arena),
                  "FNamePoolBlock kHeaderSize must equal offsetof(Arena)");
    static_assert(alignof(FNamePoolBlock) == 8,
                  "FNamePoolBlock alignment must match FNameEntry alignment");

    // -----------------------------------------------------------------
    // FNamePoolSlot -- one slot in a shard's open-addressing hash table.
    //
    // EntryOffset == kEmptySlot signals an empty slot. The slot stores
    // the full 64-bit byte hash (for collision check) plus a 32-bit
    // shard-relative entry id (which combined with the shard id forms
    // FName.Index).
    // -----------------------------------------------------------------
    struct FNamePoolSlot
    {
        ::uint64 ByteHash;       //  0  +8   FXxh3-64(bytes, len, seed=0)
        ::uint32 ShardLocalId;   //  8  +4   index into the shard's entry table
        ::uint32 _pad;           // 12  +4   pad to 16-byte slot

        // Sentinel signalling "no entry here". The 24-bit ShardLocalId
        // space tops out at 0x00FF_FFFF; we use 0xFFFF_FFFF as the
        // empty-slot marker (cannot collide with any valid id).
        static constexpr ::uint32 kEmptySlot = 0xFFFF'FFFFu;
    };

    static_assert(sizeof(FNamePoolSlot) == 16,
                  "FNamePoolSlot must be 16 bytes (8 hash + 4 id + 4 pad)");

    // -----------------------------------------------------------------
    // FNamePoolShard -- one of the 256 shards.
    //
    // The shard owns its own FRWLock, slot table, entry-pointer table,
    // and 64 KB block list. The lock guards every mutable field; the
    // EntryPointers array is grown under the exclusive lock and read
    // under the shared lock.
    // -----------------------------------------------------------------
    struct FNamePoolShard
    {
        // Per-shard read/write lock. Reads acquire shared; writes
        // acquire exclusive.
        ::XCore::HAL::FRWLock Lock;

        // Slot table (open-addressing hash table keyed by ByteHash).
        // Allocated lazily on first insertion. Capacity is a power of
        // two; mask = Capacity - 1 is used for index computation.
        FNamePoolSlot* Slots          = nullptr;
        ::SIZE_T       SlotCapacity   = 0;    // 0 until first insert
        ::SIZE_T       SlotCount      = 0;    // number of non-empty slots

        // Entry-pointer table. The N-th entry in this table is the
        // FNameEntry* for shard-local id N. Grown under exclusive lock
        // when a new entry is interned.
        const FNameEntry** EntryPointers     = nullptr;
        ::SIZE_T           EntryCapacity     = 0;
        ::uint32           NextShardLocalId  = 0;

        // 64 KB block list head/tail. Head is the first block ever
        // allocated; Tail is where new entries currently land.
        FNamePoolBlock* HeadBlock = nullptr;
        FNamePoolBlock* TailBlock = nullptr;

        // Per-shard one-shot saturation warning flag (Rev 2 FIX-1 /
        // MEDIUM-16). Flipped from false to true the first time the
        // shard's NextShardLocalId crosses kShardSaturationWarnThreshold
        // (i.e., 80% of kMaxShardLocalEntries). Subsequent crossings
        // are no-ops so the diagnostic does not spam stderr.
        //
        // The field is read AND written ONLY under the shard's exclusive
        // lock (the InsertEntry write path is the only mutator); no
        // separate atomic is needed.
        bool SaturationWarned = false;
    };

    // -----------------------------------------------------------------
    // FNamePool -- the singleton intern table.
    //
    // Accessed via Get(). The first call constructs the pool's NAME_None
    // sentinel; subsequent calls are pure reads.
    //
    // The lock is fine-grained per-shard, so cross-shard inserts
    // and lookups never contend.
    // -----------------------------------------------------------------
    class FNamePool
    {
    public:
        // -------------------------------------------------------------
        // Get -- accessor for the process-singleton FNamePool.
        //
        // Backed by a function-local static; C++11 guarantees thread-
        // safe initialisation on first call. Callers may invoke Find /
        // Intern only after EInitPhase::PostStaticInit (the allocator
        // must be live for the 64 KB block allocations).
        // -------------------------------------------------------------
        [[nodiscard]] static FNamePool& Get() noexcept;

        // -------------------------------------------------------------
        // Init -- engine bootstrap. Reserves the NAME_None sentinel
        // at shard 0 / shard-local id 0. Idempotent.
        // -------------------------------------------------------------
        void Init() noexcept;

        // -------------------------------------------------------------
        // Shutdown -- engine teardown. Releases all 64 KB shard blocks,
        // all slot tables, all entry-pointer tables. Resets every shard
        // to the post-construction state.
        //
        // After Shutdown, the pool is unusable until Init runs again.
        // Idempotent (a second Shutdown call is a no-op).
        // -------------------------------------------------------------
        void Shutdown() noexcept;

        // -------------------------------------------------------------
        // Intern -- look up or insert a UTF-8 byte sequence.
        //
        // Returns the FName.Index value (which packs the shard id and
        // the shard-local entry id). The bytes are interned exactly
        // once; subsequent calls with the same bytes return the same
        // Index.
        //
        // An empty byte sequence (ByteLen == 0) returns kNoneIndex (0).
        // A ByteLen > kFNameMaxLength aborts via XPACT_CHECK.
        // -------------------------------------------------------------
        [[nodiscard]] ::uint32 Intern(const char* Utf8, ::int32 ByteLen) noexcept;

        // -------------------------------------------------------------
        // FindEntry -- return the FNameEntry* for a given Index.
        //
        // Index == 0 (NAME_None) returns a pointer to the reserved
        // "None" sentinel entry. Index pointing at an invalid slot
        // (uninhabited shard-local id) returns nullptr.
        //
        // NOT const: lazily runs Init() if the pool has never been
        // touched (so default-constructed FName.IsValid() works without
        // an explicit Init at engine bootstrap).
        // -------------------------------------------------------------
        [[nodiscard]] const FNameEntry* FindEntry(::uint32 Index) noexcept;

        // -------------------------------------------------------------
        // IsValidIndex -- true iff Index points at an existing entry.
        //
        // NOT const for the same reason as FindEntry (lazy init).
        // -------------------------------------------------------------
        [[nodiscard]] bool IsValidIndex(::uint32 Index) noexcept;

        // -------------------------------------------------------------
        // GetShardCount -- exposed for unit tests that verify the
        // 256-shard discipline (acceptance gate A1).
        // -------------------------------------------------------------
        [[nodiscard]] static constexpr ::SIZE_T GetShardCount() noexcept
        {
            return kShardCount;
        }

        // -------------------------------------------------------------
        // GetShardId -- exposed for unit tests that verify the shard
        // distribution (acceptance gate A1).
        //
        // Returns the shard index a given byte hash maps to.
        // -------------------------------------------------------------
        [[nodiscard]] static constexpr ::uint8 GetShardIdFromHash(::uint64 Hash) noexcept
        {
            return static_cast<::uint8>(Hash >> 56);
        }

        [[nodiscard]] static constexpr ::uint8 GetShardIdFromIndex(::uint32 Index) noexcept
        {
            return static_cast<::uint8>(Index >> kShardIdShift);
        }

        [[nodiscard]] static constexpr ::uint32 GetShardLocalIdFromIndex(::uint32 Index) noexcept
        {
            return Index & kShardLocalIdMask;
        }

        [[nodiscard]] static constexpr ::uint32 MakeIndex(::uint8 ShardId, ::uint32 ShardLocalId) noexcept
        {
            return (::uint32(ShardId) << kShardIdShift) | (ShardLocalId & kShardLocalIdMask);
        }

        // -------------------------------------------------------------
        // GetShardOccupancy -- diagnostic accessor exposing the
        // shard's current NextShardLocalId (Rev 2 FIX-1 / MEDIUM-16).
        //
        // Returns the count of shard-local entries that have been
        // allocated in the named shard. Exposed for the
        // ShardSaturationWarning unit test and for the post-mortem
        // diagnostic surface (operator can dump per-shard occupancy
        // to confirm balanced distribution).
        //
        // Reads NextShardLocalId under the shard's shared lock so the
        // value is consistent with the slot-table state at the moment
        // of the read. Concurrent inserts may have advanced
        // NextShardLocalId before the caller observes the return
        // value; that's expected.
        // -------------------------------------------------------------
        [[nodiscard]] ::uint32 GetShardOccupancy(::uint8 ShardId) noexcept;

    private:
        // -------------------------------------------------------------
        // FInsertPreAlloc -- Rev 2 FIX-2 / TC3 / B11 alloc-outside-lock
        // pre-allocation carrier.
        //
        // The Intern() write path predicts allocation needs under the
        // shard's shared lock, releases the shared lock, calls FMemory::
        // MallocOrAbort for each predicted slot OUTSIDE any FNamePool
        // shard lock, then acquires the exclusive lock with the pre-
        // allocated buffers in hand. This guarantees no FMemory call
        // happens while holding an FNamePool shard lock; the AB-BA
        // hazard (lock-order ShardLock -> Pool.Mutex inverted relative
        // to a hypothetical FName-using FMallocBinnedX) is structurally
        // eliminated.
        //
        // FIELDS (each nullable; null indicates "no pre-allocation for
        // this slot"):
        //   * Block             -- a 64 KB FNamePoolBlock allocation,
        //                          ready to be linked into the shard's
        //                          tail-block list if the tail is full.
        //   * SlotTableBuf      -- a sized slot-table allocation; the
        //                          ::SIZE_T SlotTableCapacity field is
        //                          the byte count.
        //   * EntryPointerBuf   -- a sized entry-pointer-table
        //                          allocation; EntryPointerCapacity is
        //                          the SIZE_T capacity.
        //
        // DESTRUCTOR: ReleaseUnused frees any pre-allocation that was
        // not consumed by the write path (e.g., the race-check found
        // an existing entry and no insert happened, OR the prediction
        // overestimated and the lock-protected state did not need
        // growth). The Intern() caller MUST invoke ReleaseUnused
        // before exiting; the pre-allocations are NOT RAII-managed
        // because the consume-vs-release decision must happen under
        // the lock.
        // -------------------------------------------------------------
        struct FInsertPreAlloc
        {
            FNamePoolBlock*    Block               = nullptr;
            FNamePoolSlot*     SlotTableBuf        = nullptr;
            ::SIZE_T           SlotTableCapacity   = 0;
            const FNameEntry** EntryPointerBuf     = nullptr;
            ::SIZE_T           EntryPointerCapacity= 0;

            // ReleaseUnused -- frees every non-null field. Called by
            // the Intern() caller AFTER the exclusive-lock scope exits;
            // any field that the write path consumed has been zeroed,
            // so this safely no-ops on consumed slots.
            void ReleaseUnused() noexcept;
        };

        // Singleton ctor/dtor. The FNamePool storage lives inside Get()'s
        // function-local static; the static is a private member-context
        // ctor call so it can access the private ctor without friending.
        FNamePool() noexcept = default;
        ~FNamePool() noexcept;

        FNamePool(const FNamePool&)            = delete;
        FNamePool& operator=(const FNamePool&) = delete;
        FNamePool(FNamePool&&)                 = delete;
        FNamePool& operator=(FNamePool&&)      = delete;

        // -------------------------------------------------------------
        // PredictInsertPreAlloc -- shared-lock prediction of allocation
        // needs (Rev 2 FIX-2 / TC3).
        //
        // Acquires Shard.Lock in SHARED mode, peeks at TailBlock /
        // SlotCapacity / SlotCount / EntryCapacity / NextShardLocalId,
        // and pre-allocates (OUTSIDE the lock once released) whatever
        // the write path would need given the observed state. The
        // predictions can become stale if a racing writer mutates the
        // shard between PredictInsertPreAlloc returning and the
        // exclusive lock being acquired; the write path handles staleness
        // by falling back to under-lock allocation (rare) or releasing
        // unused pre-allocations.
        //
        // Caller is responsible for calling ReleaseUnused on the
        // returned struct.
        // -------------------------------------------------------------
        void PredictInsertPreAlloc(FNamePoolShard& Shard,
                                   ::int32 ByteLen,
                                   FInsertPreAlloc& Out) noexcept;

        // -------------------------------------------------------------
        // Per-shard primitives. All `noexcept` and assume the caller
        // holds the appropriate lock (shared for find, exclusive for
        // insert/grow).
        // -------------------------------------------------------------

        // Lock-free probe of an existing entry. Returns kEmptySlot on miss.
        // Caller MUST hold Shard.Lock in shared OR exclusive mode.
        ::uint32 TryFindEntry(FNamePoolShard& Shard,
                              ::uint64 ByteHash,
                              const char* Utf8,
                              ::int32 ByteLen) const noexcept;

        // Insert a new entry. Allocates from the block pool, grows the
        // slot table if needed, returns the new shard-local entry id.
        // Caller MUST hold Shard.Lock in EXCLUSIVE mode.
        ::uint32 InsertEntry(FNamePoolShard& Shard,
                             ::uint8 ShardId,
                             ::uint64 ByteHash,
                             const char* Utf8,
                             ::int32 ByteLen,
                             FInsertPreAlloc& PreAlloc) noexcept;

        // Allocate an FNameEntry record from a shard's block pool.
        // Allocates a fresh 64 KB block if the current tail does not
        // have room. Returns a pointer to the placed entry header.
        FNameEntry* AllocateEntryInPool(FNamePoolShard& Shard,
                                        ::uint32 EntryId,
                                        const char* Utf8,
                                        ::int32 ByteLen,
                                        FInsertPreAlloc& PreAlloc) noexcept;

        // Grow the slot table to the next power-of-two capacity and
        // rehash every existing slot.
        // Consumes the slot-table pre-allocation from PreAlloc; if
        // PreAlloc.SlotTableBuf is null OR PreAlloc.SlotTableCapacity
        // does not match the predicted growth target, allocates fresh
        // under the lock as a fallback. The shared-lock-then-grow path
        // makes a stale prediction the rare case.
        void GrowSlotTable(FNamePoolShard& Shard,
                           FInsertPreAlloc& PreAlloc) noexcept;

        // Grow the entry-pointer table. Same pre-allocation contract
        // as GrowSlotTable.
        void GrowEntryPointers(FNamePoolShard& Shard,
                               FInsertPreAlloc& PreAlloc) noexcept;

    private:
        FNamePoolShard m_shards[kShardCount];

        // True once Init has run successfully. Atomic so the lazy
        // first-touch Init path is safe in concurrent code (acquire-load
        // on read, release-store on Init completion). Pair with the
        // shard 0 lock for the double-checked locking pattern.
        ::std::atomic<bool> m_isInitialised{false};

        // The reserved NAME_None entry lives in shard 0 / shard-local
        // id 0. We carry a direct pointer for the fast IsNone() ToString()
        // path so callers do not pay the shard-zero entry-pointer-table
        // walk on every dereference.
        const FNameEntry* m_noneEntry = nullptr;
    };

} // namespace XCore::Reflect
