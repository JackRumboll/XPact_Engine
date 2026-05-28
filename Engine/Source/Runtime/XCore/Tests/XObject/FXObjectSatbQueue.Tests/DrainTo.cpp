// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectSatbQueue.Tests/DrainTo.cpp -- visitor drain FIFO order
// (XCoreXObject Rev 4 §5.6).
// =====================================================================
//
// Verifies:
//   * DrainTo invokes the visitor exactly N times for N pushed
//     entries.
//   * The order is FIFO (push order).
//   * The queue is empty after the drain.
//   * Empty queue + DrainTo is a no-op.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

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

    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();

    FXObjectSatbQueue Queue;

    // 8 distinct sentinel XObjects so we can verify FIFO order.
    XObject Sentinels[8];

    // -----------------------------------------------------------------
    // Test 1: Empty queue -- DrainTo is no-op.
    // -----------------------------------------------------------------
    {
        int Visited = 0;
        Queue.DrainTo([&Visited](XObject* /*OldValue*/) { ++Visited; });
        Check(Visited == 0, "Empty queue DrainTo invoked visitor");
        Check(Queue.Size() == 0, "Empty queue post-Drain Size != 0");
    }

    // -----------------------------------------------------------------
    // Test 2: Push 8; DrainTo visits in FIFO order.
    // -----------------------------------------------------------------
    {
        for (int I = 0; I < 8; ++I)
        {
            Queue.Push(&Sentinels[I]);
        }
        Check(Queue.Size() == 8, "After 8 pushes: Size != 8");

        std::vector<XObject*> Visited;
        Queue.DrainTo([&Visited](XObject* OldValue) {
            Visited.push_back(OldValue);
        });

        Check(Visited.size() == 8, "DrainTo visit count != 8");
        if (Visited.size() == 8)
        {
            for (int I = 0; I < 8; ++I)
            {
                Check(Visited[I] == &Sentinels[I],
                      "Visited entry FIFO order violation");
            }
        }
        Check(Queue.Size() == 0, "After Drain: Size != 0");
    }

    // -----------------------------------------------------------------
    // Test 3: Re-push + re-drain works.
    // -----------------------------------------------------------------
    {
        Queue.Push(&Sentinels[0]);
        Queue.Push(&Sentinels[1]);

        std::vector<XObject*> Visited;
        Queue.DrainTo([&Visited](XObject* OldValue) {
            Visited.push_back(OldValue);
        });
        Check(Visited.size() == 2, "Re-Drain visit count != 2");
        if (Visited.size() == 2)
        {
            Check(Visited[0] == &Sentinels[0], "Re-Drain FIFO [0] wrong");
            Check(Visited[1] == &Sentinels[1], "Re-Drain FIFO [1] wrong");
        }
    }

    FXObjectGlobalSatbLog::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectSatbQueue.DrainTo: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectSatbQueue.DrainTo: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
