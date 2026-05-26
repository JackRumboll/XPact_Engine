// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FEvent.Tests/ManualReset.cpp -- single Trigger wakes all waiters.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "FEvent multi-waiter wake."
//
// Manual-reset semantics: a single Trigger wakes ALL waiters and
// leaves the event in the triggered state until Reset() is called.
// We spawn 4 waiters, Trigger once, verify ALL four wake.
//
// =====================================================================

#include "HAL/FEvent.h"
#include "HAL/FMemory.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int NUM_WAITERS = 4;
}

int main()
{
    // Rev 1 audit MS3 close-out: FEvent::Create* now routes through
    // FMemory + FMemTag::Threading. __Init must run before any
    // Create* call.
    ::XCore::HAL::FMemory::__Init();

    ::XCore::HAL::FEventPtr E(::XCore::HAL::FEvent::CreateManualReset());
    if (!E)
    {
        std::cerr << "FAIL: CreateManualReset returned null\n";
        return 1;
    }

    std::atomic<int> WokenCount(0);

    std::vector<std::thread> Waiters;
    Waiters.reserve(NUM_WAITERS);
    for (int I = 0; I < NUM_WAITERS; ++I)
    {
        Waiters.emplace_back([&E, &WokenCount]()
        {
            if (E->Wait(2.0f))  // 2-second timeout
            {
                WokenCount.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    // Let all waiters reach Wait.
    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    // Trigger ONCE. Manual-reset wakes ALL waiters.
    E->Trigger();

    for (auto& W : Waiters)
    {
        W.join();
    }

    const int Woken = WokenCount.load();
    if (Woken != NUM_WAITERS)
    {
        std::cerr << "FAIL: manual-reset should wake all " << NUM_WAITERS
                  << " waiters; got " << Woken << "\n";
        return 1;
    }

    // Verify Reset clears the triggered state.
    E->Reset();
    if (E->Wait(0.05f))
    {
        std::cerr << "FAIL: after Reset, Wait should time out\n";
        return 1;
    }

    std::cout << "FEvent.ManualReset: PASS (woken=" << Woken << ")\n";
    return 0;
}
