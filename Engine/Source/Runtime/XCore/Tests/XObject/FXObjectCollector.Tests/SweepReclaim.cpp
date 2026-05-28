// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/SweepReclaim.cpp -- Phase 5.h end-to-end
// sweep reclaim path.
// =====================================================================
//
// Verifies the full Phase 5.h round trip:
//
//   1. Allocate an XObject via FXObjectAllocator.
//   2. Register it with FXObjectArray.
//   3. Do NOT pin it as a root (so the sweep will discover it as
//      unreachable).
//   4. Run a synchronous CollectGarbage cycle: the sweep should
//      dispatch BeginDestroy + enqueue on FXDeferredDestructionQueue +
//      bump m_lastReclaimedCount.
//   5. Drain the deferred destruction queue.
//   6. The slot's SerialNumber is bumped + the slot is freed +
//      previously-captured XWeakPtrs to the object deref to nullptr.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXDeferredDestructionQueue.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXSweepCandidateQueue.h"
#include "XObject/XObject.h"

#include <cstdint>
#include <iostream>
#include <new>
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
    using ::XCore::EXGCOptions;
    using ::XCore::FXDeferredDestructionQueue;
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXSweepCandidateQueue;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    FXDeferredDestructionQueue::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();

    constexpr ::std::size_t kHeapBytes = 4 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(Heap.data(), kHeapBytes);

    FXObjectArray&     Array     = FXObjectArray::Get();
    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    FXObjectCollector& Coll      = FXObjectCollector::Get();
    FXDeferredDestructionQueue& DeferredQ =
        FXDeferredDestructionQueue::Get();

    // -----------------------------------------------------------------
    // Allocate an XObject via the allocator (so the deferred-destruction
    // queue's Deallocate call will be honoured).
    //
    // We use a TestClass with no LifecycleTable; the sweep's BeginDestroy
    // dispatch will short-circuit on the null table, and the deferred
    // queue's FinishDestroy / IsReadyForFinishDestroy will likewise
    // default-true / no-op (per spec §2.8 null-slot-as-true).
    // -----------------------------------------------------------------
    FClass TestClass(FName("SweepReclaimTestClass"), nullptr);
    TestClass.PropertiesSize = static_cast<::int32>(sizeof(XObject));
    TestClass.MinAlignment   = alignof(XObject);
    Allocator.RegisterClassPool(&TestClass);

    void* const Cell = Allocator.AllocateRaw(
        sizeof(XObject), alignof(XObject), &TestClass);
    Check(Cell != nullptr, "AllocateRaw returned nullptr");

    // Placement-new the XObject header.
    XObject* const Obj = new (Cell) XObject();

    // Register with the FXObjectArray.
    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj->InternalIndex = Idx;
    Obj->SerialNumber  = Serial;
    Array.BindObject(Idx, Obj);

    // Sanity: the entry is now live + unbound from any root.
    Check(Array.NumLive() == 1, "NumLive != 1 after BindObject");

    // -----------------------------------------------------------------
    // Run a synchronous cycle. The object is NOT root-pinned and NOT
    // refcounted, so the sweep should enqueue it on the deferred queue.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);

    // The sweep should have processed exactly 1 candidate
    // (BeginDestroy dispatched + enqueued on the deferred queue).
    Check(Coll.GetLastReclaimedCount() == 1,
          "Post-cycle GetLastReclaimedCount != 1");

    // The deferred destruction queue should have exactly one pending
    // entry.
    Check(DeferredQ.Size() == 1,
          "Deferred queue Size != 1 post-sweep");

    // The slot's kPendingDestroyBit mirror should be set; the
    // EObjectFlags::BeginDestroyed bit should be set on the XObject.
    Check(Array.IsPendingDestroyUnchecked(Idx),
          "kPendingDestroyBit was not set on the swept entry");
    Check(Obj->HasAnyFlags(::XCore::EObjectFlags::BeginDestroyed),
          "EObjectFlags::BeginDestroyed was not set on the swept object");

    // The slot is STILL bound to the object (Deallocate has not run
    // yet; that happens on the deferred-queue drain pass).
    Check(Array.GetObjectAtIndexUnchecked(Idx) == Obj,
          "Slot was released before deferred drain");

    // -----------------------------------------------------------------
    // Drain the deferred destruction queue. The slot should be released;
    // the FXObjectAllocator cell should be returned to the free list.
    // -----------------------------------------------------------------
    const ::std::size_t Finalised = DeferredQ.DrainOnePassWithBudget(1'000);
    Check(Finalised == 1,
          "DrainOnePassWithBudget did not finalise the swept object");
    Check(DeferredQ.IsEmpty(), "Deferred queue not empty after drain");

    // Slot was released (Object pointer is now nullptr).
    Check(Array.GetObjectAtIndexUnchecked(Idx) == nullptr,
          "Slot Object pointer not nullptr after deferred drain");

    // NumLive dropped back to 0.
    Check(Array.NumLive() == 0, "NumLive != 0 after slot release");

    // -----------------------------------------------------------------
    // SerialNumber bump verification: GetObjectAtIndex with the OLD
    // serial should return nullptr (the bump invalidates the captured
    // serial). A fresh AllocateEntry should yield a NEW serial.
    // -----------------------------------------------------------------
    XObject* const StaleDeref = Array.GetObjectAtIndex(Idx, Serial);
    Check(StaleDeref == nullptr,
          "GetObjectAtIndex with stale serial did not return nullptr");

    // Cleanup.
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    DeferredQ.__ResetForTests();
    Coll.__ResetForTests();
    Array.__ResetForTests();
    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.SweepReclaim: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.SweepReclaim: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
