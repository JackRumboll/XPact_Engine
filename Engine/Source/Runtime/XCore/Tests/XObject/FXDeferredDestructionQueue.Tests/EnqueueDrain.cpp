// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXDeferredDestructionQueue.Tests/EnqueueDrain.cpp -- Phase 5.h
// deferred-destruction queue surface (enqueue + drain + diagnostics).
// =====================================================================
//
// Verifies:
//   * Initial queue is empty.
//   * EnqueueAfterBeginDestroy increments Size.
//   * DrainOnePassWithBudget on an empty queue is a no-op.
//   * Drain with no live objects at the indices simply drops them
//     (defensive nullptr-slot path).
//   * Last-pass diagnostics are populated.
//
// This test exercises the QUEUE surface only. The end-to-end sweep
// integration (BeginDestroy dispatch -> deferred queue -> FinishDestroy)
// lives in FXObjectCollector.Tests/SweepReclaim.cpp.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXDeferredDestructionQueue.h"
#include "XObject/FXObjectArray.h"

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
    using ::XCore::FXDeferredDestructionQueue;
    using ::XCore::FXObjectArray;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXDeferredDestructionQueue& Queue =
        FXDeferredDestructionQueue::Get();
    Queue.__ResetForTests();

    // -----------------------------------------------------------------
    // Empty baseline.
    // -----------------------------------------------------------------
    Check(Queue.IsEmpty(), "Initial queue not empty");
    Check(Queue.Size() == 0, "Initial Size != 0");

    // Drain an empty queue: no-op + zero diagnostics.
    const ::std::size_t Drained0 = Queue.DrainOnePassWithBudget(1'000);
    Check(Drained0 == 0, "Drain empty queue returned non-zero");
    Check(Queue.GetLastPassFinishDestroyCount() == 0,
          "Empty-drain last-pass count != 0");

    // -----------------------------------------------------------------
    // Enqueue three indices that have no bound XObject (defensive
    // nullptr-slot path). Drain SHOULD simply drop them.
    //
    // The indices we enqueue are not 0 (which would be the null
    // sentinel rejected at the producer); we pick indices that are
    // structurally committed (the array commits the first 32k entries
    // at construction) but have no bound Object since we haven't
    // called BindObject.
    // -----------------------------------------------------------------
    Queue.EnqueueAfterBeginDestroy(101);
    Queue.EnqueueAfterBeginDestroy(202);
    Queue.EnqueueAfterBeginDestroy(303);

    Check(Queue.Size() == 3, "Size != 3 after three EnqueueAfterBeginDestroy");
    Check(!Queue.IsEmpty(), "IsEmpty returned true with 3 entries");

    const ::std::size_t Drained1 = Queue.DrainOnePassWithBudget(1'000);
    // All three entries' GetObjectAtIndexUnchecked returns nullptr
    // (no BindObject), so DrainOnePassWithBudget drops without
    // dispatching anything. Drained1 (FinishDestroy count) should be 0.
    Check(Drained1 == 0,
          "Drain with unbound entries returned non-zero FinishDestroy count");
    // All three should have been consumed (the drop path does not
    // re-enqueue).
    Check(Queue.Size() == 0, "Post-drop drain Size != 0");

    // -----------------------------------------------------------------
    // Null-sentinel guard: enqueue with index 0 is a no-op.
    // -----------------------------------------------------------------
    Queue.EnqueueAfterBeginDestroy(0);
    Check(Queue.Size() == 0, "Index-0 enqueue admitted an entry");

    // -----------------------------------------------------------------
    // Negative-index guard: enqueue with index <= 0 is a no-op.
    // -----------------------------------------------------------------
    Queue.EnqueueAfterBeginDestroy(-1);
    Check(Queue.Size() == 0, "Negative-index enqueue admitted an entry");

    Queue.__ResetForTests();
    FXObjectArray::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXDeferredDestructionQueue.EnqueueDrain: PASS\n";
        return 0;
    }
    std::cerr << "FXDeferredDestructionQueue.EnqueueDrain: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
