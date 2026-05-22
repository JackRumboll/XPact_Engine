// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FConditionVariable.Tests/WaitNotify.cpp -- producer/consumer flag.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "FConditionVariable::Wait
// (FMutex&) succeeds; FConditionVariable::Wait(FCriticalSection&)
// fails to compile via the concept constraint."
//
// Standard producer/consumer test:
//   * Consumer waits on cv until predicate (flag) is true.
//   * Producer sets the flag under the mutex and NotifyOne.
//   * Consumer wakes, observes the flag, finishes.
//
// Also covers NotifyAll: multiple consumers wake on broadcast.
//
// The fix M-5 concept-constraint compile-error test is at the end:
// trying to pass FCriticalSection to Wait should fail to compile.
// We don't run the compile-fail test in this .cpp (which would be a
// compilation negative; XBT's per-test runner handles those
// separately); we use static_assert to verify the concept rejects
// FCriticalSection.
//
// =====================================================================

#include "HAL/FConditionVariable.h"
#include "HAL/FMutex.h"
#include "HAL/FCriticalSection.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    // Compile-time concept verification.
    static_assert(::XCore::HAL::XIsNonRecursiveMutex<::XCore::HAL::FMutex>,
                  "FMutex must satisfy XIsNonRecursiveMutex");
    static_assert(!::XCore::HAL::XIsNonRecursiveMutex<::XCore::HAL::FCriticalSection>,
                  "FCriticalSection must NOT satisfy XIsNonRecursiveMutex");

    int RunSingleConsumer()
    {
        ::XCore::HAL::FMutex M;
        ::XCore::HAL::FConditionVariable Cv;

        bool Flag = false;
        std::atomic<bool> ConsumerDone(false);

        std::thread Consumer([&]()
        {
            ::XCore::HAL::FScopedMutexLock Lock(M);
            while (!Flag)
            {
                Cv.Wait(M);
            }
            ConsumerDone.store(true);
        });

        // Let consumer enter the wait loop.
        std::this_thread::sleep_for(std::chrono::milliseconds(50));

        {
            ::XCore::HAL::FScopedMutexLock Lock(M);
            Flag = true;
        }
        Cv.NotifyOne();

        Consumer.join();

        if (!ConsumerDone.load())
        {
            std::cerr << "FAIL: consumer did not complete\n";
            return 1;
        }

        return 0;
    }

    int RunNotifyAll()
    {
        ::XCore::HAL::FMutex M;
        ::XCore::HAL::FConditionVariable Cv;

        bool Flag = false;
        std::atomic<int> WokenCount(0);

        constexpr int NUM_CONSUMERS = 4;
        std::vector<std::thread> Consumers;
        for (int I = 0; I < NUM_CONSUMERS; ++I)
        {
            Consumers.emplace_back([&]()
            {
                ::XCore::HAL::FScopedMutexLock Lock(M);
                while (!Flag)
                {
                    Cv.Wait(M);
                }
                WokenCount.fetch_add(1, std::memory_order_relaxed);
            });
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));

        {
            ::XCore::HAL::FScopedMutexLock Lock(M);
            Flag = true;
        }
        Cv.NotifyAll();

        for (auto& C : Consumers)
        {
            C.join();
        }

        if (WokenCount.load() != NUM_CONSUMERS)
        {
            std::cerr << "FAIL: NotifyAll should wake all " << NUM_CONSUMERS
                      << "; woken=" << WokenCount.load() << "\n";
            return 1;
        }

        return 0;
    }

    int RunTimedWait()
    {
        ::XCore::HAL::FMutex M;
        ::XCore::HAL::FConditionVariable Cv;

        // No notifier; WaitFor should time out.
        ::XCore::HAL::FScopedMutexLock Lock(M);
        const bool Ok = Cv.WaitFor(M, 0.1f);  // 100 ms
        if (Ok)
        {
            std::cerr << "FAIL: WaitFor should have timed out\n";
            return 1;
        }
        return 0;
    }
}

int main()
{
    int Result = 0;
    Result |= RunSingleConsumer();
    Result |= RunNotifyAll();
    Result |= RunTimedWait();
    if (Result == 0)
    {
        std::cout << "FConditionVariable.WaitNotify: PASS\n";
    }
    return Result;
}
