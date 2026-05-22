// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FAtomicInt64.Tests/MemoryOrder.cpp -- verify 64-bit memory-order
// surface. Mirror of FAtomicInt32 tests at 64-bit width.
// =====================================================================

#include "HAL/FAtomicInt64.h"
#include "Macros/XCoreTypes.h"

#include <atomic>
#include <cstdint>
#include <iostream>

namespace
{
    int RunNamedVariants()
    {
        ::XCore::HAL::FAtomicInt64 A(0);

        A.StoreRelease(0xCAFEBABEDEADBEEFLL);
        if (A.LoadAcquire() != 0xCAFEBABEDEADBEEFLL)
        {
            std::cerr << "FAIL: LoadAcquire after StoreRelease (64-bit pattern)\n";
            return 1;
        }

        A.StoreRelaxed(static_cast<::int64>(1) << 62);  // big value
        if (A.LoadRelaxed() != (static_cast<::int64>(1) << 62))
        {
            std::cerr << "FAIL: LoadRelaxed after StoreRelaxed (1<<62)\n";
            return 1;
        }

        // FetchAdd variants.
        A.StoreRelaxed(0);
        if (A.FetchAddRelaxed(1000000000LL) != 0)
        {
            std::cerr << "FAIL: FetchAddRelaxed should return previous (0)\n";
            return 1;
        }
        if (A.FetchAddAcquire(2000000000LL) != 1000000000LL)
        {
            return 1;
        }
        if (A.FetchAddRelease(3000000000LL) != 3000000000LL)
        {
            return 1;
        }
        if (A.FetchAddAcqRel(4000000000LL) != 6000000000LL)
        {
            return 1;
        }
        if (A.LoadRelaxed() != 10000000000LL)
        {
            std::cerr << "FAIL: After all 64-bit FetchAdds, value should be 10e9\n";
            return 1;
        }

        return 0;
    }

    int RunGenericApi()
    {
        ::XCore::HAL::FAtomicInt64 A(0);
        A.Store(0x123456789ABCDEF0LL, std::memory_order_release);
        if (A.Load(std::memory_order_acquire) != 0x123456789ABCDEF0LL)
        {
            return 1;
        }

        ::int64 Prev = A.FetchAdd(1, std::memory_order_relaxed);
        if (Prev != 0x123456789ABCDEF0LL) return 1;

        return 0;
    }

    int RunCAS()
    {
        ::XCore::HAL::FAtomicInt64 A(0xAAAA'BBBB'CCCC'DDDDLL);

        ::int64 Expected = 0xAAAA'BBBB'CCCC'DDDDLL;
        if (!A.CompareExchangeStrong(Expected, 0x1111'2222'3333'4444LL,
                                     std::memory_order_acq_rel,
                                     std::memory_order_relaxed))
        {
            return 1;
        }
        if (A.LoadRelaxed() != 0x1111'2222'3333'4444LL)
        {
            return 1;
        }

        Expected = 0xDEAD'BEEFLL;  // wrong
        if (A.CompareExchangeStrong(Expected, 0xCAFELL,
                                    std::memory_order_acq_rel,
                                    std::memory_order_relaxed))
        {
            return 1;
        }
        if (Expected != 0x1111'2222'3333'4444LL)
        {
            return 1;
        }

        return 0;
    }

    int RunAbiLock()
    {
        if (sizeof(::XCore::HAL::FAtomicInt64) != 8)
        {
            std::cerr << "FAIL: sizeof(FAtomicInt64) != 8\n";
            return 1;
        }
        if (alignof(::XCore::HAL::FAtomicInt64) != 8)
        {
            std::cerr << "FAIL: alignof(FAtomicInt64) != 8\n";
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
        std::cout << "FAtomicInt64.MemoryOrder: PASS\n";
    }
    return Result;
}
