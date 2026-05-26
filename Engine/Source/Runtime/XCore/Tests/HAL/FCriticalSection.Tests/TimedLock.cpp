// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCriticalSection.Tests/TimedLock.cpp -- HIGH-2 timed-lock close-out.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 + Rev 1 audit HIGH-2 close-out:
// FCriticalSection::TryLockFor honours the supplied timeout.
//
// VERIFICATION:
//   1. Uncontended: TryLockFor(1 ms) succeeds.
//   2. Recursive same-thread acquire: TryLockFor on a section the
//      calling thread already holds succeeds immediately (recursive
//      semantics preserved).
//   3. Contended (different thread holds): TryLockFor(20 ms) returns
//      false; elapsed time is at least ~5 ms.
//   4. Holder releases before timeout: TryLockFor(200 ms) acquires.
//
// =====================================================================

#include "HAL/FCriticalSection.h"
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
        ::XCore::HAL::FCriticalSection Section;

        // ---- Case 1: uncontended ----
        if (!Section.TryLockFor(::XCore::HAL::FTimespan::FromMilliseconds(1)))
        {
            std::cerr << "FAIL: TryLockFor on uncontended section returned false\n";
            return 1;
        }

        // ---- Case 2: recursive same-thread ----
        if (!Section.TryLockFor(::XCore::HAL::FTimespan::FromMilliseconds(1)))
        {
            std::cerr << "FAIL: TryLockFor on same-thread-held section returned false "
                         "(FCriticalSection must be recursive on all platforms)\n";
            Section.Unlock();
            return 1;
        }
        Section.Unlock();  // first recursive unlock
        Section.Unlock();  // matching the initial TryLockFor

        // ---- Case 3: timeout expires while contended ----
        {
            std::atomic<bool> HolderReady(false);
            std::thread Holder([&Section, &HolderReady]()
            {
                Section.Lock();
                HolderReady.store(true, std::memory_order_release);
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                Section.Unlock();
            });
            while (!HolderReady.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }

            const double Before = ::XCore::HAL::FPlatformTime::Seconds();
            const bool Acquired = Section.TryLockFor(
                ::XCore::HAL::FTimespan::FromMilliseconds(20));
            const double After  = ::XCore::HAL::FPlatformTime::Seconds();

            if (Acquired)
            {
                std::cerr << "FAIL: TryLockFor(20ms) acquired while 100ms-holder was holding\n";
                Section.Unlock();
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

        // ---- Case 4: holder releases before timeout ----
        {
            std::atomic<bool> HolderReady(false);
            std::thread Holder([&Section, &HolderReady]()
            {
                Section.Lock();
                HolderReady.store(true, std::memory_order_release);
                std::this_thread::sleep_for(std::chrono::milliseconds(5));
                Section.Unlock();
            });
            while (!HolderReady.load(std::memory_order_acquire))
            {
                std::this_thread::yield();
            }

            const double Before = ::XCore::HAL::FPlatformTime::Seconds();
            const bool Acquired = Section.TryLockFor(
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
                Section.Unlock();
                Holder.join();
                return 1;
            }
            Section.Unlock();
            Holder.join();
        }

        std::cout << "PASS: FCriticalSection.TimedLock (HIGH-2 timed variant honours timeout)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunTimedLock();
}
