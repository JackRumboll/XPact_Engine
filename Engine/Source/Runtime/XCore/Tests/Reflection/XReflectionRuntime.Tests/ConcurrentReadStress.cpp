// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/ConcurrentReadStress.cpp -- gate E3.
// =====================================================================
//
// XCore-4b Rev 4, Section 13 Acceptance gate E3:
//
//   "E3. Concurrent FindClass + RegisterType: 1k concurrent FindClass
//        calls + 100 RegisterType calls across 16 threads completes
//        TSan-clean."
//
// Scaled-down variant: 10 reader threads x 1000 FindClass calls each
// concurrent with 1 writer thread doing 100 RegisterClass calls. The
// full 16-thread variant ships at Phase 4b.7+ CI.
//
// The test pre-registers a baseline of 10 classes; the writer thread
// adds 100 more during the test window; readers concurrently call
// FindClass on the baseline + on names that may or may not be
// registered yet. The reader thread's only assertion is that
// FindClass either returns a valid (pointer == known-fixture) result
// or nullptr -- never a corrupt pointer that crashes on dereference.
//
// To validate the "never corrupt" property without an MSan/TSan run
// in the unit-test loop, the reader probes the returned FClass*'s
// GetFName() and verifies it round-trips to the expected FName for
// non-null returns. A torn read would surface as a mismatched FName
// or a segfault on the pointer dereference.
//
// =====================================================================

#include "XReflectionRuntime.h"

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "Reflection/FStruct.h"

#include <atomic>
#include <cstdio>
#include <iostream>
#include <memory>
#include <thread>
#include <vector>

namespace
{
    int g_FailureCount = 0;

    void Check(bool Condition, const char* Diagnostic)
    {
        if (!Condition)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++g_FailureCount;
        }
    }

    constexpr int kNumReaderThreads = 10;
    constexpr int kFindCallsPerReader = 1000;
    constexpr int kNumWriterClasses = 100;
    constexpr int kBaselineClasses = 10;
}

