// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectAllocator.h -- the XObject heap (XCoreXObject Rev 4 §3).
// =====================================================================
//
// XCoreXObject Rev 4 Section 3 ("The XObject Heap (Allocator)"). The
// FXObjectAllocator is the process-singleton size-class-pool allocator
// layered on top of XCore-4a's FMallocBinnedX as a backing-store
// consumer (per spec §3.1 design rationale + critical decision 1).
//
// TIERING (per spec §3.1):
//
//   Tier 1 (XCore-4a) -- FMallocBinnedX manages OS virtual memory in
//                         per-bin reserved ranges. Each allocation we
//                         request from FMemory::Malloc with
//                         FMemTag::XObject is backed by an
//                         FMallocBinnedX slab.
//
//   Tier 2 (this spec) -- FXObjectAllocator reserves slabs from
//                         FMallocBinnedX and bins them by size class.
//                         Each size class owns multiple slabs; cells
//                         within a slab are uniform width.
//
//   Tier 3 (this spec) -- Within each size class, free cells are
//                         segregated by FClass*. Each FClass registered
//                         via RegisterClassPool gets its own free-list
//                         of cells of the right width. AllocateRaw
//                         pops from the class-specific free-list to
//                         preserve type locality (cells reused by the
//                         SAME class are always sized exactly right
//                         for the next instance of that class, which
//                         bounds per-cell waste).
//
// SIZE-CLASS SCHEDULE (per spec §3.2 table; Rev 1 baseline):
//
//   Class | Size (B) | Typical use                       | Slab (KB)
//   ------+----------+-----------------------------------+----------
//     0   |     64   | XComponent leaves, XScenarioNode  |    64
//     1   |     96   | Most XComponents                  |    64
//     2   |    128   | XActor (root)                     |    64
//     3   |    192   | XActor with inlined components    |    64
//     4   |    256   | Larger custom XActor              |    64
//     5   |    384   | XScenario root, XGameMode         |    64
//     6   |    512   | XWorld, XGameInstance, XLevel     |   128
//     7   |    768   | Large editor types                |   128
//     8   |   1024   | XPackage descriptors              |   128
//     9   |  > 1024  | XAsset variable-size              | LARGE
//
//   The 9th index is the large-object path: one slab per allocation.
//
//   Size-class fit rule per spec §3.2: a request for S bytes maps to
//   the smallest size class >= S, AND requests within 1.25x of a size
//   class share a pool (a 100-byte object goes in the 128-byte class
//   because 100 * 1.25 = 125 <= 128).
//
// PER-FClass SUB-POOLS (per spec §3.4 + §3.5):
//
//   * RegisterClassPool(FClass*) is called by FClass::Link at first
//     reflection-registration. The allocator records the
//     {FClass, SizeClassIndex} mapping so AllocateRaw can dispatch in
//     O(1).
//
//   * Each FClass-segregated sub-pool maintains a free-list of cells
//     of the right width. The free-list is a singly-linked LIFO
//     in-place at the cells themselves (the first 8 bytes of a free
//     cell are the next-free-cell pointer).
//
//   * Slabs are owned by the size-class pool, not by the FClass
//     sub-pool. Multiple FClass sub-pools at the same size class
//     share slabs; the within-slab free-list intrusively threads
//     through the cells.
//
//   * RebindClassPool(OldClass, NewClass) migrates ownership of every
//     cell currently tracked under OldClass to NewClass. The cells
//     themselves do NOT move; only the sub-pool ownership rebinds.
//     Per FIX-A-MIN-40 + spec §3.6.
//
// SCENARIO BOUNDARY (per spec §3.6):
//
//   * BeginScenarioBoundary opens a scope; allocations within the
//     scope are tagged with the scope's scenario name so the GC's
//     scenario-unload protocol can identify cells eligible for the
//     mark-region clearing optimisation (whole sub-pools released to
//     the size-class free-list in O(1) without per-cell reachability
//     checks).
//
//   * EndScenarioBoundary closes the scope. Per the
//     ReleaseClassPool path (spec §3.6 implementation prose), the
//     allocator walks the scenario-scoped sub-pools and returns
//     them to the size-class free-list.
//
//   * Phase 5.b ships the scope bookkeeping + the per-cell tagging;
//     the actual mark-region clearing requires the FXObjectCollector
//     (Phase 5.h) so the SCAN over "escaped references" can run.
//     Until then EndScenarioBoundary is documentation-only -- the
//     scope name is captured for telemetry; the heap-side action is
//     a no-op until 5.h provides the reachability oracle.
//
// COALESCENCE (per spec §3.6 + FIX-A-MED-24):
//
//   * CoalesceIdleSlabs is the engineer-station idle-time entry
//     point. It walks the size-class pools, identifies under-
//     utilised slabs (live count below a CVar-tunable threshold;
//     default 25%), evacuates their cells to other slabs, and
//     returns the empty slab to FMallocBinnedX.
//
//   * Trainee builds never call this. Long-running engineer
//     sessions call it from idle hooks. The FXObjectArray
//     pointer-stability invariant is preserved: only XObject
//     instances pointed at by FXObjectArray entries can be safely
//     evacuated, and the evacuation rebinds the InternalIndex's
//     entry's Object pointer to the new cell.
//
//   * Phase 5.b ships the slab-emptying scan + return-to-allocator
//     path; cell evacuation (the actual MEMCPY + rebind of the
//     FXObjectArray.Object pointer) is structurally implemented but
//     gated until the FXObjectAllocator can coordinate with the
//     GC (which prevents the collector from observing a mid-move
//     state). Until 5.h ships, CoalesceIdleSlabs only returns
//     fully-empty slabs (live count == 0) -- the evacuation path is
//     covered by tests but the production trigger is documented as
//     "Phase 5.h gated".
//
// CONCURRENCY (per spec §3.7 + engine-wide lock-discipline FIX-R2-X-NEW):
//
//   * FRWLock protected. The lock is acquired SHARED for AllocateRaw
//     when the class pool's free-list is non-empty (the fast path; no
//     contention with other allocators). The lock is acquired
//     EXCLUSIVE for slab-grow (slow path), Deallocate, RegisterClassPool,
//     RebindClassPool, BeginScenarioBoundary, EndScenarioBoundary,
//     and CoalesceIdleSlabs.
//
//   * Cross-thread Deallocate is permitted; the spec §3.7 cross-thread
//     reclaim path is the same EXCLUSIVE lock pass through the
//     deallocate routine. Per-thread TLS caches (spec §3.7 trailing
//     prose) are a future optimisation; Phase 5.b ships the
//     no-TLS-cache baseline (one shared lock per allocate) and
//     documents the TODO for Phase 5.b' or later.
//
//   * Per the engine-wide lock-discipline contract: the
//     FMemory::Malloc call from CommitNewSlab is OUTSIDE the
//     exclusive lock. CommitNewSlab uses the standard
//     "compute-need-under-shared / allocate-without-lock /
//     integrate-under-exclusive / release-unused-on-race" pattern
//     mirroring FNamePool::PredictInsertPreAlloc.
//
// HOT-RELOAD: NO virtual methods anywhere on the allocator surface.
// All dispatch is via direct calls on the singleton; the FClass
// pointer is the key but its layout is locked at Contract Rev 13.9.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "HAL/FRWLock.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocatorStats.h"

