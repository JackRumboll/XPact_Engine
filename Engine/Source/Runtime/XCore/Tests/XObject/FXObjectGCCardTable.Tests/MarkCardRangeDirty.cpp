// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGCCardTable.Tests/MarkCardRangeDirty.cpp -- range-mark
// barrier (XCoreXObject Rev 4 §4.5).
// =====================================================================
//
// Verifies:
//   * MarkCardRangeDirty over N cards marks exactly N cards dirty.
//   * Sub-card range (within one card) marks one card.
//   * Range spanning a card boundary marks both cards.
//   * Range clamped at heap boundaries: out-of-range bytes ignored.
//   * Zero-length range is a no-op.
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
    // Test 1: Range entirely within ONE card -- one dirty card.
    //
    // Range [16, 64) -- both ends inside card 0 (0-511).
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        CardTable.MarkCardRangeDirty(Heap.data() + 16, 48);
        Check(CardTable.GetDirtyCardCount() == 1,
              "Sub-card range did not produce 1 dirty card");
    }

    // -----------------------------------------------------------------
    // Test 2: Range spanning ONE card boundary -- 2 dirty cards.
    //
    // Range [500, 600) -- spans card 0 (0-511) and card 1 (512-1023).
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        CardTable.MarkCardRangeDirty(Heap.data() + 500, 100);
        Check(CardTable.GetDirtyCardCount() == 2,
              "Range crossing one card boundary did not produce 2 "
              "dirty cards");
    }

    // -----------------------------------------------------------------
    // Test 3: Range spanning N cards -- N dirty cards.
    //
    // Range [0, 4096) -- covers cards 0..7 (8 cards).
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        CardTable.MarkCardRangeDirty(Heap.data(), 4096);
        Check(CardTable.GetDirtyCardCount() == 8,
              "4 KB range did not produce 8 dirty cards");
    }

    // -----------------------------------------------------------------
    // Test 4: Range with start BELOW the heap base is clamped.
    //
    // Start address before heap; end inside heap card 1.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        const ::std::uintptr_t BaseAddr =
            reinterpret_cast<::std::uintptr_t>(Heap.data());
        const ::std::uintptr_t BelowStart = BaseAddr - 1024;
        // 1024 bytes before heap + 700 bytes into heap = 1724 bytes
        // total. Expected cards dirtied: 0..1 (700 bytes from base
        // crosses into card 1 since 700 > 512).
        CardTable.MarkCardRangeDirty(
            reinterpret_cast<const void*>(BelowStart),
            1024 + 700);
        Check(CardTable.GetDirtyCardCount() == 2,
              "Range with start below heap: dirty count != 2 "
              "(expected: cards 0 + 1 within heap)");
    }

    // -----------------------------------------------------------------
    // Test 5: Range extending PAST the heap top is clamped.
    //
    // Start in card 30; end past heap top.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        const ::std::size_t StartOffset = 30 * 512;     // card 30
        // 4096 bytes from offset 30*512 = 15360 extends to 19456,
        // past heap (16384). Cards 30 + 31 should be dirtied (the
        // last 2 cards of the heap).
        CardTable.MarkCardRangeDirty(Heap.data() + StartOffset, 4096);
        Check(CardTable.GetDirtyCardCount() == 2,
              "Range with end past heap: dirty count != 2 "
              "(expected: cards 30 + 31)");
    }

    // -----------------------------------------------------------------
    // Test 6: Range entirely below heap -- no dirty cards.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        const ::std::uintptr_t BaseAddr =
            reinterpret_cast<::std::uintptr_t>(Heap.data());
        const ::std::uintptr_t BelowStart = BaseAddr - 4096;
        CardTable.MarkCardRangeDirty(
            reinterpret_cast<const void*>(BelowStart),
            1024);
        Check(CardTable.GetDirtyCardCount() == 0,
              "Range entirely below heap: dirty count != 0");
    }

    // -----------------------------------------------------------------
    // Test 7: Range entirely above heap -- no dirty cards.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        const ::std::uintptr_t BaseAddr =
            reinterpret_cast<::std::uintptr_t>(Heap.data());
        const ::std::uintptr_t AboveStart = BaseAddr + kHeapBytes + 1024;
        CardTable.MarkCardRangeDirty(
            reinterpret_cast<const void*>(AboveStart),
            1024);
        Check(CardTable.GetDirtyCardCount() == 0,
              "Range entirely above heap: dirty count != 0");
    }

    // -----------------------------------------------------------------
    // Test 8: Zero-length range is a no-op.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        CardTable.MarkCardRangeDirty(Heap.data() + 1024, 0);
        Check(CardTable.GetDirtyCardCount() == 0,
              "Zero-length range produced dirty marks");
    }

    CardTable.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGCCardTable.MarkCardRangeDirty: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGCCardTable.MarkCardRangeDirty: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
