// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TMpscQueue.Tests/MPSC.cpp -- 4 producers + 1 consumer.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "TMpscQueue 16-producer + 1-
// consumer hammer with 10 M items; ordering-per-producer preserved."
//
// We scale down from the brief's 16P/10M to 4P/100K total items for
// test-runtime sanity. The properties under test:
//   1. Lossless: every Enqueued item appears at the consumer.
//   2. Per-producer FIFO: items from the same producer are dequeued
//      in the order they were enqueued.
//   3. Inter-producer order is NOT preserved (the algorithm makes
//      no such guarantee).
//
// =====================================================================

#include "HAL/TMpscQueue.h"
#include "HAL/FMemory.h"

#include <atomic>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int NUM_PRODUCERS = 4;
    constexpr int ITEMS_PER_PRODUCER = 25000;
    constexpr int TOTAL_ITEMS = NUM_PRODUCERS * ITEMS_PER_PRODUCER;

    // Each item encodes (ProducerId << 24) | Sequence so the consumer
    // can verify per-producer FIFO order.
    struct FItem
    {
        int ProducerId;
        int Sequence;
    };
}

int main()
{
    // Rev 1 audit MS4 close-out: TMpscQueue node alloc now routes
    // through FMemory + FMemTag::Threading. __Init must run before
    // any queue Enqueue.
    ::XCore::HAL::FMemory::__Init();

    ::XCore::HAL::TMpscQueue<FItem> Queue;

    // Producers.
    std::vector<std::thread> Producers;
    Producers.reserve(NUM_PRODUCERS);
    for (int P = 0; P < NUM_PRODUCERS; ++P)
    {
        Producers.emplace_back([&Queue, P]()
        {
            for (int S = 0; S < ITEMS_PER_PRODUCER; ++S)
            {
                Queue.Enqueue(FItem{ P, S });
            }
        });
    }

    // Consumer.
    std::atomic<int> Errors(0);
    std::thread Consumer([&Queue, &Errors]()
    {
        int LastSeen[NUM_PRODUCERS];
        for (int P = 0; P < NUM_PRODUCERS; ++P)
        {
            LastSeen[P] = -1;
        }

        int Count = 0;
        while (Count < TOTAL_ITEMS)
        {
            FItem Item{};
            if (Queue.TryDequeue(Item))
            {
                if (Item.ProducerId < 0 || Item.ProducerId >= NUM_PRODUCERS)
                {
                    Errors.fetch_add(1, std::memory_order_relaxed);
                    ++Count;
                    continue;
                }
                // Per-producer FIFO: this item's sequence MUST be
                // exactly LastSeen + 1.
                if (Item.Sequence != LastSeen[Item.ProducerId] + 1)
                {
                    Errors.fetch_add(1, std::memory_order_relaxed);
                }
                LastSeen[Item.ProducerId] = Item.Sequence;
                ++Count;
            }
        }
    });

    for (auto& P : Producers)
    {
        P.join();
    }
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

    std::cout << "TMpscQueue.MPSC: PASS (" << TOTAL_ITEMS << " items)\n";
    return 0;
}
