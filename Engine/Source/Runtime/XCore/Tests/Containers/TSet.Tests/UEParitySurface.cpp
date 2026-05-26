// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TSet.Tests/UEParitySurface.cpp -- UE-parity API surface coverage.
// =====================================================================
//
// XCore-4a Rev 3 Round 2 audit FIX-R2-MED-NEW-2. Verifies the UE-parity
// methods added to TSet:
//   * Append (copy / move)  -- bulk merge
//   * Sort                   -- in-place ordering (transient until mutation)
//   * Array                  -- bulk extraction into TArray
//
// =====================================================================

#include "Containers/TSet.h"
#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <functional>   // std::less

#define CHECK(Expr, Msg)                                                  \
    do {                                                                  \
        if (!(Expr))                                                      \
        {                                                                 \
            std::fprintf(stderr, "FAIL: %s\n", Msg);                      \
            ::XCore::HAL::FMemory::__Shutdown();                          \
            return 1;                                                     \
        }                                                                 \
    } while (0)

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Append (copy): bulk merge with duplicate-ignore semantics.
    // -----------------------------------------------------------------
    {
        ::XCore::TSet<::int32> A;
        ::XCore::TSet<::int32> B;
        A.Add(1);
        A.Add(2);
        B.Add(2);  // duplicate; ignored
        B.Add(3);
        B.Add(4);
        A.Append(B);
        CHECK(A.Num() == 4, "Append: expected 4 unique elements");
        CHECK(A.Contains(1), "Append: missing 1");
        CHECK(A.Contains(2), "Append: missing 2");
        CHECK(A.Contains(3), "Append: missing 3");
        CHECK(A.Contains(4), "Append: missing 4");
        CHECK(B.Num() == 3, "Append(const&) must not mutate source");
    }

    // -----------------------------------------------------------------
    // Append (move): consumes source.
    // -----------------------------------------------------------------
    {
        ::XCore::TSet<::int32> A;
        ::XCore::TSet<::int32> B;
        A.Add(10);
        B.Add(20);
        B.Add(30);
        A.Append(::std::move(B));
        CHECK(A.Num() == 3, "Append(&&): expected 3 elements");
        CHECK(B.Num() == 0, "Append(&&): source must be empty post-move");
    }

    // -----------------------------------------------------------------
    // Sort: ascending integer ordering.
    // -----------------------------------------------------------------
    {
        ::XCore::TSet<::int32> S;
        S.Add(7);
        S.Add(3);
        S.Add(11);
        S.Add(1);
        S.Add(5);
        S.Sort(::std::less<::int32>());

        ::int32 Prev = -1;
        ::int32 Count = 0;
        for (const auto& E : S)
        {
            CHECK(E > Prev, "Sort: iteration not ascending");
            Prev = E;
            ++Count;
        }
        CHECK(Count == 5, "Sort: visited count mismatch");
    }

    // -----------------------------------------------------------------
    // Array: bulk extraction.
    // -----------------------------------------------------------------
    {
        ::XCore::TSet<::int32> S;
        S.Add(100);
        S.Add(200);
        S.Add(300);

        ::XCore::TArray<::int32> Out = S.Array();
        CHECK(Out.Num() == 3, "Array(): count mismatch");

        // Iteration order is implementation-defined; verify by sum.
        ::int32 Sum = 0;
        for (::int32 I = 0; I < Out.Num(); ++I) Sum += Out[I];
        CHECK(Sum == 600, "Array(): sum mismatch");
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TSet.UEParitySurface: PASS\n");
    return 0;
}
