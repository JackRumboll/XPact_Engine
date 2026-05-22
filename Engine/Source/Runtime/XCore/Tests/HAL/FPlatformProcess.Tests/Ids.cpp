// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformProcess.Tests/Ids.cpp -- process + thread ID sanity check.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1. Verifies:
//   * GetCurrentProcessId() returns a non-zero value.
//   * GetCurrentThreadId() returns a non-zero value.
//   * Two spawned std::threads observe distinct GetCurrentThreadId
//     values (the kernel TID is per-thread).
//
// =====================================================================

#include "HAL/FPlatformProcess.h"

#include <atomic>
#include <cstdint>
#include <iostream>
#include <thread>

int main()
{
    using ::XCore::HAL::FPlatformProcess;

    const std::uint32_t Pid = FPlatformProcess::GetCurrentProcessId();
    if (Pid == 0)
    {
        std::cerr << "FAIL: GetCurrentProcessId returned 0\n";
        return 1;
    }

    const std::uint32_t MainTid = FPlatformProcess::GetCurrentThreadId();
    if (MainTid == 0)
    {
        std::cerr << "FAIL: GetCurrentThreadId returned 0\n";
        return 1;
    }

    // Spawn two threads; verify each observes its own TID and the
    // main thread observes a distinct value.
    std::atomic<std::uint32_t> ChildTid1{ 0 };
    std::atomic<std::uint32_t> ChildTid2{ 0 };

    std::thread T1([&ChildTid1]()
    {
        ChildTid1.store(FPlatformProcess::GetCurrentThreadId(),
                        std::memory_order_release);
    });
    std::thread T2([&ChildTid2]()
    {
        ChildTid2.store(FPlatformProcess::GetCurrentThreadId(),
                        std::memory_order_release);
    });
    T1.join();
    T2.join();

    const std::uint32_t Tid1 = ChildTid1.load(std::memory_order_acquire);
    const std::uint32_t Tid2 = ChildTid2.load(std::memory_order_acquire);

    if (Tid1 == 0 || Tid2 == 0)
    {
        std::cerr << "FAIL: child thread TIDs (Tid1=" << Tid1
                  << ", Tid2=" << Tid2 << ") must both be non-zero\n";
        return 1;
    }
    if (Tid1 == Tid2)
    {
        std::cerr << "FAIL: two threads observed the same TID " << Tid1 << "\n";
        return 1;
    }
    if (Tid1 == MainTid || Tid2 == MainTid)
    {
        std::cerr << "FAIL: child TID collides with main TID " << MainTid << "\n";
        return 1;
    }

    std::cout << "Ids: PASS (pid=" << Pid << " main=" << MainTid
              << " child1=" << Tid1 << " child2=" << Tid2 << ")\n";
    return 0;
}
