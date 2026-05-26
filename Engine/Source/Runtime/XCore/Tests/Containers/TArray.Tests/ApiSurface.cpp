// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TArray.Tests/ApiSurface.cpp -- HIGH-3 API surface expansion.
// =====================================================================
//
// XCore-4a Section 5.6 + Rev 1 audit HIGH-3 close-out: covers each
// of the new TArray API methods (Empty, Pop, Last, Top, Append (x3),
// Insert (x2), Find, FindByPredicate, IndexOfByPredicate, Contains,
// Swap, Sort (x2), StableSort, RemoveAll, RemoveSingle, HeapPush,
// HeapPop).
//
// Each section exercises one method's contract.
//
// =====================================================================

#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <functional>

namespace
{
    using Arr = ::XCore::TArray<::int32, ::XCore::DefaultAllocator>;

    bool TestEmpty()
    {
        Arr A;
        for (::int32 I = 0; I < 10; ++I) A.Add(I);
        A.Empty(5);
        if (A.Num() != 0) { std::fprintf(stderr, "Empty: Num != 0 (got %d)\n", A.Num()); return false; }
        if (A.Max() < 5)  { std::fprintf(stderr, "Empty: Max < 5 (got %d)\n", A.Max()); return false; }
        // Empty(0) drops buffer.
        A.Empty(0);
        if (A.Num() != 0 || A.Max() != 0)
        {
            std::fprintf(stderr, "Empty(0): expected Num=0 Max=0 got %d/%d\n", A.Num(), A.Max());
            return false;
        }
        return true;
    }

    bool TestPopLastTop()
    {
        Arr A;
        for (::int32 I = 0; I < 5; ++I) A.Add(I * 10);   // [0,10,20,30,40]
        if (A.Last() != 40)       { std::fprintf(stderr, "Last(0) != 40 (got %d)\n", A.Last()); return false; }
        if (A.Last(1) != 30)      { std::fprintf(stderr, "Last(1) != 30 (got %d)\n", A.Last(1)); return false; }
        if (A.Top() != 40)        { std::fprintf(stderr, "Top != 40 (got %d)\n", A.Top()); return false; }
        const ::int32 V = A.Pop();
        if (V != 40)              { std::fprintf(stderr, "Pop != 40 (got %d)\n", V); return false; }
        if (A.Num() != 4)         { std::fprintf(stderr, "Post-Pop Num != 4 (got %d)\n", A.Num()); return false; }
        if (A.Top() != 30)        { std::fprintf(stderr, "Post-Pop Top != 30 (got %d)\n", A.Top()); return false; }
        return true;
    }

    bool TestAppend()
    {
        Arr A; Arr B;
        for (::int32 I = 0; I < 3; ++I) A.Add(I);        // [0,1,2]
        for (::int32 I = 3; I < 6; ++I) B.Add(I);        // [3,4,5]

        // Copy-append.
        const ::int32 StartIdx = A.Append(B);
        if (StartIdx != 3)        { std::fprintf(stderr, "Append copy: StartIdx != 3 (got %d)\n", StartIdx); return false; }
        if (A.Num() != 6)         { std::fprintf(stderr, "Append copy: Num != 6 (got %d)\n", A.Num()); return false; }
        for (::int32 I = 0; I < 6; ++I)
            if (A[I] != I)        { std::fprintf(stderr, "Append copy: A[%d] != %d\n", I, I); return false; }
        if (B.Num() != 3)         { std::fprintf(stderr, "Append copy: B mutated (Num=%d)\n", B.Num()); return false; }

        // Move-append.
        Arr C;
        for (::int32 I = 10; I < 13; ++I) C.Add(I);     // [10,11,12]
        const ::int32 StartIdx2 = A.Append(::std::move(C));
        if (StartIdx2 != 6)       { std::fprintf(stderr, "Append move: StartIdx != 6 (got %d)\n", StartIdx2); return false; }
        if (A.Num() != 9)         { std::fprintf(stderr, "Append move: Num != 9 (got %d)\n", A.Num()); return false; }
        if (A.Last() != 12)       { std::fprintf(stderr, "Append move: Last != 12\n"); return false; }
        if (C.Num() != 0)         { std::fprintf(stderr, "Append move: C not emptied (Num=%d)\n", C.Num()); return false; }

        // Raw-range append.
        const ::int32 Tail[] = {100, 101, 102};
        A.Append(Tail, 3);
        if (A.Num() != 12)        { std::fprintf(stderr, "Append raw: Num != 12 (got %d)\n", A.Num()); return false; }
        if (A.Last() != 102)      { std::fprintf(stderr, "Append raw: Last != 102\n"); return false; }
        return true;
    }

