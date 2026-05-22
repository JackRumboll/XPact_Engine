// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformAtomics.Tests/CAS.cpp -- atomic-CAS contention check.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1. Verifies that 4 threads racing to
// InterlockedAdd a shared counter all 1000 iterations succeed and the
// final value equals the expected sum (4 * 1000 = 4000).
//
// The Section 7.7 spec test plan calls for "8-thread hammer" CAS
// contention; we use 4 threads here for Phase 1b (the 8-thread
// integration test lands once the threading-primitives module is up
// in Phase 1c). 4 threads is sufficient to expose races in the
// std::atomic_ref backing.
//
// =====================================================================

#include "HAL/FPlatformAtomics.h"

#include <cstdint>
#include <iostream>
#include <thread>
#include <vector>

int main()
{
    using ::XCore::HAL::FPlatformAtomics;

    constexpr int NUM_THREADS = 4;
    constexpr int ITERATIONS_PER_THREAD = 1000;

    std::int32_t Counter = 0;

    std::vector<std::thread> Threads;
    Threads.reserve(NUM_THREADS);
    for (int t = 0; t < NUM_THREADS; ++t)
    {
        Threads.emplace_back([&Counter]()
        {
            for (int i = 0; i < ITERATIONS_PER_THREAD; ++i)
            {
                // InterlockedAdd: returns the PREVIOUS value; we discard.
                (void)FPlatformAtomics::InterlockedAdd(&Counter, 1);
            }
        });
    }
    for (auto& T : Threads)
    {
        T.join();
    }

    const std::int32_t Expected = NUM_THREADS * ITERATIONS_PER_THREAD;
    if (Counter != Expected)
    {
        std::cerr << "FAIL: InterlockedAdd race lost; expected "
                  << Expected << " got " << Counter << "\n";
        return 1;
    }

    // Also exercise CAS directly: 4 threads racing to flip a sentinel
    // from 0 to their thread index. Exactly one CAS succeeds; the
    // other three observe failure and learn the sentinel value.
    std::int32_t Sentinel = 0;
    std::vector<std::int32_t> CASResults(NUM_THREADS, 0);
    std::vector<std::thread> CasThreads;
    CasThreads.reserve(NUM_THREADS);
    for (int t = 0; t < NUM_THREADS; ++t)
    {
        CasThreads.emplace_back([&Sentinel, &CASResults, t]()
        {
            // Try to flip 0 -> (t+1). The first thread wins.
            CASResults[t] = FPlatformAtomics::InterlockedCompareExchange(
                &Sentinel, t + 1, 0);
        });
    }
    for (auto& T : CasThreads)
    {
        T.join();
    }

    // The final Sentinel value must be one of 1..NUM_THREADS (whichever
    // thread won the race).
    if (Sentinel < 1 || Sentinel > NUM_THREADS)
    {
        std::cerr << "FAIL: CAS sentinel ended at " << Sentinel
                  << "; expected in [1, " << NUM_THREADS << "]\n";
        return 1;
    }

    // Count winners: exactly one thread's CAS returns 0 (the winner;
    // *Dest was 0 at the time of its CAS).
    int Winners = 0;
    for (int t = 0; t < NUM_THREADS; ++t)
    {
        if (CASResults[t] == 0)
        {
            ++Winners;
        }
    }
    if (Winners != 1)
    {
        std::cerr << "FAIL: CAS winner count " << Winners
                  << "; expected exactly 1\n";
        return 1;
    }

    std::cout << "CAS: PASS (counter=" << Counter
              << ", sentinel=" << Sentinel << ", winners=" << Winners << ")\n";
    return 0;
}
