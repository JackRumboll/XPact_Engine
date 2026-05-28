// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGC_WriteBarrier.Tests/MacroExpansionEquivalent.cpp -- XPACT_GC_STORE
// expands to barrier + store (XCoreXObject Rev 4 §10.7).
// =====================================================================
//
// Per spec §10.7:
//   "XPACT_GC_STORE(parent, slot, newValue);" expands to:
//     XGC_WriteBarrierImpl(&slot, newValue);
//     slot = newValue;
//
// Verifies:
//   * After XPACT_GC_STORE: the slot DOES hold NEW value (store
//     occurred).
//   * Same side effects as calling XGCWriteBarrierImpl + slot
//     assignment manually:
//     * Card table dirtied.
//     * SATB queue gets OLD value (when concurrent-mark active).
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
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();
    CardTable.__ResetForTests();

    constexpr ::std::size_t kHeapBytes = 4096;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    CardTable.Initialize(Heap.data(), kHeapBytes);

    XObject OldRef;
    XObject NewRef;

    // The "slot" is an XObject* embedded in the heap. We test via a
    // local variable that aliases inside the heap range so the
    // card-table mark sees an in-range address.
    XObject** SlotAddr = reinterpret_cast<XObject**>(Heap.data() + 1024);
    *SlotAddr = &OldRef;

    // -----------------------------------------------------------------
    // Test 1: XPACT_GC_STORE performs barrier + slot write.
    // -----------------------------------------------------------------
    {
        const ::std::size_t CardCountBefore = CardTable.GetDirtyCardCount();

        // The macro takes (SLOT, NEW_VALUE) where SLOT is the lvalue.
        // We use *SlotAddr to name the lvalue at the heap-resident
        // slot. The macro takes &(SLOT) under the hood; for an
        // lvalue-dereference like *SlotAddr, &(*SlotAddr) == SlotAddr,
        // and the macro's reinterpret_cast<XObject**>(&SLOT) yields
        // SlotAddr itself. The bind is correct.
        XPACT_GC_STORE(*SlotAddr, &NewRef);

        // Post-condition: slot now holds NEW value.
        Check(*SlotAddr == &NewRef,
              "Macro did NOT perform the slot store");

        // Post-condition: card was dirtied.
        Check(CardTable.GetDirtyCardCount() == CardCountBefore + 1,
              "Macro did NOT dirty the slot's containing card");
    }

    // -----------------------------------------------------------------
    // Test 2: XPACT_GC_STORE with concurrent-mark active captures
    // OLD value to SATB queue.
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);
        GetThreadSatbQueue().DrainTo([](XObject*) {});

        *SlotAddr = &OldRef;
        g_XGCIsConcurrentMarkActive.store(true, ::std::memory_order_release);

        XPACT_GC_STORE(*SlotAddr, &NewRef);

        // Slot updated.
        Check(*SlotAddr == &NewRef,
              "Macro under concurrent-mark did NOT store new value");

        // SATB captured OLD value.
        XObject* CapturedOld = nullptr;
        GetThreadSatbQueue().DrainTo([&CapturedOld](XObject* OldValue) {
            CapturedOld = OldValue;
        });
        Check(CapturedOld == &OldRef,
              "Macro under concurrent-mark did NOT capture OLD value");
    }

    // Cleanup.
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    CardTable.__ResetForTests();
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    if (g_FailureCount == 0)
    {
        std::cout << "XGC_WriteBarrier.MacroExpansionEquivalent: PASS\n";
        return 0;
    }
    std::cerr << "XGC_WriteBarrier.MacroExpansionEquivalent: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
