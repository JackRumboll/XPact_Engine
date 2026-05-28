// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGCCardTable.cpp -- card-table singleton body (Phase 5.f).
// =====================================================================
//
// XCoreXObject Rev 4 §4.5 + §5.1 + Rev 2 FIX-A-MIN-39 (FMemTag::
// Reflection attribution).
//
// Implements the lazy-allocated, FRWLock-protected card table singleton
// declared in Public/XObject/FXObjectGCCardTable.h.
//
// MarkCardDirty is inline in the header (XPACT_FORCEINLINE); only the
// non-hot-path bodies (Initialize, __ResetForTests, MarkCardRangeDirty,
// IsSaturated) live here.
//
// =====================================================================

#include "XObject/FXObjectGCCardTable.h"
#include "XObject/XInsightsEmitHelpers.h"   // Phase 5.k: telemetry emit hooks.

#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"

#include <cstring>      // std::memset

namespace XCore
{

// ---------------------------------------------------------------------
// Singleton accessor.
//
// Magic-static. The ctor zero-initialises the struct; the card array
// is allocated lazily on first Initialize call. The function-local
// static guarantees thread-safe initialisation per [stmt.dcl]/3.
// ---------------------------------------------------------------------
FXObjectGCCardTable& FXObjectGCCardTable::Get() noexcept
{
    static FXObjectGCCardTable s_instance;
    return s_instance;
}

// ---------------------------------------------------------------------
// Ctor / dtor. The ctor zero-initialises every field (the card array
// is allocated lazily on first Initialize). The dtor frees the card
// array if it was allocated.
// ---------------------------------------------------------------------
FXObjectGCCardTable::FXObjectGCCardTable() noexcept
    : m_heapBaseAddr(0)
    , m_heapByteSize(0)
    , m_totalCards(0)
    , m_cards(nullptr)
    , m_dirtyCount(0)
    // m_saturationLatched is initialised inline in the header to
    // `false` via the brace-init `{false}` syntax. Phase 5.k: the
    // member-init list omits it deliberately so the brace-init is the
    // single source of truth.
    , m_lock()
{
}

FXObjectGCCardTable::~FXObjectGCCardTable() noexcept
{
    // The destructor runs at process exit (after main returns) on
    // the magic-static path. By then FMemory may already have begun
    // teardown; we still attempt the Free because FMemory's
    // shutdown-tolerant Free is a no-op rather than an error.
    if (m_cards != nullptr)
    {
        ::XCore::HAL::FMemory::Free(m_cards);
        m_cards = nullptr;
    }
}

// ---------------------------------------------------------------------
// Initialize -- allocate the card array for a heap range.
//
// EXCLUSIVE lock acquired. Per the engine-wide lock discipline
// (FRWLock.h Principle 2), allocations happen OUTSIDE the lock; we
// pre-compute the byte count, allocate without lock, then integrate
// under the exclusive lock. The integrate step is a pointer-swap +
// counter reset; the lock window is microseconds.
// ---------------------------------------------------------------------
void FXObjectGCCardTable::Initialize(
    const void*   HeapBase,
    ::std::size_t HeapByteSize) noexcept
{
    // Pre-condition checks (defence-in-depth; the spec contract says
    // the caller honours them, but the cost of a single XPACT_CHECK
    // is one cycle on Dev/Debug and zero on Shipping).
    XPACT_CHECK(HeapBase != nullptr);
    XPACT_CHECK(HeapByteSize > 0);

    // Round HeapByteSize UP to the nearest card boundary so the card
    // array fully covers the range. This is "infallible-style": the
    // caller passes the intended range; we round up to avoid the
    // trailing-byte off-by-one where the final card would only
    // partially cover the heap.
    const ::std::size_t TotalCards =
        (HeapByteSize + kCardSize - 1) >> kCardShift;

    // Allocate the new card array (zero-filled; see below).
    // FMemTag::Reflection per Rev 2 FIX-A-MIN-39: "card table is
    // reflection-runtime metadata, not gameplay heap; tagged
    // Reflection for proper telemetry attribution".
    //
    // We allocate OUTSIDE the lock (the malloc has its own internal
    // locking; nesting our lock would risk an AB-BA against the
    // allocator's pool mutex; see FRWLock.h Principle 2).
    void* const Raw = ::XCore::HAL::FMemory::MallocOrAbort(
        TotalCards,
        /*Align=*/8,
        ::XCore::HAL::FMemTag::Reflection);
    ::std::uint8_t* const NewCards = static_cast<::std::uint8_t*>(Raw);

    // Zero-fill: every card starts clean per the contract.
    ::std::memset(NewCards, kCardClean, TotalCards);

    // Integrate under EXCLUSIVE lock. The swap is a pointer write +
    // counter resets; the lock window is microseconds.
    ::std::uint8_t* OldCards = nullptr;
    {
        ::XCore::HAL::FScopedWriteLock Lock(m_lock);

        // If we are re-initialising with the SAME range, the new
        // allocation is redundant: free it and short-circuit.
        if (m_cards != nullptr
            && m_heapBaseAddr ==
                reinterpret_cast<::std::uintptr_t>(HeapBase)
            && m_heapByteSize == HeapByteSize)
        {
            // Idempotent re-init with the same range. Just clear the
            // existing card array so the contract "all cards clean
            // post-init" holds.
            ::std::memset(m_cards, kCardClean, m_totalCards);
            m_dirtyCount.store(0, ::std::memory_order_relaxed);
            // Phase 5.k: re-arm the saturation telemetry latch.
            m_saturationLatched.store(false, ::std::memory_order_relaxed);
            // Discard the new allocation.
            OldCards = NewCards;
        }
        else
        {
            // Replace the array (and the metadata).
            OldCards            = m_cards;
            m_cards             = NewCards;
            m_heapBaseAddr      =
                reinterpret_cast<::std::uintptr_t>(HeapBase);
            m_heapByteSize      = HeapByteSize;
            m_totalCards        = TotalCards;
            m_dirtyCount.store(0, ::std::memory_order_relaxed);
            // Phase 5.k: re-arm the saturation telemetry latch.
            m_saturationLatched.store(false, ::std::memory_order_relaxed);
        }
    }

    // Free outside the lock (per FRWLock.h Principle 2).
    if (OldCards != nullptr)
    {
        ::XCore::HAL::FMemory::Free(OldCards);
    }
}

// ---------------------------------------------------------------------
// Test-only reset. Frees the card array; returns to the freshly-
// constructed state.
// ---------------------------------------------------------------------
void FXObjectGCCardTable::__ResetForTests() noexcept
{
    ::std::uint8_t* OldCards = nullptr;
    {
        ::XCore::HAL::FScopedWriteLock Lock(m_lock);
        OldCards        = m_cards;
        m_cards         = nullptr;
        m_heapBaseAddr  = 0;
        m_heapByteSize  = 0;
        m_totalCards    = 0;
        m_dirtyCount.store(0, ::std::memory_order_relaxed);
        // Phase 5.k: re-arm the saturation telemetry latch.
        m_saturationLatched.store(false, ::std::memory_order_relaxed);
    }
    if (OldCards != nullptr)
    {
        ::XCore::HAL::FMemory::Free(OldCards);
    }
}

// ---------------------------------------------------------------------
// MarkCardRangeDirty -- bulk-store barrier path.
//
// Computes the card index for the first byte of the range + walks
// every card touched. Each card touched is unconditionally set to
// kCardDirty; the m_dirtyCount counter is bumped once per card
// transition (not once per write to a dirty card).
//
// NO LOCK ACQUIRED. Same wait-free contract as MarkCardDirty per spec
// §4.5.
// ---------------------------------------------------------------------
void FXObjectGCCardTable::MarkCardRangeDirty(
    const void*   Address,
    ::std::size_t ByteLength) noexcept
{
    if (ByteLength == 0)
    {
        return;
    }
    const ::std::uintptr_t StartAddr =
        reinterpret_cast<::std::uintptr_t>(Address);
    const ::std::uintptr_t EndAddr  = StartAddr + ByteLength;
    const ::std::uintptr_t Base     = m_heapBaseAddr;
    const ::std::size_t    HeapSize = m_heapByteSize;
    const ::std::size_t    Total    = m_totalCards;
    ::std::uint8_t* const  Cards    = m_cards;

    if (Cards == nullptr || Total == 0)
    {
        return;
    }

    // Clamp the range to the registered heap range. We use unsigned
    // subtraction with explicit underflow / overflow checks to keep
    // the math simple + branch-free in the common case.
    if (EndAddr <= Base)
    {
        // Entirely below the heap base.
        return;
    }
    const ::std::uintptr_t ClampedStart =
        (StartAddr < Base) ? Base : StartAddr;
    const ::std::uintptr_t HeapEnd = Base + HeapSize;
    if (ClampedStart >= HeapEnd)
    {
        return;
    }
    const ::std::uintptr_t ClampedEnd =
        (EndAddr > HeapEnd) ? HeapEnd : EndAddr;

    // Range now [ClampedStart, ClampedEnd) within heap.
    const ::std::size_t FirstCard =
        static_cast<::std::size_t>(ClampedStart - Base) >> kCardShift;
    // Last card index is ((ClampedEnd - 1) - Base) >> kCardShift.
    // ClampedEnd > ClampedStart >= Base, so the subtraction is
    // non-negative.
    const ::std::size_t LastCard =
        static_cast<::std::size_t>(ClampedEnd - 1 - Base) >> kCardShift;

    // Defensive bounds clamp (LastCard < Total). m_heapByteSize was
    // rounded UP to a card boundary at Initialize, so LastCard < Total
    // always; this check is belt-and-braces.
    const ::std::size_t HighCard = (LastCard < Total) ? LastCard : (Total - 1);

    ::std::size_t NewlyDirtied = 0;
    for (::std::size_t I = FirstCard; I <= HighCard; ++I)
    {
        if (Cards[I] == kCardClean)
        {
            Cards[I] = kCardDirty;
            ++NewlyDirtied;
        }
    }
    if (NewlyDirtied != 0)
    {
        m_dirtyCount.fetch_add(NewlyDirtied, ::std::memory_order_relaxed);
        // Phase 5.k: bulk-write path consults the saturation transition
        // hook. The one-shot latch in CheckAndEmitSaturationTransition
        // ensures at most one emit per GC cycle even if MarkCardRangeDirty
        // is called many times past the threshold.
        (void)CheckAndEmitSaturationTransition();
    }
}

// ---------------------------------------------------------------------
// IsSaturated -- predicate for the GC trigger.
//
// Returns true iff dirty-card ratio is >= kSaturationThresholdPercent /
// 100. The ratio is computed without floating point: (Dirty * 100) >=
// (Total * kSaturationThresholdPercent).
// ---------------------------------------------------------------------
bool FXObjectGCCardTable::IsSaturated() const noexcept
{
    const ::std::size_t Dirty = m_dirtyCount.load(::std::memory_order_relaxed);
    const ::std::size_t Total = m_totalCards;
    if (Total == 0)
    {
        return false;
    }
    // Multiplication doesn't overflow on size_t for realistic ranges
    // (Dirty <= Total <= 8M cards on 4 GB; 8M * 100 = 800M; fits in
    // 32-bit even).
    return (Dirty * 100) >= (Total * kSaturationThresholdPercent);
}

// ---------------------------------------------------------------------
// CheckAndEmitSaturationTransition -- one-shot saturation telemetry
// (Phase 5.k; spec §10.12).
//
// Atomically transitions the m_saturationLatched flag from false to
// true on the first call past the saturation threshold; emits the
// RememberedSetSaturation event once. Subsequent calls see the latch
// already set and short-circuit. The latch is re-armed at the next
// ForEachDirtyCardAndClear / Initialize / __ResetForTests boundary.
//
// Concurrency: the CAS on m_saturationLatched is the synchronisation
// primitive that guarantees exactly-one emit even under concurrent
// callers (the bulk MarkCardRangeDirty paths from multiple writer
// threads + the GC trigger's explicit consult). The emit itself runs
// AFTER the CAS wins, so a losing thread observes the latch already
// set and does not emit.
// ---------------------------------------------------------------------
bool FXObjectGCCardTable::CheckAndEmitSaturationTransition() noexcept
{
    if (!IsSaturated())
    {
        // Not yet saturated; no transition.
        return false;
    }

    // Saturation predicate is true. Try to CAS the latch from false
    // to true; only the winner emits.
    bool Expected = false;
    if (!m_saturationLatched.compare_exchange_strong(
            Expected, true,
            ::std::memory_order_acq_rel,
            ::std::memory_order_relaxed))
    {
        // Lost the CAS; another caller already emitted in this cycle.
        return false;
    }

    // We won the CAS; compute the payload + emit.
    const ::std::size_t Dirty = m_dirtyCount.load(::std::memory_order_relaxed);
    const ::std::size_t Total = m_totalCards;
    const double SaturationPct = (Total == 0)
        ? 0.0
        : (static_cast<double>(Dirty) * 100.0 / static_cast<double>(Total));

    ::XCore::HAL::XInsightsEmitHelpers::EmitGCRememberedSetSaturation(
        /*DirtyCardCount=*/static_cast<::std::int64_t>(Dirty),
        /*TotalCardCount=*/static_cast<::std::int64_t>(Total),
        /*SaturationPct=*/ SaturationPct,
        /*CycleId=*/       0);  // Phase 5.k Phase-1: 0; Phase 5.g sets real id.

    return true;
}

} // namespace XCore
