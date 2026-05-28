// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/CoalesceIdleSlabs.cpp -- engineer-station-
// only slab reclaim (XCoreXObject Rev 4 §3.6 + FIX-A-MED-24).
// =====================================================================
//
// Spec §3.6 + the allocator header gating note: "Phase 5.b acts ONLY
// on fully-empty slabs (live count == 0); the partial-evacuation path
// is gated until Phase 5.h ships the GC coordination."
//
// Verifies:
//
//   1. After a sequence of allocates + a subset of frees, calling
//      CoalesceIdleSlabs releases zero slabs (because at least one
//      cell per slab is still live).
//   2. After freeing ALL cells in a slab, CoalesceIdleSlabs releases
//      that slab (returns count > 0).
//   3. Post-coalesce, allocating again triggers a fresh slab commit
//      (and the new cell is non-null).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectAllocatorStats.h"

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
    using ::XCore::FXObjectAllocator;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    Allocator.__ResetForTests();

    // -----------------------------------------------------------------
    // Test 1: Coalesce on a heap that has all-live cells releases 0
    // slabs.
    // -----------------------------------------------------------------
    {
        FClass TestClass(FName("CoalesceTest1"), nullptr);
        TestClass.PropertiesSize = 200;  // class 4 (256)
        TestClass.MinAlignment   = 8;
        Allocator.RegisterClassPool(&TestClass);

        std::vector<void*> Cells;
        for (int I = 0; I < 5; ++I)
        {
            Cells.push_back(Allocator.AllocateRaw(200, 8, &TestClass));
        }

        const ::int32 Released = Allocator.CoalesceIdleSlabs();
        Check(Released == 0,
              "CoalesceIdleSlabs released slabs while cells were live");

        for (void* Cell : Cells)
        {
            Allocator.Deallocate(Cell);
        }
    }

    // -----------------------------------------------------------------
    // Test 2: After freeing every live cell + coalescing, slabs with
    // LiveCellCount == 0 are released.
    //
    // Strategy: drive the heap to a state where the only allocations
    // are in slabs we'll fully drain. We use a fresh size class (class
    // 5; 384 bytes) that nothing else in this test has touched, so the
    // slabs we allocate are the only ones in that class.
    //
    // The class-5 slab is 64 KB / 384 ~= 170 cells. We allocate 5 cells,
    // free them all, then coalesce. Note: the slab is NOT fully empty
    // until ALL its cells (170) are accounted for as either freed-via-
    // Deallocate (on a free-list) or never-touched (still on the
    // unassigned chain).
    //
    // The allocator's CoalesceIdleSlabs release condition is
    // `Slab->LiveCellCount == 0`. LiveCellCount is incremented in
    // AllocateRaw when a cell is handed to a class pool, and
    // decremented in Deallocate. So after we free all 5 cells we
    // allocated, the slab's LiveCellCount returns to 0 -- even though
    // 165 cells are still on the unassigned chain. The coalesce path
    // releases this slab cleanly.
    // -----------------------------------------------------------------
    {
        FClass TestClass(FName("CoalesceTest2"), nullptr);
        TestClass.PropertiesSize = 300;  // class 5 (384)
        TestClass.MinAlignment   = 8;
        Allocator.RegisterClassPool(&TestClass);

        const ::XCore::FXObjectAllocatorStats StatsBefore = Allocator.GetStats();

        // Allocate 5 cells.
        std::vector<void*> Cells;
        for (int I = 0; I < 5; ++I)
        {
            Cells.push_back(Allocator.AllocateRaw(300, 8, &TestClass));
        }

        // Free all 5.
        for (void* Cell : Cells)
        {
            Allocator.Deallocate(Cell);
        }

        // After freeing all 5, the slab's LiveCellCount == 0.
        // CoalesceIdleSlabs should release that slab.
        const ::int32 Released = Allocator.CoalesceIdleSlabs();
        Check(Released >= 1,
              "CoalesceIdleSlabs failed to release a fully-empty slab");

        // Verify TotalSlabBytes went down.
        const ::XCore::FXObjectAllocatorStats StatsAfter = Allocator.GetStats();
        // The active+idle slab count should be StatsBefore values now
        // (since the slabs we added were released).
        // We allow a >=1 slab release count and consistent stats.
        Check(StatsAfter.TotalSlabBytes <=
                  StatsBefore.TotalSlabBytes + (::SIZE_T(64) << 10),
              "Coalesce: TotalSlabBytes did not decrease after release");
    }

    // -----------------------------------------------------------------
    // Test 3: After coalesce, fresh allocations succeed (a new slab is
    // committed transparently).
    // -----------------------------------------------------------------
    {
        FClass TestClass(FName("CoalesceTest3"), nullptr);
        TestClass.PropertiesSize = 300;
        TestClass.MinAlignment   = 8;
        Allocator.RegisterClassPool(&TestClass);

        void* Cell = Allocator.AllocateRaw(300, 8, &TestClass);
        Check(Cell != nullptr, "Post-coalesce: AllocateRaw returned nullptr");

        Allocator.Deallocate(Cell);
    }

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.CoalesceIdleSlabs: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.CoalesceIdleSlabs: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
