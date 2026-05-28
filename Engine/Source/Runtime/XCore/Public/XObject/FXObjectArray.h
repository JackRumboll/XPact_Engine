// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectArray.h -- process-singleton global object table
// (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// XCoreXObject Rev 4 Section 3.3 ("FXObjectArray (the global object
// table)"). Sparse-array of `FXObjectArrayEntry` indexed by
// `XObject::InternalIndex`.
//
// ARCHITECTURE (per spec §3.3 + §9.1):
//
//   * SINGLE CONTIGUOUS allocation -- NOT UE's chunk-pointer-array
//     indirection. The collector's mark inner loop dereferences
//     `Entries[InternalIndex]` as a direct array index with no extra
//     pointer chase. UE divergence per spec §3.3 leading prose.
//
//   * ENTRIES ARE NEVER RELOCATED. The entry struct's address is
//     stable for the entire process lifetime once allocated -- a
//     concurrent reader holding a raw `FXObjectArrayEntry*` is safe
//     across a grow. This is a stronger invariant than a plain TArray
//     can provide (which relocates on grow). Implementation: the array
//     storage is built on FPlatformMemory's reserve/commit primitives.
//     We reserve a maximum-XObject-count VM range up front (256 MB =
//     8 388 608 entries; well beyond Foundation Prototype's ~50k
//     typical and Quest 3's 4 GB working-set budget) and commit pages
//     incrementally as the live count grows.
//
//   * GROW IN 1 MB INCREMENTS (32 768 entries per grow per spec §3.3
//     trailing prose). At ~50k typical XObjects this is 2 grows over
//     engine lifetime. Each grow `CommitVirtual`s another 1 MB chunk;
//     no allocation, no copy, no relocation.
//
//   * INDEX 0 IS RESERVED as the null sentinel. A freshly-zeroed
//     XWeakPtr / XObjectKey / XPtr with InternalIndex == 0 represents
//     "null"; the array's bootstrap reserves index 0 at construction
//     time with Object = nullptr and SerialNumber = 0 so the slot can
//     never be assigned to a real object. See spec §3.3 trailing prose.
//
// CONCURRENCY (per spec §3.3 + engine-wide lock discipline FIX-R2-X-NEW):
//
//   * FRWLock-protected. Reads (Get / GetUnchecked / ForEachObject)
//     acquire SHARED; mutations (AllocateEntry / FreeEntry / commit
//     grow) acquire EXCLUSIVE.
//
//   * The grow path is the only place where `Entries` itself is
//     observed in a transient state (after CommitVirtual returns but
//     before the capacity counter is bumped). The capacity counter is
//     a `std::atomic<int32_t>` with release-store at the end of grow
//     so SHARED readers using `Capacity() <= InternalIndex` as a
//     guard see a consistent view.
//
//   * Allocation-outside-exclusive-lock discipline: the entry storage
//     itself uses FPlatformMemory::CommitVirtual which does NOT route
//     through FMemory::Malloc (no global-allocator-pool mutex). The
//     FXObjectArray never holds its own lock while calling out to any
//     other lock-holding subsystem.
//
// API SURFACE (per spec §3.3 + Phase 5.b dispatch wording):
//
//   * AllocateEntry(XObject*)            -- one-shot: returns the
//                                            assigned int32 InternalIndex,
//                                            bumps SerialNumber on slot
//                                            reuse. PRIMARY API.
//   * ReserveSlot(uint32_t* OutSerial)   -- two-step: NewObject hot
//                                            path reserves the slot
//                                            first (returns index +
//                                            captured SerialNumber),
//                                            then BindObject after the
//                                            XObject header is filled.
//                                            Spec §3.3 + §3.5 hot path.
//   * BindObject(InternalIndex, XObject*) -- step 2 of the two-step.
//   * FreeEntry(InternalIndex)           -- marks slot free, bumps
//                                            SerialNumber so every
//                                            XWeakPtr captured before
//                                            free returns nullptr on
//                                            deref.
//   * GetObjectAtIndex(Idx, Serial)      -- weak-ptr deref entry point;
//                                            returns nullptr on serial-
//                                            mismatch.
//   * GetObjectAtIndexUnchecked(Idx)     -- known-live fast path;
//                                            skips the serial check.
//   * NumLive()                          -- live entry count.
//   * Capacity()                         -- committed entry capacity.
//   * ForEachObject(Visitor)             -- iterate every live entry
//                                            exactly once.
//
// HOT-RELOAD: NO virtual methods. The singleton is a function-local
// static; the body's reserved VM range persists through hot-reload of
// downstream modules (the VM reservation is process-global; only
// individual entries vacated by class replacement are touched).
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FRWLock.h"
#include "XObject/FXObjectArrayEntry.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

