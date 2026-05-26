// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TMap.Tests/UEParitySurface.cpp -- UE-parity API surface coverage.
// =====================================================================
//
// XCore-4a Rev 3 Round 2 audit FIX-R2-MED-NEW-1. Verifies the UE-parity
// methods added to TMap match UE's behaviour:
//   * FindOrAdd       -- get-or-create (vs operator[] semantics)
//   * FindRef         -- by-value or default
//   * Append          -- bulk merge with overwrite semantics
//   * KeySort         -- in-place key ordering (transient until mutation)
//   * ValueSort       -- in-place value ordering (transient until mutation)
//   * GenerateKeyArray / GenerateValueArray -- bulk extraction
//   * FindAndRemoveChecked -- find + remove + return
//   * RemoveAndCopyValue   -- find + remove + copy out
//
// =====================================================================

#include "Containers/TMap.h"
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
    // FindOrAdd: inserts default-constructed value when absent;
    // returns existing reference when present.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        ::int32& Created = M.FindOrAdd(7);
        CHECK(Created == 0, "FindOrAdd absent: default value not 0");
        Created = 42;
        CHECK(M.FindOrAdd(7) == 42, "FindOrAdd present: did not return existing");
        CHECK(M.Num() == 1, "FindOrAdd Num after one insert/get");
    }

    // -----------------------------------------------------------------
    // FindRef: returns value-or-default; const-correct (does not insert).
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        M.Add(11, 1234);
        CHECK(M.FindRef(11) == 1234, "FindRef present");
        CHECK(M.FindRef(99) == 0,    "FindRef absent: not default");
        CHECK(M.Num() == 1, "FindRef absent must NOT insert");
    }

    // -----------------------------------------------------------------
    // Append (copy): copy-merge with overwrite semantics.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> A;
        ::XCore::TMap<::int32, ::int32> B;
        A.Add(1, 100);
        A.Add(2, 200);
        B.Add(2, 250);  // collision; B wins
        B.Add(3, 300);
        A.Append(B);
        CHECK(A.Num() == 3, "Append: expected 3 keys");
        CHECK(A.FindRef(1) == 100, "Append: key 1 unchanged");
        CHECK(A.FindRef(2) == 250, "Append: key 2 overwritten by B");
        CHECK(A.FindRef(3) == 300, "Append: key 3 added");
        CHECK(B.Num() == 2, "Append(const&) must not mutate source");
    }

    // -----------------------------------------------------------------
    // Append (move): consumes source.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> A;
        ::XCore::TMap<::int32, ::int32> B;
        A.Add(1, 100);
        B.Add(2, 200);
        B.Add(3, 300);
        A.Append(::std::move(B));
        CHECK(A.Num() == 3, "Append(&&): expected 3 keys");
        CHECK(B.Num() == 0, "Append(&&): source must be empty post-move");
    }

    // -----------------------------------------------------------------
    // KeySort: ascending integer keys.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        M.Add(50, 5);
        M.Add(10, 1);
        M.Add(30, 3);
        M.Add(20, 2);
        M.Add(40, 4);
        M.KeySort(::std::less<::int32>());

        ::int32 Prev = -1;
        for (const auto& P : M)
        {
            CHECK(P.Key > Prev, "KeySort: iteration not ascending");
            Prev = P.Key;
        }
        CHECK(M.Num() == 5, "KeySort: Num changed");
        CHECK(M.FindRef(10) == 1, "KeySort: value 10 lost");
        CHECK(M.FindRef(50) == 5, "KeySort: value 50 lost");
    }

    // -----------------------------------------------------------------
    // ValueSort: ascending integer values.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        M.Add(1, 99);
        M.Add(2, 17);
        M.Add(3, 42);
        M.ValueSort(::std::less<::int32>());

        ::int32 Prev = -1;
        for (const auto& P : M)
        {
            CHECK(P.Value > Prev, "ValueSort: iteration not ascending");
            Prev = P.Value;
        }
    }

    // -----------------------------------------------------------------
    // GenerateKeyArray / GenerateValueArray: bulk extraction.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        M.Add(1, 11);
        M.Add(2, 22);
        M.Add(3, 33);
        ::XCore::TArray<::int32> Keys;
        ::XCore::TArray<::int32> Vals;
        M.GenerateKeyArray(Keys);
        M.GenerateValueArray(Vals);
        CHECK(Keys.Num() == 3, "GenerateKeyArray: count mismatch");
        CHECK(Vals.Num() == 3, "GenerateValueArray: count mismatch");

        // Confirm every map entry is present (iteration order is
        // implementation-defined per the SwissTable contract).
        ::int32 KeySum = 0;
        ::int32 ValSum = 0;
        for (::int32 I = 0; I < Keys.Num(); ++I) KeySum += Keys[I];
        for (::int32 I = 0; I < Vals.Num(); ++I) ValSum += Vals[I];
        CHECK(KeySum == 6,  "GenerateKeyArray: sum mismatch");
        CHECK(ValSum == 66, "GenerateValueArray: sum mismatch");
    }

    // -----------------------------------------------------------------
    // FindAndRemoveChecked: present-path returns + removes.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        M.Add(7, 700);
        M.Add(8, 800);
        const ::int32 Got = M.FindAndRemoveChecked(7);
        CHECK(Got == 700, "FindAndRemoveChecked: wrong value returned");
        CHECK(!M.Contains(7), "FindAndRemoveChecked: key not removed");
        CHECK(M.Num() == 1, "FindAndRemoveChecked: Num post-remove");
    }

    // -----------------------------------------------------------------
    // RemoveAndCopyValue: present and absent paths.
    // -----------------------------------------------------------------
    {
        ::XCore::TMap<::int32, ::int32> M;
        M.Add(9, 900);
        ::int32 Out = -1;
        const bool Got = M.RemoveAndCopyValue(9, Out);
        CHECK(Got, "RemoveAndCopyValue: present returned false");
        CHECK(Out == 900, "RemoveAndCopyValue: wrong copy-out value");
        CHECK(!M.Contains(9), "RemoveAndCopyValue: key not removed");

        Out = -1;
        const bool MissGot = M.RemoveAndCopyValue(123, Out);
        CHECK(!MissGot, "RemoveAndCopyValue: absent returned true");
        CHECK(Out == -1, "RemoveAndCopyValue: absent mutated Out");
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TMap.UEParitySurface: PASS\n");
    return 0;
}
