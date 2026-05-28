// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/CollectGarbageEmptyHeap.cpp -- end-to-end
// synchronous cycle on an empty heap (Phase 5.g).
// =====================================================================
//
// Verifies:
//   * CollectGarbage on an empty heap completes without crashing.
//   * Post-cycle: phase returns to kIdle.
//   * Cycle counter increments by 1.
//   * Reachability index advances mod 3 (0 -> 1).
//   * IsMarking returns false post-cycle.
//   * MarkedThisCycle == 0 (no live objects).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"

#include <iostream>
#include <vector>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }
}

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::EXGCOptions;
    using ::XCore::EXGCPhase;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();

    // Initialize the card table with a small heap so the dirty-card
    // drain path has somewhere to read.
    constexpr ::std::size_t kHeapBytes = 4 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(Heap.data(), kHeapBytes);

    FXObjectCollector& Coll = FXObjectCollector::Get();

    // -----------------------------------------------------------------
    // Pre-cycle baseline.
    // -----------------------------------------------------------------
    Check(Coll.GetCycleCounter() == 0,
          "Pre-cycle counter != 0");
    Check(Coll.GetCurrentReachabilityIndex() == 0,
          "Pre-cycle reachability index != 0");
    Check(Coll.GetPhase() == EXGCPhase::kIdle,
          "Pre-cycle phase != kIdle");

    // -----------------------------------------------------------------
    // Run one synchronous cycle.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);

    // -----------------------------------------------------------------
    // Post-cycle assertions.
    // -----------------------------------------------------------------
    Check(Coll.GetPhase() == EXGCPhase::kIdle,
          "Post-cycle phase != kIdle (state machine did not return to idle)");
    Check(Coll.GetCycleCounter() == 1,
          "Post-cycle counter != 1 (cycle did not increment)");
    Check(Coll.GetCurrentReachabilityIndex() == 1,
          "Post-cycle reachability index != 1 (rotation did not advance)");
    Check(Coll.CurrentCycleReachabilityMask() == (1u << 1),
          "Post-cycle mask != bit 1");
    Check(!Coll.IsMarking(),
          "Post-cycle IsMarking returned true (should be false at kIdle)");
    Check(Coll.GetMarkedThisCycle() == 0,
          "Post-cycle MarkedThisCycle != 0 (no live objects expected)");

    // -----------------------------------------------------------------
    // Run TWO more cycles; verify rotation hits index 2 then back to 0.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);
    Check(Coll.GetCurrentReachabilityIndex() == 2,
          "Cycle 2 reachability index != 2");
    Check(Coll.GetCycleCounter() == 2,
          "Cycle 2 counter != 2");

    Coll.CollectGarbage(EXGCOptions::kNone);
    Check(Coll.GetCurrentReachabilityIndex() == 0,
          "Cycle 3 reachability index != 0 (mod 3 rotation broken)");
    Check(Coll.GetCycleCounter() == 3,
          "Cycle 3 counter != 3");

    // Cleanup.
    FXObjectGCCardTable::Get().__ResetForTests();
    Coll.__ResetForTests();
    FXObjectArray::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.CollectGarbageEmptyHeap: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.CollectGarbageEmptyHeap: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