// Forward declaration. XObject is defined in XObject/XObject.h.
namespace XCore { class XObject; }

namespace XCore
{

    // -----------------------------------------------------------------
    // Tunables (compile-time constants per the no-runtime-CVar
    // discipline of XCore-4a's allocator surface).
    // -----------------------------------------------------------------

    // Per-grow commit size: 1 MB / 32 = 32 768 entries per grow.
    // Spec §3.3 trailing prose: "the array grows in 1 MB increments".
    inline constexpr ::SIZE_T kFXObjectArrayCommitBytes      = ::SIZE_T(1) << 20;   // 1 MB
    inline constexpr ::int32  kFXObjectArrayEntriesPerCommit =
        static_cast<::int32>(kFXObjectArrayCommitBytes / sizeof(FXObjectArrayEntry));

    // Reserved (but not yet committed) VM range. 256 MB = 8 388 608
    // entries. At the Foundation Prototype scale (~50k typical) this
    // is 168x headroom; at the Quest 3 ARM64 4 GB budget this is 6%.
    // VM reservation is essentially free on 64-bit OSes (page-table
    // entries only; no physical RAM until commit).
    //
    // PRINCIPLED CHOICE per Prime Directive: reserve generously up
    // front so the never-relocate invariant holds without ever needing
    // a re-reservation path. UE's FUObjectArray uses a chunked
    // structure (chunk size = 2^16); we trade that complexity for the
    // simpler single-base-pointer indexing at the cost of one large
    // VM reservation.
    inline constexpr ::SIZE_T kFXObjectArrayReservedBytes  = ::SIZE_T(256) << 20;   // 256 MB
    inline constexpr ::int32  kFXObjectArrayMaxEntries     =
        static_cast<::int32>(kFXObjectArrayReservedBytes / sizeof(FXObjectArrayEntry));

    // The XPACT_XOBJECTARRAY_ENTRY_LAYOUT_TAG sizeof must agree.
    static_assert(sizeof(FXObjectArrayEntry) == 32,
                  "FXObjectArray relies on FXObjectArrayEntry == 32 bytes "
                  "for its 32k-entries-per-1MB grow geometry; ABI lock.");

    // Index 0 is the null sentinel. The bootstrap pre-reserves the
    // slot with Object = nullptr; AllocateEntry never returns 0.
    inline constexpr ::int32 kFXObjectArrayNullIndex = 0;

    // -----------------------------------------------------------------
    // FXObjectArray -- the process-singleton global object table.
    //
    // Accessed via `FXObjectArray::Get()`. The function-local static
    // initialiser runs at first call (typically PostStaticInit when
    // XObjectAllocatorBootstrap installs the allocator hook).
    //
    // No copy, no move, no public ctor: the singleton owns its VM
    // reservation for the entire process lifetime; multiple instances
    // are nonsensical.
    // -----------------------------------------------------------------
    class FXObjectArray
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        //
        // Magic-static (C++11 guarantees thread-safe initialisation
        // of function-local statics). The ctor reserves the 256 MB VM
        // range and commits the first 1 MB chunk; failures abort via
        // FPlatformMemory's standard error path.
        // -------------------------------------------------------------
        [[nodiscard]] static FXObjectArray& Get() noexcept;

        // -------------------------------------------------------------
        // Test-only reset. Drops every live entry, decommits all
        // slabs back to the initial 1 MB committed region, resets
        // counters to zero, and re-installs index 0 as the null
        // sentinel.
        //
        // The reset is destructive: any XObject still pointed to by an
        // entry is "forgotten" (its raw memory in the FXObjectAllocator
        // is NOT freed; the test harness is responsible for tearing
        // down both subsystems if both have residual state).
        //
        // ONLY used by the Phase 5.b test suite to ensure each test
        // starts from a clean state; production callers MUST NOT call
        // this method. The name + the function-name comment document
        // the constraint; XBT static analysis can add a banned-symbol
        // rule in a future phase.
        // -------------------------------------------------------------
        void __ResetForTests() noexcept;

