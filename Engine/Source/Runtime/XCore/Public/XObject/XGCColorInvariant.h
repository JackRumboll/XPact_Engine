// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCColorInvariant.h -- 3-color GC invariant documentation (Phase 5.f).
// =====================================================================
//
// XCoreXObject Rev 4 §4.2.2 (3-color invariant SATB preserves) +
// Rev 2 FIX-A-MED-34 / MAJOR-A11.
//
// THIS HEADER IS DOCUMENTATION-ONLY: it captures the white/gray/black
// invariant SATB preserves and defines the EXObjectGCColor enum that
// the Phase 5.g mark phase will use. Phase 5.f provides the BARRIER
// MECHANISM (write barrier + per-thread SATB queue + global SATB log +
// card table); the actual color-marking happens in Phase 5.g.
//
// Phase 5.f's barrier WORKS even without Phase 5.g being implemented:
//
//   * The SATB queue accumulates entries any time the global
//     concurrent-mark flag is true. (Phase 5.g flips the flag.)
//   * The card table accumulates dirty marks every store.
//   * The Phase 5.g mark phase consumes both.
//
// =====================================================================
//
// 3-COLOR INVARIANT (per spec §4.2.2):
//
//   * White : candidate-unreachable. Not yet visited, or known dead.
//             All entries start the cycle as White.
//   * Gray  : visited but its references not yet traversed (in the mark
//             queue).
//   * Black : visited AND its references fully traversed (mark bit set;
//             no entry in any worker's gray queue).
//
// The mostly-concurrent SATB algorithm preserves the invariant: a
// Black object NEVER holds a reference to a White object.
//
// SATB PRE-STORE BARRIER (per spec §5.2):
//
// The barrier captures the OLD value before reassignment, ensuring
// objects that were reachable at snapshot time remain reachable across
// the cycle even if the mutator removes references mid-collection.
// The snapshot is taken at the safe-point handshake; from then on any
// reference deleted from the snapshot is still considered reachable
// until the cycle ends.
//
// CONCRETE PRESERVATION ARGUMENT.
//
// Suppose a Black object B holds a reference to a Gray-or-White object
// W. Mid-cycle, the mutator overwrites the slot in B from W to some
// other reference V. Without the SATB barrier W might become
// unreachable and be reclaimed -- BUT if W was reachable at snapshot
// time and is the ONLY path to some grandchild G, the mutator could
// have a stack reference to G that gets dangled.
//
// The SATB barrier records the OLD reference (W) into the per-thread
// SATB queue BEFORE the store. When the mark phase drains the queue,
// W is re-marked Gray (if still White) and its references are
// re-traversed. The invariant is preserved: any path that existed at
// snapshot time is fully scanned by cycle-end, even if the mutator has
// since severed the path.
//
// FLOATING GARBAGE.
//
// Objects deleted mid-cycle are not reclaimed until the next cycle.
// For XPact's tick rate (90 Hz Quest 3) and cycle cadence (per-second
// typical, per-frame stress), floating garbage is at most one cycle's
// worth of allocation -- well within the heap budget.
//
// =====================================================================
//
// THIS HEADER PROVIDES:
//
//   * EXObjectGCColor enum (uint8). Phase 5.g will read this; Phase 5.f
//     just establishes the type so cross-phase code can name the colors
//     consistently.
//
//   * Documentation comments only otherwise; no runtime code.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <cstdint>

namespace XCore
{
    // -----------------------------------------------------------------
    // EXObjectGCColor -- 3-color invariant enum.
    //
    // The color is NOT stored explicitly on the XObject -- it is
    // INFERRED from the reachability bits (XObject::ReachabilityFlag,
    // offset 36, Rev 3 per FIX-M-R2-3) + presence in the per-worker
    // gray queue:
    //
    //   * Black -- mark bit (current cycle's reachability bit) set AND
    //              not in any worker's gray queue.
    //   * Gray  -- mark bit set AND in some worker's gray queue.
    //   * White -- mark bit NOT set (the default starting state for
    //              every object at cycle begin).
    //
    // The enum exists so cross-phase code (Phase 5.g mark workers,
    // Phase 5.h sweep workers, diagnostic dumps) can name the colors
    // with type-safety. Phase 5.f only references it from the
    // documentation surface.
    //
    // uint8 backing: the enum could in principle be packed alongside
    // ReachabilityFlag's reserved bits 3..7 in a future scheme; the
    // explicit width matches the most-compact reasonable shape.
    // -----------------------------------------------------------------
    enum class EXObjectGCColor : ::std::uint8_t
    {
        White = 0,   // unreached so far this cycle
        Gray  = 1,   // reached; references not yet scanned
        Black = 2,   // reached; references fully scanned
    };

    // Compile-time width lock so future schemes that pack the color
    // into a reachability-flag sub-field have a stable contract.
    static_assert(sizeof(EXObjectGCColor) == 1,
                  "EXObjectGCColor must be 1 byte (uint8 underlying type).");

} // namespace XCore
