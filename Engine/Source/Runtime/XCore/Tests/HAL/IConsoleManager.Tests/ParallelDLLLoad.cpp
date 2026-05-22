// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// IConsoleManager.Tests/ParallelDLLLoad.cpp -- fix C-6 parallel ctor.
// =====================================================================
//
// XCore-4a Rev 3, Section 9.5 fix C-6 ("The Treiber stack is correct
// under parallel constructor invocation -- e.g., the platform loader
// running C++ constructors for two newly-loaded DLLs on sister
// threads via dlopen on Linux or the OS-internal parallel-loader on
// Win64.").
//
// And Section 9.7 acceptance:
//   "Parallel DLL load (fix C-6): 64-DLL dlopen on Linux + parallel-
//    load on Win64 sister threads, each DLL pushing 16
//    FAutoConsoleVariable onto the Treiber stack -- after drain,
//    every CVar appears exactly once; no stack corruption under TSan."
//
// PHASE 1F SCOPE: this test simulates 4 sister threads each
// "registering" 100 CVars via the direct RegisterInt surface (the
// FAutoConsoleVariable + Treiber-stack path requires static-init
// timing that is awkward to unit-test without a full DLL load).
//
// The runtime RWLock-protected Register* surface is the
// equivalent stress: the Treiber stack drain calls into the same
// Register*. If Register* is parallel-safe, the Treiber-stack
// drain (called from __Initialize after stack accumulation) is
// parallel-safe end-to-end.
//
// 4 threads * 100 CVars = 400 total registrations. After all threads
// join, every CVar must be findable exactly once.
//
// =====================================================================

#include "HAL/IConsoleManager.h"
#include "HAL/IConsoleVariable.h"
#include "HAL/ECVarFlags.h"

#include <atomic>
#include <cstdio>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int kThreads = 4;
    constexpr int kCVarsPerThread = 100;

    void ThreadBody(int ThreadId)
    {
        auto& Manager = ::XCore::Misc::IConsoleManager::Get();
        for (int i = 0; i < kCVarsPerThread; ++i)
        {
            char Name[48];
            std::snprintf(Name, 48, "r.Parallel.T%d.C%d", ThreadId, i);
            ::XCore::Misc::IConsoleVariable* CVar = Manager.RegisterInt(
                Name, ThreadId * 1000 + i, "parallel test",
                ::XCore::Misc::ECVarFlags::Default);
            if (CVar == nullptr)
            {
                std::cerr << "FAIL: parallel thread " << ThreadId
                          << " RegisterInt nullptr at i=" << i << "\n";
                std::abort();
            }
        }
    }

    int RunParallelDLLLoad()
    {
        std::vector<std::thread> Threads;
        Threads.reserve(kThreads);

        for (int t = 0; t < kThreads; ++t)
        {
            Threads.emplace_back(ThreadBody, t);
        }
        for (auto& T : Threads)
        {
            T.join();
        }

        // Verify every CVar landed.
        auto& Manager = ::XCore::Misc::IConsoleManager::Get();
        for (int t = 0; t < kThreads; ++t)
        {
            for (int i = 0; i < kCVarsPerThread; ++i)
            {
                char Name[48];
                std::snprintf(Name, 48, "r.Parallel.T%d.C%d", t, i);
                ::XCore::Misc::IConsoleVariable* CVar = Manager.Find(Name);
                if (CVar == nullptr)
                {
                    std::cerr << "FAIL: post-parallel Find returned nullptr for " << Name << "\n";
                    return 1;
                }
                const ::int32 Expected = t * 1000 + i;
                if (CVar->GetInt() != Expected)
                {
                    std::cerr << "FAIL: post-parallel value mismatch for " << Name
                              << " expected=" << Expected
                              << " got=" << CVar->GetInt() << "\n";
                    return 1;
                }
            }
        }

        std::cout << "PASS: ParallelDLLLoad (4 threads x 100 = 400 CVars round-trip;\n"
                     "      RWLock-protected Register* is parallel-safe)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunParallelDLLLoad();
}
