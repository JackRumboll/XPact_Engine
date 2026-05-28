// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGCCardTable.Tests/Saturation.cpp -- IsSaturated predicate
// (XCoreXObject Rev 4 §4.5 + Rev 3 FIX-N-R2-7).
// =====================================================================
//
// Verifies the 50% saturation threshold:
//   * Empty table: not saturated.
//   * Below threshold (e.g., 25%): not saturated.
//   * At threshold (50%): saturated.
//   * Above threshold (e.g., 75%): saturated.
//   * After ForEachDirtyCardAndClear: not saturated again.
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
    constexpr ::std::size_t kTotalCards = 32;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    CardTable.Initialize(Heap.data(), kHeapBytes);

    Check(CardTable.GetTotalCards() == kTotalCards,
          "Total cards != 32");

    // -----------------------------------------------------------------
    // Test 1: Empty table -- not saturated.
    // -----------------------------------------------------------------
    Check(!CardTable.IsSaturated(),
          "Empty card table reports saturated");

    // -----------------------------------------------------------------
    // Test 2: 25% dirty (8 cards) -- not saturated.
    // -----------------------------------------------------------------
    {
        for (::std::size_t I = 0; I < 8; ++I)
        {
            CardTable.MarkCardDirty(Heap.data() + I * 512);
        }
        Check(CardTable.GetDirtyCardCount() == 8,
              "25% dirty: dirty count != 8");
        Check(!CardTable.IsSaturated(),
              "25% dirty reports saturated (threshold is 50%)");
    }

    // -----------------------------------------------------------------
    // Test 3: ~47% dirty (15 cards) -- not saturated (just below 50%).
    // -----------------------------------------------------------------
    {
        // Mark 7 more cards (total 15 = 46.875%).
        for (::std::size_t I = 8; I < 15; ++I)
        {
            CardTable.MarkCardDirty(Heap.data() + I * 512);
        }
        Check(CardTable.GetDirtyCardCount() == 15,
              "~47% dirty: dirty count != 15");
        Check(!CardTable.IsSaturated(),
              "~47% dirty reports saturated");
    }

    // -----------------------------------------------------------------
    // Test 4: 50% dirty (16 cards) -- saturated.
    // -----------------------------------------------------------------
    {
        CardTable.MarkCardDirty(Heap.data() + 15 * 512);
        Check(CardTable.GetDirtyCardCount() == 16,
              "50% dirty: dirty count != 16");
        Check(CardTable.IsSaturated(),
              "50% dirty does NOT report saturated (threshold met)");
    }

    // -----------------------------------------------------------------
    // Test 5: 75% dirty (24 cards) -- saturated.
    // -----------------------------------------------------------------
    {
        for (::std::size_t I = 16; I < 24; ++I)
        {
            CardTable.MarkCardDirty(Heap.data() + I * 512);
        }
        Check(CardTable.GetDirtyCardCount() == 24,
              "75% dirty: dirty count != 24");
        Check(CardTable.IsSaturated(),
              "75% dirty does NOT report saturated");
    }

    // -----------------------------------------------------------------
    // Test 6: ForEach + Clear returns to not-saturated.
    // -----------------------------------------------------------------
    {
        const ::std::size_t Visited = CardTable.ForEachDirtyCardAndClear(
            [](::std::size_t /*Idx*/) {});
        Check(Visited == 24, "ForEachDirtyCardAndClear visited != 24 cards");
        Check(!CardTable.IsSaturated(),
              "After clear: still saturated");
    }

    CardTable.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGCCardTable.Saturation: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGCCardTable.Saturation: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