    bool TestInsert()
    {
        Arr A;
        for (::int32 I = 0; I < 3; ++I) A.Add(I * 10);   // [0,10,20]
        const ::int32 Idx = A.Insert(99, 1);             // [0,99,10,20]
        if (Idx != 1)             { std::fprintf(stderr, "Insert: Idx != 1 (got %d)\n", Idx); return false; }
        if (A.Num() != 4)         { std::fprintf(stderr, "Insert: Num != 4 (got %d)\n", A.Num()); return false; }
        if (A[0] != 0 || A[1] != 99 || A[2] != 10 || A[3] != 20)
        {
            std::fprintf(stderr, "Insert: layout wrong (%d,%d,%d,%d)\n", A[0], A[1], A[2], A[3]);
            return false;
        }
        // Insert at end.
        A.Insert(::int32(500), A.Num());
        if (A.Num() != 5 || A.Last() != 500) { std::fprintf(stderr, "Insert at end failed\n"); return false; }
        return true;
    }

    bool TestFindContains()
    {
        Arr A;
        for (::int32 I = 0; I < 5; ++I) A.Add(I * 10);
        if (A.Find(20) != 2)      { std::fprintf(stderr, "Find(20) != 2\n"); return false; }
        if (A.Find(999) != ::INDEX_NONE) { std::fprintf(stderr, "Find(999) != INDEX_NONE\n"); return false; }
        if (!A.Contains(30))      { std::fprintf(stderr, "Contains(30) false\n"); return false; }
        if (A.Contains(999))      { std::fprintf(stderr, "Contains(999) true\n"); return false; }

        const ::int32 Idx = A.IndexOfByPredicate([](::int32 V) { return V > 25; });
        if (Idx != 3)             { std::fprintf(stderr, "IndexOfByPredicate(>25) != 3 (got %d)\n", Idx); return false; }

        const ::int32* P = A.FindByPredicate([](::int32 V) { return V == 40; });
        if (P == nullptr || *P != 40) { std::fprintf(stderr, "FindByPredicate(==40) failed\n"); return false; }

        const ::int32* PNull = A.FindByPredicate([](::int32 V) { return V == 999; });
        if (PNull != nullptr) { std::fprintf(stderr, "FindByPredicate(==999) not null\n"); return false; }

        return true;
    }

    bool TestSwapSort()
    {
        Arr A;
        const ::int32 Source[] = {5, 2, 8, 1, 9, 3};
        for (::int32 V : Source) A.Add(V);
        A.Swap(0, 5);
        if (A[0] != 3 || A[5] != 5) { std::fprintf(stderr, "Swap broken\n"); return false; }

        A.Sort();
        for (::int32 I = 1; I < A.Num(); ++I)
            if (A[I-1] > A[I]) { std::fprintf(stderr, "Sort not monotone\n"); return false; }

        A.Sort([](::int32 X, ::int32 Y) { return X > Y; });
        for (::int32 I = 1; I < A.Num(); ++I)
            if (A[I-1] < A[I]) { std::fprintf(stderr, "Sort(desc) not monotone\n"); return false; }

        Arr B;
        const ::int32 Stable[] = {2, 1, 2, 1, 2};
        for (::int32 V : Stable) B.Add(V);
        B.StableSort();
        for (::int32 I = 1; I < B.Num(); ++I)
            if (B[I-1] > B[I]) { std::fprintf(stderr, "StableSort not monotone\n"); return false; }

        return true;
    }

