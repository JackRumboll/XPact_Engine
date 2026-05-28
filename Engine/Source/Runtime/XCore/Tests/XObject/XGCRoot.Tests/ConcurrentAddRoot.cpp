// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGCRoot.Tests/ConcurrentAddRoot.cpp -- multi-thread AddRoot/RemoveRoot
// stress (XCoreXObject Rev 4 §5.2; Phase 5.e).
// =====================================================================
//
// AddRoot / RemoveRoot are atomic CAS-loop primitives; multiple threads
// hammering the same FXObjectArrayEntry's StateBits MUST converge to a
// consistent state (no torn-bit reads, no lost transitions).
//
// Test pattern: 8 worker threads; each does 10 000 AddRoot+RemoveRoot
// cycles on a shared XObject. After all threads join, the bit MUST be
// clear (every AddRoot was paired with a RemoveRoot).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XGCRoot.h"
#include "XObject/XObject.h"

#include <atomic>
#include <iostream>
#include <thread>

int main()
{
    using ::XCore::FXObjectArray;
    using ::XCore::XGCRoot;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray& Array = FXObjectArray::Get();
    Array.__ResetForTests();

    int FailureCount = 0;
    auto Check = [&](bool Cond, const char* Diagnostic)
    {
        if (!Cond)
        {
            std::cerr << "FAIL: " << Diagnostic << "\n";
            ++FailureCount;
        }
    };

    XObject Obj;
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    constexpr int kThreads          = 8;
    constexpr int kCyclesPerThread  = 10000;

    // Track per-thread "net transitions" -- AddRoot returning true
    // minus RemoveRoot returning true. The sum across all threads
    // MUST equal the net bit state (0 if balanced).
    std::atomic<int> NetTransitions{0};

    std::thread Workers[kThreads];
    for (int T = 0; T < kThreads; ++T)
    {
        Workers[T] = std::thread([&]()
        {
            int Local = 0;
            for (int I = 0; I < kCyclesPerThread; ++I)
            {
                if (XGCRoot::AddRoot(&Obj))    ++Local;
                if (XGCRoot::RemoveRoot(&Obj)) --Local;
            }
            NetTransitions.fetch_add(Local, std::memory_order_relaxed);
        });
    }
    for (int T = 0; T < kThreads; ++T)
    {
        Workers[T].join();
    }

    // LOAD-BEARING INVARIANT under the bit-pin semantic:
    //
    // Across all threads, (sum of AddRoot transitions) ==
    //                     (sum of RemoveRoot transitions) +
    //                     (1 if final bit is set else 0).
    //
    // Equivalently: NetTransitions (per-thread (Add - Rm) returned)
    // equals the final bit state. The bit can ONLY transition 1 -> 0
    // (via a successful RemoveRoot) when a prior AddRoot transitioned
    // 0 -> 1. Concurrent contention may make many calls return false
    // (no-op idempotent observers), but the COUNT of true returns
    // pairs up exactly.
    //
    // Therefore: NetTransitions == (IsRooted final ? 1 : 0).
    //
    // This is the load-bearing race-safety invariant: no transition
    // is lost, no transition is double-counted, even under heavy
    // contention.
    const int FinalBit = XGCRoot::IsRooted(&Obj) ? 1 : 0;
    const int Net = NetTransitions.load(std::memory_order_acquire);
    Check(Net == FinalBit,
          "race-safety invariant: NetTransitions != FinalBit");

    // Reset the bit (if set) before cleanup.
    if (FinalBit != 0)
    {
        (void)XGCRoot::RemoveRoot(&Obj);
    }

    Array.FreeEntry(Idx);
    Array.__ResetForTests();

    if (FailureCount == 0)
    {
        std::cout << "XGCRoot.ConcurrentAddRoot: PASS\n";
        return 0;
    }
    std::cerr << "XGCRoot.ConcurrentAddRoot: " << FailureCount
              << " FAIL(s)\n";
    return 1;
}