#include <atomic>
#include <cstddef>
#include <cstdint>

// Forward declarations.
namespace XCore::Reflect { struct FClass; }

namespace XCore
{

    // -----------------------------------------------------------------
    // Tunables (compile-time per the no-runtime-CVar discipline).
    // -----------------------------------------------------------------

    // Size-class schedule per spec §3.2 table.
    inline constexpr ::int32 kFXObjectAllocatorNumSizeClasses = 10;

    // Number of "normal" classes (the 10th is the large-object path).
    inline constexpr ::int32 kFXObjectAllocatorNumNormalClasses = 9;

    // Per-size-class cell width in bytes. Index kFXObjectAllocatorNumNormalClasses
    // sentinel indicates the large-object path (no fixed cell width).
    inline constexpr ::SIZE_T kFXObjectAllocatorSizeClasses[
        kFXObjectAllocatorNumSizeClasses] =
    {
        64,    96,   128,   192,   256,   384,
        512,   768,  1024,
        // Sentinel: anything > 1024 routes to the large-object path.
        // The value is never compared as a cell width.
        ::SIZE_T(0),
    };

    // Per-size-class slab byte count per spec §3.2 table. Slabs are
    // requested from FMemory::Malloc as one allocation; subdivided into
    // cells of `kFXObjectAllocatorSizeClasses[i]` width.
    inline constexpr ::SIZE_T kFXObjectAllocatorSlabBytes[
        kFXObjectAllocatorNumSizeClasses] =
    {
        ::SIZE_T(64) << 10,  // 64 KB
        ::SIZE_T(64) << 10,
        ::SIZE_T(64) << 10,
        ::SIZE_T(64) << 10,
        ::SIZE_T(64) << 10,
        ::SIZE_T(64) << 10,
        ::SIZE_T(128) << 10, // 128 KB
        ::SIZE_T(128) << 10,
        ::SIZE_T(128) << 10,
        // Sentinel: large-object slabs are exactly the requested size,
        // not a fixed slab size.
        ::SIZE_T(0),
    };

