// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FAtomicInt64.Tests/CASContention.cpp -- 8-thread compare-exchange
// hammer (Phase 1g fix MIN-2).
// =====================================================================
//
// XCore-4a Rev 3 Section 8.7 row 6: "8-thread CAS hammer; net delta
// = num_threads * iterations".
//
// Each of 8 worker threads performs 10,000 successful compare-and-
// exchange increments on a shared FAtomicInt64 (using
// CompareExchangeStrong in a loop). Post-join the counter must
// equal 8 * 10,000 = 80,000.
//
// =====================================================================

#include "HAL/FAtomicInt64.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <cstdio>
#include <thread>
#include <vector>

namespace
{
    constexpr ::int64 kIterationsPerThread = 10000;
    constexpr ::int32 kNumWorkers          = 8;

    ::XCore::HAL::FAtomicInt64 g_counter{static_cast<::int64>(0)};

    void HammerCAS()
    {
        for (::int64 I = 0; I < kIterationsPerThread; ++I)
        {
            ::int64 Expected = g_counter.Load(::std::memory_order_relaxed);
            while (!g_counter.CompareExchangeStrong(
                        Expected,
                        Expected + 1,
                        ::std::memory_order_relaxed,
                        ::std::memory_order_relaxed))
            {
                // CompareExchangeStrong updates Expected on failure;
                // retry with the new observed value.
            }
        }
    }

    int RunCASContention()
    {
        g_counter.Store(0, ::std::memory_order_relaxed);

        ::std::vector<::std::thread> Workers;
        Workers.reserve(kNumWorkers);
        for (::int32 T = 0; T < kNumWorkers; ++T)
        {
            Workers.emplace_back(HammerCAS);
        }
        for (auto& W : Workers)
        {
            W.join();
        }

        const ::int64 Expected = static_cast<::int64>(kNumWorkers) *
                                 kIterationsPerThread;
        const ::int64 Actual = g_counter.Load(::std::memory_order_relaxed);
        if (Actual != Expected)
        {
            std::fprintf(stderr,
                "FAIL: CAS contention final value = %lld, expected %lld.\n",
                static_cast<long long>(Actual),
                static_cast<long long>(Expected));
            return 1;
        }

        std::printf("PASS: FAtomicInt64 CAS contention (%d threads x %lld iters)\n",
                    kNumWorkers,
                    static_cast<long long>(kIterationsPerThread));
        return 0;
    }
}

int main()
{
    return RunCASContention();
}
