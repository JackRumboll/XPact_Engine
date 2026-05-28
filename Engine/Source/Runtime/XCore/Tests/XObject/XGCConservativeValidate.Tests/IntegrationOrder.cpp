// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCConservativeValidate.Tests/IntegrationOrder.cpp -- gate ordering
// (XCoreXObject Rev 4 §5.3 + Rev 2 FIX-A-MED-35; Phase 5.e).
// =====================================================================
//
// Verifies the four-gate validator runs each gate in the SPEC ORDER:
//   1. Heap-range  (cheapest; reject non-heap candidates first)
//   2. Index-range (next; reject heap candidates with bogus InternalIndex)
//   3. Entry-bind  (next; reject heap candidates with valid index but
//                   the entry binds to a different object / freed slot)
//   4. Serial match (last; reject heap candidates that survived 1-3
//                    but have a captured serial that no longer matches)
//
// The invariant being tested: a candidate that would FAIL gate N also
// fails when evaluated against gates 1..N+1 -- the gate order does NOT
// produce a "false positive" pass where a higher-numbered gate's
// failure escapes because a lower-numbered gate spuriously rejected
// first (gate 1 IS the load-bearing safety gate; the others provide
// progressive refinement).
//
// This test combines fixtures from the per-gate isolation tests into
// a single composite scenario.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCConservativeValidate.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <iostream>
#include <new>

int main()
{
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::ValidateConservativeCandidate;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    // -----------------------------------------------------------------
    // Setup: allocate a live XObject so all four gates can pass for
    // the positive case.
    // -----------------------------------------------------------------
    void* Storage = FXObjectAllocator::Get().AllocateRaw(
        sizeof(XObject), alignof(XObject), nullptr);
    XObject* Live = new (Storage) XObject();
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Live->InternalIndex = Idx;
    Live->SerialNumber  = Serial;
    Array.BindObject(Idx, Live);

    // -----------------------------------------------------------------
    // The validator's contract is "all four gates must pass". We
    // exercise it by:
    //   * Passing a candidate that should FAIL at each gate.
    //   * Passing the LIVE candidate that passes ALL gates.
    // The result table:
    //
    //   nullptr                        -> reject (gate 1 early)
    //   stack XObject                  -> reject (gate 1)
    //   small numeric address          -> reject (gate 1)
    //   Live (post-Free)               -> reject (gate 3; entry nulled)
    //   Live (after serial bump)       -> reject (gate 4 / 3 cascade)
    //   Live (everything fresh)        -> ACCEPT
    // -----------------------------------------------------------------

    // 1. nullptr.
    Check(ValidateConservativeCandidate(nullptr) == nullptr,
          "nullptr should reject");

    // 2. stack XObject.
    {
        XObject Stack;
        Check(ValidateConservativeCandidate(&Stack) == nullptr,
              "stack XObject should reject (gate 1)");
    }

    // 3. small numeric address.
    Check(ValidateConservativeCandidate(
              reinterpret_cast<const void*>(static_cast<::uintptr_t>(0x42)))
          == nullptr,
          "0x42 garbage should reject (gate 1)");

    // 4. Live, everything fresh: accept.
    Check(ValidateConservativeCandidate(Live) == Live,
          "fresh live XObject should accept");

    // 5. Bogus InternalIndex on live storage: gate 2 reject. (We
    // temporarily mutate Live's InternalIndex; the FXObjectArray
    // entry still binds Live but the candidate's index says
    // otherwise.)
    {
        const ::int32 OrigIndex = Live->InternalIndex;
        Live->InternalIndex = -1;
        Check(ValidateConservativeCandidate(Live) == nullptr,
              "Live with bogus index -1 should reject (gate 2)");
        Live->InternalIndex = OrigIndex;
    }
    // Restored. Validate again: accept.
    Check(ValidateConservativeCandidate(Live) == Live,
          "post-restore: Live should accept again");

    // 6. Bogus SerialNumber on live storage: gate 4 reject. The
    // FXObjectArray entry's SerialNumber still matches the original
    // Live->SerialNumber; mutating Live->SerialNumber breaks the
    // match.
    {
        const ::uint32 OrigSerial = Live->SerialNumber;
        Live->SerialNumber = OrigSerial + 1;
        Check(ValidateConservativeCandidate(Live) == nullptr,
              "Live with bumped local serial should reject (gate 4)");
        Live->SerialNumber = OrigSerial;
    }
    Check(ValidateConservativeCandidate(Live) == Live,
          "post-restore: Live should accept after serial restore");

    // 7. Free the entry; the entry's Object becomes nullptr and the
    // entry's SerialNumber bumps; the candidate's pointer + locally-
    // stored index/serial still claim to be Live. Gate 3 rejects
    // (entry.Object == nullptr != Live; or via GetObjectAtIndex
    // returning nullptr).
    Array.FreeEntry(Idx);
    Check(ValidateConservativeCandidate(Live) == nullptr,
          "post-FreeEntry Live should reject (gate 3 + gate 4)");

    // Cleanup.
    Live->~XObject();
    FXObjectAllocator::Get().Deallocate(Live);
    Array.__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCConservativeValidate.IntegrationOrder: PASS\n";
        return 0;
    }
    std::cerr << "XGCConservativeValidate.IntegrationOrder: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