    // Size-class fit rule constants per spec §3.2: an allocation within
    // 1.25x of a size class may share its pool. Multiplied by 4 and
    // divided by 5 (i.e., 0.8x) to express "the SMALLEST size class
    // such that ceil(Size * 0.8) <= Class". Stored as a rational so the
    // 1.25x bound is exact.
    inline constexpr ::int32 kFXObjectAllocatorFitRuleNumerator   = 4;
    inline constexpr ::int32 kFXObjectAllocatorFitRuleDenominator = 5;

    // Idle-slab coalescence threshold per FIX-A-MED-24: a slab whose
    // live-cell count is BELOW this percent (default 25%) is a
    // coalescence candidate. The Phase 5.b body only acts on FULLY
    // empty slabs (live == 0) per the gated note above; the threshold
    // is published here for the Phase 5.h continuation.
    inline constexpr ::int32 kFXObjectAllocatorCoalesceThresholdPercent = 25;

    // Free-list head sentinel: end of intrusive singly-linked free
    // chain.
    inline constexpr ::SIZE_T kFXObjectAllocatorFreeListEnd = ::SIZE_T(0);

    // -----------------------------------------------------------------
    // FXObjectAllocator -- the process-singleton XObject heap.
    //
    // Accessed via `FXObjectAllocator::Get()`. The function-local-
    // static initialiser runs at first call (typically PostStaticInit
    // when XObjectAllocatorBootstrap installs the s_XObjectAllocator
    // hook).
    //
    // No copy, no move, no public ctor.
    // -----------------------------------------------------------------
    class FXObjectAllocator
    {
    public:
        // -------------------------------------------------------------
        // Singleton accessor.
        //
        // Magic-static. The ctor initialises the size-class pool
        // metadata + the per-FClass sub-pool map; it does NOT
        // pre-allocate slabs (the first AllocateRaw triggers a slab
        // commit lazily).
        // -------------------------------------------------------------
        [[nodiscard]] static FXObjectAllocator& Get() noexcept;

        // -------------------------------------------------------------
        // Test-only reset.
        //
        // Drops every slab + every registered FClass pool +
        // scenario-boundary state, then returns to the freshly-
        // constructed state. ONLY used by the Phase 5.b test suite.
        // -------------------------------------------------------------
        void __ResetForTests() noexcept;

        // =============================================================
        // AllocateRaw -- the 3-arg s_XObjectAllocator hook handler.
        //
        // Per spec §3.5 NewObject hot-path:
        //   1. Map (Size, Align) to a SizeClassIndex via SizeToSizeClass.
        //   2. Look up the FClass sub-pool via the registered class
        //      mapping. If ClassDescriptor is nullptr (bootstrap path
        //      per Phase 5.a' note: "Caller obligation: pass nullptr
        //      for the FClass argument until XCoreXObject's
        //      FXObjectAllocator owns the hook. NewObject<T> in
        //      XCoreXObject Phase 5.b populates the real FClass"),
        //      the allocation routes through a shared no-class sub-
        //      pool (correct but loses the per-class fragmentation
        //      bound; documented as a transient state until callers
        //      pass the real FClass).
        //   3. Pop a cell from the sub-pool's free-list. If empty,
        //      grow the pool (allocate a fresh slab + thread its cells
        //      onto the free-list under the EXCLUSIVE lock).
        //   4. Return the cell pointer.
        //
        // The returned pointer is uninitialised (the NewObject hot
        // path placement-news the XObject into it). Alignment is
        // honored: cells are naturally aligned at the size-class
        // boundary; the requested `Align` is verified (NOT enforced;
        // the caller is responsible for ensuring `Align <=
        // SizeClassWidth` since the size-class width IS the alignment
        // of the cell).
        //
        // The method is `noexcept`; the spec NewObject is "infallible-
        // style" (asserts on OOM via the standard FMemory::Malloc
        // abort path). The `Try` variant lands at a later phase per
        // FIX-A-MED-29.
        // =============================================================
        void* AllocateRaw(
            ::SIZE_T Size,
            ::SIZE_T Align,
            const ::XCore::Reflect::FClass* ClassDescriptor) noexcept;

