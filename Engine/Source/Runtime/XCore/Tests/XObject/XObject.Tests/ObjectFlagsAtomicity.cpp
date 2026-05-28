// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XObject.Tests/ObjectFlagsAtomicity.cpp -- XObject::SetFlags /
// ClearFlags concurrent stress (XCoreXObject Rev 4 §2.3).
//
// Spawns N worker threads that each repeatedly toggle a disjoint set of
// EObjectFlags bits on a single XObject. Each thread owns a distinct
// bit; if SetFlags / ClearFlags is genuinely atomic (CAS-loop) the
// final ObjectFlags value matches the expected per-thread parity.
//
// The acceptance criterion: NO bit owned by thread T should ever be
// torn by thread U's concurrent SetFlags / ClearFlags on a DIFFERENT
// bit. The CAS loop's read-modify-write is the protective barrier;
// a naive `Flags |= Bit` would lose updates under contention.
//
// =====================================================================

#include "XObject/EObjectFlags.h"
#include "XObject/XObject.h"

#include <atomic>
#include <cstdint>
#include <iostream>
#include <thread>
#include <vector>

namespace
{
    constexpr int kNumThreads          = 8;
    constexpr int kIterationsPerThread = 20000;

    // We use 8 distinct UserFlag bits (bits 24..31) so each worker
    // owns its own bit and we can audit per-thread parity. UserFlag_1
    // .. UserFlag_8 are the spec-reserved plugin range; using them
    // here is safe + isolated from engine-internal flag semantics.
    constexpr ::XCore::EObjectFlags kThreadBits[kNumThreads] = {
        ::XCore::EObjectFlags::UserFlag_1,
        ::XCore::EObjectFlags::UserFlag_2,
        ::XCore::EObjectFlags::UserFlag_3,
        ::XCore::EObjectFlags::UserFlag_4,
        ::XCore::EObjectFlags::UserFlag_5,
        ::XCore::EObjectFlags::UserFlag_6,
        ::XCore::EObjectFlags::UserFlag_7,
        ::XCore::EObjectFlags::UserFlag_8,
    };

    // Test 1: every thread alternately Sets + Clears its own bit
    // kIterationsPerThread times. Final expected state: every thread's
    // bit is CLEAR (even number of toggles per thread).
    int SetClearParityTest()
    {
        ::XCore::XObject Obj;
        std::vector<std::thread> Threads;
        Threads.reserve(kNumThreads);

        for (int T = 0; T < kNumThreads; ++T)
        {
            Threads.emplace_back([&Obj, T]()
            {
                const ::XCore::EObjectFlags MyBit = kThreadBits[T];
                for (int I = 0; I < kIterationsPerThread; ++I)
                {
                    Obj.SetFlags(MyBit);
                    Obj.ClearFlags(MyBit);
                }
            });
        }
        for (auto& Th : Threads) Th.join();

        const ::XCore::EObjectFlags Final =
            Obj.GetObjectFlags(::std::memory_order_acquire);

        // Every UserFlag bit must be CLEAR (Set+Clear is an even
        // toggle per thread; no SetFlags from another thread bleeds
        // into another bit).
        for (int T = 0; T < kNumThreads; ++T)
        {
            if ((Final & kThreadBits[T]) != ::XCore::EObjectFlags::None)
            {
                std::cerr << "FAIL: Set/Clear parity: bit " << T
                          << " ended set when it should be clear.\n";
                return 1;
            }
        }
        return 0;
    }

    // Test 2: every thread SETS its own bit kIterationsPerThread
    // times (idempotent Set; the final state should be "all 8 bits
    // set" regardless of interleaving).
    int SetOnlyMutualExclusionTest()
    {
        ::XCore::XObject Obj;
        std::vector<std::thread> Threads;
        Threads.reserve(kNumThreads);

        for (int T = 0; T < kNumThreads; ++T)
        {
            Threads.emplace_back([&Obj, T]()
            {
                const ::XCore::EObjectFlags MyBit = kThreadBits[T];
                for (int I = 0; I < kIterationsPerThread; ++I)
                {
                    Obj.SetFlags(MyBit);
                }
            });
        }
        for (auto& Th : Threads) Th.join();

        const ::XCore::EObjectFlags Final =
            Obj.GetObjectFlags(::std::memory_order_acquire);

        // All 8 plugin bits must be set.
        ::XCore::EObjectFlags Expected = ::XCore::EObjectFlags::None;
        for (int T = 0; T < kNumThreads; ++T)
        {
            Expected |= kThreadBits[T];
        }

        if ((Final & Expected) != Expected)
        {
            std::cerr << "FAIL: Set-only race lost; expected 0x"
                      << std::hex << ::XCore::ToUnderlying(Expected)
                      << " got 0x"
                      << ::XCore::ToUnderlying(Final & Expected)
                      << std::dec << "\n";
            return 1;
        }
        return 0;
    }

    // Test 3: half the threads Set their bit; the other half Clear
    // their bit. The "Set" threads' bits must end SET; the "Clear"
    // threads' bits must end CLEAR. Verifies no cross-thread leakage
    // from Set into Clear OR vice-versa.
    int SetVsClearCrossTrafficTest()
    {
        ::XCore::XObject Obj;

        // Pre-seed all 8 bits so the Clear threads have something
        // to clear (otherwise their work would be a no-op).
        ::XCore::EObjectFlags AllUserBits = ::XCore::EObjectFlags::None;
        for (int T = 0; T < kNumThreads; ++T) AllUserBits |= kThreadBits[T];
        Obj.SetFlags(AllUserBits);

        std::vector<std::thread> Threads;
        Threads.reserve(kNumThreads);

        for (int T = 0; T < kNumThreads; ++T)
        {
            const bool bDoSet = (T % 2 == 0);  // even=Set, odd=Clear
            Threads.emplace_back([&Obj, T, bDoSet]()
            {
                const ::XCore::EObjectFlags MyBit = kThreadBits[T];
                for (int I = 0; I < kIterationsPerThread; ++I)
                {
                    if (bDoSet) Obj.SetFlags(MyBit);
                    else        Obj.ClearFlags(MyBit);
                }
            });
        }
        for (auto& Th : Threads) Th.join();

        const ::XCore::EObjectFlags Final =
            Obj.GetObjectFlags(::std::memory_order_acquire);

        for (int T = 0; T < kNumThreads; ++T)
        {
            const bool bExpectedSet = (T % 2 == 0);
            const bool bActuallySet =
                (Final & kThreadBits[T]) != ::XCore::EObjectFlags::None;
            if (bExpectedSet != bActuallySet)
            {
                std::cerr << "FAIL: SetVsClear crosstraffic: bit " << T
                          << " expected " << (bExpectedSet ? "SET" : "CLEAR")
                          << " got " << (bActuallySet ? "SET" : "CLEAR")
                          << "\n";
                return 1;
            }
        }
        return 0;
    }
}

int main()
{
    int Result = 0;
    Result |= SetClearParityTest();
    Result |= SetOnlyMutualExclusionTest();
    Result |= SetVsClearCrossTrafficTest();
    if (Result == 0)
    {
        std::cout << "XObject.ObjectFlagsAtomicity: PASS\n";
    }
    return Result;
}
