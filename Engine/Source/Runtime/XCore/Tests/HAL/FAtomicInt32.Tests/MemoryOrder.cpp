// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FAtomicInt32.Tests/MemoryOrder.cpp -- verify memory-order surface.
// =====================================================================
//
// XCore-4a Rev 3, Section 8.1 + fix M-4 (no default memory_order).
//
// This is a compile-time + runtime smoke test for FAtomicInt32's
// memory-order discipline:
//
//   1. Every named convenience variant compiles + returns the right
//      type.
//   2. Generic Load(order)/Store(value,order)/FetchAdd(delta,order)
//      compile with each std::memory_order value.
//   3. CompareExchangeStrong requires two memory_order parameters
//      (success + failure).
//
// The spec's "verify each method emits the expected instruction" test
// (disassembly hash) is a Phase 1d follow-up; Phase 1c proves the
// surface compiles correctly and produces the right runtime
// behaviour.
//
// =====================================================================

#include "HAL/FAtomicInt32.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <cstdint>
#include <iostream>

namespace
{
    int RunNamedVariants()
    {
        ::XCore::HAL::FAtomicInt32 A(0);

        // StoreRelease + LoadAcquire round-trip.
        A.StoreRelease(42);
        if (A.LoadAcquire() != 42)
        {
            std::cerr << "FAIL: LoadAcquire after StoreRelease\n";
            return 1;
        }

        // StoreRelaxed + LoadRelaxed.
        A.StoreRelaxed(100);
        if (A.LoadRelaxed() != 100)
        {
            std::cerr << "FAIL: LoadRelaxed after StoreRelaxed\n";
            return 1;
        }

        // FetchAdd variants.
        A.StoreRelaxed(0);
        if (A.FetchAddRelaxed(5) != 0)
        {
            std::cerr << "FAIL: FetchAddRelaxed should return previous (0)\n";
            return 1;
        }
        if (A.LoadRelaxed() != 5)
        {
            std::cerr << "FAIL: After FetchAddRelaxed(5), value should be 5\n";
            return 1;
        }

        if (A.FetchAddAcquire(10) != 5)
        {
            std::cerr << "FAIL: FetchAddAcquire should return previous (5)\n";
            return 1;
        }

        if (A.FetchAddRelease(3) != 15)
        {
            std::cerr << "FAIL: FetchAddRelease should return previous (15)\n";
            return 1;
        }

        if (A.FetchAddAcqRel(2) != 18)
        {
            std::cerr << "FAIL: FetchAddAcqRel should return previous (18)\n";
            return 1;
        }

        if (A.LoadRelaxed() != 20)
        {
            std::cerr << "FAIL: After all FetchAdds, value should be 20\n";
            return 1;
        }

        // ExchangeAcqRel.
        if (A.ExchangeAcqRel(999) != 20)
        {
            std::cerr << "FAIL: ExchangeAcqRel should return previous (20)\n";
            return 1;
        }
        if (A.LoadRelaxed() != 999)
        {
            std::cerr << "FAIL: After ExchangeAcqRel(999), value should be 999\n";
            return 1;
        }

        return 0;
    }

    int RunGenericApi()
    {
        ::XCore::HAL::FAtomicInt32 A(0);

        // Each memory_order should compile + work.
        A.Store(7, std::memory_order_relaxed);
        if (A.Load(std::memory_order_relaxed) != 7) return 1;

        A.Store(8, std::memory_order_release);
        if (A.Load(std::memory_order_acquire) != 8) return 1;

        A.Store(9, std::memory_order_seq_cst);
        if (A.Load(std::memory_order_seq_cst) != 9) return 1;

        ::int32 Prev = A.FetchAdd(1, std::memory_order_relaxed);
        if (Prev != 9) return 1;
        if (A.Load(std::memory_order_relaxed) != 10) return 1;

        return 0;
    }

    int RunCAS()
    {
        ::XCore::HAL::FAtomicInt32 A(100);

        // Successful CAS.
        ::int32 Expected = 100;
        if (!A.CompareExchangeStrong(Expected, 200,
                                     std::memory_order_acq_rel,
                                     std::memory_order_relaxed))
        {
            std::cerr << "FAIL: CompareExchangeStrong should succeed\n";
            return 1;
        }
        if (A.LoadRelaxed() != 200)
        {
            std::cerr << "FAIL: After CAS success, value should be 200\n";
            return 1;
        }

        // Failed CAS: Expected mismatches.
        Expected = 999;  // wrong
        if (A.CompareExchangeStrong(Expected, 300,
                                    std::memory_order_acq_rel,
                                    std::memory_order_relaxed))
        {
            std::cerr << "FAIL: CompareExchangeStrong should fail with wrong expected\n";
            return 1;
        }
        // Expected should now hold the actual value (200).
        if (Expected != 200)
        {
            std::cerr << "FAIL: After CAS failure, Expected should hold actual (200), got "
                      << Expected << "\n";
            return 1;
        }

        return 0;
    }

    // ABI lock smoke -- the static_assert in the header pins these
    // at compile time; the runtime check here is defensive.
    int RunAbiLock()
    {
        if (sizeof(::XCore::HAL::FAtomicInt32) != 4)
        {
            std::cerr << "FAIL: sizeof(FAtomicInt32) != 4; got "
                      << sizeof(::XCore::HAL::FAtomicInt32) << "\n";
            return 1;
        }
        if (alignof(::XCore::HAL::FAtomicInt32) != 4)
        {
            std::cerr << "FAIL: alignof(FAtomicInt32) != 4; got "
                      << alignof(::XCore::HAL::FAtomicInt32) << "\n";
            return 1;
        }
        return 0;
    }
}

int main()
{
    int Result = 0;
    Result |= RunNamedVariants();
    Result |= RunGenericApi();
    Result |= RunCAS();
    Result |= RunAbiLock();

    if (Result == 0)
    {
        std::cout << "FAtomicInt32.MemoryOrder: PASS\n";
    }
    return Result;
}