        // =============================================================
        // Slot allocation -- one-shot variant (Phase 5.b dispatch
        // wording's primary API).
        //
        // Atomically:
        //   1. Acquire the EXCLUSIVE lock.
        //   2. If the free list is non-empty, pop the head; increment
        //      its SerialNumber so any prior weak-handle deref returns
        //      nullptr; assign Object = ForObject; return the index.
        //   3. Otherwise consume the next bump-allocated slot. If the
        //      bump pointer is at the committed boundary, CommitVirtual
        //      the next 1 MB chunk first (rare; ~2-3 grows over engine
        //      lifetime at Foundation Prototype scale).
        //   4. Return the assigned int32 InternalIndex.
        //
        // ForObject MAY be nullptr (matches the spec's spec §3.5 NewObject
        // hot path: the entry is reserved BEFORE the XObject header is
        // populated; the caller binds the real pointer after). The
        // simpler convenience pattern is ForObject != nullptr +
        // skip-the-bind, which we support symmetrically.
        // =============================================================
        ::int32 AllocateEntry(XObject* ForObject) noexcept;

        // =============================================================
        // Slot allocation -- two-step variant (spec §3.3 + §3.5
        // NewObject hot path).
        //
        // ReserveSlot bumps SerialNumber + assigns Object = nullptr;
        // returns the index AND the captured SerialNumber via
        // OutSerialNumber. The NewObject hot path uses this so the
        // XObject's own SerialNumber field can be filled to match
        // BEFORE BindObject installs the back-pointer (the consistency
        // invariant the weak-ptr deref relies on).
        //
        // BindObject installs the back-pointer; pre-conditions:
        //   * InternalIndex was returned by a prior ReserveSlot call
        //     on this same thread.
        //   * The slot has NOT been Free'd in the meantime.
        //   * Object's own SerialNumber field has been populated to
        //     match the value returned by ReserveSlot.
        //
        // Both methods take the EXCLUSIVE lock (the slot table is the
        // shared protected resource); the caller MUST not hold a
        // separate read lock when calling either.
        // =============================================================
        ::int32 ReserveSlot(::uint32* OutSerialNumber) noexcept;
        void    BindObject(::int32 InternalIndex, XObject* Object) noexcept;

        // =============================================================
        // Slot release.
        //
        // FreeEntry:
        //   * Sets Object = nullptr.
        //   * Bumps SerialNumber so every XWeakPtr captured before the
        //     free returns nullptr on its next deref.
        //   * Pushes the slot index onto the LIFO free list so the
        //     next AllocateEntry consumes recently-freed slots first
        //     (cache-warm locality).
        //
        // EXCLUSIVE lock acquired.
        // =============================================================
        void FreeEntry(::int32 InternalIndex) noexcept;

        // =============================================================
        // Lookups.
        //
        // GetObjectAtIndex: weak-ptr deref entry point. Returns the
        // bound XObject* iff the captured SerialNumber matches the
        // current entry's SerialNumber. Returns nullptr on:
        //   * InternalIndex == 0 (null sentinel).
        //   * InternalIndex out of committed range.
        //   * SerialNumber mismatch (slot was reused since the weak
        //     handle was captured).
        //   * Entry's Object pointer is nullptr (rare; transient state
        //     between ReserveSlot and BindObject).
        //
        // GetObjectAtIndexUnchecked: known-live fast path for callers
        // that already know the entry is valid (e.g., the GC mark loop
        // iterating Entries directly). Skips the SerialNumber check
        // and the range check; returns Entries[InternalIndex].Object
        // verbatim. UB on invalid index.
        //
        // SHARED lock acquired on GetObjectAtIndex; UNLOCKED read for
        // the Unchecked variant (the caller is responsible for the
        // happens-before ordering against the array's grow). The
        // Unchecked variant is intended ONLY for the GC mark sweep
        // which runs under its own safe-point protocol per spec §5.5.
        // =============================================================
        [[nodiscard]] XObject* GetObjectAtIndex(
            ::int32 InternalIndex,
            ::uint32 ExpectedSerialNumber) const noexcept;

        [[nodiscard]] XObject* GetObjectAtIndexUnchecked(
            ::int32 InternalIndex) const noexcept;

        // =============================================================
        // Counts.
        // =============================================================

        // Live entries (excludes the null sentinel at index 0 + every
        // freed slot still on the free list). Atomic read; no lock.
        [[nodiscard]] ::int32 NumLive() const noexcept
        {
            return m_numLive.load(::std::memory_order_acquire);
        }

