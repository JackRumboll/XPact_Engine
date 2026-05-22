// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TStaticArray.Tests/StackOnly.cpp -- fixed-size stack array.
// =====================================================================
//
// XCore-4a Section 5.1 + Section 18 OPEN-4 RECOMMENDED: TStaticArray
// is pure stack storage; no heap fallback. This test exercises:
//   * constexpr instantiation + constexpr access.
//   * Num() returns N at runtime.
//   * Bounds-check via At() (deferred to Phase 1d for runtime
//     exercise; compile-time signature declared).
//   * Layout: sizeof == N * sizeof(T) (modulo T's alignment).
//   * No heap interaction (FMemory::GetAllocatedBytes(Container)
//     is 0 throughout).
//
// =====================================================================

#include "Containers/TStaticArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <type_traits>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::HAL::FMemory;
    using ::XCore::HAL::FMemTag;

    // ---------------------------------------------------------------
    // 1. constexpr instantiation.
    // ---------------------------------------------------------------
    constexpr ::XCore::TStaticArray<::int32, 4> ConstArr{ {1, 2, 3, 4} };

    static_assert(ConstArr.Num() == 4, "TStaticArray constexpr Num()");
    static_assert(ConstArr[0] == 1,    "TStaticArray constexpr operator[]");
    static_assert(ConstArr[3] == 4,    "TStaticArray constexpr operator[] end");
    static_assert(!ConstArr.IsEmpty(), "TStaticArray constexpr IsEmpty (non-zero N)");

    // ---------------------------------------------------------------
    // 2. Runtime instantiation + accessors.
    // ---------------------------------------------------------------
    ::XCore::TStaticArray<::int32, 8> Arr{ {10, 20, 30, 40, 50, 60, 70, 80} };

    if (Arr.Num() != 8)
    {
        std::fprintf(stderr, "FAIL: Num=%d (expected 8)\n", Arr.Num());
        return 1;
    }
    if (Arr.IsEmpty())
    {
        std::fprintf(stderr, "FAIL: IsEmpty() is true for N=8\n");
        return 1;
    }

    // Each element read.
    const ::int32 Expected[8] = {10, 20, 30, 40, 50, 60, 70, 80};
    for (::int32 I = 0; I < 8; ++I)
    {
        if (Arr[I] != Expected[I])
        {
            std::fprintf(stderr, "FAIL: Arr[%d]=%d (expected %d)\n",
                         I, Arr[I], Expected[I]);
            return 1;
        }
    }

    // ---------------------------------------------------------------
    // 3. Mutation.
    // ---------------------------------------------------------------
    Arr[0] = 999;
    if (Arr[0] != 999)
    {
        std::fprintf(stderr, "FAIL: post-mutate Arr[0]=%d (expected 999)\n", Arr[0]);
        return 1;
    }

    // ---------------------------------------------------------------
    // 4. Iteration.
    // ---------------------------------------------------------------
    ::int32 Count = 0;
    for (auto V : Arr)
    {
        (void)V;
        ++Count;
    }
    if (Count != 8)
    {
        std::fprintf(stderr, "FAIL: range-for visited %d (expected 8)\n", Count);
        return 1;
    }

    // ---------------------------------------------------------------
    // 5. Layout: sizeof == N * sizeof(T) for non-zero N.
    // ---------------------------------------------------------------
    static_assert(sizeof(::XCore::TStaticArray<::int32,  8>) == 8 * sizeof(::int32),
                  "TStaticArray<int32, 8> size = 32 bytes");
    static_assert(sizeof(::XCore::TStaticArray<::int64, 16>) == 16 * sizeof(::int64),
                  "TStaticArray<int64, 16> size = 128 bytes");

    // ---------------------------------------------------------------
    // 6. No heap interaction.
    //
    // TStaticArray's storage is automatic; no FMemory::Malloc should
    // be invoked anywhere in this test. The Container-tag accumulator
    // should stay at its pre-test value.
    // ---------------------------------------------------------------
    const ::uint64 ContainerBytesBefore = FMemory::GetAllocatedBytes(FMemTag::Container);

    {
        ::XCore::TStaticArray<::int64, 100> LocalArr{};
        // Touch every slot to make sure the compiler doesn't elide.
        ::int64 Sum = 0;
        for (::int32 I = 0; I < LocalArr.Num(); ++I)
        {
            LocalArr[I] = I;
            Sum += LocalArr[I];
        }
        // Sanity: sum of 0..99 = 4950.
        if (Sum != 4950)
        {
            std::fprintf(stderr, "FAIL: stack-array iter sum=%lld (expected 4950)\n",
                         static_cast<long long>(Sum));
            return 1;
        }
    }

    const ::uint64 ContainerBytesAfter = FMemory::GetAllocatedBytes(FMemTag::Container);
    if (ContainerBytesAfter != ContainerBytesBefore)
    {
        std::fprintf(stderr,
            "FAIL: TStaticArray triggered heap alloc (before=%llu after=%llu)\n",
            static_cast<unsigned long long>(ContainerBytesBefore),
            static_cast<unsigned long long>(ContainerBytesAfter));
        return 1;
    }

    // ---------------------------------------------------------------
    // 7. N = 0 edge case.
    // ---------------------------------------------------------------
    ::XCore::TStaticArray<::int32, 0> EmptyArr{};
    if (EmptyArr.Num() != 0)
    {
        std::fprintf(stderr, "FAIL: N=0 Num=%d (expected 0)\n", EmptyArr.Num());
        return 1;
    }
    if (!EmptyArr.IsEmpty())
    {
        std::fprintf(stderr, "FAIL: N=0 IsEmpty=false\n");
        return 1;
    }
    if (EmptyArr.begin() != EmptyArr.end())
    {
        std::fprintf(stderr, "FAIL: N=0 begin != end\n");
        return 1;
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TStaticArray.StackOnly: PASS\n");
    return 0;
}
