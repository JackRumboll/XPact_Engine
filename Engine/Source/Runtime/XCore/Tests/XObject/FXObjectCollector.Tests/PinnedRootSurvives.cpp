// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/PinnedRootSurvives.cpp -- pinned XObject is
// marked + not enqueued for sweep (Phase 5.g end-to-end).
// =====================================================================
//
// Verifies:
//   * An XGCRoot::AddRoot-pinned XObject is marked during root
//     enumeration.
//   * Post-cycle: the pinned object's ReachabilityFlag has the
//     current-cycle bit set.
//   * The pinned object is NOT enqueued onto the sweep-candidate
//     queue (the rotating-flag check at end-of-mark sees it as
//     reachable).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/EObjectFlags.h"
#include "XObject/FXDeferredDestructionQueue.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXSweepCandidateQueue.h"
#include "XObject/XGCRoot.h"
#include "XObject/XObject.h"

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
    using ::XCore::EObjectFlags;
    using ::XCore::FXDeferredDestructionQueue;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXSweepCandidateQueue;
    using ::XCore::XGCRoot;
    using ::XCore::XObject;
    using ::XCore::EXGCOptions;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    FXDeferredDestructionQueue::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();

    constexpr ::std::size_t kHeapBytes = 4 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(Heap.data(), kHeapBytes);

    FXObjectArray& Array = FXObjectArray::Get();
    FXObjectCollector& Coll = FXObjectCollector::Get();

    // -----------------------------------------------------------------
    // Allocate two stack XObjects + register them with the array.
    //
    // Obj A: pinned via XGCRoot::AddRoot. Expected to be marked.
    // Obj B: NOT pinned. Expected to land on the sweep candidate
    //        queue (the rotating-flag rotation makes its bit clear at
    //        end-of-mark).
    // -----------------------------------------------------------------
    XObject ObjA;
    XObject ObjB;
    {
        ::uint32 SerialA = 0;
        const ::int32 IdxA = Array.ReserveSlot(&SerialA);
        ObjA.InternalIndex = IdxA;
        ObjA.SerialNumber  = SerialA;
        Array.BindObject(IdxA, &ObjA);

        ::uint32 SerialB = 0;
        const ::int32 IdxB = Array.ReserveSlot(&SerialB);
        ObjB.InternalIndex = IdxB;
        ObjB.SerialNumber  = SerialB;
        Array.BindObject(IdxB, &ObjB);

        Check(XGCRoot::AddRoot(&ObjA),
              "AddRoot(ObjA) returned false (expected first-pin transition)");
        Check(!XGCRoot::AddRoot(&ObjA),
              "AddRoot(ObjA) idempotency check failed");
    }

    // -----------------------------------------------------------------
    // Run one synchronous cycle.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);

    // -----------------------------------------------------------------
    // Post-cycle: ObjA is marked (the cycle counter is now 1, so the
    // current cycle's mask is bit 1).
    //
    // The collector advanced reachability index from 0 to 1 at the
    // END of the cycle, so the bit ObjA was MARKED with during the
    // cycle was bit 0 (the cycle's reachability mask at the time of
    // mark). ObjA's ReachabilityFlag now has bit 0 set; the NEXT
    // cycle's mask will be bit 1, and IsMarked(ObjA) for the new
    // cycle would return false until the next mark.
    // -----------------------------------------------------------------
    {
        const ::std::uint32_t Reach =
            ObjA.ReachabilityFlag.load(::std::memory_order_acquire);
        Check((Reach & (1u << 0)) != 0u,
              "Post-cycle: ObjA's bit 0 (cycle 0 mark) is not set");
        // ObjB was unreachable; its bit 0 should be clear.
        const ::std::uint32_t ReachB =
            ObjB.ReachabilityFlag.load(::std::memory_order_acquire);
        Check((ReachB & (1u << 0)) == 0u,
              "Post-cycle: ObjB's bit 0 was set (it was unreachable)");
    }

    // -----------------------------------------------------------------
    // Sweep behaviour: ObjB should have been processed by the Phase
    // 5.h sweep (BeginDestroy set + enqueued on deferred queue);
    // ObjA should NOT (pinned + reachable; never reaches the sweep).
    //
    // Phase 5.g shipped this test asserting the FXSweepCandidateQueue
    // post-mark contents. Phase 5.h drains that queue inside the
    // cycle so its post-cycle state is empty; we now observe the
    // EQUIVALENT post-sweep state via:
    //   * EObjectFlags::BeginDestroyed on ObjB (set by sweep).
    //   * FXObjectArray's kPendingDestroyBit mirror on ObjB.
    //   * FXDeferredDestructionQueue's pending count (one entry).
    //   * ObjA's flags + state-bit are CLEAN.
    // -----------------------------------------------------------------
    {
        Check(!ObjA.HasAnyFlags(EObjectFlags::BeginDestroyed),
              "Pinned ObjA was BeginDestroy'd (BUG: pinned object swept)");
        Check(ObjB.HasAnyFlags(EObjectFlags::BeginDestroyed),
              "Unreachable ObjB was NOT BeginDestroy'd (BUG: missed candidate)");

        Check(!Array.IsPendingDestroyUnchecked(ObjA.InternalIndex),
              "Pinned ObjA has kPendingDestroyBit set");
        Check(Array.IsPendingDestroyUnchecked(ObjB.InternalIndex),
              "Unreachable ObjB does NOT have kPendingDestroyBit set");

        Check(FXDeferredDestructionQueue::Get().Size() == 1,
              "FXDeferredDestructionQueue Size != 1 post-sweep");

        // The candidate queue should be drained.
        Check(FXSweepCandidateQueue::Get().IsEmpty(),
              "FXSweepCandidateQueue is not empty post-sweep (Phase 5.h "
              "should consume the queue as part of EnterSweep)");
    }

    // Cleanup.
    Check(XGCRoot::RemoveRoot(&ObjA),
          "RemoveRoot(ObjA) returned false");
    // ObjB was sweep-processed but not deferred-drained (we want to
    // skip the FXObjectAllocator::Deallocate path because ObjB is a
    // stack XObject). Reset the deferred queue without dispatching its
    // contents.
    FXDeferredDestructionQueue::Get().__ResetForTests();
    Array.FreeEntry(ObjA.InternalIndex);
    Array.FreeEntry(ObjB.InternalIndex);
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    Coll.__ResetForTests();
    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.PinnedRootSurvives: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.PinnedRootSurvives: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
