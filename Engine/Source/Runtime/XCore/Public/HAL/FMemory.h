// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FMemory.h -- public allocator surface (Section 4.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 4.1.
//
// The thin facade dispatching to FMallocBinnedX (Private/HAL). The
// facade exists so:
//
//   1. Downstream modules link against a stable public symbol set
//      (FMemory::Malloc et al.) rather than the Phase-1b implementation
//      type. A future Phase-2 swap of the implementation
//      (e.g. a different binning scheme; an experimental allocator
//      under a CVar gate) requires zero downstream changes.
//
//   2. The allocator implementation type itself can be header-private
//      to the XCore-4a module, keeping its enormous symbol surface out
//      of downstream compilation units. The Phase-1b FMallocBinnedX
//      header lives in Private/HAL/; downstream TUs see only this file.
//
//   3. The OS-side cross-thread reclaim path (Section 4.2 fix B-C2)
//      can be re-shaped without touching the facade -- the swap
//      from "simple atomic linked list" to "TBoundedMpscQueue" in
//      Phase 1c is a Private/HAL/FMallocBinnedX.cpp edit only.
//
// FArchive forward-declaration only. The actual FArchive (XSerialization
// per Section 1.2 fix Rev 3 m5) is not in XCore-4a; the
// DumpUsageReport method exists in the spec body so callers know how
// to dump; XSerialization will provide the FArchive consumer.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "HAL/FMemTag.h"

namespace XCore
{
    // Forward declaration only. The body lives in XSerialization
    // (Layer 1, post-XCore-4a per Master Plan Section 3). XCore-4a
    // never includes FArchive's definition; the void return type of
    // DumpUsageReport keeps the surface link-compatible regardless of
    // FArchive's eventual layout.
    class FArchive;
}

namespace XCore::HAL
{
    // -----------------------------------------------------------------
    // FMemory -- the public allocator facade.
    //
    // All methods are static + noexcept (per Section 4 spec body).
    // The static-only shape mirrors UE's FMemory class. Every method
    // dispatches to the underlying FMallocBinnedX (Private/HAL/).
    //
    // Thread safety contract (Section 4.2): Malloc / Realloc / Free
    // are all thread-safe. The fast path is lock-free per-thread TLS
    // cache; cross-thread Free routes via owner-MPSC reclaim queue
    // drained on next allocation on the owner thread. The global
    // large-bin path acquires a single global mutex.
    //
    // OOM contract (Section 4.1 fix M-2): direct Malloc callers may
    // receive nullptr under FOOMPolicy::ReturnNull (Dev only);
    // container layers route through MallocOrAbort which converts
    // null returns to abort. The choice is deliberate: a
    // half-constructed container with an already-registered
    // XGCRootSpan would expose a dangling buffer to the collector.
    //
    // Phase ordering (Section 1.5): FMemory is a PreStaticInit-phase
    // subsystem. __Init runs at PreStaticInit; the allocator is live
    // for every constinit constructor that fires after FMemory's
    // __Init. The g_Allocator instance is constinit-initialised in
    // Private/HAL/FMemory.cpp so it is ready before any user-tier
    // static constructor runs.
    // -----------------------------------------------------------------
    class FMemory
    {
    public:
        // ============================================================
        // Core allocator surface.
        // ============================================================

