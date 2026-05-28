// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXSweepCandidateQueue.Tests/AppendDrain.cpp -- producer/consumer
// round-trip (Phase 5.g producer + Phase 5.h consumer surface).
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXSweepCandidateQueue.h"

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
    using ::XCore::FXSweepCandidateQueue;

    ::XCore::HAL::FMemory::__Init();

    FXSweepCandidateQueue& Queue = FXSweepCandidateQueue::Get();
    Queue.__ResetForTests();

    // -----------------------------------------------------------------
    // Empty baseline.
    // -----------------------------------------------------------------
    Check(Queue.IsEmpty(), "Initial queue not empty");
    Check(Queue.Size() == 0, "Initial Size != 0");

    // -----------------------------------------------------------------
    // Append three; DrainAll observes in FIFO order.
    // -----------------------------------------------------------------
    Queue.Append(101);
    Queue.Append(202);
    Queue.Append(303);

    Check(Queue.Size() == 3, "Size != 3 after three Append");
    Check(!Queue.IsEmpty(), "IsEmpty returned true with 3 entries");

    std::vector<::std::int32_t> Drained;
    const ::std::size_t DrainedCount = Queue.DrainAll(
        [&](::std::int32_t Idx) noexcept { Drained.push_back(Idx); });

    Check(DrainedCount == 3, "DrainAll returned wrong count");
    Check(Drained.size() == 3, "Visitor invoked wrong number of times");
    Check(Drained[0] == 101 && Drained[1] == 202 && Drained[2] == 303,
          "FIFO order violated");

    Check(Queue.IsEmpty(), "Post-drain queue not empty");

    // -----------------------------------------------------------------
    // AppendBatch path.
    // -----------------------------------------------------------------
    {
        const ::std::int32_t Batch[] = { 10, 20, 30, 40, 50 };
        Queue.AppendBatch(Batch, 5);
        Check(Queue.Size() == 5, "Post-AppendBatch Size != 5");

        std::vector<::std::int32_t> Drained2;
        Queue.DrainAll(
            [&](::std::int32_t Idx) noexcept { Drained2.push_back(Idx); });
        Check(Drained2.size() == 5, "Batch drain count != 5");
        for (::std::size_t I = 0; I < 5; ++I)
        {
            Check(Drained2[I] == Batch[I],
                  "Batch drain order/value mismatch");
        }
    }

    // -----------------------------------------------------------------
    // Large append to force buffer growth.
    // -----------------------------------------------------------------
    {
        constexpr ::std::size_t kBig = 1000;
        for (::std::size_t I = 0; I < kBig; ++I)
        {
            Queue.Append(static_cast<::std::int32_t>(I));
        }
        Check(Queue.Size() == kBig,
              "Size after 1000 individual Appends != 1000");

        ::std::size_t Sum = 0;
        Queue.DrainAll(
            [&](::std::int32_t Idx) noexcept
            { Sum += static_cast<::std::size_t>(Idx); });
        // Expected: sum(0..999) = 999*1000/2 = 499500.
        Check(Sum == 499500,
              "Drained sum != expected 499500 (entries lost across grow)");
    }

    Queue.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXSweepCandidateQueue.AppendDrain: PASS\n";
        return 0;
    }
    std::cerr << "FXSweepCandidateQueue.AppendDrain: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