    bool TestRemoveAllSingle()
    {
        Arr A;
        for (::int32 I = 0; I < 10; ++I) A.Add(I);          // [0..9]
        const ::int32 Removed = A.RemoveAll([](::int32 V) { return (V % 2) == 0; });
        if (Removed != 5)         { std::fprintf(stderr, "RemoveAll: %d removed (expected 5)\n", Removed); return false; }
        if (A.Num() != 5)         { std::fprintf(stderr, "RemoveAll: Num != 5 (got %d)\n", A.Num()); return false; }
        // Remaining: [1,3,5,7,9].
        for (::int32 I = 0; I < 5; ++I)
            if (A[I] != (2*I + 1)) { std::fprintf(stderr, "RemoveAll: A[%d] = %d (expected %d)\n", I, A[I], 2*I+1); return false; }

        // RemoveSingle.
        const ::int32 NumRemoved = A.RemoveSingle(5);
        if (NumRemoved != 1)      { std::fprintf(stderr, "RemoveSingle: %d (expected 1)\n", NumRemoved); return false; }
        if (A.Num() != 4)         { std::fprintf(stderr, "RemoveSingle: Num != 4 (got %d)\n", A.Num()); return false; }
        // Remaining: [1,3,7,9].
        if (A.Find(5) != ::INDEX_NONE) { std::fprintf(stderr, "RemoveSingle: 5 still present\n"); return false; }

        // Removing a non-existent value returns 0.
        const ::int32 NumNot = A.RemoveSingle(999);
        if (NumNot != 0)          { std::fprintf(stderr, "RemoveSingle: non-existent returned %d\n", NumNot); return false; }
        return true;
    }

    bool TestHeap()
    {
        Arr A;
        // Build a max-heap with operator<.
        ::std::less<::int32> Less;
        A.HeapPush(::int32(5), Less);
        A.HeapPush(::int32(20), Less);
        A.HeapPush(::int32(3), Less);
        A.HeapPush(::int32(15), Less);
        // Max-heap top = greatest.
        if (A.Num() != 4)         { std::fprintf(stderr, "Heap: Num != 4 (got %d)\n", A.Num()); return false; }
        const ::int32 Top1 = A.HeapPop(Less);
        if (Top1 != 20)           { std::fprintf(stderr, "Heap: pop1 != 20 (got %d)\n", Top1); return false; }
        const ::int32 Top2 = A.HeapPop(Less);
        if (Top2 != 15)           { std::fprintf(stderr, "Heap: pop2 != 15 (got %d)\n", Top2); return false; }
        const ::int32 Top3 = A.HeapPop(Less);
        if (Top3 != 5)            { std::fprintf(stderr, "Heap: pop3 != 5 (got %d)\n", Top3); return false; }
        const ::int32 Top4 = A.HeapPop(Less);
        if (Top4 != 3)            { std::fprintf(stderr, "Heap: pop4 != 3 (got %d)\n", Top4); return false; }
        return true;
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    if (!TestEmpty())          return 1;
    if (!TestPopLastTop())     return 1;
    if (!TestAppend())         return 1;
    if (!TestInsert())         return 1;
    if (!TestFindContains())   return 1;
    if (!TestSwapSort())       return 1;
    if (!TestRemoveAllSingle()) return 1;
    if (!TestHeap())           return 1;

    std::printf("PASS: TArray.ApiSurface (Rev 1 HIGH-3 close-out: Empty/Pop/Last/Top/Append/Insert/"
                "Find*/Contains/Swap/Sort*/StableSort/RemoveAll/RemoveSingle/HeapPush/HeapPop)\n");

    ::XCore::HAL::FMemory::__Shutdown();
    return 0;
}
