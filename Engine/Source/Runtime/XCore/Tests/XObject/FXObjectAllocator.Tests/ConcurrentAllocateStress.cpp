// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/ConcurrentAllocateStress.cpp -- multi-thread
// AllocateRaw + Deallocate stress (XCoreXObject Rev 4 §3.7).
// =====================================================================
//
// Spec §3.7: "FRWLock protected. The lock is acquired SHARED for
// AllocateRaw when the class pool's free-list is non-empty (the fast
// path; no contention with other allocators). The lock is acquired
// EXCLUSIVE for slab-grow, Deallocate, RegisterClassPool, ...".
//
// Phase 5.b body comment update: actually the Phase 5.b body acquires
// EXCLUSIVE for AllocateRaw too (per the lock-discipline simplification
// noted in FXObjectAllocator.cpp). The thread-safety contract is the
// same: under contention NO torn state, no double-free, no live count
// drift.
//
// VERIFIES:
//
//   * 8 threads each perform 1000 Allocate + Deallocate cycles.
//   * Each thread uses its own FClass instance to exercise the per-
//     FClass free-list under contention.
//   * Post-run, GetStats reports zero live objects (all cells freed).
//   * No assertion fires; no crash; no data race surfaces.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectAllocatorStats.h"

#include <atomic>
#include <cstdint>
#include <iostream>
#include <memory>
#include <thread>
#include <vector>

namespace
{
    constexpr int kNumThreads          = 8;
    constexpr int kIterationsPerThread = 1000;

    int g_FailureCount = 0;
    std::atomic<::int32> g_TotalAllocs{0};
    std::atomic<::int32> g_TotalFrees{0};

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
    using ::XCore::FXObjectAllocator;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    Allocator.__ResetForTests();

    // FClass is non-copyable + non-movable (it carries a non-copyable
    // std::atomic + non-copyable TArray); we cannot put FClass instances
    // into std::vector. Use std::unique_ptr<FClass>[N] for per-thread
    // FClass storage; each pointer is stable for the test lifetime.
    static constexpr ::int32 kSizes[kNumThreads] = {
        80, 100, 120, 200, 300, 400, 500, 800
    };
    std::unique_ptr<FClass> Classes[kNumThreads];
    for (int T = 0; T < kNumThreads; ++T)
    {
        Classes[T] = std::make_unique<FClass>(FName("StressClass"), nullptr);
        Classes[T]->PropertiesSize = kSizes[T];
        Classes[T]->MinAlignment   = 8;
        Allocator.RegisterClassPool(Classes[T].get());
    }

    std::vector<std::thread> Threads;
    Threads.reserve(kNumThreads);

    for (int T = 0; T < kNumThreads; ++T)
    {
        FClass* MyClass = Classes[T].get();
        Threads.emplace_back([&Allocator, T, MyClass]() {
            for (int I = 0; I < kIterationsPerThread; ++I)
            {
                const ::SIZE_T Size =
                    static_cast<::SIZE_T>(MyClass->PropertiesSize);
                void* Cell = Allocator.AllocateRaw(Size, 8, MyClass);
                if (Cell == nullptr)
                {
                    // AllocateRaw is noexcept + infallible-style; nullptr
                    // would be a real failure.
                    std::cerr << "FAIL: thread " << T
                              << " AllocateRaw returned nullptr\n";
                    return;
                }
                g_TotalAllocs.fetch_add(1, std::memory_order_relaxed);

                // Touch the cell to force any data-race detector to
                // surface the issue.
                static_cast<::std::uint8_t*>(Cell)[0] = static_cast<::std::uint8_t>(T);

                Allocator.Deallocate(Cell);
                g_TotalFrees.fetch_add(1, std::memory_order_relaxed);
            }
        });
    }

    for (auto& Th : Threads) Th.join();

    Check(g_TotalAllocs.load() == kNumThreads * kIterationsPerThread,
          "Concurrent: allocate count mismatch");
    Check(g_TotalFrees.load() == kNumThreads * kIterationsPerThread,
          "Concurrent: free count mismatch");

    // Post-run: GetStats should report zero live objects (every alloc
    // was paired with a free).
    {
        ::XCore::FXObjectAllocatorStats Stats = Allocator.GetStats();
        Check(Stats.TotalLiveObjects == 0,
              "Concurrent: TotalLiveObjects != 0 after balanced "
              "allocate/free run");
    }

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.ConcurrentAllocateStress: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.ConcurrentAllocateStress: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
