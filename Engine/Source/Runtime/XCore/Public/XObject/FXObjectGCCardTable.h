// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectGCCardTable.h -- card-table generational scan-reduction
// (XCoreXObject Rev 4 §4.5 + §5.1).
// =====================================================================
//
// XCoreXObject Rev 4 §4.5 (card-table generational scan-reduction) +
// §5.1 (card-table layout) + Rev 2 FIX-A-MIN-39 (FMemTag::Reflection
// attribution) + Rev 3 FIX-N-R2-7 (50% saturation threshold
// calibration note).
//
// Contract Rev 13.9 ABI tag XPACT_XGC_CARDTABLE_LAYOUT_TAG pins the
// runtime byte layout: "byte-per-card flat array; card size = 512
// bytes; max heap = 4 GB; card table = 8 MB; clean = 0x00, dirty =
// 0x01".
//
// =====================================================================
//
// DESIGN (per spec §4.5 + §5.1):
//
// The card table is a flat byte array, one byte per 512-byte card of
// XObject heap. Card index from address:
//
//     CardIndex = (Address - HeapBase) >> kCardShift
//
// where kCardShift = 9 (log2(512)). The 512-byte card size is chosen
// per spec §15 O2: "matches typical L2 cache line group on Quest 3
// ARM64; bigger cards mean more false-sharing of dirty marks, smaller
// cards mean larger card table. 512 is the empirically common middle".
//
// One byte (not one bit) per card per spec §4.5 trailing prose:
// "simpler memory ordering on ARM64; the byte cost is negligible:
// 1 MB of XObject heap = 2 KB card table = 0.2% overhead".
//
// On Quest 3 with a 4 GB nominal XObject heap budget, the card table
// is 8 MB. This is allocated lazily on first Initialize call (the
// reservation cost is one-time + amortised over the engine's lifetime).
//
// =====================================================================
//
// HEAP-RANGE NOTE (Phase 5.f reconciliation):
//
// The spec §5.1 envisions a single contiguous reserved VM range for
// the XObject heap. The current FXObjectAllocator (Phase 5.b) uses a
// slab-based allocator over FMallocBinnedX, which produces multiple
// disjoint slabs (NOT a single contiguous range).
//
// Phase 5.f's card table is parameterised by [HeapBase, HeapBase +
// HeapByteSize) at Initialize-time. Out-of-range MarkCardDirty calls
// are silently ignored (clamped to bounds checking; no card is dirtied
// for an address outside the registered range).
//
// This is the principled posture for Phase 5.f:
//   * The card table is a separable component with a well-defined
//     contract.
//   * The FXObjectAllocator integration (Phase 5.b' or a follow-up)
//     will either (a) reserve a contiguous VM range up-front matching
//     spec §5.1 design, OR (b) extend the card table to handle
//     multiple disjoint ranges.
//   * For Phase 5.f, the unit-tested contract is "address within the
//     registered range marks the corresponding card; address outside
//     the range is a no-op".
//
// TODO(Phase 5.f' / Phase 5.h integration): wire the FXObjectAllocator
// to either reserve a contiguous heap range or register each slab
// with the card table. Until then, callers from outside the unit
// test surface (the write barrier in particular) ARE within range
// only when their slot pointer resolves into a slab the card table
// was initialised to cover.
//
// =====================================================================
//
// CONCURRENCY (per spec §4.5):
//
// MarkCardDirty is wait-free: a single non-atomic byte store. No
// atomic is needed for CORRECTNESS because:
//
//   * The card table's contract is "if the mutator wrote to a card,
//     the GC observes the dirty mark on its NEXT safepoint". The
//     safepoint handshake (Phase 5.g) provides the synchronisation
//     boundary; any store the mutator made before the safepoint is
//     observable to the GC after the handshake.
//
//   * Two threads both writing 0x01 to the same byte cannot tear the
//     value: 0x01 is a single byte, atomic at the hardware level on
//     every supported platform (Win64 x86, Linux x86, Android ARM64
//     all guarantee single-byte atomicity).
//
//   * The clear path (ForEachDirtyCardAndClear) is GC-internal and
//     runs ONLY during the GC's safepoint window per spec §4.2 step 2;
//     no mutator concurrently writes during the clear.
//
// The exception: m_dirtyCount IS atomic (relaxed RMW on every
// MarkCardDirty) because it's read by the saturation predicate from
// the GC trigger thread, possibly concurrently. The cost is
// negligible (relaxed atomic increment on a uint64 is ~3 cycles on
// modern hardware).
//
// =====================================================================
//
// SATURATION (per Rev 3 FIX-N-R2-7):
//
// When dirty-card count exceeds kSaturationThreshold (50% of total
// cards), the collector may fall back to a full-heap scan instead of
// a card-table-walk. The 50% threshold is calibrated against
// Foundation Prototype criterion (b) <50ms on Quest 3: the
// 50%-dirty-card scan time must complete within budget.
//
// Phase 5.f exposes IsSaturated() as the predicate; the Phase 5.g
// trigger reads it to decide between minor (card-walk) and full-heap
// (no-card-walk) scan.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FRWLock.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace XCore
{
    // -----------------------------------------------------------------
    // FXObjectGCCardTable -- process-singleton card table.
    //
    // Accessed via FXObjectGCCardTable::Get(). The singleton's ctor
    // does NOT allocate; the card array is allocated lazily on first
    // Initialize call.
    //
    // No virtual methods.
    // -----------------------------------------------------------------
    class FXObjectGCCardTable
    {
    public:
        // =============================================================
        // Tunables (compile-time constants per the no-runtime-CVar
        // discipline).
        // =============================================================

        // Card size in bytes. 512 per spec §15 O2; 2^9 -> kCardShift.
        static constexpr ::std::size_t kCardSize  = 512;
        static constexpr ::std::size_t kCardShift = 9;

        // Card byte values (per Contract Rev 13.9
        // XPACT_XGC_CARDTABLE_LAYOUT_TAG: "clean = 0x00, dirty = 0x01").
        static constexpr ::std::uint8_t kCardClean = 0x00;
        static constexpr ::std::uint8_t kCardDirty = 0x01;

        // Saturation threshold percent (Rev 3 FIX-N-R2-7; initial 50%;
        // calibrated against Foundation Prototype criterion (b)).
        static constexpr ::std::size_t kSaturationThresholdPercent = 50;

        // Static contract: kCardSize must match the shift.
        static_assert((static_cast<::std::size_t>(1) << kCardShift) == kCardSize,
                      "XGC card-size / card-shift consistency lock: "
                      "(1 << kCardShift) must equal kCardSize.");
        static_assert(kCardSize == 512,
                      "XGC card-size lock (per XPACT_XGC_CARDTABLE_LAYOUT_TAG).");
        static_assert(kCardShift == 9,
                      "XGC card-shift lock (per XPACT_XGC_CARDTABLE_LAYOUT_TAG).");

        // =============================================================
        // Singleton accessor.
        //
        // Magic-static. The ctor zero-initialises the struct; the card
        // array is allocated lazily on first Initialize call.
        // =============================================================
        [[nodiscard]] static FXObjectGCCardTable& Get() noexcept;

        // =============================================================
        // Initialize -- allocate the card array for a heap range.
        //
        // Called from FXObjectAllocator at __Init (PostStaticInit
        // phase) once the heap range is known. The card array is
        // allocated via FMemory::MallocOrAbort with FMemTag::Reflection
        // per Rev 2 FIX-A-MIN-39 (the card table is reflection-runtime
        // metadata, not gameplay heap).
        //
        // The card array size is HeapByteSize / kCardSize bytes. For
        // the 4 GB Quest 3 budget this is 8 MB.
        //
        // PRE-CONDITIONS:
        //   * HeapBase != nullptr.
        //   * HeapByteSize > 0.
        //   * HeapByteSize is a multiple of kCardSize (or rounded up).
        //
        // POST-CONDITIONS:
        //   * GetTotalCards() == ceil(HeapByteSize / kCardSize).
        //   * All cards are clean (kCardClean).
        //   * GetDirtyCardCount() == 0.
        //
        // RE-INITIALISATION:
        //   * Calling Initialize a second time with the same range is
        //     idempotent (the card array is retained).
        //   * Calling Initialize with a DIFFERENT range frees the old
        //     card array + allocates a new one. Test harnesses use
        //     this to reset state between fixtures.
        //
        // EXCLUSIVE lock acquired for the duration.
        // =============================================================
        void Initialize(const void* HeapBase, ::std::size_t HeapByteSize) noexcept;

        // =============================================================
        // Test-only reset. Frees the card array; returns to the
        // freshly-constructed state. ONLY used by the Phase 5.f test
        // suite.
        // =============================================================
        void __ResetForTests() noexcept;

        // =============================================================
        // MarkCardDirty -- mark the card containing Address as dirty.
        //
        // HOT PATH. Called from the write barrier on every store to
        // an XObject** slot.
        //
        // FAST PATH:
        //   1. Compute CardIndex = (Address - HeapBase) >> kCardShift.
        //   2. Bounds-check CardIndex < m_totalCards (silently ignore
        //      out-of-range addresses; see "Heap-range note" above).
        //   3. If the card byte is already kCardDirty, return (no
        //      counter bump).
        //   4. Otherwise: write kCardDirty + relaxed-RMW bump
        //      m_dirtyCount.
        //
        // The "already dirty" short-circuit saves the counter bump on
        // repeated stores to the same card (typical: 5-10 ref props
        // per XObject; same card).
        //
        // NO LOCK ACQUIRED. Per spec §4.5: "the byte cost is
        // negligible; the GC consumes at safepoint" -- the safepoint
        // handshake (Phase 5.g) sequences the consumer; the producer
        // is unsynchronised.
        // =============================================================
        XPACT_FORCEINLINE void MarkCardDirty(const void* Address) noexcept
        {
            // Range check + index compute. The bounds branch is the
            // common-case predicted-taken (the slot IS in the heap
            // range during normal operation).
            const ::std::uintptr_t Addr =
                reinterpret_cast<::std::uintptr_t>(Address);
            const ::std::uintptr_t Base = m_heapBaseAddr;
            // Wraparound-safe range check via unsigned subtraction:
            // if Addr < Base then (Addr - Base) underflows to a huge
            // value > m_heapByteSize.
            const ::std::uintptr_t Offset = Addr - Base;
            if (XPACT_UNLIKELY(Offset >= m_heapByteSize))
            {
                return;
            }
            const ::std::size_t CardIndex =
                static_cast<::std::size_t>(Offset) >> kCardShift;
            // Fast-path: already dirty -> skip the counter bump.
            // Volatile-style read via std::atomic_ref is unnecessary
            // here because the byte read is safe (single-byte atomic
            // at hardware) and the worst case is one extra counter
            // bump on a card just-marked by another thread.
            if (m_cards[CardIndex] == kCardDirty)
            {
                return;
            }
            m_cards[CardIndex] = kCardDirty;
            m_dirtyCount.fetch_add(1u, ::std::memory_order_relaxed);
        }

        // =============================================================
        // MarkCardRangeDirty -- mark every card spanning [Address,
        // Address + ByteLength) as dirty.
        //
        // For memcpy of XObject* arrays / TArray<XPtr<T>> reallocation.
        // Per spec §5.4 trailing prose, the bulk-store barrier path
        // walks each card touched by the range.
        //
        // Range is clamped to the registered heap range; out-of-range
        // bytes are silently ignored (same posture as MarkCardDirty).
        // =============================================================
        void MarkCardRangeDirty(const void* Address, ::std::size_t ByteLength) noexcept;

        // =============================================================
        // ForEachDirtyCardAndClear -- GC mark-phase consumer.
        //
        // Visits every dirty card index, invokes the visitor, and
        // clears the card. Returns the count of dirty cards processed.
        //
        // Visitor signature: `void(size_t CardIndex)`. The visitor is
        // invoked with the card index (0-based), NOT the byte address;
        // callers compute the byte range as
        // [HeapBase + CardIndex * kCardSize,
        //  HeapBase + (CardIndex + 1) * kCardSize).
        //
        // Called ONLY from the GC safepoint (Phase 5.g) so no mutator
        // is concurrently dirtying cards. EXCLUSIVE lock acquired
        // (defence-in-depth against mid-cycle Initialize / ResetForTests).
        // =============================================================
        template<typename Visitor>
        ::std::size_t ForEachDirtyCardAndClear(Visitor&& V) noexcept
        {
            ::XCore::HAL::FScopedWriteLock Lock(m_lock);
            ::std::size_t Cleared = 0;
            ::std::uint8_t* const Cards = m_cards;
            const ::std::size_t Total = m_totalCards;
            for (::std::size_t I = 0; I < Total; ++I)
            {
                if (Cards[I] != kCardClean)
                {
                    V(I);
                    Cards[I] = kCardClean;
                    ++Cleared;
                }
            }
            // Bulk decrement the dirty-count by the number of cards
            // cleared. Relaxed is correct: ForEachDirtyCardAndClear
            // runs under the safepoint so no concurrent MarkCardDirty
            // race exists. (The fetch_sub is required only because
            // MarkCardDirty uses relaxed RMW on the same counter; the
            // GC-side decrement keeps the counter consistent for the
            // next cycle's IsSaturated check.)
            m_dirtyCount.fetch_sub(Cleared, ::std::memory_order_relaxed);
            return Cleared;
        }

        // =============================================================
        // IsSaturated -- predicate for the GC trigger (Rev 3 FIX-N-R2-7).
        //
        // Returns true iff GetDirtyCardCount() >= GetTotalCards() *
        // kSaturationThresholdPercent / 100.
        //
        // When true, the GC may choose to do a full-heap scan instead
        // of a card-walk per spec §4.5 "Failure mode: remembered-set
        // saturation".
        //
        // CHEAP: two atomic loads + one multiply + one compare.
        // =============================================================
        [[nodiscard]] bool IsSaturated() const noexcept;

        // =============================================================
        // Diagnostic accessors.
        // =============================================================

        [[nodiscard]] XPACT_FORCEINLINE
        ::std::size_t GetTotalCards() const noexcept
        {
            return m_totalCards;
        }

        [[nodiscard]] XPACT_FORCEINLINE
        ::std::size_t GetDirtyCardCount() const noexcept
        {
            return m_dirtyCount.load(::std::memory_order_relaxed);
        }

        [[nodiscard]] XPACT_FORCEINLINE
        const void* GetHeapBase() const noexcept
        {
            return reinterpret_cast<const void*>(m_heapBaseAddr);
        }

        [[nodiscard]] XPACT_FORCEINLINE
        ::std::size_t GetHeapByteSize() const noexcept
        {
            return m_heapByteSize;
        }

    private:
        FXObjectGCCardTable() noexcept;
        ~FXObjectGCCardTable() noexcept;

        FXObjectGCCardTable(const FXObjectGCCardTable&)            = delete;
        FXObjectGCCardTable(FXObjectGCCardTable&&)                 = delete;
        FXObjectGCCardTable& operator=(const FXObjectGCCardTable&) = delete;
        FXObjectGCCardTable& operator=(FXObjectGCCardTable&&)      = delete;

        // -------------------------------------------------------------
        // Heap base as uintptr_t for branch-free pointer math on the
        // hot path. Initialised by Initialize; read by MarkCardDirty
        // without lock (the value is immutable after Initialize until
        // the next Initialize / __ResetForTests under the exclusive
        // lock).
        // -------------------------------------------------------------
        ::std::uintptr_t  m_heapBaseAddr;       //  8 bytes

        // -------------------------------------------------------------
        // Heap byte-size. Stored as size_t for the unsigned-subtraction
        // bounds check in MarkCardDirty.
        // -------------------------------------------------------------
        ::std::size_t     m_heapByteSize;       //  8 bytes

        // -------------------------------------------------------------
        // Total card count = m_heapByteSize / kCardSize.
        // -------------------------------------------------------------
        ::std::size_t     m_totalCards;         //  8 bytes

        // -------------------------------------------------------------
        // Card array. m_cards[i] == kCardClean | kCardDirty.
        // nullptr until first Initialize call. Allocated via
        // FMemory::MallocOrAbort with FMemTag::Reflection (Rev 2
        // FIX-A-MIN-39).
        // -------------------------------------------------------------
        ::std::uint8_t*   m_cards;              //  8 bytes

        // -------------------------------------------------------------
        // Atomic running count of dirty cards. Incremented by
        // MarkCardDirty (relaxed RMW); decremented by
        // ForEachDirtyCardAndClear (relaxed RMW under the exclusive
        // lock).
        //
        // The counter is APPROXIMATE under concurrent producers (two
        // threads marking the same clean card race: both observe
        // kCardClean, both write kCardDirty, both fetch_add). The
        // overcount is bounded by the number of concurrent producers
        // racing on the same card simultaneously; in practice this
        // is ~1-2 per card per GC cycle. The saturation predicate
        // tolerates this (a saturation threshold of 50% has natural
        // hysteresis well beyond +1/+2 overcounts).
        // -------------------------------------------------------------
        ::std::atomic<::std::size_t> m_dirtyCount;   //  8 bytes

        // -------------------------------------------------------------
        // Reader/writer lock protecting Initialize +
        // __ResetForTests + ForEachDirtyCardAndClear (which clears the
        // array under EXCLUSIVE).
        //
        // MarkCardDirty does NOT acquire the lock (per spec §4.5
        // wait-free contract). This is safe because:
        //   * MarkCardDirty reads m_heapBaseAddr / m_heapByteSize /
        //     m_cards without lock; these are immutable between
        //     Initialize / __ResetForTests boundaries.
        //   * Initialize / __ResetForTests acquire EXCLUSIVE; no
        //     mutator concurrently calls MarkCardDirty across the
        //     boundary (the GC trigger sequences these via the
        //     safepoint handshake).
        // -------------------------------------------------------------
        mutable ::XCore::HAL::FRWLock m_lock;        // 64 bytes
    };

    // -----------------------------------------------------------------
    // ABI / size lock for the card-table struct.
    //
    // The exact size is:
    //   m_heapBaseAddr   :  8 bytes
    //   m_heapByteSize   :  8 bytes
    //   m_totalCards     :  8 bytes
    //   m_cards          :  8 bytes
    //   m_dirtyCount     :  8 bytes (atomic<size_t>)
    //   m_lock           : 64 bytes (FRWLock, alignof 16)
    //   total            : 40 (data) + 64 (lock) = 104 bytes;
    //                       alignof 16 (from FRWLock) -> rounds to 112.
    //
    // FRWLock has alignof 16 which dominates the struct's alignof.
    // The 8-byte members preceding it pack to 40 bytes; the lock then
    // adds 64 bytes at offset 48 (after 8 bytes of padding to align
    // to 16). Total 48 + 64 = 112 bytes. The static_assert below
    // pins it; any layout drift will be caught here + at the
    // XPACT_VERIFY_XOBJECT_LAYOUT macro.
    //
    // The struct is NOT part of the cross-DLL ABI lock set per Phase
    // 5.f (the singleton accessor is the surface; the in-process layout
    // is internal). The size lock is still useful for catching
    // accidental drift.
    // -----------------------------------------------------------------
    static_assert(sizeof(FXObjectGCCardTable) == 112,
                  "FXObjectGCCardTable size lock: 40 bytes of scalar "
                  "state + 8 bytes alignment pad + 64 bytes FRWLock = "
                  "112 bytes. Any change is a deliberate ABI bump; "
                  "update XPACT_VERIFY_XOBJECT_LAYOUT() and this lock.");
    static_assert(alignof(FXObjectGCCardTable) == 16,
                  "FXObjectGCCardTable alignof lock: 16 (dominated by "
                  "FRWLock's 16-byte alignment).");

} // namespace XCore
