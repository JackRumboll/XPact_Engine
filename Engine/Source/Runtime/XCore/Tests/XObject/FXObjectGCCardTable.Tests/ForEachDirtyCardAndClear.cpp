// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGCCardTable.Tests/ForEachDirtyCardAndClear.cpp -- GC mark
// consumer iteration (XCoreXObject Rev 4 §4.5).
// =====================================================================
//
// Verifies:
//   * Visitor is invoked exactly once for each dirty card.
//   * Cards are cleared after iteration (subsequent calls see no
//     dirty cards).
//   * Returned count matches the number of dirty cards visited.
//   * Visiting an EMPTY table is a no-op (0 returned; visitor not
//     called).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectGCCardTable.h"

#include <cstdint>
#include <iostream>
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
}

int main()
{
    using ::XCore::FXObjectGCCardTable;

    ::XCore::HAL::FMemory::__Init();

    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();
    CardTable.__ResetForTests();

    // 16 KB heap = 32 cards.
    constexpr ::std::size_t kHeapBytes = 16 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    CardTable.Initialize(Heap.data(), kHeapBytes);

    // -----------------------------------------------------------------
    // Test 1: Empty table -- ForEach returns 0 + visitor uncalled.
    // -----------------------------------------------------------------
    {
        int VisitorCallCount = 0;
        const ::std::size_t Visited = CardTable.ForEachDirtyCardAndClear(
            [&VisitorCallCount](::std::size_t /*Idx*/) {
                ++VisitorCallCount;
            });
        Check(Visited == 0, "ForEach on empty table returned non-zero");
        Check(VisitorCallCount == 0,
              "ForEach on empty table invoked visitor");
    }

    // -----------------------------------------------------------------
    // Test 2: Mark 3 distinct cards -- visit + clear.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        // Mark cards 0, 5, 10.
        CardTable.MarkCardDirty(Heap.data() + 0);
        CardTable.MarkCardDirty(Heap.data() + 5 * 512);
        CardTable.MarkCardDirty(Heap.data() + 10 * 512);
        Check(CardTable.GetDirtyCardCount() == 3,
              "Pre-Visit: dirty count != 3");

        std::vector<::std::size_t> VisitedIndices;
        const ::std::size_t Visited = CardTable.ForEachDirtyCardAndClear(
            [&VisitedIndices](::std::size_t Idx) {
                VisitedIndices.push_back(Idx);
            });

        Check(Visited == 3,
              "ForEach returned count != 3");
        Check(VisitedIndices.size() == 3,
              "Visitor invoked != 3 times");
        if (VisitedIndices.size() == 3)
        {
            // ForEach should visit in ascending order.
            Check(VisitedIndices[0] == 0,
                  "First visited card index != 0");
            Check(VisitedIndices[1] == 5,
                  "Second visited card index != 5");
            Check(VisitedIndices[2] == 10,
                  "Third visited card index != 10");
        }

        // Cards should be cleared after the visit.
        Check(CardTable.GetDirtyCardCount() == 0,
              "Post-Visit: dirty count != 0 (cards not cleared)");

        // Second ForEach finds no dirty cards.
        int SecondVisitorCallCount = 0;
        const ::std::size_t SecondVisited =
            CardTable.ForEachDirtyCardAndClear(
                [&SecondVisitorCallCount](::std::size_t /*Idx*/) {
                    ++SecondVisitorCallCount;
                });
        Check(SecondVisited == 0,
              "Second ForEach found dirty cards (clear failed)");
        Check(SecondVisitorCallCount == 0,
              "Second ForEach invoked visitor");
    }

    // -----------------------------------------------------------------
    // Test 3: Mark all 32 cards -- visit + clear.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        for (::std::size_t I = 0; I < 32; ++I)
        {
            CardTable.MarkCardDirty(Heap.data() + I * 512);
        }
        Check(CardTable.GetDirtyCardCount() == 32,
              "Pre-Visit (all cards dirty): dirty count != 32");

        ::std::size_t VisitCount = 0;
        const ::std::size_t Visited = CardTable.ForEachDirtyCardAndClear(
            [&VisitCount](::std::size_t /*Idx*/) {
                ++VisitCount;
            });
        Check(Visited == 32, "ForEach on all-dirty returned count != 32");
        Check(VisitCount == 32, "ForEach on all-dirty invoked visitor != 32 times");
        Check(CardTable.GetDirtyCardCount() == 0,
              "Post-Visit (all dirty): dirty count != 0");
    }

    CardTable.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGCCardTable.ForEachDirtyCardAndClear: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGCCardTable.ForEachDirtyCardAndClear: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
