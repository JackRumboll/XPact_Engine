// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TBoundedMpscQueue.Tests/Backpressure.cpp -- fill then drain.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 fix B-C4 test plan: "TBoundedMpscQueue
// 16-producer + 1-consumer hammer at capacity 1024 with 10 M items;
// TryEnqueue returns false on overflow; no per-enqueue allocations
// confirmed via leak-tracker hooks."
//
// Phase 1c covers:
//   1. Single-thread fill-to-capacity: TryEnqueue returns true N
//      times, then false on the N+1th attempt.
//   2. Drain-then-refill: after draining, TryEnqueue succeeds again.
//   3. Multi-producer round-trip with bounded capacity.
//
// Allocation-free verification (no per-Enqueue alloc) is a Phase 1d
// follow-up gated on FLeakTracker integration; here we exercise the
// algorithm correctness.
//
// =====================================================================

#include "HAL/TBoundedMpscQueue.h"

#include <atomic>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr ::SIZE_T QUEUE_CAPACITY = 16;

    int RunFillToCapacity()
    {
        ::XCore::HAL::TBoundedMpscQueue<int, QUEUE_CAPACITY> Queue;
        Queue.Initialize();

        // Fill to exactly capacity.
        for (::SIZE_T I = 0; I < QUEUE_CAPACITY; ++I)
        {
            if (!Queue.TryEnqueue(static_cast<int>(I)))
            {
                std::cerr << "FAIL: TryEnqueue should succeed at i=" << I << "\n";
                return 1;
            }
        }

        // Next TryEnqueue should return false (queue full).
        if (Queue.TryEnqueue(999))
        {
            std::cerr << "FAIL: TryEnqueue should return false on full queue\n";
            return 1;
        }

        // Drain all elements; verify FIFO order.
        for (::SIZE_T I = 0; I < QUEUE_CAPACITY; ++I)
        {
            int Item = -1;
            if (!Queue.TryDequeue(Item))
            {
                std::cerr << "FAIL: TryDequeue should succeed at i=" << I << "\n";
                return 1;
            }
            if (Item != static_cast<int>(I))
            {
                std::cerr << "FAIL: FIFO violated; expected " << I
                          << " got " << Item << "\n";
                return 1;
            }
        }

        // TryDequeue on empty: false.
        int Item;
        if (Queue.TryDequeue(Item))
        {
            std::cerr << "FAIL: TryDequeue should return false on empty queue\n";
            return 1;
        }

        // After draining, TryEnqueue succeeds again.
        if (!Queue.TryEnqueue(42))
        {
            std::cerr << "FAIL: TryEnqueue should succeed after drain\n";
            return 1;
        }

        return 0;
    }

    int RunMultiProducer()
    {
        constexpr ::SIZE_T LargeCap = 1024;
        ::XCore::HAL::TBoundedMpscQueue<int, LargeCap> Queue;
        Queue.Initialize();

        constexpr int NUM_PRODUCERS = 4;
        constexpr int ITEMS_PER_PRODUCER = 250;
        constexpr int TOTAL = NUM_PRODUCERS * ITEMS_PER_PRODUCER;

        std::atomic<int> EnqueueFailures(0);
        std::vector<std::thread> Producers;
        for (int P = 0; P < NUM_PRODUCERS; ++P)
        {
            Producers.emplace_back([&Queue, &EnqueueFailures, P]()
            {
                for (int I = 0; I < ITEMS_PER_PRODUCER; ++I)
                {
                    const int Item = (P << 16) | I;
                    // Retry on full -- the consumer is draining
                    // concurrently so eventually we get through.
                    while (!Queue.TryEnqueue(Item))
                    {
                        std::this_thread::yield();
                    }
                }
            });
        }

        std::atomic<int> ItemsDrained(0);
        std::thread Consumer([&Queue, &ItemsDrained]()
        {
            int Item;
            while (ItemsDrained.load() < TOTAL)
            {
                if (Queue.TryDequeue(Item))
                {
                    ItemsDrained.fetch_add(1, std::memory_order_relaxed);
                }
            }
        });

        for (auto& P : Producers)
        {
            P.join();
        }
        Consumer.join();

        if (ItemsDrained.load() != TOTAL)
        {
            std::cerr << "FAIL: multi-producer drained "
                      << ItemsDrained.load() << ", expected " << TOTAL << "\n";
            return 1;
        }

        return 0;
    }

    int RunSizeApi()
    {
        ::XCore::HAL::TBoundedMpscQueue<int, 8> Queue;
        Queue.Initialize();

        if (Queue.Size() != 0)
        {
            std::cerr << "FAIL: initial Size should be 0\n";
            return 1;
        }

        if (Queue.Capacity() != 8)
        {
            std::cerr << "FAIL: Capacity should be 8\n";
            return 1;
        }

        (void)Queue.TryEnqueue(1);
        (void)Queue.TryEnqueue(2);
        (void)Queue.TryEnqueue(3);
        if (Queue.Size() != 3)
        {
            std::cerr << "FAIL: Size after 3 enqueues should be 3; got "
                      << Queue.Size() << "\n";
            return 1;
        }

        int Item;
        (void)Queue.TryDequeue(Item);
        if (Queue.Size() != 2)
        {
            std::cerr << "FAIL: Size after 1 dequeue should be 2\n";
            return 1;
        }

        return 0;
    }
}

int main()
{
    int Result = 0;
    Result |= RunFillToCapacity();
    Result |= RunMultiProducer();
    Result |= RunSizeApi();
    if (Result == 0)
    {
        std::cout << "TBoundedMpscQueue.Backpressure: PASS\n";
    }
    return Result;
}
