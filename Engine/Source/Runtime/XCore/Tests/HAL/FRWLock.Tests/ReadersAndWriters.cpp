// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FRWLock.Tests/ReadersAndWriters.cpp -- 4 readers + 1 writer.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "FRWLock writer-priority
// confirmed; reader-starvation absent under heavy reader load; TSan
// clean."
//
// 4 reader threads concurrently read a shared int; 1 writer thread
// increments it. The mutex serialises writes against any concurrent
// reads; readers may execute concurrently with each other.
//
// Verification:
//   * All reads see consistent values (no torn 32-bit read despite
//     std::atomic not being used internally).
//   * The writer's increments are all visible at the end.
//
// =====================================================================

#include "HAL/FRWLock.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int NUM_READERS = 4;
    constexpr int ITERATIONS_PER_READER = 1000;
    constexpr int WRITER_ITERATIONS = 100;
}

int main()
{
    ::XCore::HAL::FRWLock RwLock;
    int SharedValue = 0;
    std::atomic<int> ReadErrors(0);

    // Writer thread: increments under exclusive lock.
    std::thread Writer([&RwLock, &SharedValue]()
    {
        for (int I = 0; I < WRITER_ITERATIONS; ++I)
        {
            RwLock.LockExclusive();
            ++SharedValue;
            RwLock.UnlockExclusive();
        }
    });

    // Reader threads: read under shared lock; verify monotonicity.
    std::vector<std::thread> Readers;
    Readers.reserve(NUM_READERS);
    for (int R = 0; R < NUM_READERS; ++R)
    {
        Readers.emplace_back([&RwLock, &SharedValue, &ReadErrors]()
        {
            int LastSeen = 0;
            for (int I = 0; I < ITERATIONS_PER_READER; ++I)
            {
                RwLock.LockShared();
                const int Current = SharedValue;
                RwLock.UnlockShared();

                // Monotonicity: value only increases (writer never
                // decrements). Catch torn reads or write-write
                // race corruption.
                if (Current < LastSeen)
                {
                    ReadErrors.fetch_add(1, std::memory_order_relaxed);
                }
                if (Current < 0 || Current > WRITER_ITERATIONS)
                {
                    ReadErrors.fetch_add(1, std::memory_order_relaxed);
                }
                LastSeen = Current;
            }
        });
    }

    Writer.join();
    for (auto& R : Readers)
    {
        R.join();
    }

    if (SharedValue != WRITER_ITERATIONS)
    {
        std::cerr << "FAIL: writer increments lost; expected "
                  << WRITER_ITERATIONS << " got " << SharedValue << "\n";
        return 1;
    }

    if (ReadErrors.load() > 0)
    {
        std::cerr << "FAIL: " << ReadErrors.load() << " read errors\n";
        return 1;
    }

    // Also verify TryLock variants.
    ::XCore::HAL::FRWLock Other;
    if (!Other.TryLockShared())
    {
        std::cerr << "FAIL: TryLockShared on unheld lock should succeed\n";
        return 1;
    }
    Other.UnlockShared();

    if (!Other.TryLockExclusive())
    {
        std::cerr << "FAIL: TryLockExclusive on unheld lock should succeed\n";
        return 1;
    }
    Other.UnlockExclusive();

    std::cout << "FRWLock.ReadersAndWriters: PASS\n";
    return 0;
}
