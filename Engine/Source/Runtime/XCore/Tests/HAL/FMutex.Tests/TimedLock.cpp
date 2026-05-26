// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMutex.Tests/TimedLock.cpp -- HIGH-2 timed-lock close-out.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 + Rev 1 audit HIGH-2 close-out:
// FMutex::TryLockFor honours the supplied timeout.
//
// VERIFICATION:
//   1. Uncontended: TryLockFor(1 ms) succeeds.
//   2. Contended: TryLockFor(20 ms) against a 100 ms holder returns
//      false; elapsed time >= ~5 ms.
//   3. Holder releases before timeout: TryLockFor(200 ms) acquires.
//   4. Zero timeout = TryLock.
//
// NOTE: FMutex is non-recursive; same-thread re-acquire would
// deadlock per spec (this test does NOT exercise that path).
//
// =====================================================================

#include "HAL/FMutex.h"
#include "HAL/FTimespan.h"
#include "HAL/FPlatformTime.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>

namespace
{
    int RunTimedLock()
    {
        ::XCore::HAL::FMutex Mutex;

        // ---- Case 1: uncontended ----
        if (!Mutex.TryLockFor(::XCore::HAL::FTimespan::FromMilliseconds(1)))
        {
            std::cerr << "FAIL: TryLockFor on uncontended mutex returned false\n";
            return 1;
        }
        Mutex.Unlock();

        // ---- Case 2: timeout-expires while contended ----
        {
            std::atomic<bool> HolderReady(false);
            std::thread Holder([&Mutex, &HolderReady]()
            {
                Mutex.Lock();
                HolderReady.store(true, std::memory_order_release);
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                Mutex.Unlock();
            });
            while (!HolderReady.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }

            const double Before = ::XCore::HAL::FPlatformTime::Seconds();
            const bool Acquired = Mutex.TryLockFor(
                ::XCore::HAL::FTimespan::FromMilliseconds(20));
            const double After  = ::XCore::HAL::FPlatformTime::Seconds();

            if (Acquired)
            {
                std::cerr << "FAIL: TryLockFor(20ms) acquired while 100ms-holder was holding\n";
                Mutex.Unlock();
                Holder.join();
                return 1;
            }
            const double ElapsedSec = After - Before;
            if (ElapsedSec < 0.005)
            {
                std::cerr << "FAIL: TryLockFor(20ms) returned in "
                          << ElapsedSec << "s; expected at least ~5ms wait\n";
                Holder.join();
                return 1;
            }
            Holder.join();
        }

        // ---- Case 3: holder releases before timeout ----
        {
            std::atomic<bool> HolderReady(false);
            std::thread Holder([&Mutex, &HolderReady]()
            {
                Mutex.Lock();
                HolderReady.store(true, std::memory_order_release);
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                Mutex.Unlock();
            });
            while (!HolderReady.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }

            const double Before = ::XCore::HAL::FPlatformTime::Seconds();
            const bool Acquired = Mutex.TryLockFor(
                ::XCore::HAL::FTimespan::FromMilliseconds(200));
            const double After  = ::XCore::HAL::FPlatformTime::Seconds();

            if (!Acquired)
            {
                std::cerr << "FAIL: TryLockFor(200ms) should have acquired after 5ms holder\n";
                Holder.join();
                return 1;
            }
            const double ElapsedSec = After - Before;
            if (ElapsedSec > 0.18)
            {
                std::cerr << "FAIL: TryLockFor took " << ElapsedSec
                          << "s; expected acquire shortly after the 5ms release\n";
                Mutex.Unlock();
                Holder.join();
                return 1;
            }
            Mutex.Unlock();
            Holder.join();
        }

        // ---- Case 4: zero timeout = non-blocking ----
        Mutex.Lock();
        if (Mutex.TryLockFor(::XCore::HAL::FTimespan::FromMilliseconds(0)))
        {
            std::cerr << "FAIL: TryLockFor(0ms) should fail on a held mutex\n";
            return 1;
        }
        Mutex.Unlock();

        std::cout << "PASS: FMutex.TimedLock (HIGH-2 timed variant honours timeout)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunTimedLock();
}
