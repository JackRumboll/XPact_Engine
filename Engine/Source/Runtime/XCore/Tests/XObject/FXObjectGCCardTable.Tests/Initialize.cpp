// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectGCCardTable.Tests/Initialize.cpp -- card-table initialisation
// (XCoreXObject Rev 4 §4.5 + §5.1).
// =====================================================================
//
// Verifies:
//   * Get() returns a stable singleton reference.
//   * Initialize allocates the card array with the correct total card
//     count (HeapByteSize / kCardSize, rounded up).
//   * Post-Initialize: GetTotalCards / GetDirtyCardCount / GetHeapBase
//     / GetHeapByteSize report the expected values.
//   * Re-Initialize with the same range is idempotent (no new
//     allocation visible; counters reset).
//   * Re-Initialize with a different range replaces the array.
//   * __ResetForTests releases the array; subsequent accessors return
//     zero / nullptr.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectGCCardTable.h"

#include <cstdint>
#include <cstring>
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

    // -----------------------------------------------------------------
    // Test 1: Get() is stable.
    // -----------------------------------------------------------------
    {
        FXObjectGCCardTable& Other = FXObjectGCCardTable::Get();
        Check(&CardTable == &Other, "Get() did not return a stable singleton");
    }

    // -----------------------------------------------------------------
    // Test 2: Pre-Initialize state.
    // -----------------------------------------------------------------
    {
        Check(CardTable.GetTotalCards() == 0,
              "Pre-Initialize GetTotalCards != 0");
        Check(CardTable.GetDirtyCardCount() == 0,
              "Pre-Initialize GetDirtyCardCount != 0");
        Check(CardTable.GetHeapBase() == nullptr,
              "Pre-Initialize GetHeapBase != nullptr");
        Check(CardTable.GetHeapByteSize() == 0,
              "Pre-Initialize GetHeapByteSize != 0");
    }

    // -----------------------------------------------------------------
    // Test 3: Initialize with a 4 KB heap (= 8 cards of 512 bytes).
    //
    // Use a heap-allocated buffer so the address range is real (the
    // card table's MarkCardDirty path bounds-checks the slot address).
    // -----------------------------------------------------------------
    {
        constexpr ::std::size_t kHeapBytes = 4 * 1024;        // 4 KB
        constexpr ::std::size_t kExpectedCards = 8;
        std::vector<::std::uint8_t> Heap(kHeapBytes, 0);

        CardTable.Initialize(Heap.data(), kHeapBytes);

        Check(CardTable.GetTotalCards() == kExpectedCards,
              "Initialize(4 KB) -> GetTotalCards != 8");
        Check(CardTable.GetDirtyCardCount() == 0,
              "Initialize(4 KB) -> GetDirtyCardCount != 0");
        Check(CardTable.GetHeapBase() == Heap.data(),
              "Initialize(4 KB) -> GetHeapBase mismatch");
        Check(CardTable.GetHeapByteSize() == kHeapBytes,
              "Initialize(4 KB) -> GetHeapByteSize mismatch");
        Check(!CardTable.IsSaturated(),
              "Initialize(4 KB) -> IsSaturated true on empty heap");
    }

    // -----------------------------------------------------------------
    // Test 4: Re-Initialize with same range is idempotent (counters
    // reset).
    // -----------------------------------------------------------------
    {
        constexpr ::std::size_t kHeapBytes = 4 * 1024;
        std::vector<::std::uint8_t> Heap(kHeapBytes, 0);

        CardTable.Initialize(Heap.data(), kHeapBytes);

        // Dirty a card explicitly.
        CardTable.MarkCardDirty(Heap.data() + 1024);
        Check(CardTable.GetDirtyCardCount() == 1,
              "MarkCardDirty post-Init did not set dirty count to 1");

        // Re-initialize with same range -> counters reset.
        CardTable.Initialize(Heap.data(), kHeapBytes);
        Check(CardTable.GetDirtyCardCount() == 0,
              "Re-Initialize did not reset dirty count");
        Check(CardTable.GetTotalCards() == 8,
              "Re-Initialize changed total cards unexpectedly");
    }

    // -----------------------------------------------------------------
    // Test 5: Re-Initialize with DIFFERENT range replaces the array.
    // -----------------------------------------------------------------
    {
        constexpr ::std::size_t kHeapBytesA = 4 * 1024;       // 8 cards
        constexpr ::std::size_t kHeapBytesB = 16 * 1024;      // 32 cards
        std::vector<::std::uint8_t> HeapA(kHeapBytesA, 0);
        std::vector<::std::uint8_t> HeapB(kHeapBytesB, 0);

        CardTable.Initialize(HeapA.data(), kHeapBytesA);
        Check(CardTable.GetTotalCards() == 8,
              "Initialize(A 4 KB) total cards != 8");

        CardTable.Initialize(HeapB.data(), kHeapBytesB);
        Check(CardTable.GetTotalCards() == 32,
              "Re-Initialize(B 16 KB) total cards != 32");
        Check(CardTable.GetHeapBase() == HeapB.data(),
              "Re-Initialize did not update HeapBase to B");
        Check(CardTable.GetHeapByteSize() == kHeapBytesB,
              "Re-Initialize did not update HeapByteSize to B");
    }

    // -----------------------------------------------------------------
    // Test 6: Non-multiple-of-card heap rounds UP.
    //
    // 1000 bytes -> ceil(1000 / 512) = 2 cards.
    // -----------------------------------------------------------------
    {
        constexpr ::std::size_t kHeapBytes = 1000;
        std::vector<::std::uint8_t> Heap(kHeapBytes, 0);

        CardTable.Initialize(Heap.data(), kHeapBytes);

        Check(CardTable.GetTotalCards() == 2,
              "Initialize(1000) -> GetTotalCards != 2 (round-up failed)");
    }

    // -----------------------------------------------------------------
    // Test 7: __ResetForTests clears the state.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        Check(CardTable.GetTotalCards() == 0, "Post-Reset GetTotalCards != 0");
        Check(CardTable.GetDirtyCardCount() == 0,
              "Post-Reset GetDirtyCardCount != 0");
        Check(CardTable.GetHeapBase() == nullptr,
              "Post-Reset GetHeapBase != nullptr");
        Check(CardTable.GetHeapByteSize() == 0,
              "Post-Reset GetHeapByteSize != 0");
    }

    CardTable.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectGCCardTable.Initialize: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectGCCardTable.Initialize: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
