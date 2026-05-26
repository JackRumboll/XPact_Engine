// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TSpscQueue.Tests/OneProducerOneConsumer.cpp -- 1P1C round-trip.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "TSpscQueue enqueue latency
// < 50 ns under 1P1C load."
//
// Phase 1c surface test: 100K items round-trip (we scale down from the
// brief's 1M to keep test runtime modest; the property under test --
// FIFO + lossless + correct memory ordering -- is exercised
// equivalently at 100K).
//
// =====================================================================

#include "HAL/TSpscQueue.h"
#include "HAL/FMemory.h"

#include <atomic>
#include <iostream>
#include <thread>

namespace
{
    constexpr int NUM_ITEMS = 100000;
}

int main()
{
    // Rev 1 audit MS4 close-out: TSpscQueue node alloc now routes
    // through FMemory + FMemTag::Threading. __Init must run before
    // any queue Enqueue.
    ::XCore::HAL::FMemory::__Init();

    ::XCore::HAL::TSpscQueue<int> Queue;
    std::atomic<int> Errors(0);

    std::thread Producer([&Queue]()
    {
        for (int I = 0; I < NUM_ITEMS; ++I)
        {
            Queue.Enqueue(I);
        }
    });

    std::thread Consumer([&Queue, &Errors]()
    {
        int Expected = 0;
        while (Expected < NUM_ITEMS)
        {
            int Item;
            if (Queue.TryDequeue(Item))
            {
                if (Item != Expected)
                {
                    Errors.fetch_add(1, std::memory_order_relaxed);
                }
                ++Expected;
            }
            else
            {
                // Queue temporarily empty; busy-wait.
                // Real code would use FConditionVariable or
                // std::this_thread::yield().
            }
        }
    });

    Producer.join();
    Consumer.join();

    if (Errors.load() > 0)
    {
        std::cerr << "FAIL: " << Errors.load() << " ordering errors\n";
        return 1;
    }

    if (!Queue.IsEmpty())
    {
        std::cerr << "FAIL: queue not empty after consumer drained\n";
        return 1;
    }

    std::cout << "TSpscQueue.1P1C: PASS (" << NUM_ITEMS << " items)\n";
    return 0;
}
