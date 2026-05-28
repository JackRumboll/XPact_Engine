// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/SingletonAndPhase.cpp -- collector singleton
// + phase-state-machine baseline (Phase 5.g).
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectCollector.h"

#include <iostream>

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
    using ::XCore::FXObjectCollector;
    using ::XCore::EXGCPhase;

    ::XCore::HAL::FMemory::__Init();

    FXObjectCollector& Coll = FXObjectCollector::Get();
    Coll.__ResetForTests();

    // -----------------------------------------------------------------
    // Singleton stability.
    // -----------------------------------------------------------------
    {
        FXObjectCollector& Other = FXObjectCollector::Get();
        Check(&Coll == &Other, "Get() did not return a stable singleton");
    }

    // -----------------------------------------------------------------
    // Initial state: phase = kIdle; cycle counter 0; reachability
    // index 0; not marking.
    // -----------------------------------------------------------------
    {
        Check(Coll.GetPhase() == EXGCPhase::kIdle,
              "Initial phase != kIdle");
        Check(Coll.GetCycleCounter() == 0,
              "Initial cycle counter != 0");
        Check(Coll.GetCurrentReachabilityIndex() == 0,
              "Initial reachability index != 0");
        Check(Coll.CurrentCycleReachabilityMask() == (1u << 0),
              "Initial mask != bit 0");
        Check(!Coll.IsMarking(),
              "Initial IsMarking returned true (should be false at kIdle)");
        Check(!Coll.IsMarkingActive(),
              "Initial IsMarkingActive returned true");
    }

    // -----------------------------------------------------------------
    // Reset is idempotent.
    // -----------------------------------------------------------------
    {
        Coll.__ResetForTests();
        Coll.__ResetForTests();
        Check(Coll.GetPhase() == EXGCPhase::kIdle,
              "Double-Reset failed to leave at kIdle");
        Check(Coll.GetCycleCounter() == 0,
              "Double-Reset failed to zero cycle counter");
    }

    Coll.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.SingletonAndPhase: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.SingletonAndPhase: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
