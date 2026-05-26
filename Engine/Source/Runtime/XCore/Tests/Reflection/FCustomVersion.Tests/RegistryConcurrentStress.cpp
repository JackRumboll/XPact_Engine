// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCustomVersion.Tests/RegistryConcurrentStress.cpp -- thread-safety.
// =====================================================================
//
// XCore-4b Rev 3, Section 8.3 thread-safety contract:
//
//   "Thread-safe via FRWLock (read-heavy, write at module init)."
//
// And Section 13 gate E3 (analogous, for XReflectionRuntime):
//
//   "Concurrent FindClass + RegisterType: 1k concurrent FindClass calls
//    + 100 RegisterType calls across 16 threads completes TSan-clean."
//
// This test mirrors gate E3's shape for the FCustomVersionRegistry:
// 10 threads x 1000 RegisterCustomVersion calls each across distinct
// GUIDs (10000 unique entries total) + concurrent reads from those
// threads. The test asserts:
//
//   * Every registration succeeds (no Rejected / InvalidKey).
//   * The post-state registry contains exactly 10000 distinct entries
//     (or fewer if the test's deterministic-GUID generator collides --
//     the generator is designed to NOT collide).
//   * Concurrent Contains / Size calls do not deadlock and do not see
//     torn state.
//
// THREADING NOTE: this test stresses the FRWLock under contention but
// does not strictly verify "lock-free" or "wait-free" guarantees --
// those are not contracted by Section 8.3. The verifiable contract is
// "TSan-clean and produces correct results"; deadlock and corruption
// are the failure modes.
//
// =====================================================================

#include "Reflection/FCustomVersionRegistry.h"
#include "Reflection/FCustomVersion.h"
#include "Reflection/FGuid.h"

#include <atomic>
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

    using ::XCore::Reflect::ERegisterResult;
    using ::XCore::Reflect::FCustomVersion;
    using ::XCore::Reflect::FCustomVersionRegistry;
    using ::XCore::Reflect::FGuid;
    using ::XCore::Reflect::FName;

    constexpr int kThreadCount = 10;
    constexpr int kRegistrationsPerThread = 1000;
    constexpr int kReadsPerThread = 1000;

    // Per-thread distinct-GUID generator: each thread `T` registers
    // GUIDs with A == ThreadId+1 (avoid zero), B == iteration index,
    // C/D == 0xCAFE / 0xBABE. Distinct (T, I) -> distinct GUID.
    FGuid MakeThreadGuid(int ThreadId, int Iteration)
    {
        FGuid G;
        G.A = static_cast<::uint32>(ThreadId + 1);
        G.B = static_cast<::uint32>(Iteration + 1);
        G.C = 0xCAFE0001u;
        G.D = 0xBABE0002u;
        return G;
    }
}

int main()
{
    FCustomVersionRegistry& Registry = FCustomVersionRegistry::Get();
    Registry.EmptyForTesting();

    std::atomic<int> RegisterFailures(0);
    std::atomic<int> ReadFailures(0);

    // Spawn writer threads.
    std::vector<std::thread> Writers;
    Writers.reserve(kThreadCount);
    for (int T = 0; T < kThreadCount; ++T)
    {
        Writers.emplace_back([T, &Registry, &RegisterFailures]()
        {
            for (int I = 0; I < kRegistrationsPerThread; ++I)
            {
                const FGuid Key = MakeThreadGuid(T, I);
                const FName Friendly(static_cast<::uint32>((T << 16) | I), 0);
                const ERegisterResult Result =
                    Registry.RegisterCustomVersion(Key, 1, Friendly);
                if (Result != ERegisterResult::Registered)
                {
                    // Updated is acceptable in theory (if a thread
                    // restarted), but in this test design each (T, I)
                    // is unique so Registered is the only expected
                    // outcome.
                    RegisterFailures.fetch_add(1, std::memory_order_relaxed);
                }
            }
        });
    }

    // Spawn reader threads (separate from writers; the same threads
    // could read between writes, but separate readers more cleanly
    // exercise the shared-lock path).
    std::vector<std::thread> Readers;
    Readers.reserve(kThreadCount);
    for (int T = 0; T < kThreadCount; ++T)
    {
        Readers.emplace_back([T, &Registry, &ReadFailures]()
        {
            // Reader probes random-ish keys; some will hit (registered
            // by another thread), some will miss (not yet registered or
            // never will be). Neither outcome is a failure -- the
            // failure is a crash / deadlock / torn read.
            for (int I = 0; I < kReadsPerThread; ++I)
            {
                const FGuid Probe = MakeThreadGuid(T, I);
                bool bFound = false;
                const FCustomVersion Copy = Registry.GetRegisteredVersionCopy(Probe, bFound);
                if (bFound)
                {
                    // If found, the Version must be 1 (writers register
                    // with Version=1). A non-1 Version would indicate
                    // state corruption.
                    if (Copy.Version != 1)
                    {
                        ReadFailures.fetch_add(1, std::memory_order_relaxed);
                    }
                    if (Copy.Key != Probe)
                    {
                        ReadFailures.fetch_add(1, std::memory_order_relaxed);
                    }
                }
                // Also call Contains / Size to exercise the other
                // shared-lock paths.
                (void)Registry.Contains(Probe);
                (void)Registry.Size();
            }
        });
    }

    for (auto& W : Writers) { W.join(); }
    for (auto& R : Readers) { R.join(); }

    Check(RegisterFailures.load() == 0,
          "one or more RegisterCustomVersion calls failed unexpectedly");
    Check(ReadFailures.load() == 0,
          "one or more reader threads observed corrupt state");

    // Final size: kThreadCount * kRegistrationsPerThread distinct GUIDs.
    const ::int32 ExpectedSize = static_cast<::int32>(kThreadCount * kRegistrationsPerThread);
    Check(Registry.Size() == ExpectedSize,
          "post-stress Registry size != expected");

    // Spot-check a sample of registered entries.
    for (int T = 0; T < kThreadCount; ++T)
    {
        for (int I = 0; I < kRegistrationsPerThread; I += 137 /* prime stride */)
        {
            const FGuid Key = MakeThreadGuid(T, I);
            bool bFound = false;
            const FCustomVersion Copy = Registry.GetRegisteredVersionCopy(Key, bFound);
            if (!bFound)
            {
                Check(false, "post-stress spot-check could not find expected entry");
            }
            else
            {
                Check(Copy.Version == 1,
                      "post-stress spot-check entry has unexpected Version");
            }
        }
    }

    // Clean up.
    Registry.EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "FCustomVersion.RegistryConcurrentStress: FAIL ("
                  << g_FailureCount << " failures, "
                  << RegisterFailures.load() << " register-failures, "
                  << ReadFailures.load() << " read-failures)\n";
        return 1;
    }
    std::cout << "FCustomVersion.RegistryConcurrentStress: PASS\n";
    return 0;
}
