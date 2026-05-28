// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/BeginDestroyDispatch.cpp -- Phase 5.h sweep
// dispatches BeginDestroy via the lifecycle table.
// =====================================================================
//
// Verifies:
//
//   * An XObject whose FClass exposes a BeginDestroy slot in its
//     FXObjectLifecycleTable has the slot dispatched when the sweep
//     processes it as an unreachable candidate.
//   * The EObjectFlags::BeginDestroyed bit is set on the XObject by
//     the sweep prior to the dispatch.
//   * The kPendingDestroyBit on the array entry is set.
//   * The object lands on FXDeferredDestructionQueue.
//
// We use a stack-allocated XObject (the deferred destruction queue's
// FinishDestroy dispatch + Deallocate would normally require an
// allocator-allocated cell, but we do NOT drain the deferred queue in
// this test; we only verify the BEGIN DESTROY half of the cycle).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/EObjectFlags.h"
#include "XObject/FXDeferredDestructionQueue.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectLifecycleTable.h"
#include "XObject/FXSweepCandidateQueue.h"
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

    // -----------------------------------------------------------------
    // BeginDestroy callback. Increments g_BeginDestroyCallCount when
    // invoked.
    // -----------------------------------------------------------------
    int g_BeginDestroyCallCount = 0;

    void TestBeginDestroy(::XCore::XObject* /*Self*/) noexcept
    {
        ++g_BeginDestroyCallCount;
    }
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::EXGCOptions;
    using ::XCore::FXDeferredDestructionQueue;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXSweepCandidateQueue;
    using ::XCore::XObject;
    using ::XCore::Reflect::EXObjectLifecycleCapability;
    using ::XCore::Reflect::EXObjectLifecycleSlot;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FXObjectGenericFn;
    using ::XCore::Reflect::FXObjectLifecycleTable;
    using ::XCore::Reflect::ToUnderlying;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    FXDeferredDestructionQueue::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();
    g_BeginDestroyCallCount = 0;

    constexpr ::std::size_t kHeapBytes = 4 * 1024;
    std::vector<::std::uint8_t> Heap(kHeapBytes, 0);
    FXObjectGCCardTable::Get().Initialize(Heap.data(), kHeapBytes);

    FXObjectArray& Array = FXObjectArray::Get();
    FXObjectCollector& Coll = FXObjectCollector::Get();
    FXDeferredDestructionQueue& DeferredQ = FXDeferredDestructionQueue::Get();

    // -----------------------------------------------------------------
    // Build a lifecycle table with a single BeginDestroy slot.
    // -----------------------------------------------------------------
    static FXObjectLifecycleTable s_LifecycleTable =
    {
        /*Capabilities=*/ ToUnderlying(EXObjectLifecycleCapability::HasBeginDestroy),
        /*_padHeader=*/   0,
        /*Slots=*/ {
            /*[0] PostInitProperties*/      nullptr,
            /*[1] BeginDestroy*/            reinterpret_cast<FXObjectGenericFn>(&TestBeginDestroy),
            /*[2] IsReadyForFinishDestroy*/ nullptr,
            /*[3] FinishDestroy*/           nullptr,
            /*[4] AddReferencedObjects*/    nullptr,
            /*[5] Serialize*/               nullptr,
            /*[6] PostLoad*/                nullptr,
            /*[7] ConvertFromType*/         nullptr,
        },
    };

    // FClass wired to that table.
    FClass TestClass(FName("BeginDestroyTestClass"), nullptr);
    TestClass.PropertiesSize  = static_cast<::int32>(sizeof(XObject));
    TestClass.MinAlignment    = alignof(XObject);
    TestClass.LifecycleTable  = &s_LifecycleTable;

    // -----------------------------------------------------------------
    // Allocate a stack XObject + register with the array.
    //
    // Not root-pinned + not refcounted, so the sweep will enqueue it.
    // -----------------------------------------------------------------
    XObject Obj;
    Obj.ClassPrivate = &TestClass;

    ::uint32 Serial = 0;
    const ::int32 Idx = Array.ReserveSlot(&Serial);
    Obj.InternalIndex = Idx;
    Obj.SerialNumber  = Serial;
    Array.BindObject(Idx, &Obj);

    // -----------------------------------------------------------------
    // Pre-cycle sanity.
    // -----------------------------------------------------------------
    Check(g_BeginDestroyCallCount == 0,
          "g_BeginDestroyCallCount != 0 pre-cycle");
    Check(!Obj.HasAnyFlags(EObjectFlags::BeginDestroyed),
          "BeginDestroyed flag set pre-cycle");
    Check(!Array.IsPendingDestroyUnchecked(Idx),
          "kPendingDestroyBit set pre-cycle");

    // -----------------------------------------------------------------
    // Run a synchronous cycle. The sweep should:
    //   * Discover Obj as unreachable (not pinned).
    //   * Set EObjectFlags::BeginDestroyed.
    //   * Set kPendingDestroyBit on the array entry.
    //   * Dispatch BeginDestroy via the lifecycle table.
    //   * Enqueue on FXDeferredDestructionQueue.
    //
    // We do NOT drain the deferred queue here -- the stack XObject is
    // not allocator-owned; the FinishDestroy + Deallocate path would
    // assert in Deallocate (the pointer is not in any allocator slab).
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);

    Check(g_BeginDestroyCallCount == 1,
          "BeginDestroy slot was not dispatched exactly once");
    Check(Obj.HasAnyFlags(EObjectFlags::BeginDestroyed),
          "BeginDestroyed flag was not set on the swept object");
    Check(Array.IsPendingDestroyUnchecked(Idx),
          "kPendingDestroyBit was not set on the swept entry");
    Check(DeferredQ.Size() == 1,
          "Deferred queue size != 1 after sweep");

    // -----------------------------------------------------------------
    // Re-run another cycle: the BeginDestroy slot should NOT be
    // dispatched a second time. Two layers guard against this:
    //
    //   * EnumerateSweepCandidates SKIPS entries with the kPendingDestroy
    //     Bit already set (the entry is on the deferred queue from the
    //     previous cycle's sweep). This is the primary guard; the
    //     object never reaches the candidate queue.
    //
    //   * DrainSweepCandidates ALSO checks EObjectFlags::BeginDestroyed
    //     as defence-in-depth (the prompt's corner-case wording). This
    //     guard fires only if a candidate somehow re-enters the queue
    //     by another path (e.g., a future synchronous CollectGarbage
    //     that bypasses the candidate-queue filter).
    //
    // Either guard prevents double-dispatch; in this test the first
    // guard is the one that fires.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);

    Check(g_BeginDestroyCallCount == 1,
          "BeginDestroy dispatched twice (the PendingDestroy / "
          "BeginDestroyed bit guards failed to suppress the second "
          "dispatch)");

    // -----------------------------------------------------------------
    // Cleanup -- manually drain the deferred queue without calling
    // Deallocate / FinishDestroy. The stack XObject's slot can be
    // FreeEntry'd directly.
    // -----------------------------------------------------------------
    DeferredQ.__ResetForTests();
    Array.FreeEntry(Idx);
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    Coll.__ResetForTests();
    Array.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.BeginDestroyDispatch: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.BeginDestroyDispatch: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
