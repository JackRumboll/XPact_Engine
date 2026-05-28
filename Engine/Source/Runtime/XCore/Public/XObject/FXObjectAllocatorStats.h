// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FXObjectAllocatorStats.h -- POD diagnostic snapshot of the XObject
// heap (XCoreXObject Rev 4 §3 telemetry).
// =====================================================================
//
// Returned by `FXObjectAllocator::GetStats()`. Plain-data carrier for
// the engine-station telemetry pipeline (XInsights weak-symbol bridge
// per spec §10.5; the actual bridge wires in at Phase 5.k). The struct
// itself is internal to XCoreXObject -- it is NOT part of the Stage B
// addendum / Contract Rev 13.9 ABI lock (per spec §11.3 the layout pin
// set covers only the XObject base type, FXObjectArrayEntry, handles,
// FStruct, FClass, FXObjectLifecycleTable, FXObjectRefSchema). The
// allocator-internal types deliberately STAY out of the contract
// surface so the heap implementation can evolve without breaking
// downstream consumers.
//
// FIELD SET (per spec §3.6 + §10.5 + §3 trailing telemetry prose):
//
//   * TotalAllocatedBytes  -- sum of user-requested bytes across every
//                              live XObject. NOT the size-class-rounded
//                              capacity; that is `TotalSlabBytes`. The
//                              two differ by per-cell waste; the spread
//                              determines the FragmentationPercent.
//
//   * TotalSlabBytes       -- sum of bytes reserved across every active
//                              size-class slab + every large-object
//                              slab. Includes the per-cell waste; the
//                              difference vs TotalAllocatedBytes IS the
//                              fragmentation overhead.
//
//   * TotalLiveObjects     -- count of live XObject instances at
//                              snapshot time. Sourced from the
//                              FXObjectArray's live-entry counter.
//
//   * ActiveSlabs          -- count of slabs currently holding at least
//                              one live cell.
//
//   * IdleSlabs            -- count of slabs whose live-cell count is
//                              zero but which are still resident in the
//                              size-class pool (awaiting coalescence
//                              per FIX-A-MED-24 + spec §3.6).
//
//   * LargeObjectSlabs     -- count of slabs in the large-object path
//                              (one slab per allocation > 1536 bytes
//                              per spec §3.2 trailing row).
//
//   * FragmentationPercent -- 100 * (TotalSlabBytes - TotalAllocatedBytes)
//                              / TotalSlabBytes; the percentage of
//                              reserved heap that is NOT user-payload.
//                              double-precision so the 0.00-100.00
//                              range round-trips losslessly. Zero on
//                              an empty heap.
//
//   * PerClassAllocations  -- per-FClass live-instance count. Keyed by
//                              FClass pointer (the same key
//                              GetClassPool uses). The dense TArray
//                              shape lets the consumer walk the entries
//                              without a hash probe. Phase 5.k+ may
//                              swap in TMap<FName, int32_t> per the
//                              spec wording if the dense shape becomes
//                              expensive.
//
// LAYOUT CHOICE: the struct is intentionally NOT static_assert pinned
// (no Contract Rev 13.9 tag) because the heap-telemetry shape is
// expected to evolve; the consumers (XInsights diagnostic UI, leak
// tracker integration) read field-by-name and tolerate additive
// changes. Documented per Phase 5.b dispatch wording.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "Containers/TArray.h"
#include "Reflection/FName.h"

#include <cstddef>
#include <cstdint>

// Forward declare FClass; the stats carrier holds a pointer-only
// reference (no dereference). Pulling Reflection/FClass.h here would
// inflate every XInsights consumer's include cost; the forward decl
// keeps the surface lean.
namespace XCore::Reflect { struct FClass; }

namespace XCore
{

    // -----------------------------------------------------------------
    // FXObjectAllocatorStats::FPerClassEntry -- one row in the dense
    // per-class instance-count table.
    //
    // The Class pointer is the same key FXObjectAllocator::GetClassPool
    // dispatches on; LiveCount is the count of live cells in that
    // class's sub-pool.
    //
    // The struct is trivially-copyable so the dense TArray of entries
    // is cheap to deep-copy as part of the GetStats return value.
    // -----------------------------------------------------------------
    struct FXObjectAllocatorStatsPerClassEntry
    {
        const ::XCore::Reflect::FClass* Class;
        ::int32                          LiveCount;

        constexpr FXObjectAllocatorStatsPerClassEntry() noexcept
            : Class(nullptr)
            , LiveCount(0)
        {
        }

        constexpr FXObjectAllocatorStatsPerClassEntry(
            const ::XCore::Reflect::FClass* InClass,
            ::int32                          InLiveCount) noexcept
            : Class(InClass)
            , LiveCount(InLiveCount)
        {
        }
    };

    // -----------------------------------------------------------------
    // FXObjectAllocatorStats -- POD snapshot of FXObjectAllocator
    // diagnostics.
    //
    // Captured atomically (the lock-discipline contract pre-acquires
    // the allocator's RWLock in SHARED mode, snapshots all counters
    // into stack locals, then releases before allocating + returning
    // the per-class TArray). The returned struct is a value-type
    // disconnected from the live heap.
    //
    // NOT trivially-copyable (the TArray member's atomic-counter
    // protocol is non-trivial-copy on the GC-aware specialization;
    // here the TArray<TrivialT> primary template IS copyable). The
    // struct supports value-copy + move; the caller may stash + diff
    // multiple snapshots for fragmentation-over-time analysis.
    // -----------------------------------------------------------------
    struct FXObjectAllocatorStats
    {
        // ----- Bytes + counts -----
        ::SIZE_T   TotalAllocatedBytes;
        ::SIZE_T   TotalSlabBytes;
        ::int32    TotalLiveObjects;
        ::int32    ActiveSlabs;
        ::int32    IdleSlabs;
        ::int32    LargeObjectSlabs;

        // ----- Derived ratio (0.00 - 100.00; zero on empty heap) -----
        double     FragmentationPercent;

        // ----- Per-class breakdown (dense; one entry per class with
        //       a non-zero live count) -----
        ::XCore::TArray<FXObjectAllocatorStatsPerClassEntry>
                   PerClassAllocations;

        // Default-construct = zero counters + empty PerClassAllocations.
        // The default ctor IS callable from any context (no static
        // initialisation order issues; the TArray's default ctor is
        // noexcept + allocation-free).
        FXObjectAllocatorStats() noexcept
            : TotalAllocatedBytes(0)
            , TotalSlabBytes(0)
            , TotalLiveObjects(0)
            , ActiveSlabs(0)
            , IdleSlabs(0)
            , LargeObjectSlabs(0)
            , FragmentationPercent(0.0)
            , PerClassAllocations()
        {
        }

        // Move-only at the surface to discourage accidental deep-copy
        // of the per-class TArray; explicit caller code may still
        // duplicate via copy-construct if needed. (Underlying TArray
        // primary template is value-copyable.)
        FXObjectAllocatorStats(const FXObjectAllocatorStats&)            = default;
        FXObjectAllocatorStats(FXObjectAllocatorStats&&) noexcept        = default;
        FXObjectAllocatorStats& operator=(const FXObjectAllocatorStats&) = default;
        FXObjectAllocatorStats& operator=(FXObjectAllocatorStats&&) noexcept = default;
        ~FXObjectAllocatorStats() noexcept                               = default;
    };

} // namespace XCore
