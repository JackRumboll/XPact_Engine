// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XGC_WriteBarrier.Tests/BarrierPushesSatbWhenActive.cpp -- SATB push
// fires when mark is active (XCoreXObject Rev 4 §4.3 + §5.2).
// =====================================================================
//
// Verifies:
//   * g_XGCIsConcurrentMarkActive == true.
//   * Slot's OLD value is non-null.
//   * Barrier emits dirty card mark AND pushes OLD value to the
//     per-thread SATB queue.
//   * If OLD value is null, no SATB push happens (per spec §4.3
//     barrier code).
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

    // Pre-conditions.
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
    XObject** Slot = reinterpret_cast<XObject**>(Heap.data() + 1024);
    *Slot = &OldRef;

    // -----------------------------------------------------------------
    // Test 1: With concurrent-mark active + OLD value non-null, the
    // SATB push fires.
    // -----------------------------------------------------------------
    {
        g_XGCIsConcurrentMarkActive.store(true, ::std::memory_order_release);

        const ::std::size_t QSizeBefore = GetThreadSatbQueue().Size();

        XGCWriteBarrierImpl(Slot, &NewRef);

        const ::std::size_t QSizeAfter = GetThreadSatbQueue().Size();
        Check(QSizeAfter == QSizeBefore + 1,
              "Barrier with concurrent-mark active did NOT push to SATB queue");

        // Drain the queue and verify the OLD value was captured.
        XObject* CapturedOld = nullptr;
        GetThreadSatbQueue().DrainTo([&CapturedOld](XObject* OldValue) {
            CapturedOld = OldValue;
        });
        Check(CapturedOld == &OldRef,
              "SATB-captured OLD value != actual OLD slot value");
    }

    // -----------------------------------------------------------------
    // Test 2: Card table is ALSO dirtied (not exclusive of SATB push).
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        *Slot = &OldRef;
        g_XGCIsConcurrentMarkActive.store(true, ::std::memory_order_release);

        const ::std::size_t CardCountBefore = CardTable.GetDirtyCardCount();
        XGCWriteBarrierImpl(Slot, &NewRef);
        const ::std::size_t CardCountAfter = CardTable.GetDirtyCardCount();

        Check(CardCountAfter == CardCountBefore + 1,
              "Card table NOT dirtied when concurrent mark active");

        GetThreadSatbQueue().DrainTo([](XObject*) {});
    }

    // -----------------------------------------------------------------
    // Test 3: With concurrent-mark active but OLD value IS null, no
    // SATB push (per spec §4.3 barrier code: "if (oldValue != nullptr)").
    // -----------------------------------------------------------------
    {
        CardTable.__ResetForTests();
        CardTable.Initialize(Heap.data(), kHeapBytes);

        *Slot = nullptr;
        g_XGCIsConcurrentMarkActive.store(true, ::std::memory_order_release);

        const ::std::size_t QSizeBefore = GetThreadSatbQueue().Size();
        XGCWriteBarrierImpl(Slot, &NewRef);
        const ::std::size_t QSizeAfter = GetThreadSatbQueue().Size();

        Check(QSizeAfter == QSizeBefore,
              "Null OLD value triggered an unnecessary SATB push");
    }

    // Cleanup.
    g_XGCIsConcurrentMarkActive.store(false, ::std::memory_order_release);
    CardTable.__ResetForTests();
    FXObjectGlobalSatbLog::Get().__ResetForTests();
    GetThreadSatbQueue().DrainTo([](XObject*) {});

    if (g_FailureCount == 0)
    {
        std::cout << "XGC_WriteBarrier.BarrierPushesSatbWhenActive: PASS\n";
        return 0;
    }
    std::cerr << "XGC_WriteBarrier.BarrierPushesSatbWhenActive: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
