// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGC_WriteBarrier.Tests/IntegrationWithAllocator.cpp -- write barrier
// across the allocator boundary (XCoreXObject Rev 4 §4.5 + §5.2).
// =====================================================================
//
// Verifies the barrier works correctly when the slot lives inside an
// FXObjectAllocator-allocated cell.
//
// SCOPE NOTE (Phase 5.f reconciliation): the FXObjectAllocator's slabs
// are NOT registered with the card table (Phase 5.f leaves the
// allocator-side integration as a forward commitment per the
// FXObjectGCCardTable.h "HEAP-RANGE NOTE"). For this test we manually
// initialise the card table to cover the cell's heap range -- the
// allocator cell is the "heap base" + the cell size is the "heap
// byte size".
//
// The test verifies that the barrier:
//   * Correctly maps the cell's slot address to a card index.
//   * Dirties the card (because the cell is within the registered
//     range).
//   * Does NOT crash on the allocator-owned memory.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectSatbQueue.h"
#include "XObject/XGCConcurrentState.h"
#include "XObject/XGCWriteBarrier.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <iostream>

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
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXObjectGlobalSatbLog;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::g_XGCIsConcurrentMarkActive;
    using ::XCore::GetThreadSatbQueue;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    // Reset state.
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});
    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    Allocator.__ResetForTests();
    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();
    CardTable.__ResetForTests();

    // -----------------------------------------------------------------
    // Allocate a cell from the FXObjectAllocator and run the barrier
    // against a slot inside the cell.
    // -----------------------------------------------------------------
    FClass IntegrationClass(FName("IntegrationClass"), nullptr);
    IntegrationClass.PropertiesSize = 128;
    IntegrationClass.MinAlignment   = 8;
    Allocator.RegisterClassPool(&IntegrationClass);

    void* Cell = Allocator.AllocateRaw(128, 8, &IntegrationClass);
    Check(Cell != nullptr, "Allocator returned nullptr cell");
    if (Cell == nullptr)
    {
        Allocator.__ResetForTests();
        std::cerr << "IntegrationWithAllocator: SKIP "
                     "(allocator did not produce a cell)\n";
        return 1;
    }

    // Initialise the card table to cover this single cell's address
    // range. (Real integration in a later phase will register every
    // slab; for this test we manually scope the card table to the
    // cell-under-test.)
    CardTable.Initialize(Cell, 128);

    // -----------------------------------------------------------------
    // Test 1: Slot at the start of the cell.
    // -----------------------------------------------------------------
    {
        XObject OldRef;
        XObject NewRef;

        // Treat the cell as XObject** at the first slot.
        XObject** Slot = reinterpret_cast<XObject**>(Cell);
        *Slot = &OldRef;

        const ::std::size_t CountBefore = CardTable.GetDirtyCardCount();
        XPACT_GC_STORE(*Slot, &NewRef);
        Check(CardTable.GetDirtyCardCount() == CountBefore + 1,
              "Card table did NOT dirty for allocator-backed cell slot");
        Check(*Slot == &NewRef,
              "Macro did NOT store new value into allocator cell slot");
    }

    // -----------------------------------------------------------------
    // Test 2: Slot near the end of the cell still maps to a card.
    //
    // For a 128-byte cell, all slots fall in card 0 (since 128 <
    // kCardSize = 512). After Initialize(Cell, 128), the card table
    // has exactly 1 card (ceil(128/512) = 1).
    // -----------------------------------------------------------------
    {
        Check(CardTable.GetTotalCards() == 1,
              "128-byte cell: total cards != 1");

        XObject OldRef;
        XObject NewRef;
        XObject** Slot = reinterpret_cast<XObject**>(
            static_cast<::std::uint8_t*>(Cell) + 120);
        *Slot = &OldRef;

        XPACT_GC_STORE(*Slot, &NewRef);
        Check(*Slot == &NewRef,
              "Macro did NOT store new value at end-of-cell slot");
    }

    Allocator.Deallocate(Cell);
    Allocator.__ResetForTests();
    CardTable.__ResetForTests();
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    if (g_FailureCount == 0)
    {
        std::cout << "XGC_WriteBarrier.IntegrationWithAllocator: PASS\n";
        return 0;
    }
    std::cerr << "XGC_WriteBarrier.IntegrationWithAllocator: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
