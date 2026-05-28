// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectArray.Tests/ConcurrentLookupStress.cpp -- multi-reader +
// single-writer stress (XCoreXObject Rev 4 §3.3).
// =====================================================================
//
// Spec §3.3: "FRWLock-protected. Reads (Get / GetUnchecked /
// ForEachObject) acquire SHARED; mutations (AllocateEntry / FreeEntry
// / commit grow) acquire EXCLUSIVE."
//
// Verifies:
//
//   * 8 reader threads each perform 1000 GetObjectAtIndex calls.
//   * 1 writer thread performs AllocateEntry + FreeEntry cycles
//     concurrently with the readers.
//   * No torn reads: a returned non-null XObject* either equals the
//     expected pointer (for a known-live slot) or is nullptr (for a
//     freed-since-captured slot).
//   * No crash; no data race.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"

#include <atomic>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int kNumReaderThreads     = 8;
    constexpr int kReadsPerThread       = 1000;
    constexpr int kWriterIterations     = 1000;

    int g_FailureCount = 0;
    std::atomic<bool> g_StopWriter{false};
    std::atomic<::int32> g_TornReads{0};

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
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    // Pin some "fixed" entries that will live for the entire test.
    // Readers probe these expecting to see the bound object.
    constexpr int kNumFixedEntries = 16;
    XObject FixedObjs[kNumFixedEntries];
    ::int32 FixedIdx[kNumFixedEntries];
    ::uint32 FixedSerial[kNumFixedEntries];
    for (int I = 0; I < kNumFixedEntries; ++I)
    {
        FixedIdx[I] = Array.ReserveSlot(&FixedSerial[I]);
        FixedObjs[I].SerialNumber = FixedSerial[I];
        Array.BindObject(FixedIdx[I], &FixedObjs[I]);
    }

    // -----------------------------------------------------------------
    // Reader threads: each probes the fixed entries 1000 times and
    // verifies the returned XObject* equals the expected pointer.
    // -----------------------------------------------------------------
    std::vector<std::thread> Readers;
    Readers.reserve(kNumReaderThreads);
    for (int T = 0; T < kNumReaderThreads; ++T)
    {
        Readers.emplace_back([&Array, &FixedObjs, &FixedIdx, &FixedSerial]() {
            for (int I = 0; I < kReadsPerThread; ++I)
            {
                const int Slot = I % kNumFixedEntries;
                XObject* Got = Array.GetObjectAtIndex(
                    FixedIdx[Slot], FixedSerial[Slot]);
                if (Got != &FixedObjs[Slot])
                {
                    g_TornReads.fetch_add(1, std::memory_order_relaxed);
                }
            }
        });
    }

    // -----------------------------------------------------------------
    // Writer thread: allocates + frees transient entries in the
    // background to drive contention on the exclusive lock path.
    // -----------------------------------------------------------------
    std::thread Writer([&Array]() {
        XObject Trans;
        for (int I = 0; I < kWriterIterations; ++I)
        {
            const ::int32 Idx = Array.AllocateEntry(&Trans);
            Array.FreeEntry(Idx);
        }
    });

    for (auto& R : Readers) R.join();
    Writer.join();

    Check(g_TornReads.load() == 0,
          "Concurrent readers observed a torn / wrong GetObjectAtIndex "
          "result -- writer's exclusive lock did not isolate readers");

    // Cleanup.
    for (int I = 0; I < kNumFixedEntries; ++I)
    {
        Array.FreeEntry(FixedIdx[I]);
    }

    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectArray.ConcurrentLookupStress: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectArray.ConcurrentLookupStress: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
