// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGC_WriteBarrier.Tests/BarrierMarksCard.cpp -- card dirtied for slot
// containing address (XCoreXObject Rev 4 §4.5 + §5.2).
// =====================================================================
//
// Per spec §4.3: "Card-table dirty mark (always; cost is one byte
// store)". The barrier marks the card containing the SLOT address
// (i.e., &slot), NOT the card containing the SLOT's value.
//
// Verifies:
//   * Barrier dirties exactly the card containing &slot.
//   * Two writes to slots in the SAME card produce ONE dirty entry.
//   * Two writes to slots in DIFFERENT cards produce TWO dirty
//     entries.
//   * Barrier on a slot OUTSIDE the heap range is silently ignored.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XGCWriteBarrier.h"
#include "XObject/XObject.h"

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
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::g_XGCIsConcurrentMarkActive;
    using ::XCore::GetThreadSatbQueue;
    using ::XCore::XGCWriteBarrierImpl;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    // Concurrent mark INACTIVE (we focus on card-side behaviour).
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();
    CardTable.__ResetForTests();

    // 16 KB heap = 32 cards.
    constexpr ::std::size_t kHeapBytes = 16 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    CardTable.Initialize(Heap.data(), kHeapBytes);

    XObject Sentinel;

    // -----------------------------------------------------------------
    // Test 1: Slot in card 0; barrier dirties card 0.
    // -----------------------------------------------------------------
    {
        XObject** Slot = reinterpret_cast<XObject**>(Heap.data() + 16);
        *Slot = nullptr;
        XGCWriteBarrierImpl(Slot, &Sentinel);
        Check(CardTable.GetDirtyCardCount() == 1,
              "After one barrier in card 0: dirty count != 1");
    }

    // -----------------------------------------------------------------
    // Test 2: Second slot in card 0; idempotent.
    // -----------------------------------------------------------------
    {
        XObject** Slot = reinterpret_cast<XObject**>(Heap.data() + 256);
        *Slot = nullptr;
        XGCWriteBarrierImpl(Slot, &Sentinel);
        Check(CardTable.GetDirtyCardCount() == 1,
              "Second barrier in same card: dirty count != 1");
    }

    // -----------------------------------------------------------------
    // Test 3: Slot in card 1; bumps to 2 dirty cards.
    // -----------------------------------------------------------------
    {
        XObject** Slot = reinterpret_cast<XObject**>(Heap.data() + 768);
        *Slot = nullptr;
        XGCWriteBarrierImpl(Slot, &Sentinel);
        Check(CardTable.GetDirtyCardCount() == 2,
              "Barrier in card 1: dirty count != 2");
    }

    // -----------------------------------------------------------------
    // Test 4: Out-of-range slot is silently ignored.
    // -----------------------------------------------------------------
    {
        const ::std::size_t CountBefore = CardTable.GetDirtyCardCount();
        const ::std::uintptr_t HeapBase =
            reinterpret_cast<::std::uintptr_t>(Heap.data());
        const ::std::uintptr_t OutOfRangeAddr = HeapBase + kHeapBytes + 4096;
        XObject** Slot = reinterpret_cast<XObject**>(OutOfRangeAddr);
        // Cannot deref this slot; the barrier loads OLD value from it
        // only if concurrent mark is active. We have it OFF; barrier
        // just dirties the (out-of-range) card.
        XGCWriteBarrierImpl(Slot, &Sentinel);
        Check(CardTable.GetDirtyCardCount() == CountBefore,
              "Out-of-range slot bumped dirty count");
    }

    // Cleanup.
    CardTable.__ResetForTests();
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    if (g_FailureCount == 0)
    {
        std::cout << "XGC_WriteBarrier.BarrierMarksCard: PASS\n";
        return 0;
    }
    std::cerr << "XGC_WriteBarrier.BarrierMarksCard: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
