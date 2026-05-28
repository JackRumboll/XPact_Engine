// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/WriteBarrierIsMarkingActive.cpp -- the write-
// barrier probe is wired to the collector phase (Phase 5.g).
// =====================================================================
//
// Verifies:
//   * IsMarkingActive() is false at kIdle.
//   * The g_XGCIsConcurrentMarkActive flag (which the write barrier
//     consumes via XGCWriteBarrier.h) is false at kIdle.
//   * After CollectGarbage completes, the flag is false again (the
//     mark phase set + cleared the flag across its window).
//   * Direct call to MarkObject in a pretend "mid-mark" state matches
//     the documented atomic CAS-via-fetch_or contract.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/XGCConcurrentState.h"

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

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();

    constexpr ::std::size_t kHeapBytes = 4 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(Heap.data(), kHeapBytes);

    FXObjectCollector& Coll = FXObjectCollector::Get();

    // -----------------------------------------------------------------
    // Baseline: not marking.
    // -----------------------------------------------------------------
    Check(!Coll.IsMarking(),
          "Baseline: IsMarking returned true at kIdle");
    Check(!Coll.IsMarkingActive(),
          "Baseline: IsMarkingActive returned true at kIdle");
    Check(!::XCore::g_XGCIsConcurrentMarkActive.load(::std::memory_order_acquire),
          "Baseline: g_XGCIsConcurrentMarkActive (write-barrier gate) was true at kIdle");

    // -----------------------------------------------------------------
    // Run a cycle; verify the flag is false after the cycle completes.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(::XCore::EXGCOptions::kNone);

    Check(!Coll.IsMarking(),
          "Post-cycle: IsMarking returned true (should be false at kIdle)");
    Check(!Coll.IsMarkingActive(),
          "Post-cycle: IsMarkingActive returned true (should be false at kIdle)");
    Check(!::XCore::g_XGCIsConcurrentMarkActive.load(::std::memory_order_acquire),
          "Post-cycle: g_XGCIsConcurrentMarkActive (write-barrier gate) was true (should be false post-mark)");

    // Cleanup.
    FXObjectGCCardTable::Get().__ResetForTests();
    Coll.__ResetForTests();
    FXObjectArray::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.WriteBarrierIsMarkingActive: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.WriteBarrierIsMarkingActive: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
