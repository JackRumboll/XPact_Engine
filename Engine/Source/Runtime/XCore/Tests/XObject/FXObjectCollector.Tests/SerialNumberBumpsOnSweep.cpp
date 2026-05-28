// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectCollector.Tests/SerialNumberBumpsOnSweep.cpp -- Phase 5.h
// SerialNumber-bump invariant for XWeakPtr-held references.
// =====================================================================
//
// Verifies:
//
//   * An XWeakPtr captured against an XObject that is then reclaimed
//     by the full sweep + deferred-destruction cycle returns nullptr
//     on subsequent Get() / IsValid() calls.
//
//   * The SerialNumber on the freshly-released slot has bumped past
//     the captured value (the load-bearing weak-handle invalidation
//     event per XCoreXObject Rev 4 §3.3 + §6.2 + spec §11.5 Phase 5.h
//     "Tests confirm reclaim correctness, SerialNumber bumping, slot
//     reuse").
//
// This is the canonical Phase 5.h validation that the
// FXDeferredDestructionQueue's drain path correctly invokes
// FXObjectArray::ReleaseSlot's SerialNumber bump.
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
#include "XObject/XWeakPtr.h"

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
    using ::XCore::XWeakPtr;
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
    FXDeferredDestructionQueue& DeferredQ = FXDeferredDestructionQueue::Get();

    // -----------------------------------------------------------------
    // Build an FClass (no LifecycleTable; null-slot dispatch is the
    // default-true / no-op posture per spec §2.8).
    // -----------------------------------------------------------------
    FClass TestClass(FName("SerialBumpTestClass"), nullptr);
    TestClass.PropertiesSize = static_cast<::int32>(sizeof(XObject));
    TestClass.MinAlignment   = alignof(XObject);
    Allocator.RegisterClassPool(&TestClass);

    // -----------------------------------------------------------------
    // Allocate + bind an XObject. Capture an XWeakPtr.
    // -----------------------------------------------------------------
    void* const Cell = Allocator.AllocateRaw(
        sizeof(XObject), alignof(XObject), &TestClass);
    Check(Cell != nullptr, "AllocateRaw returned nullptr");
    XObject* const Obj = new (Cell) XObject();

    ::uint32 OriginalSerial = 0;
    const ::int32 Idx = Array.ReserveSlot(&OriginalSerial);
    Obj->InternalIndex = Idx;
    Obj->SerialNumber  = OriginalSerial;
    Array.BindObject(Idx, Obj);

    // Capture an XWeakPtr. The XWeakPtr stores {Idx, OriginalSerial}.
    XWeakPtr<XObject> Weak(Obj);

    // Pre-cycle: the weak ptr resolves to the bound object.
    Check(Weak.Get() == Obj,
          "Pre-cycle: XWeakPtr::Get() did not resolve to the bound object");
    Check(Weak.IsValid(),
          "Pre-cycle: XWeakPtr::IsValid() returned false");

    // Remember the original serial for the explicit comparison below.
    const ::uint32 CapturedSerial = Weak.SerialNumber;
    const ::int32  CapturedIndex  = Weak.InternalIndex;
    Check(CapturedSerial == OriginalSerial,
          "Captured XWeakPtr serial != ReserveSlot's returned serial");

    // -----------------------------------------------------------------
    // Run the full sweep + deferred drain.
    // -----------------------------------------------------------------
    Coll.CollectGarbage(EXGCOptions::kNone);
    Check(DeferredQ.Size() == 1,
          "Deferred queue size != 1 post-sweep");

    const ::std::size_t Finalised = DeferredQ.DrainOnePassWithBudget(1'000);
    Check(Finalised == 1,
          "DrainOnePassWithBudget did not finalise the swept object");

    // -----------------------------------------------------------------
    // Post-cycle: the XWeakPtr's Get() returns nullptr.
    // -----------------------------------------------------------------
    Check(Weak.Get() == nullptr,
          "Post-sweep: XWeakPtr::Get() did not return nullptr");
    Check(!Weak.IsValid(),
          "Post-sweep: XWeakPtr::IsValid() returned true (stale serial honoured?)");

    // Also: direct probe of the array using the captured serial
    // returns nullptr.
    Check(Array.GetObjectAtIndex(CapturedIndex, CapturedSerial) == nullptr,
          "Post-sweep: GetObjectAtIndex with stale serial did not return nullptr");

    // -----------------------------------------------------------------
    // Slot reuse: AllocateEntry should yield the SAME index (LIFO free
    // list) but with a NEW serial. The XWeakPtr captured against the
    // ORIGINAL serial still derefs to nullptr (the slot now binds a
    // different object).
    // -----------------------------------------------------------------
    {
        ::uint32 NewSerial = 0;
        const ::int32 NewIdx = Array.ReserveSlot(&NewSerial);
        Check(NewIdx == CapturedIndex,
              "Slot reuse did not return the same InternalIndex (LIFO free-list violation)");
        Check(NewSerial != CapturedSerial,
              "Slot reuse did not bump the SerialNumber");

        // The XWeakPtr still derefs to nullptr because the captured
        // serial does not match the new one.
        Check(Weak.Get() == nullptr,
              "XWeakPtr::Get() did NOT return nullptr after slot reuse");

        // Release the freshly-reserved slot for cleanup.
        Array.FreeEntry(NewIdx);
    }

    // -----------------------------------------------------------------
    // Cleanup.
    // -----------------------------------------------------------------
    FXObjectGCCardTable::Get().__ResetForTests();
    FXSweepCandidateQueue::Get().__ResetForTests();
    DeferredQ.__ResetForTests();
    Coll.__ResetForTests();
    Array.__ResetForTests();
    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectCollector.SerialNumberBumpsOnSweep: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectCollector.SerialNumberBumpsOnSweep: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