        // Static dispatch trampoline for the s_XObjectAllocator hook
        // registration. Forwards to Get().AllocateRaw. The hook
        // signature requires a free function pointer; this static
        // wrapper bridges it onto the singleton.
        static void* AllocateRawStatic(
            ::SIZE_T Size,
            ::SIZE_T Align,
            const ::XCore::Reflect::FClass* ClassDescriptor) noexcept;

        // =============================================================
        // Deallocate -- by-pointer free.
        //
        // Per spec §3.4 trailing prose: routes via reverse-lookup of
        // the cell's owning slab. The lookup is a binary search over
        // the sorted-by-base slab pointer table per size class (the
        // same shape FMallocBinnedX uses for its PoolIndexFromPtr).
        //
        // Pre-conditions:
        //   * Storage was previously returned by AllocateRaw on this
        //     same FXObjectAllocator instance.
        //   * No live XObject references the cell (the caller is
        //     responsible for tearing down + unregistering from
        //     FXObjectArray before Deallocate; we do NOT chase the
        //     XObject header here).
        //
        // EXCLUSIVE lock acquired.
        // =============================================================
        void Deallocate(void* Storage) noexcept;

        // =============================================================
        // RegisterClassPool -- pre-create a type-segregated free-list.
        //
        // Called by FClass::Link at first reflection-registration. The
        // allocator records the {FClass, SizeClassIndex} mapping (the
        // SizeClass is derived from the FClass's PropertiesSize +
        // MinAlignment) so subsequent AllocateRaw(s, a, cls) calls can
        // dispatch in O(1).
        //
        // Idempotent: a second call with the same FClass is a no-op.
        // EXCLUSIVE lock acquired.
        // =============================================================
        void RegisterClassPool(const ::XCore::Reflect::FClass* ClassDescriptor) noexcept;

        // =============================================================
        // RebindClassPool -- hot-reload class replacement.
        //
        // Per FIX-A-MIN-40 + spec §3.6: when a downstream module is
        // hot-reloaded and a class is replaced, the allocator must
        // rebind every cell currently tracked under OldClass to
        // NewClass. The cells themselves do NOT move; only the
        // sub-pool ownership transfers.
        //
        // Pre-conditions:
        //   * OldClass and NewClass must have the same SizeClassIndex
        //     (the hot-reload pipeline validates this before invoking
        //     us; mismatch indicates a class-shape change which is
        //     NOT supported by the rebind path).
        //   * Both classes must already be RegisterClassPool'd.
        //
        // The slot-by-slot walk is O(cells in OldClass's sub-pool);
        // EXCLUSIVE lock held for the duration. Hot-reload is rare
        // (engineer-station-only) so this is acceptable.
        // =============================================================
        void RebindClassPool(
            const ::XCore::Reflect::FClass* OldClass,
            const ::XCore::Reflect::FClass* NewClass) noexcept;

        // =============================================================
        // CoalesceIdleSlabs -- engineer-station-only slab reclaim.
        //
        // Per FIX-A-MED-24 + spec §3.6 trailing prose. Walks every
        // size-class pool, releases fully-empty slabs back to
        // FMallocBinnedX (returns memory to the OS via the per-bin VM
        // decommit path). Phase 5.b acts ONLY on fully-empty slabs;
        // the partial-evacuation path (move live cells to another
        // slab to free up THIS slab) is gated until Phase 5.h ships
        // the GC coordination.
        //
        // Trainee builds never call this; long-running engineer
        // sessions call it from an idle hook. Returns the count of
        // slabs released.
        //
        // EXCLUSIVE lock acquired for the duration.
        // =============================================================
        ::int32 CoalesceIdleSlabs() noexcept;

