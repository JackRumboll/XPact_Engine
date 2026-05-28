// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGlobalSatbLog.Tests/ConcurrentAppend.cpp -- 8-thread
// AppendBatch stress (XCoreXObject Rev 4 §4.3 + §5.6).
// =====================================================================
//
// Verifies:
//   * 8 threads each performing 100 AppendBatch calls of 16 entries
//     each = 8 * 100 * 16 = 12 800 total entries.
//   * Final Log.Size() == 12 800.
//   * No torn state; no data race observed under TSan-style assertion
//     (the lock-protected append must serialise).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstdint>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int kNumThreads        = 8;
    constexpr int kAppendsPerThread  = 100;
    constexpr int kEntriesPerAppend  = 16;
    constexpr int kExpectedTotal     =
        kNumThreads * kAppendsPerThread * kEntriesPerAppend;

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
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectGlobalSatbLog& Log = FXObjectGlobalSatbLog::Get();
    Log.__ResetForTests();

    // 16 sentinels per thread, distinct so each thread can verify it
    // sees its own batch's pointers.
    std::vector<XObject> Sentinels(kNumThreads * kEntriesPerAppend);

    std::vector<std::thread> Threads;
    Threads.reserve(kNumThreads);

    for (int T = 0; T < kNumThreads; ++T)
    {
        Threads.emplace_back([&Log, &Sentinels, T]() {
            // Each thread's batch is its slice of the sentinel array.
            XObject* Batch[kEntriesPerAppend];
            for (int I = 0; I < kEntriesPerAppend; ++I)
            {
                Batch[I] = &Sentinels[T * kEntriesPerAppend + I];
            }
            for (int I = 0; I < kAppendsPerThread; ++I)
            {
                Log.AppendBatch(Batch, kEntriesPerAppend);
            }
        });
    }

    for (auto& Th : Threads) Th.join();

    // -----------------------------------------------------------------
    // Final assertion: total count.
    // -----------------------------------------------------------------
    Check(Log.Size() == static_cast<::std::size_t>(kExpectedTotal),
          "8-thread AppendBatch stress: final Size != 12800");

    // -----------------------------------------------------------------
    // Drain + verify every entry is one of the sentinels (no torn /
    // garbage values).
    // -----------------------------------------------------------------
    XObject* const SentinelStart = &Sentinels[0];
    XObject* const SentinelEnd =
        &Sentinels[0] + (kNumThreads * kEntriesPerAppend);
    ::std::size_t SanityViolations = 0;
    Log.DrainAll([&SanityViolations, SentinelStart, SentinelEnd](
                     XObject* OldValue) {
        if (OldValue < SentinelStart || OldValue >= SentinelEnd)
        {
            ++SanityViolations;
        }
    });
    Check(SanityViolations == 0,
          "Drained entries contained out-of-range / torn pointers");

    Log.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGlobalSatbLog.ConcurrentAppend: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGlobalSatbLog.ConcurrentAppend: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
