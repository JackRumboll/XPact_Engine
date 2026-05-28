// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectSatbQueue.Tests/Capacity.cpp -- kCapacity-entry fill +
// auto-drain behaviour (XCoreXObject Rev 4 §5.6 + Rev 2 FIX-A-MED-37).
// =====================================================================
//
// Verifies:
//   * Pushing kCapacity (256) entries fills the queue.
//   * IsFull() reports true at exactly kCapacity.
//   * Pushing one more entry triggers an auto-drain into the global
//     SATB log; the queue resets to Size() == 1 (the just-pushed
//     entry).
//   * After drain, the global SATB log contains the prior 256 entries.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

#include <cstdint>
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
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::FXObjectSatbQueue;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();

    FXObjectSatbQueue Queue;
    XObject Sentinel;

    // -----------------------------------------------------------------
    // Test 1: Push kCapacity entries -> queue full.
    // -----------------------------------------------------------------
    {
        for (::std::size_t I = 0; I < FXObjectSatbQueue::kCapacity; ++I)
        {
            Queue.Push(&Sentinel);
        }
        Check(Queue.Size() == FXObjectSatbQueue::kCapacity,
              "After kCapacity pushes: Size != kCapacity");
        Check(Queue.IsFull(),
              "After kCapacity pushes: IsFull != true");
    }

    // -----------------------------------------------------------------
    // Test 2: One more push -- triggers auto-drain into the global log.
    // -----------------------------------------------------------------
    {
        const ::std::size_t LogSizeBefore = FXObjectGlobalSatbLog::Get().Size();

        Queue.Push(&Sentinel);

        // Queue should have just the one new entry post-drain.
        Check(Queue.Size() == 1, "Post-drain queue Size != 1");
        Check(!Queue.IsFull(), "Post-drain queue IsFull != false");

        // Global log should have the prior 256 entries.
        const ::std::size_t LogSizeAfter = FXObjectGlobalSatbLog::Get().Size();
        Check(LogSizeAfter == LogSizeBefore + FXObjectSatbQueue::kCapacity,
              "Global log did not receive 256 drained entries");
    }

    FXObjectGlobalSatbLog::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectSatbQueue.Capacity: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectSatbQueue.Capacity: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
