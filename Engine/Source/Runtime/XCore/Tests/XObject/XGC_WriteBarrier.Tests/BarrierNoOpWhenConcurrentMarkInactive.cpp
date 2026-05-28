// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGC_WriteBarrier.Tests/BarrierNoOpWhenConcurrentMarkInactive.cpp --
// SATB push is gated on the concurrent-mark flag (XCoreXObject Rev 4
// §4.3 + §5.2).
// =====================================================================
//
// Per spec §4.3: "In Shipping (no marking active), the barrier collapses
// to: FXGC_CardTable::MarkCardDirty(slot); *slot = newValue;"
//
// Per spec §4.3: "Constant-folding eliminates the IsMarking() branch
// when marking is provably inactive at the call site". At runtime
// when the flag is FALSE, the SATB push does NOT happen.
//
// Verifies:
//   * g_XGCIsConcurrentMarkActive == false.
//   * Barrier emits dirty card mark (post-condition observable via the
//     card table).
//   * Per-thread SATB queue is NOT modified.
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
    using ::XCore::FXObjectSatbQueue;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::g_XGCIsConcurrentMarkActive;
    using ::XCore::GetThreadSatbQueue;
    using ::XCore::XGCWriteBarrierImpl;
    using ::XCore::XObject;

    ::XCore::HAL::FMemory::__Init();

    // Pre-conditions:
    //   * Drain-accept on (so any incidental drains don't deadlock).
    //   * Concurrent mark INACTIVE.
    //   * Card table initialised over our test heap.
    g_XGCAcceptDrains.store(true, ::std::memory_order_release);
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    FXObjectGCCardTable& CardTable = FXObjectGCCardTable::Get();
    CardTable.__ResetForTests();

    // Allocate a 4 KB pseudo-heap; the "slot" lives inside it.
    constexpr ::std::size_t kHeapBytes = 4096;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    CardTable.Initialize(Heap.data(), kHeapBytes);

    // The "slot" is an XObject** stored inside the heap at offset
    // 1024 (lands in card 2). The slot's current value is an
    // arbitrary non-null sentinel (the barrier captures OLD value but
    // doesn't deref it).
    XObject Sentinel;
    XObject NewValue;
    XObject** Slot = reinterpret_cast<XObject**>(Heap.data() + 1024);
    *Slot = &Sentinel;

    // Pre-barrier state.
    const ::std::size_t QSizeBefore = GetThreadSatbQueue().Size();
    const ::std::size_t CardCountBefore = CardTable.GetDirtyCardCount();

    // Invoke the barrier directly (NOT via XPACT_GC_STORE so the test
    // is purely observing the barrier's side effects; the slot write
    // is the macro's responsibility).
    XGCWriteBarrierImpl(Slot, &NewValue);

    // -----------------------------------------------------------------
    // Post-condition 1: SATB queue NOT modified.
    // -----------------------------------------------------------------
    Check(GetThreadSatbQueue().Size() == QSizeBefore,
          "SATB queue Size changed despite g_XGCIsConcurrentMarkActive == false");

    // -----------------------------------------------------------------
    // Post-condition 2: Card table dirty count bumped by 1.
    // -----------------------------------------------------------------
    Check(CardTable.GetDirtyCardCount() == CardCountBefore + 1,
          "Card table dirty count did NOT bump when barrier fired "
          "(card should be dirtied regardless of mark active)");

    // Cleanup.
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    CardTable.__ResetForTests();
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    if (g_FailureCount == 0)
    {
        std::cout << "XGC_WriteBarrier.BarrierNoOpWhenConcurrentMarkInactive: PASS\n";
        return 0;
    }
    std::cerr << "XGC_WriteBarrier.BarrierNoOpWhenConcurrentMarkInactive: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
