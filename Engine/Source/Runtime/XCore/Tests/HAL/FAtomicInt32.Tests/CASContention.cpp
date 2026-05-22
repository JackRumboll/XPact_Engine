// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FAtomicInt32.Tests/CASContention.cpp -- 8-thread CAS hammer.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "FAtomicInt32 CAS matches
// std::atomic semantics under contention for all memory_order values."
//
// 8 threads each perform N CAS-loops incrementing a shared counter.
// At the end, the counter must equal 8 * N (no lost updates; CAS
// successfully arbitrates between threads).
//
// =====================================================================

#include "HAL/FAtomicInt32.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <cstdint>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int NUM_THREADS = 8;
    constexpr int ITERATIONS_PER_THREAD = 10000;

    int RunFetchAddContention()
    {
        ::XCore::HAL::FAtomicInt32 Counter(0);

        std::vector<std::thread> Threads;
        Threads.reserve(NUM_THREADS);
        for (int T = 0; T < NUM_THREADS; ++T)
        {
            Threads.emplace_back([&Counter]()
            {
                for (int I = 0; I < ITERATIONS_PER_THREAD; ++I)
                {
                    // Discard the previous-value return.
                    (void)Counter.FetchAddRelaxed(1);
                }
            });
        }
        for (auto& Th : Threads)
        {
            Th.join();
        }

        const ::int32 Expected = NUM_THREADS * ITERATIONS_PER_THREAD;
        const ::int32 Actual   = Counter.LoadRelaxed();
        if (Actual != Expected)
        {
            std::cerr << "FAIL: FetchAdd race lost; expected " << Expected
                      << " got " << Actual << "\n";
            return 1;
        }

        return 0;
    }

    int RunCasLoopContention()
    {
        ::XCore::HAL::FAtomicInt32 Counter(0);

        // Each thread does a CAS-loop increment. The CAS-loop pattern
        // is the standard "implement atomic-increment via CAS" idiom;
        // verifies that CompareExchangeWeak's failure return correctly
        // updates Expected.
        std::vector<std::thread> Threads;
        Threads.reserve(NUM_THREADS);
        for (int T = 0; T < NUM_THREADS; ++T)
        {
            Threads.emplace_back([&Counter]()
            {
                for (int I = 0; I < ITERATIONS_PER_THREAD; ++I)
                {
                    ::int32 Old = Counter.LoadRelaxed();
                    while (!Counter.CompareExchangeWeak(
                               Old, Old + 1,
                               std::memory_order_acq_rel,
                               std::memory_order_relaxed))
                    {
                        // Old was updated by the failed CAS; retry.
                    }
                }
            });
        }
        for (auto& Th : Threads)
        {
            Th.join();
        }

        const ::int32 Expected = NUM_THREADS * ITERATIONS_PER_THREAD;
        const ::int32 Actual   = Counter.LoadRelaxed();
        if (Actual != Expected)
        {
            std::cerr << "FAIL: CAS-loop race lost; expected " << Expected
                      << " got " << Actual << "\n";
            return 1;
        }

        return 0;
    }
}

int main()
{
    int Result = 0;
    Result |= RunFetchAddContention();
    Result |= RunCasLoopContention();
    if (Result == 0)
    {
        std::cout << "FAtomicInt32.CASContention: PASS\n";
    }
    return Result;
}