        // =============================================================
        // Scenario boundary scope.
        //
        // BeginScenarioBoundary opens a scope; allocations inside the
        // scope are tagged with the scope's scenario name. The TLS-
        // backed scope state is a stack-like push/pop pair; nested
        // scenarios are supported (the innermost scope wins for the
        // tag).
        //
        // EndScenarioBoundary closes the most recently opened scope.
        // The ScenarioName parameter is verified against the scope
        // that's being closed (mismatch is an XPACT_CHECK violation).
        //
        // Phase 5.b ships the scope bookkeeping + per-allocation
        // tagging. The actual MARK-REGION-CLEARING action at scope
        // close requires the FXObjectCollector for reachability
        // verification per spec §3.6; until then EndScenarioBoundary
        // is a counter-bump + a debug-log entry. The scope-name
        // assignment is structurally complete so the Phase 5.h
        // continuation only adds the validation + release action.
        // =============================================================
        void BeginScenarioBoundary() noexcept;
        void EndScenarioBoundary(::XCore::Reflect::FName ScenarioName) noexcept;

        // =============================================================
        // GetStats -- diagnostic snapshot.
        //
        // Walks every size-class pool + every per-FClass sub-pool,
        // composing a POD snapshot. Returned by value (matches the
        // engine-wide return-by-value-out-of-lock discipline).
        //
        // The walk is performed under the SHARED lock so allocates
        // are blocked for the duration. At Foundation Prototype
        // scale (~50k objects + ~100 registered classes) the snapshot
        // takes microseconds; production telemetry consumers should
        // sample at a low frequency (~1 Hz).
        // =============================================================
        [[nodiscard]] FXObjectAllocatorStats GetStats() const noexcept;

        // =============================================================
        // Helpers exposed for diagnostics + tests.
        // =============================================================

        // Returns the size-class index for an allocation of `Size`
        // bytes per the spec §3.2 fit rule. Returns
        // `kFXObjectAllocatorNumNormalClasses` (i.e., the large-object
        // path) if Size exceeds every normal class's spec-allowed fit.
        [[nodiscard]] static ::int32 SizeToSizeClass(::SIZE_T Size) noexcept;

        // Returns the cell width for a given size-class index. For the
        // large-object sentinel index returns 0.
        [[nodiscard]] static ::SIZE_T SizeClassToWidth(::int32 SizeClassIndex) noexcept;

    private:
        FXObjectAllocator() noexcept;
        ~FXObjectAllocator() noexcept;

        FXObjectAllocator(const FXObjectAllocator&)            = delete;
        FXObjectAllocator(FXObjectAllocator&&)                 = delete;
        FXObjectAllocator& operator=(const FXObjectAllocator&) = delete;
        FXObjectAllocator& operator=(FXObjectAllocator&&)      = delete;

        // -------------------------------------------------------------
        // Internal types: kept opaque at the header so consumers
        // need not pull TArray + TMap. The full layout lives in the
        // .cpp.
        // -------------------------------------------------------------
        struct FSlab;
        struct FClassPool;
        struct FSizeClassPool;
        struct FLargeSlab;
        struct FState;

        // PImpl: all state lives in m_state allocated on first use.
        // The forward-declared FState pointer keeps the header
        // dependency surface to FRWLock + FName + the stats struct.
        FState* m_state;

        // -------------------------------------------------------------
        // Private static helpers used by the .cpp body. Declared here
        // (not in an anonymous namespace) so they can name the private
        // nested FState / FSizeClassPool / FClassPool types without
        // additional friend declarations.
        //
        // EnsureStateInitialised  -- lazy-init the PImpl on first use.
        // FindClassPoolUnderLock  -- linear-search the class-pool
        //                            registry; caller holds Lock.
        // RegisterClassPoolUnderLock
        //                         -- grow the class-pool registry +
        //                            install a new FClassPool entry.
        // ClassToSizeClass        -- derive the size-class index from
        //                            an FClass's PropertiesSize +
        //                            MinAlignment via the §3.2 fit rule.
        // -------------------------------------------------------------
        static FState* EnsureStateInitialised(FState*& StateRef) noexcept;

        static FClassPool* FindClassPoolUnderLock(
            FState* State,
            const ::XCore::Reflect::FClass* Class) noexcept;

        static void RegisterClassPoolUnderLock(
            FState* State,
            const ::XCore::Reflect::FClass* Class,
            ::int32 SizeClassIndex) noexcept;

        static ::int32 ClassToSizeClass(
            const ::XCore::Reflect::FClass* Class) noexcept;
    };

} // namespace XCore
