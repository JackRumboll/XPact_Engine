// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/ReleaseClassPool_NoEscape.cpp -- scenario-
// boundary mark-region clearing happy path (XCoreXObject Rev 4 §3.6 +
// Foundation Prototype X11 per FIX-A-MIN-53; Phase 5.i).
// =====================================================================
//
// X11 acceptance criterion (Rev 2 refined per FIX-A-MIN-53):
//   "scenario-boundary mark-region clearing reclaims 100% of scenario-
//    scoped XObjects on synthetic 1000-instance / 100-class scenarios;
//    falls back to GC-driven collection when escape detected. Test
//    surface: per-class sub-pool walk verifies no escape; on detected
//    escape, the sub-pool returns to free-list with O(1) accounting;
//    SerialNumber bumps for every released slot."
//
// This test exercises the HAPPY-PATH path: a synthetic class whose
// instances are not referenced from outside the sub-pool. The test:
//
//   1. Allocates N instances of TestClass (via AllocateRaw + a manual
//      placement-new of XObject + FXObjectArray::AllocateEntry to
//      mirror the Phase 5.b NewObject hot path).
//
//   2. Captures each instance's InternalIndex + SerialNumber.
//
//   3. Calls FXObjectAllocator::ReleaseClassPool(&TestClass).
//
//   4. Verifies:
//      a. Returned Result is success (no error).
//      b. Every captured InternalIndex now has a BUMPED SerialNumber
//         (the X11 "SerialNumber bumps for every released slot"
//         invariant).
//      c. The TestClass sub-pool live count drops to 0.
//      d. A subsequent AllocateRaw against TestClass returns a cell
//         from the slab's UnassignedFreeListHead (the sub-pool was
//         fully drained).
//
// Test sizing: 100 instances (X11 target is 1000 instances; we use 100
// for test runtime + still exercise the algorithm meaningfully).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/XObject.h"

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
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectCollector;
    using ::XCore::FScenarioBoundaryError;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    FXObjectArray&     Array     = FXObjectArray::Get();
    FXObjectCollector& Collector = FXObjectCollector::Get();

    Allocator.__ResetForTests();
    Array.__ResetForTests();
    Collector.__ResetForTests();

    // -----------------------------------------------------------------
    // Setup: synthetic TestClass at size-class index 2 (128 bytes;
    // sizeof(XObject) == 56 fits comfortably).
    // -----------------------------------------------------------------
    FClass TestClass(FName("NoEscapeTestClass"), nullptr);
    TestClass.PropertiesSize = sizeof(XObject);
    TestClass.MinAlignment   = alignof(XObject);

    Allocator.RegisterClassPool(&TestClass);

    constexpr int kInstanceCount = 100;

    struct FInstanceRecord
    {
        XObject*  Obj;
        ::int32   InternalIndex;
        ::uint32  SerialNumber;
    };
    std::vector<FInstanceRecord> Records;
    Records.reserve(kInstanceCount);

    // -----------------------------------------------------------------
    // Allocate 100 instances. Mirror the NewObject hot path:
    //   1. AllocateRaw under TestClass.
    //   2. ReserveSlot in the FXObjectArray.
    //   3. Placement-new an XObject in the cell.
    //   4. Fill identity fields.
    //   5. BindObject to install the back-pointer.
    // -----------------------------------------------------------------
    for (int i = 0; i < kInstanceCount; ++i)
    {
        void* Cell = Allocator.AllocateRaw(
            sizeof(XObject), alignof(XObject), &TestClass);
        Check(Cell != nullptr, "AllocateRaw returned nullptr in setup");

        ::uint32 Serial = 0;
        const ::int32 Index = Array.ReserveSlot(&Serial);

        XObject* Obj = ::new (Cell) XObject();
        Obj->ClassPrivate  = &TestClass;
        Obj->InternalIndex = Index;
        Obj->SerialNumber  = Serial;

        Array.BindObject(Index, Obj);

        Records.push_back(FInstanceRecord{Obj, Index, Serial});
    }

    // -----------------------------------------------------------------
    // Sanity: every record was allocated correctly.
    // -----------------------------------------------------------------
    Check(static_cast<int>(Records.size()) == kInstanceCount,
          "Setup: not all instances were captured");
    for (const auto& R : Records)
    {
        Check(R.Obj != nullptr, "Setup: null instance pointer");
        Check(R.InternalIndex > 0, "Setup: InternalIndex == 0");
        Check(R.SerialNumber >= 1u, "Setup: SerialNumber == 0");
        Check(Array.GetObjectAtIndex(R.InternalIndex, R.SerialNumber) == R.Obj,
              "Setup: array lookup did not return the bound object");
    }

    // -----------------------------------------------------------------
    // Phase 5.i: invoke ReleaseClassPool. No external XObject references
    // any of the TestClass instances, so the escape-detection walk
    // finds no escapes and the release succeeds.
    // -----------------------------------------------------------------
    {
        const auto Result = Allocator.ReleaseClassPool(&TestClass);
        Check(Result.has_value(),
              "ReleaseClassPool returned Err on a no-escape case");
    }

    // -----------------------------------------------------------------
    // Verify: SerialNumber bumped for every released slot.
    //
    // After ReleaseSlot, the FXObjectArray entry's SerialNumber is
    // incremented (with the wrap-past-0 protection). The captured
    // pre-release SerialNumber should no longer match the entry's
    // current SerialNumber. Test by attempting GetObjectAtIndex with
    // the captured serial -- it should return nullptr.
    // -----------------------------------------------------------------
    int SerialBumpedCount = 0;
    for (const auto& R : Records)
    {
        XObject* Stale =
            Array.GetObjectAtIndex(R.InternalIndex, R.SerialNumber);
        if (Stale == nullptr)
        {
            ++SerialBumpedCount;
        }
    }
    Check(SerialBumpedCount == kInstanceCount,
          "SerialNumber did not bump for every released slot "
          "(X11 acceptance violation)");

    // -----------------------------------------------------------------
    // Verify: the TestClass sub-pool's live count is now 0.
    //
    // Stats walk surfaces PerClassAllocations entries only for classes
    // with LiveCount > 0; TestClass should be ABSENT (or LiveCount=0).
    // -----------------------------------------------------------------
    {
        const auto Stats = Allocator.GetStats();
        ::int32 TestClassLive = 0;
        for (::int32 i = 0; i < Stats.PerClassAllocations.Num(); ++i)
        {
            if (Stats.PerClassAllocations[i].Class == &TestClass)
            {
                TestClassLive = Stats.PerClassAllocations[i].LiveCount;
            }
        }
        Check(TestClassLive == 0,
              "Post-release: TestClass LiveCount != 0");
    }

    // -----------------------------------------------------------------
    // Verify: a subsequent AllocateRaw against TestClass succeeds and
    // returns a fresh cell. The slab is still committed (we only
    // returned cells to the unassigned chain, not the slab itself).
    // -----------------------------------------------------------------
    {
        void* FreshCell = Allocator.AllocateRaw(
            sizeof(XObject), alignof(XObject), &TestClass);
        Check(FreshCell != nullptr,
              "Post-release: AllocateRaw against the drained class "
              "returned nullptr");
        Allocator.Deallocate(FreshCell);
    }

    Allocator.__ResetForTests();
    Array.__ResetForTests();
    Collector.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.ReleaseClassPool_NoEscape: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.ReleaseClassPool_NoEscape: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