        // -----------------------------------------------------------------
        // Malloc -- allocate `Size` bytes with alignment `Align`, tagged
        // with `Tag`.
        //
        // Returns the allocated block (>= `Size` bytes; >= `Align`-aligned),
        // or nullptr if the active FOOMPolicy is ReturnNull and the
        // allocation fails. Under Abort policy this method either
        // succeeds or aborts (it never returns nullptr).
        //
        // `Size` may be zero; the allocator returns a non-null distinct
        // pointer that satisfies the alignment, callable with Free.
        // This matches the C++ standard library `operator new` semantics
        // and avoids the "is null OK?" branch at every call site.
        //
        // `Align` must be a power of two AND >= 8 (the allocator's
        // minimum alignment; smaller alignments are silently rounded
        // up). Power-of-two enforcement is a Debug-only XPACT_CHECK; an
        // unaligned-power-of-two Align in Shipping is UB.
        //
        // `Tag` must NOT be FMemTag::Generic in Shipping (Section 4.1
        // banned-by-linker-check; TODO(Phase 1c) XBT scanner).
        //
        // Thread-safety: fully thread-safe (Section 4.2). The fast path
        // is the per-thread TLS bin cache; large allocations and
        // cross-thread frees go through the central allocator's locked
        // path.
        // -----------------------------------------------------------------
        static void* Malloc(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept;

        // -----------------------------------------------------------------
        // Realloc -- resize an existing allocation.
        //
        // `Ptr` may be nullptr (equivalent to Malloc(NewSize, Align, Tag)).
        // `NewSize` may be zero (equivalent to Free(Ptr); returns nullptr).
        // `Align` -- the alignment of the resulting block; must match the
        //            original Malloc's alignment for the move-path to
        //            preserve user-visible alignment.
        // `Tag` -- the tag for the (possibly new) allocation; if the
        //            existing block fits the new size in place, the tag
        //            on the existing block is updated (so per-tag bytes
        //            stay consistent).
        //
        // The implementation attempts an in-place resize first (when
        // the new size fits the existing bin's capacity); falls back
        // to Malloc-new + Memcpy + Free-old when growing past the
        // existing bin.
        // -----------------------------------------------------------------
        static void* Realloc(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align, FMemTag Tag) noexcept;

        // -----------------------------------------------------------------
        // Free -- release a previously-allocated block.
        //
        // `Ptr` may be nullptr (no-op). Double-free aborts in Debug
        // (Section 4.6 unit test "double-Free aborts in Debug");
        // double-free in Shipping is UB (the canary-byte
        // free-block-header pattern in FMallocBinnedX detects it in
        // Debug but is compiled out in Shipping for perf).
        //
        // Cross-thread Free: if `Ptr` was allocated on a different
        // thread than the calling thread, the Free routes the block
        // to the owner thread's MPSC reclaim queue (drained on owner's
        // next allocation). Phase 1c will swap the simple atomic
        // linked list for a proper TBoundedMpscQueue (TODO(Phase 1c)).
        // -----------------------------------------------------------------
        static void Free(void* Ptr) noexcept;

        // ============================================================
        // Abort-on-null wrappers (fix M-2).
        //
        // Container layers (TArray, TMap, TSet, FString, TBitArray,
        // the lock-free queues' growth paths) call MallocOrAbort /
        // ReallocOrAbort exclusively; only direct callers see the
        // FOOMPolicy::ReturnNull path in Dev.
        //
        // MallocOrAbort is internally `Malloc + null-check + abort`.
        // Under FOOMPolicy::Abort (Shipping/Test) the abort path is
        // equivalent to Malloc's internal abort (one extra branch);
        // under FOOMPolicy::ReturnNull (Dev) the wrapper converts the
        // null return to a clean abort with the tagged diagnostic.
        // ============================================================

        // -----------------------------------------------------------------
        // MallocOrAbort -- as Malloc, but aborts on null return.
        //
        // The abort message names the offending tag + the requested
        // size, e.g. "OOM in FMemory::MallocOrAbort: tag=Container,
        // size=4096, align=16". Tag-name resolution via
        // GetMemTagName in FMemTag.h.
        //
        // Performance: in Shipping (where FOOMPolicy is Abort), this
        // is equivalent to Malloc -- the null-check branch is
        // predicted-not-taken cold-path code that the optimiser
        // moves out of the hot ICache region.
        // -----------------------------------------------------------------
        static void* MallocOrAbort(::SIZE_T Size, ::SIZE_T Align, FMemTag Tag) noexcept;

        // -----------------------------------------------------------------
        // ReallocOrAbort -- as Realloc, but aborts on null return.
        //
        // Same contract as MallocOrAbort. Routes through Realloc's
        // in-place-resize path first; falls back to MallocOrAbort +
        // Memcpy + Free on grow-past-bin paths.
        // -----------------------------------------------------------------
        static void* ReallocOrAbort(void* Ptr, ::SIZE_T NewSize, ::SIZE_T Align, FMemTag Tag) noexcept;

        // ============================================================
        // Diagnostics + accounting.
        // ============================================================

        // -----------------------------------------------------------------
        // GetAllocatedBytes -- the sum of currently-allocated bytes
        // tagged with the given tag.
        //
        // Returns the requested-size sum (not the bin-rounded-up size).
        // For example, allocating one 17-byte block tagged Container
        // returns 17 (not 32, even though the bin is 32 bytes wide).
        // This matches Section 17.1 A1's "per-tag-byte-total trace"
        // determinism criterion: the per-tag total is the user-side
        // size, not the allocator's bin-rounding.
        //
        // Implementation note: per-tag atomic counter, updated on
        // every Malloc / Free / Realloc. The counter for FMemTag::Generic
        // is always 0 in Shipping (the XBT scanner enforces the ban)
        // and may be non-zero in Dev / Test as a debugging aid.
        // -----------------------------------------------------------------
        [[nodiscard]] static ::uint64 GetAllocatedBytes(FMemTag Tag) noexcept;

        // -----------------------------------------------------------------
        // DumpUsageReport -- write a human-readable per-tag breakdown
        // to the given archive.
        //
        // The archive is XSerialization's FArchive (forward-declared
        // above). XCore-4a never produces FArchive instances; this
        // method is a sink for the higher-layer diagnostic facility
        // (XLog / `stat memory` console command) to drain into.
        //
        // The output format is a single header row + one row per tag
        // with a non-zero byte count:
        //
        //   "FMemory usage by tag:\n"
        //   "  Container:    234567 bytes  (1234 allocs)\n"
        //   "  Math:           5678 bytes  (  12 allocs)\n"
        //   ...
        //   "  Total:        240245 bytes  (1246 allocs)\n"
        //
        // Phase 1c will land the FArchive consumer; for Phase 1b the
        // method exists as a declaration that compiles cleanly when
        // FArchive is a forward-declaration only. The method is
        // implemented in Private/HAL/FMemory.cpp but its body
        // composes the report into a std::string and writes via a
        // direct stderr emission (the FArchive is held as a forward-
        // declared reference; calling any method on it would require
        // the full type which we do not have at Phase 1b).
        // TODO(Phase 1c): wire FArchive::Serialize calls once the
        // type is defined.
        // -----------------------------------------------------------------
        static void DumpUsageReport(::XCore::FArchive& Archive) noexcept;

        // ============================================================
        // Engine init / shutdown hooks (Section 1.5 phase ladder).
        //
        // __Init is called at PreStaticInit; __Shutdown at engine
        // shutdown. The double-underscore prefix flags these as
        // engine-internal bootstrap calls (per XInitPhase.h's
        // __AdvanceInitPhase convention); user-tier modules MUST
        // NOT call them directly.
        // ============================================================

        // -----------------------------------------------------------------
        // __Init -- engine bootstrap allocator init.
        //
        // Called once, before any constinit constructor that uses the
        // allocator. Initialises:
        //   * the constinit g_Allocator instance's per-bin VM
        //     reservations (FPlatformMemory::ReserveVirtual call
        //     per bin),
        //   * the per-tag accounting array (zero-initialised),
        //   * the active FOOMPolicy (set to kDefaultOOMPolicy from
        //     FOOMPolicy.h).
        //
        // Returns void; on bootstrap failure (e.g., VM reservation
        // failed for the very first bin) the function aborts cleanly
        // via XCore::HAL::AbortWithMessage. The engine cannot start
        // without a working allocator; there is no fallback.
        // -----------------------------------------------------------------
        static void __Init() noexcept;

        // -----------------------------------------------------------------
        // __Shutdown -- engine shutdown allocator teardown.
        //
        // Called once, after every other subsystem has shut down.
        // Drains all per-thread caches, validates that the per-tag
        // byte counts are zero (mismatch is logged but not aborted;
        // leak-tracker is responsible for the per-allocation
        // accountability), and releases the per-bin VM reservations
        // via FPlatformMemory::ReleaseVirtual.
        //
        // In Debug + Development, a non-zero per-tag count at shutdown
        // emits a diagnostic to stderr listing the leaking tag + the
        // FLeakTracker callstack bucket if FLeakTracker is enabled.
        // -----------------------------------------------------------------
        static void __Shutdown() noexcept;

        // ============================================================
        // FScopedNoAlloc -- the sim-path RAII alloc-free assertion.
        // ============================================================

        // -----------------------------------------------------------------
        // FScopedNoAlloc -- proves a sim-path inner loop is allocation-
        // free (Section 4.3 fix A-MIN4).
        //
        // Debug / Development: the constructor increments a per-thread
        // counter; the destructor decrements. While the counter is
        // non-zero, every FMemory::Malloc / Realloc on the same
        // thread aborts with a tagged diagnostic naming the offending
        // tag + size. Move-only (no copy / no assignment) so the
        // scope is unambiguous.
        //
        // Shipping / Test: the constructor + destructor compile to
        // `((void)0)`. The discipline is split by config: Section 4.3
        // wording -- "sim-path TUs are exhaustively unit-tested in
        // Dev/Test to be alloc-free; a Test-config violation slipping
        // through to Shipping is a process failure". The cost of one
        // extra TLS-counter increment per allocation is unjustified
        // when the Test gate is the right place to catch the bug.
        //
        // Usage:
        //     {
        //         FMemory::FScopedNoAlloc Guard;
        //         // sim-path inner loop here; no FMemory::Malloc allowed.
        //     }
        // -----------------------------------------------------------------
        class FScopedNoAlloc
        {
        public:
            FScopedNoAlloc() noexcept;
            ~FScopedNoAlloc() noexcept;

            // Move-only (per Section 4.3 spec body: "the scope is
            // unambiguous").
            FScopedNoAlloc(const FScopedNoAlloc&)            = delete;
            FScopedNoAlloc& operator=(const FScopedNoAlloc&) = delete;
            FScopedNoAlloc(FScopedNoAlloc&&)                 = delete;
            FScopedNoAlloc& operator=(FScopedNoAlloc&&)      = delete;
        };

        // -----------------------------------------------------------------
        // IsNoAllocScopeActive -- internal helper.
        //
        // Called by the allocator's Malloc / Realloc hot path to abort
        // if a sim-path scope is currently active on this thread.
        // Public so unit tests can inspect the state; not part of the
        // formal public API.
        //
        // Returns false in Shipping/Test (the FScopedNoAlloc methods
        // are no-ops in those configs).
        // -----------------------------------------------------------------
        [[nodiscard]] static bool IsNoAllocScopeActive() noexcept;

        // -----------------------------------------------------------------
        // IsAlive -- has the allocator been initialised and not yet
        // shut down?
        //
        // Phase 1g fix F-3: published for the FStatShard destructor (and
        // any future thread-local destructor that allocates) to guard
        // against the shutdown-ordering race in which a thread exits
        // AFTER FMemory::__Shutdown has torn down the allocator.
        // Calling FMemory::Malloc on a torn-down allocator is undefined
        // behaviour; consumers must check IsAlive() and skip the
        // allocation when it returns false. Counts / data leaked at
        // process-shutdown post-allocator-teardown are by definition
        // unobservable (no live thread can read them) and are
        // acceptable.
        //
        // Internally backed by an atomic<bool> in FMemory.cpp, stored
        // with memory_order_release at the END of __Init() and at the
        // START of __Shutdown(). Reads use memory_order_acquire so a
        // subsequent FMemory::Malloc call's stores are happens-after
        // the publish of `true`.
        //
        // The state machine is monotonic per-process: false -> true at
        // __Init; true -> false at __Shutdown; no further transitions.
        // -----------------------------------------------------------------
        [[nodiscard]] static bool IsAlive() noexcept;
    };

