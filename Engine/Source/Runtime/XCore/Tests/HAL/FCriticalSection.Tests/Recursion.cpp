// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCriticalSection.Tests/Recursion.cpp -- same-thread recursion.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.7 test plan: "FCriticalSection same-thread
// recursion".
//
// FCriticalSection is always-recursive on every platform. A single
// thread should be able to Lock-Lock-Unlock-Unlock without deadlock.
//
// =====================================================================

#include "HAL/FCriticalSection.h"

#include <iostream>

int main()
{
    ::XCore::HAL::FCriticalSection Cs;

    // First-level lock.
    Cs.Lock();

    // Recursive lock (same thread).
    Cs.Lock();

    // Recursive TryLock should also succeed.
    if (!Cs.TryLock())
    {
        std::cerr << "FAIL: TryLock on already-held recursive mutex should succeed\n";
        Cs.Unlock();
        Cs.Unlock();
        return 1;
    }

    // Now held three times; unlock each level.
    Cs.Unlock();
    Cs.Unlock();
    Cs.Unlock();

    // After full unlock, TryLock should succeed (mutex unheld).
    if (!Cs.TryLock())
    {
        std::cerr << "FAIL: TryLock on unheld mutex should succeed\n";
        return 1;
    }
    Cs.Unlock();

    std::cout << "FCriticalSection.Recursion: PASS\n";
    return 0;
}
