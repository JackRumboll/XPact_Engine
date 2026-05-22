// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCriticalSection.Tests/Contention.cpp -- 8-thread contention hammer.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7: "10000 contended locks across 8 threads".
//
// Each thread acquires the same FCriticalSection, increments a shared
// (non-atomic) counter, releases. Final counter must equal
// 8 * 10000 = 80000.
//
// =====================================================================

#include "HAL/FCriticalSection.h"
#include "Macros/XCoreTypes.h"

#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int NUM_THREADS = 8;
    constexpr int ITERATIONS_PER_THREAD = 10000;
}

int main()
{
    ::XCore::HAL::FCriticalSection Cs;
    int Counter = 0;  // non-atomic; the mutex must serialise

    std::vector<std::thread> Threads;
    Threads.reserve(NUM_THREADS);
    for (int T = 0; T < NUM_THREADS; ++T)
    {
        Threads.emplace_back([&Cs, &Counter]()
        {
            for (int I = 0; I < ITERATIONS_PER_THREAD; ++I)
            {
                ::XCore::HAL::FScopedLock Lock(Cs);
                ++Counter;
            }
        });
    }
    for (auto& Th : Threads)
    {
        Th.join();
    }

    const int Expected = NUM_THREADS * ITERATIONS_PER_THREAD;
    if (Counter != Expected)
    {
        std::cerr << "FAIL: counter mismatch; expected " << Expected
                  << " got " << Counter << "\n";
        return 1;
    }

    std::cout << "FCriticalSection.Contention: PASS (counter=" << Counter << ")\n";
    return 0;
}
