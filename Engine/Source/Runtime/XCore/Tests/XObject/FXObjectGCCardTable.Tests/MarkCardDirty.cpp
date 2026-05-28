// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGCCardTable.Tests/MarkCardDirty.cpp -- single-card dirty mark
// (XCoreXObject Rev 4 §4.5).
// =====================================================================
//
// Verifies:
//   * MarkCardDirty(addr) sets one card dirty.
//   * Repeated MarkCardDirty on the same card does not over-bump the
//     dirty counter (idempotency).
//   * Distinct addresses within the same card map to the same dirty
//     mark.
//   * Address at card boundary maps to the higher card.
//   * Out-of-range MarkCardDirty is silently ignored (no crash; no
//     counter bump).
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

    // Use a 16 KB heap (32 cards of 512 bytes).
    constexpr ::std::size_t kHeapBytes = 16 * 1024;
    constexpr ::std::size_t kExpectedCards = 32;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);

    CardTable.Initialize(Heap.data(), kHeapBytes);
    Check(CardTable.GetTotalCards() == kExpectedCards,
          "Initialize failed for 16 KB heap");

    // -----------------------------------------------------------------
    // Test 1: Mark a single card.
    //
    // Address at offset 1024 = card 2 (1024 / 512 = 2).
    // -----------------------------------------------------------------
    {
        const void* Addr = Heap.data() + 1024;
        CardTable.MarkCardDirty(Addr);
        Check(CardTable.GetDirtyCardCount() == 1,
              "After one MarkCardDirty: dirty count != 1");
    }

    // -----------------------------------------------------------------
    // Test 2: Re-marking the SAME card is idempotent.
    // -----------------------------------------------------------------
    {
        const void* Addr = Heap.data() + 1024;
        CardTable.MarkCardDirty(Addr);
        CardTable.MarkCardDirty(Addr);
        CardTable.MarkCardDirty(Addr);
        Check(CardTable.GetDirtyCardCount() == 1,
              "Repeated MarkCardDirty on same card over-bumped counter");
    }

    // -----------------------------------------------------------------
    // Test 3: Different offsets within the same card map to the same
    // dirty mark.
    //
    // Offsets 1024, 1025, 1500 all fall in card 2 (1024-1535).
    // -----------------------------------------------------------------
    {
        CardTable.MarkCardDirty(Heap.data() + 1024);
        CardTable.MarkCardDirty(Heap.data() + 1025);
        CardTable.MarkCardDirty(Heap.data() + 1500);
        Check(CardTable.GetDirtyCardCount() == 1,
              "Multiple offsets within same card produced multiple "
              "dirty entries");
    }

    // -----------------------------------------------------------------
    // Test 4: Card boundary -- offset 1536 is the first byte of card 3.
    // -----------------------------------------------------------------
    {
        CardTable.MarkCardDirty(Heap.data() + 1536);
        Check(CardTable.GetDirtyCardCount() == 2,
              "Card boundary: distinct card was not marked");
    }

    // -----------------------------------------------------------------
    // Test 5: Out-of-range addresses are silently ignored.
    //
    // Address BELOW the heap base.
    // -----------------------------------------------------------------
    {
        const ::std::size_t BeforeCount = CardTable.GetDirtyCardCount();
        const ::std::uintptr_t BelowAddr =
            reinterpret_cast<::std::uintptr_t>(Heap.data()) - 4096;
        CardTable.MarkCardDirty(reinterpret_cast<const void*>(BelowAddr));
        Check(CardTable.GetDirtyCardCount() == BeforeCount,
              "Below-heap address bumped dirty count");
    }

    // -----------------------------------------------------------------
    // Test 6: Out-of-range above heap is also silently ignored.
    // -----------------------------------------------------------------
    {
        const ::std::size_t BeforeCount = CardTable.GetDirtyCardCount();
        const ::std::uintptr_t AboveAddr =
            reinterpret_cast<::std::uintptr_t>(Heap.data()) + kHeapBytes + 4096;
        CardTable.MarkCardDirty(reinterpret_cast<const void*>(AboveAddr));
        Check(CardTable.GetDirtyCardCount() == BeforeCount,
              "Above-heap address bumped dirty count");
    }

    // -----------------------------------------------------------------
    // Test 7: nullptr is silently ignored.
    // -----------------------------------------------------------------
    {
        const ::std::size_t BeforeCount = CardTable.GetDirtyCardCount();
        CardTable.MarkCardDirty(nullptr);
        Check(CardTable.GetDirtyCardCount() == BeforeCount,
              "nullptr MarkCardDirty bumped dirty count");
    }

    CardTable.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGCCardTable.MarkCardDirty: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGCCardTable.MarkCardDirty: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
