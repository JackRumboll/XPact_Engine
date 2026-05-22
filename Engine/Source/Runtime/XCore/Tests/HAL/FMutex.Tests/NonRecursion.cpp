// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FMutex.Tests/NonRecursion.cpp -- non-recursive deadlock detection.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "FMutex same-thread
// recursive-lock attempts deadlock cleanly".
//
// We can't simply call Lock() twice in the same thread (that would
// deadlock the test runner forever). Instead, we use a watchdog
// thread that joins-with-timeout: spawn a worker that does
// Lock()-Lock() and sleeps; if the watchdog observes the worker is
// still alive after the timeout, the non-recursive behaviour is
// confirmed (the worker deadlocked on the second Lock).
//
// Additionally, we verify the concept `XIsNonRecursiveMutex<FMutex>`
// is true (compile-time check; FCriticalSection should be false).
//
// =====================================================================

#include "HAL/FMutex.h"
#include "HAL/FCriticalSection.h"

#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>

namespace
{
    // Compile-time concept verification (fix M-5).
    static_assert(::XCore::HAL::XIsNonRecursiveMutex<::XCore::HAL::FMutex>,
                  "FMutex must satisfy XIsNonRecursiveMutex");

    // FCriticalSection MUST NOT satisfy the concept (it's recursive).
    static_assert(!::XCore::HAL::XIsNonRecursiveMutex<::XCore::HAL::FCriticalSection>,
                  "FCriticalSection must NOT satisfy XIsNonRecursiveMutex");

    int RunSurfaceTest()
    {
        // Basic Lock / TryLock / Unlock cycle.
        ::XCore::HAL::FMutex M;

        M.Lock();
        // While holding, TryLock from same thread should fail
        // (non-recursive). Some POSIX implementations may differ
        // (PTHREAD_MUTEX_NORMAL has implementation-defined behaviour
        // for self-trylock; some return EBUSY, some block). We
        // accept either:
        //   * TryLock returns false (expected; PTHREAD_MUTEX_NORMAL
        //     under glibc returns EBUSY for self-trylock).
        //   * TryLock returns true (some platforms allow self-trylock
        //     without enforcement). In this case we Unlock the spare
        //     hold to maintain test invariants.
        //
        // We log the result but don't fail on the latter -- the test
        // for non-recursion in Lock (blocking variant) is the
        // watchdog test below.
        const bool TryResult = M.TryLock();
        if (TryResult)
        {
            // Permissive platform; unlock the spare.
            M.Unlock();
        }
        M.Unlock();

        // After full unlock, TryLock succeeds.
        if (!M.TryLock())
        {
            std::cerr << "FAIL: TryLock on unheld mutex should succeed\n";
            return 1;
        }
        M.Unlock();

        return 0;
    }

    // Watchdog-based deadlock detection.
    //
    // Spawns a worker that:
    //   1. Locks the mutex.
    //   2. Sets the "reached-step-2" flag.
    //   3. Tries to Lock again (will deadlock on non-recursive).
    //   4. Sets "reached-step-4" flag (should not happen).
    //
    // The main thread waits up to N ms; if "reached-step-2" is set
    // but "reached-step-4" is NOT set, the non-recursive behaviour
    // is confirmed.
    //
    // We don't join the worker (it's deadlocked); detach it. The
    // mutex held by the worker is leaked along with the worker
    // thread itself; on test process exit this is harmless.
    int RunDeadlockTest()
    {
        // Use a heap-allocated mutex so the leaked worker thread
        // doesn't race with destruction at scope exit.
        auto* M                     = new ::XCore::HAL::FMutex;
        std::atomic<bool> ReachedStep2(false);
        std::atomic<bool> ReachedStep4(false);

        std::thread Worker([M, &ReachedStep2, &ReachedStep4]()
        {
            M->Lock();
            ReachedStep2.store(true);
            // Second Lock from same thread: should DEADLOCK on
            // PTHREAD_MUTEX_NORMAL or Win SRWLock.
            M->Lock();
            ReachedStep4.store(true);
            M->Unlock();
            M->Unlock();
        });

        // Wait for Worker to reach step 2.
        for (int I = 0; I < 200; ++I)
        {
            if (ReachedStep2.load())
            {
                break;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }

        if (!ReachedStep2.load())
        {
            std::cerr << "FAIL: worker did not reach step 2 (initial Lock failed)\n";
            // Detach worker; we leak it.
            Worker.detach();
            return 1;
        }

        // Wait a generous interval to be sure the second Lock is
        // blocking.
        std::this_thread::sleep_for(std::chrono::milliseconds(500));

        if (ReachedStep4.load())
        {
            std::cerr << "FAIL: worker reached step 4 (mutex was recursive)\n";
            Worker.detach();
            return 1;
        }

        // Confirmed: worker is deadlocked on the second Lock.
        // Detach it; the OS reclaims at process exit.
        Worker.detach();
        // NOTE: we intentionally leak `M` -- the worker holds it.
        return 0;
    }
}

int main()
{
    int Result = 0;
    Result |= RunSurfaceTest();
    Result |= RunDeadlockTest();
    if (Result == 0)
    {
        std::cout << "FMutex.NonRecursion: PASS\n";
    }
    return Result;
}