int main()
{
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FStruct;
    using ::XCore::Reflect::XReflectionRuntime;

    ::XCore::HAL::FMemory::__Init();

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // Set up the baseline: 10 classes named "Base_0" through "Base_9".
    //
    // FClass is non-copyable + non-movable (it owns a TArray +
    // std::atomic; FStruct deletes both copy and move). We can't
    // store FClass-by-value in a std::vector; instead we hold
    // std::unique_ptr<FClass>, which is move-only and works with
    // std::vector::reserve. The unique_ptr's contained address is
    // stable for the lifetime of the unique_ptr.
    // -----------------------------------------------------------------
    std::vector<std::unique_ptr<FClass>> Baseline;
    Baseline.reserve(kBaselineClasses);
    for (int I = 0; I < kBaselineClasses; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "Base_%d", I);
        Baseline.emplace_back(std::make_unique<FClass>(FName(NameBuf), nullptr));
    }
    for (int I = 0; I < kBaselineClasses; ++I)
    {
        Check(XReflectionRuntime::RegisterClass(Baseline[I].get()),
              "Baseline RegisterClass failed");
    }

    // The writer's 100 classes; same unique_ptr wrapping for stable
    // addresses.
    std::vector<std::unique_ptr<FClass>> WriterFixtures;
    WriterFixtures.reserve(kNumWriterClasses);
    for (int I = 0; I < kNumWriterClasses; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "Writer_%d", I);
        WriterFixtures.emplace_back(std::make_unique<FClass>(FName(NameBuf), nullptr));
    }

    // -----------------------------------------------------------------
    // Reader thread body.
    //
    // Each reader does kFindCallsPerReader iterations; each iteration
    // probes either a baseline name (always-resolves) or a writer name
    // (may-or-may-not-resolve depending on the writer's progress).
    //
    // For baseline lookups the result MUST be non-null and the
    // returned FClass*'s GetFName() MUST equal the expected FName.
    //
    // For writer lookups the result is nullptr OR a non-null pointer
    // whose GetFName equals the expected FName; never a torn read.
    // -----------------------------------------------------------------
    std::atomic<int> ReaderFailures{0};
    std::atomic<bool> StartFlag{false};

    auto ReaderBody = [&](int ThreadId)
    {
        // Spin until all threads are ready (to maximise overlap).
        while (!StartFlag.load(std::memory_order_acquire))
        {
            // busy-wait
        }

        for (int Iter = 0; Iter < kFindCallsPerReader; ++Iter)
        {
            const int Choice = (ThreadId * 7 + Iter * 3) % 2;
            char NameBuf[32];
            const FClass* Expected = nullptr;
            if (Choice == 0)
            {
                // Baseline lookup.
                const int Idx = (Iter + ThreadId) % kBaselineClasses;
                std::snprintf(NameBuf, sizeof(NameBuf), "Base_%d", Idx);
                Expected = Baseline[Idx].get();
                const FClass* Found = XReflectionRuntime::FindClass(FName(NameBuf));
                if (Found != Expected)
                {
                    ReaderFailures.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }
                // Dereference to validate the pointer's integrity.
                if (!(Found->GetFName() == FName(NameBuf)))
                {
                    ReaderFailures.fetch_add(1, std::memory_order_relaxed);
                }
            }
            else
            {
                // Writer lookup. May be null (writer hasn't gotten to
                // this slot yet) or non-null (writer has registered).
                const int Idx = (Iter * 11 + ThreadId * 17) % kNumWriterClasses;
                std::snprintf(NameBuf, sizeof(NameBuf), "Writer_%d", Idx);
                const FClass* Found = XReflectionRuntime::FindClass(FName(NameBuf));
                if (Found != nullptr)
                {
                    // Non-null must be the expected fixture pointer with
                    // the expected FName.
                    if (Found != WriterFixtures[Idx].get()
                        || !(Found->GetFName() == FName(NameBuf)))
                    {
                        ReaderFailures.fetch_add(1, std::memory_order_relaxed);
                    }
                }
            }
        }
    };

    // -----------------------------------------------------------------
    // Writer thread body.
    //
    // Registers WriterFixtures[0] through WriterFixtures[99] one at a
    // time. Each RegisterClass call grabs the exclusive lock; the
    // reader threads will momentarily block during each registration.
    // -----------------------------------------------------------------
    std::atomic<int> WriterFailures{0};

    auto WriterBody = [&]()
    {
        while (!StartFlag.load(std::memory_order_acquire))
        {
            // busy-wait
        }

        for (int I = 0; I < kNumWriterClasses; ++I)
        {
            if (!XReflectionRuntime::RegisterClass(WriterFixtures[I].get()))
            {
                WriterFailures.fetch_add(1, std::memory_order_relaxed);
            }
        }
    };

    // Spawn threads.
    std::vector<std::thread> Readers;
    Readers.reserve(kNumReaderThreads);
    for (int I = 0; I < kNumReaderThreads; ++I)
    {
        Readers.emplace_back(ReaderBody, I);
    }
    std::thread Writer(WriterBody);

    // Release the start flag.
    StartFlag.store(true, std::memory_order_release);

    // Join.
    for (auto& T : Readers)
    {
        T.join();
    }
    Writer.join();

    Check(ReaderFailures.load() == 0,
          "Reader threads observed inconsistent FindClass results");
    Check(WriterFailures.load() == 0,
          "Writer thread RegisterClass failures (collision or other)");

    // Post-test: every writer fixture must be findable.
    for (int I = 0; I < kNumWriterClasses; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "Writer_%d", I);
        const FClass* Found = XReflectionRuntime::FindClass(FName(NameBuf));
        if (Found != WriterFixtures[I].get())
        {
            Check(false, "Post-stress: writer fixture not findable");
            break;
        }
    }

    // Final count = baseline + writer.
    Check(XReflectionRuntime::GetClassCount() == kBaselineClasses + kNumWriterClasses,
          "Post-stress GetClassCount != baseline + writer");

    XReflectionRuntime::EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.ConcurrentReadStress: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.ConcurrentReadStress: PASS\n";
    return 0;
}
