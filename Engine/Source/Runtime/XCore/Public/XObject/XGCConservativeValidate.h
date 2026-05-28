// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XGCConservativeValidate.h -- conservative-root four-gate validator
// (XCoreXObject Rev 4 §5.3 + Rev 2 FIX-A-MED-35; Phase 5.e).
// =====================================================================
//
// XCoreXObject Rev 4 Section 5.3 ("Conservative root registration for
// non-statically-typed C# containers"). Phase 5.e ships the validator
// that XGCRootSpanRegistry uses internally for kConservative spans;
// the same validator is exposed as a public API so XIL2CPP-generated
// stack roots + ad-hoc callers can validate candidate pointers
// against the spec §5.3 invariant.
//
// =====================================================================
// FOUR-GATE VALIDATION ORDER (LOCKED; spec §5.3 + FIX-A-MED-35)
// =====================================================================
//
// Given a candidate pointer, the validator runs gates 1..4 in this
// EXACT order. The candidate is NEVER dereferenced before all four
// gates pass. The order is load-bearing because each gate is cheaper
// than the next + each gate gates the safety of the next:
//
//   1. HEAP-RANGE CHECK
//      ------------------
//      The candidate address must lie within the FXObjectAllocator's
//      slab byte range. If not, the candidate is NOT a pointer into
//      the XObject heap; reject.
//
//      Cost: O(log N) over normal slabs + O(M) over large slabs.
//      Phase 5.e implementation: FXObjectAllocator::IsHeapAddress.
//
//   2. FXObjectArray INDEX-RANGE CHECK
//      -------------------------------
//      Read the candidate's putative InternalIndex (offset 8; uint32
//      / int32 layout pinned at Phase 5.a). Check InternalIndex IN
//      [1, FXObjectArray::Capacity()). Index 0 is the null sentinel
//      (rejected); out-of-range indices are stale / garbage (rejected).
//
//      Cost: O(1) atomic load + 2 integer compares.
//
//      SAFETY: this is the FIRST gate that READS from the candidate.
//      The heap-range check guarantees the read does not page-fault
//      or read into an unrelated allocation -- the candidate IS
//      within the allocator's slab range, so the bytes at
//      Candidate+8 are valid memory (either a live XObject's
//      InternalIndex or stale-but-allocated memory).
//
//   3. ENTRY-BIND CHECK
//      ----------------
//      Look up FXObjectArrayEntry at the candidate's InternalIndex.
//      Verify Entry.Object == Candidate. If not, the candidate is
//      either pointing into a freed-then-reused cell (the entry
//      now binds a different XObject) or into a "look-alike" byte
//      sequence within an XObject's body that happens to start with
//      a plausible InternalIndex value (reject).
//
//      Cost: O(1) atomic load (Entry.Object) + 1 pointer compare.
//
//   4. SERIAL-NUMBER MATCH CHECK
//      -------------------------
//      Read the candidate's putative SerialNumber (offset 12; uint32
//      layout pinned at Phase 5.a). Verify SerialNumber ==
//      Entry.SerialNumber. The entry's SerialNumber may have bumped
//      since the candidate was captured (the slot was freed +
//      reused for a different object); mismatch rejects the candidate.
//
//      Cost: O(1) atomic load + 1 integer compare.
//
// If all four gates pass, the candidate IS a live XObject pointer
// (gate 3 verified the entry binds to this exact pointer; gate 4
// verified the slot has not been recycled since capture). Return the
// validated XObject*.
//
// If ANY gate fails, return nullptr -- the candidate is not a live
// XObject reference.
//
// =====================================================================
// PERF
// =====================================================================
//
// Per spec §5.3 trailing prose: ~10-20 cycles per Conservative slot.
// The breakdown:
//   * Gate 1 (heap-range):     ~6-10 cycles (binary search amortised)
//   * Gate 2 (index-range):    ~2-3 cycles
//   * Gate 3 (entry-bind):     ~3-5 cycles
//   * Gate 4 (serial match):   ~2-3 cycles
//
// Total: 13-21 cycles per call. For a Conservative span with 1000
// slots scanned per GC cycle: 13-21 us per scan. Bursts of
// Conservative scanning show in the pause budget but are bounded by
// the active-span count.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include "XObject/FXObjectAllocator.h"        // gate 1: IsHeapAddress
#include "XObject/FXObjectArray.h"             // gates 2-4: Capacity / Entry / SerialNumber
#include "XObject/XObject.h"                    // candidate pointer type

namespace XCore
{

    // -----------------------------------------------------------------
    // ValidateConservativeCandidate -- public API.
    //
    // Given a candidate pointer that MAY OR MAY NOT be a live XObject,
    // runs the four-gate validation order and returns:
    //   * The XObject* iff all four gates pass.
    //   * nullptr iff any gate fails.
    //
    // PRE-CONDITION: none. The function accepts ANY pointer (nullptr,
    // a random garbage value, a stale freed XObject pointer, a real
    // live XObject pointer); the four-gate check correctly classifies
    // each.
    //
    // POST-CONDITION: the candidate is NEVER dereferenced before gate
    // 1 (heap-range) passes. Specifically:
    //   * nullptr returns nullptr without any memory access.
    //   * Non-heap pointers return nullptr after gate 1 (no read).
    //   * Heap pointers with bogus InternalIndex return nullptr after
    //     gates 1+2 (one read; the InternalIndex word).
    //   * Heap pointers with valid InternalIndex but stale binding
    //     return nullptr after gates 1+3 (entry.Object compare).
    //   * Heap pointers with mismatched serial return nullptr after
    //     gates 1+2+3+4 (full validation).
    //
    // PERFORMANCE: see header docstring; ~13-21 cycles typical.
    //
    // THREAD SAFETY: the validator acquires SHARED locks on
    // FXObjectAllocator + FXObjectArray internally (via IsHeapAddress
    // and the public FXObjectArray surface). Race-safe against
    // concurrent allocate / free / serial-bump on the same / different
    // objects.
    //
    // INVARIANT (Prime Directive): the implementation MUST NOT
    // dereference `Candidate` before gate 1 succeeds. This is the
    // load-bearing safety property the Conservative root protocol
    // relies on; any subagent modifying this function MUST preserve
    // the invariant.
    // -----------------------------------------------------------------
    [[nodiscard]] XObject* ValidateConservativeCandidate(
        const void* Candidate) noexcept;

} // namespace XCore