        // Committed capacity (number of entries the array can hold
        // without a fresh CommitVirtual call). Atomic read; no lock.
        [[nodiscard]] ::int32 Capacity() const noexcept
        {
            return m_committedCount.load(::std::memory_order_acquire);
        }

        // =============================================================
        // Iteration.
        //
        // Visits each live entry (Object != nullptr) exactly once in
        // index order. The visitor receives `(int32_t InternalIndex,
        // XObject* Object)`. Iteration is performed under the SHARED
        // lock so concurrent allocates are blocked for the duration;
        // visitors should be cheap (e.g., emitting a single counter
        // bump or appending to a pre-sized buffer).
        //
        // For the GC sweep + diagnostic walks ("dump all live
        // XObjects") + the FXObjectAllocator's per-class instance
        // counting in GetStats.
        // =============================================================
        template <typename Visitor>
        void ForEachObject(Visitor&& v) const noexcept
        {
            ::XCore::HAL::FScopedReadLock ReadLock(m_lock);
            const ::int32 LocalCapacity = m_committedCount.load(::std::memory_order_acquire);
            for (::int32 Index = 1; Index < LocalCapacity; ++Index)
            {
                XObject* Object = m_entries[Index].Object;
                if (Object != nullptr)
                {
                    v(Index, Object);
                }
            }
        }

    private:
        // Private ctor; constructed exactly once via Get(). The ctor
        // reserves the 256 MB VM range and commits the first 1 MB
        // chunk; if either operation fails the ctor calls
        // FPlatformMisc::Abort with a diagnostic (the engine cannot
        // start without the global object table).
        FXObjectArray() noexcept;

        // Non-virtual destructor; the singleton lives for the process
        // lifetime so the destructor only runs at clean shutdown.
        // Releases the VM reservation back to the OS.
        ~FXObjectArray() noexcept;

        FXObjectArray(const FXObjectArray&)            = delete;
        FXObjectArray(FXObjectArray&&)                 = delete;
        FXObjectArray& operator=(const FXObjectArray&) = delete;
        FXObjectArray& operator=(FXObjectArray&&)      = delete;

        // -------------------------------------------------------------
        // Commit the next 1 MB chunk. Pre-condition: the caller holds
        // m_lock exclusively. Aborts on commit failure (the engine
        // cannot grow past the committed boundary).
        // -------------------------------------------------------------
        void CommitNextChunkUnderLock() noexcept;

        // -------------------------------------------------------------
        // Per-slot free-list pool growth helper. Pre-condition: the
        // caller holds m_lock exclusively. The free-list is a
        // contiguous TArray-equivalent that grows via FMemory::Malloc;
        // the array doubles capacity. Pre-allocated outside the lock
        // would be ideal per the engine-wide lock discipline; here the
        // grow is structurally rare (only when MANY simultaneous
        // FreeEntry calls have queued up unfreed entries) and the
        // FMemory call is to FMemTag::XObject which has no inverse
        // dependency on the FXObjectArray.
        // -------------------------------------------------------------
        void GrowFreeListUnderLock() noexcept;

        // The base pointer of the reserved 256 MB VM range. Casts to
        // `FXObjectArrayEntry*` for index access. nullptr only between
        // failed-reserve abort and process exit (which is unreachable;
        // the ctor aborts on reserve failure).
        FXObjectArrayEntry*               m_entries;

        // Highest committed-and-usable entry count. Atomic so a
        // SHARED reader can do `Capacity() > InternalIndex` without
        // taking the lock; the release-store at the end of
        // CommitNextChunkUnderLock pairs with the acquire-load here.
        ::std::atomic<::int32>            m_committedCount;

        // Bump-allocation high-water mark (the index of the next slot
        // to assign when the free list is empty). Always <=
        // m_committedCount.
        ::int32                           m_bumpHead;

        // Live entry count (Object != nullptr; excludes index 0).
        ::std::atomic<::int32>            m_numLive;

        // Free-list LIFO of recently-released slot indices. The next
        // AllocateEntry consumes from here first.
        ::int32*                          m_freeList;
        ::int32                           m_freeListCount;
        ::int32                           m_freeListCapacity;

        // Reader/writer lock for the slot table + free list + commit
        // counter mutations.
        mutable ::XCore::HAL::FRWLock     m_lock;
    };

} // namespace XCore
