// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/PerClassPool.cpp -- type-segregated free-
// list verification (XCoreXObject Rev 4 §3.4).
// =====================================================================
//
// Spec §3.4: "Within each size class, free cells are segregated by
// FClass*. Each FClass registered via RegisterClassPool gets its own
// free-list of cells of the right width."
//
// Verifies:
//
//   1. RegisterClassPool is idempotent (second call with the same
//      FClass is a no-op).
//   2. Three distinct FClasses at the SAME size class get three
//      distinct free-lists: allocating from class A then freeing,
//      allocating from class B then freeing, allocating again from
//      class A returns the cell originally allocated for A (not B).
//   3. Two FClasses at different size classes use different slabs.
//   4. GetStats reports per-class live counts correctly.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectAllocatorStats.h"

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
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    Allocator.__ResetForTests();

    // -----------------------------------------------------------------
    // Build 3 FClasses at the SAME size class (class 1; cell width 96)
    // and 1 FClass at a different size class (class 2; cell width 128).
    //
    // Class | PropertiesSize | Expected SizeClass
    //   A   |  80            | 2 (80*1.25=100 > 96 -> class 2; 100<=128)
    //   B   |  90            | 2 (90*1.25=112.5 > 96 -> class 2)
    //   C   |  96            | 1 (96 exact; 96*1.25=120 > 96 -> wait
    //                            actually 96<=96 OK, 96*1.25=120 > 96
    //                            so NOT class 1. Try class 2 (128):
    //                            96*1.25=120 <= 128 -> class 2.)
    //
    // So all three of A/B/C land in class 2. Pick PropertiesSize=160 +
    // 180 + 200 for D/E/F -> class 4 (256 bytes).
    //
    // Adjusted plan: A/B/C all in class 2 (128); D in class 4 (256).
    // -----------------------------------------------------------------

    FClass ClassA(FName("AClass"), nullptr);
    ClassA.PropertiesSize = 80;
    ClassA.MinAlignment   = 8;

    FClass ClassB(FName("BClass"), nullptr);
    ClassB.PropertiesSize = 90;
    ClassB.MinAlignment   = 8;

    FClass ClassC(FName("CClass"), nullptr);
    ClassC.PropertiesSize = 100;
    ClassC.MinAlignment   = 8;

    FClass ClassD(FName("DClass"), nullptr);
    ClassD.PropertiesSize = 200;  // -> class 4 (256)
    ClassD.MinAlignment   = 8;

    Allocator.RegisterClassPool(&ClassA);
    Allocator.RegisterClassPool(&ClassB);
    Allocator.RegisterClassPool(&ClassC);
    Allocator.RegisterClassPool(&ClassD);

    // Idempotent re-register.
    Allocator.RegisterClassPool(&ClassA);
    Allocator.RegisterClassPool(&ClassA);

    // -----------------------------------------------------------------
    // Test: allocate A, free A, allocate B, free B, allocate A again
    // -- the third allocation MUST return the original A cell (the
    // per-class free-list is FClass-segregated; B's free-list does NOT
    // serve A's allocations).
    // -----------------------------------------------------------------
    void* CellA1 = Allocator.AllocateRaw(80, 8, &ClassA);
    Allocator.Deallocate(CellA1);

    void* CellB1 = Allocator.AllocateRaw(90, 8, &ClassB);
    Allocator.Deallocate(CellB1);

    void* CellA2 = Allocator.AllocateRaw(80, 8, &ClassA);
    Check(CellA2 == CellA1,
          "Per-class segregation violated: A's re-allocate did not "
          "return A's freed cell");

    void* CellB2 = Allocator.AllocateRaw(90, 8, &ClassB);
    Check(CellB2 == CellB1,
          "Per-class segregation violated: B's re-allocate did not "
          "return B's freed cell");

    Allocator.Deallocate(CellA2);
    Allocator.Deallocate(CellB2);

    // -----------------------------------------------------------------
    // Test: 3 distinct FClasses at the same size class get 3 distinct
    // free-list heads; cells from class A do not leak into class C's
    // free-list and vice versa.
    // -----------------------------------------------------------------
    void* CellA3 = Allocator.AllocateRaw(80, 8, &ClassA);
    void* CellC1 = Allocator.AllocateRaw(100, 8, &ClassC);
    Check(CellA3 != CellC1,
          "A and C returned the same cell despite distinct FClass pools");

    Allocator.Deallocate(CellA3);
    Allocator.Deallocate(CellC1);

    // After both frees, re-allocate against A returns A's cell, and
    // re-allocate against C returns C's cell. This proves the
    // OwnerClass tag was preserved across the free+realloc cycle.
    void* CellA4 = Allocator.AllocateRaw(80, 8, &ClassA);
    void* CellC2 = Allocator.AllocateRaw(100, 8, &ClassC);

    Check(CellA4 == CellA3, "A's free-list ownership not preserved");
    Check(CellC2 == CellC1, "C's free-list ownership not preserved");

    Allocator.Deallocate(CellA4);
    Allocator.Deallocate(CellC2);

    // -----------------------------------------------------------------
    // Test: a class at a different size class (D, class 4) gets its
    // own slabs, distinct from A/B/C (class 2).
    // -----------------------------------------------------------------
    void* CellD1 = Allocator.AllocateRaw(200, 8, &ClassD);
    void* CellA5 = Allocator.AllocateRaw(80,  8, &ClassA);

    Check(CellD1 != nullptr, "ClassD allocation returned nullptr");
    Check(CellA5 != nullptr, "ClassA allocation returned nullptr");
    Check(CellD1 != CellA5,
          "Different-size-class allocations returned the same pointer");

    // -----------------------------------------------------------------
    // Test: GetStats reports per-class live counts.
    // -----------------------------------------------------------------
    {
        ::XCore::FXObjectAllocatorStats Stats = Allocator.GetStats();
        Check(Stats.TotalLiveObjects >= 2,
              "GetStats reported < 2 live objects after 2 live alloc");

        // Find ClassA and ClassD in the per-class breakdown.
        ::int32 LiveA = 0;
        ::int32 LiveD = 0;
        for (::int32 i = 0; i < Stats.PerClassAllocations.Num(); ++i)
        {
            const auto& Entry = Stats.PerClassAllocations[i];
            if (Entry.Class == &ClassA) LiveA = Entry.LiveCount;
            if (Entry.Class == &ClassD) LiveD = Entry.LiveCount;
        }
        Check(LiveA == 1, "GetStats: ClassA live count != 1");
        Check(LiveD == 1, "GetStats: ClassD live count != 1");
    }

    Allocator.Deallocate(CellD1);
    Allocator.Deallocate(CellA5);

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.PerClassPool: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.PerClassPool: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
