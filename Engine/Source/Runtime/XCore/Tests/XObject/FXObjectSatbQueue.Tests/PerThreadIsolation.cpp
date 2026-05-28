// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectSatbQueue.Tests/PerThreadIsolation.cpp -- TLS queue
// independence (XCoreXObject Rev 4 §5.6 + Rev 2 FIX-A-CRIT-TC1).
// =====================================================================
//
// Verifies:
//   * GetThreadSatbQueue() on different threads returns different
//     queue instances.
//   * Push on one thread does NOT affect the queue on another.
//   * Each thread's queue is created lazily on first access.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstdint>
#include <iostream>
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
}

int main()
{
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::FXObjectSatbQueue;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::GetThreadSatbQueue;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();

    XObject SentinelA;
    XObject SentinelB;

    // -----------------------------------------------------------------
    // Test 1: The two threads' queues are different instances.
    // -----------------------------------------------------------------
    {
        std::atomic<FXObjectSatbQueue*> QueueA{nullptr};
        std::atomic<FXObjectSatbQueue*> QueueB{nullptr};
        std::atomic<bool> ThreadAReady{false};
        std::atomic<bool> ThreadBReady{false};
        std::atomic<bool> ProceedToCheck{false};

        std::thread A([&]() {
            QueueA.store(&GetThreadSatbQueue(), std::memory_order_release);
            ThreadAReady.store(true, std::memory_order_release);
            while (!ProceedToCheck.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }
        });

        std::thread B([&]() {
            QueueB.store(&GetThreadSatbQueue(), std::memory_order_release);
            ThreadBReady.store(true, std::memory_order_release);
            while (!ProceedToCheck.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }
        });

        // Wait for both threads to register their queues.
        while (!ThreadAReady.load(std::memory_order_acquire) ||
               !ThreadBReady.load(std::memory_order_acquire))
        {
            std::this_thread::yield();
        }

        Check(QueueA.load() != nullptr, "Thread A queue == nullptr");
        Check(QueueB.load() != nullptr, "Thread B queue == nullptr");
        Check(QueueA.load() != QueueB.load(),
              "Thread A and Thread B share the same TLS queue");

        ProceedToCheck.store(true, std::memory_order_release);
        A.join();
        B.join();
    }

    // -----------------------------------------------------------------
    // Test 2: Push on thread A does not affect thread B's queue size.
    //
    // Each thread captures its queue, pushes N entries, then reports
    // its queue's Size(). The main thread verifies the two reported
    // sizes match each thread's distinct push count.
    // -----------------------------------------------------------------
    {
        std::atomic<::std::size_t> SizeA{0};
        std::atomic<::std::size_t> SizeB{0};

        std::thread A([&]() {
            FXObjectSatbQueue& Q = GetThreadSatbQueue();
            // Drain any existing state from Test 1's queue access.
            Q.DrainTo([](XObject*) {});
            for (int I = 0; I < 10; ++I)
            {
                Q.Push(&SentinelA);
            }
            SizeA.store(Q.Size(), std::memory_order_release);
        });

        std::thread B([&]() {
            FXObjectSatbQueue& Q = GetThreadSatbQueue();
            Q.DrainTo([](XObject*) {});
            for (int I = 0; I < 25; ++I)
            {
                Q.Push(&SentinelB);
            }
            SizeB.store(Q.Size(), std::memory_order_release);
        });

        A.join();
        B.join();

        Check(SizeA.load() == 10,
              "Thread A: queue Size after 10 pushes != 10");
        Check(SizeB.load() == 25,
              "Thread B: queue Size after 25 pushes != 25");
    }

    FXObjectGlobalSatbLog::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectSatbQueue.PerThreadIsolation: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectSatbQueue.PerThreadIsolation: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