    // =====================================================================
    // s_XObjectAllocator hook (Section 4.4) -- 3-arg signature per Phase
    // 5.a' Contract Rev 13.9 prerequisite (XCoreXObject Rev 4 §1.4 +
    // FIX-A-CRIT-4 follow-up).
    //
    // Rev 3 of XCore-4a documented the hook with a 2-arg signature
    // `void* (*)(size_t Size, size_t Align) noexcept`. Rev 4 follow-up
    // (Phase 5.a' Contract prerequisite per XCoreXObject Rev 4 §1.4)
    // expands to 3 args:
    //
    //     void* (*)(size_t Size, size_t Align,
    //               const ::XCore::Reflect::FClass* ClassDescriptor) noexcept
    //
    // The third argument is a forward-declared FClass pointer (XCore-4b
    // type; XCore-4a depends on XCore-4b for the pointer-type-only
    // forward declaration, which is fine -- XCore-4a/4b are part of the
    // same XCore module and the dependency is a one-way pointer-only
    // reference). XCoreXObject's FXObjectAllocator::AllocateRaw populates
    // the hook via RegisterXObjectAllocator(); the FClass argument is
    // load-bearing for type-segregated free-list lookup per XCoreXObject
    // Rev 4 §3.4 ("size-class segregated free-lists + type-segregated
    // within size class") to bound fragmentation per Foundation Prototype
    // criterion (h) <15% wasted heap over 4 hours of continuous load on
    // Quest 3.
    //
    // The hook is declared as a tentative-definition extern; until
    // XCoreXObject ships (System 5), the symbol is supplied by a weak-
    // symbol fallback that initializes to nullptr. An XCore-4a-only
    // test build that attempts to construct an XObject-derived type
    // aborts cleanly via the call-site XPACT_CHECK with the offending
    // call-site stack trace per acceptance criterion A5 (fix C-8) +
    // XCore-4a §4.4 + spec §17.1 A1.
    //
    // Caller obligation: pass nullptr for the FClass argument until
    // XCoreXObject's FXObjectAllocator owns the hook. NewObject<T> in
    // XCoreXObject Phase 5.b populates the real FClass; intermediate
    // bootstrap callers (none exist at Phase 5.a') stay on nullptr.
    // =====================================================================

} // namespace XCore::HAL

namespace XCore::Reflect
{
    // Forward declaration only. XCore-4b type; the FMemory side needs
    // a pointer-only reference for the s_XObjectAllocator hook
    // signature. Including FClass.h here would pull the full reflection
    // surface into every FMemory consumer; the forward decl keeps the
    // dependency boundary clean.
    struct FClass;
}

namespace XCore::HAL
{
    // Tentative-definition extern. Strong definition lands at
    // XCoreXObject (Phase 5.b) via FXObjectAllocator::AllocateRaw.
    // Weak-symbol fallback (initialized to nullptr) ships alongside
    // the XCore-4a-only test build per acceptance criterion A5.
    extern void* (*s_XObjectAllocator)(
        ::SIZE_T Size,
        ::SIZE_T Align,
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept;

    // RegisterXObjectAllocator installs the strong implementation at
    // XCoreXObject's PostStaticInit. Called exactly once per process
    // lifetime; subsequent calls overwrite the slot (the spec invariant
    // is one allocator per process, but the slot is mutable to support
    // future hot-replace scenarios under XLiveCoding cascade).
    //
    // The function pointer signature MUST match s_XObjectAllocator's
    // signature exactly; mismatches produce a compile error at the
    // RegisterXObjectAllocator call site.
    void RegisterXObjectAllocator(
        void* (*Fn)(::SIZE_T Size, ::SIZE_T Align,
                    const ::XCore::Reflect::FClass* ClassDescriptor) noexcept) noexcept;

    // NewXObject is the public dispatch routine for the hook. Asserts
    // s_XObjectAllocator != nullptr via XPACT_CHECK in Debug/Dev (per
    // §4.4 fix C-8); abort path is clean (call-site attributable rather
    // than generic nullptr segfault). Production callers (XCoreXObject's
    // NewObject<T>) go through s_XObjectAllocator directly; this routine
    // exists for the XCore-4a-only test build's defensive abort path.
    void* NewXObject(
        ::SIZE_T Size,
        ::SIZE_T Align,
        const ::XCore::Reflect::FClass* ClassDescriptor) noexcept;

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1c):
//   * Swap the cross-thread reclaim queue from the Phase 1b simple
//     atomic linked list to TBoundedMpscQueue (Section 8.1 + fix
//     B-C4). The Free path's enqueue is the only edit; the queue
//     header (Private/HAL/FMallocBinnedX.h) holds the simple list
//     today.
//   * Wire FArchive::Serialize in DumpUsageReport once XSerialization
//     ships.
//   * Land the XBT linker-scan rule banning FMemTag::Generic in
//     Shipping builds (Section 4.1 fix B-M1 + Section 17.1 A1).
// =====================================================================
