// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectSatbQueue.Tests/Push.cpp -- per-thread queue push fast path
// (XCoreXObject Rev 4 §5.6).
// =====================================================================
//
// Verifies:
//   * Push 1 entry -> Size() == 1.
//   * Push N entries -> Size() == N.
//   * Push interleaved with Drain returns Size() == 0 + Drain returns
//     N entries.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstdint>
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
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::FXObjectSatbQueue;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    // Pre-condition: drains accepted; concurrent mark INACTIVE (the
    // tests below directly invoke Push on a local queue, NOT via the
    // write barrier).
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();

    FXObjectSatbQueue Queue;
    Check(Queue.Size() == 0, "Fresh queue: Size != 0");

    // Sentinel XObject for non-null pointers. The queue stores raw
    // pointers; the test never dereferences them.
    XObject Sentinel;

    // -----------------------------------------------------------------
    // Test 1: Push 1 entry.
    // -----------------------------------------------------------------
    {
        Queue.Push(&Sentinel);
        Check(Queue.Size() == 1, "Post 1-push: Size != 1");
    }

    // -----------------------------------------------------------------
    // Test 2: Push 5 more entries.
    // -----------------------------------------------------------------
    {
        for (int I = 0; I < 5; ++I)
        {
            Queue.Push(&Sentinel);
        }
        Check(Queue.Size() == 6, "Post 6-push: Size != 6");
    }

    // -----------------------------------------------------------------
    // Test 3: Drain via visitor; queue empties.
    // -----------------------------------------------------------------
    {
        int VisitCount = 0;
        Queue.DrainTo([&VisitCount](XObject* /*OldValue*/) {
            ++VisitCount;
        });
        Check(VisitCount == 6, "DrainTo visited count != 6");
        Check(Queue.Size() == 0, "Post-Drain: Size != 0");
    }

    // -----------------------------------------------------------------
    // Test 4: Push after drain works.
    // -----------------------------------------------------------------
    {
        Queue.Push(&Sentinel);
        Queue.Push(&Sentinel);
        Check(Queue.Size() == 2, "Post-Drain re-Push: Size != 2");
    }

    // -----------------------------------------------------------------
    // Test 5: IsFull predicate.
    // -----------------------------------------------------------------
    {
        // Queue currently has 2 entries; not full.
        Check(!Queue.IsFull(), "Queue with 2 entries reports IsFull");
    }

    FXObjectGlobalSatbLog::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectSatbQueue.Push: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectSatbQueue.Push: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
