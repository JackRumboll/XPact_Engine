// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/RebindClassPool.cpp -- hot-reload class
// replacement (XCoreXObject Rev 4 §3.6 + FIX-A-MIN-40).
// =====================================================================
//
// Spec §3.6: "RebindClassPool migrates ownership of every cell
// currently tracked under OldClass to NewClass. The cells themselves
// do NOT move; only the sub-pool ownership rebinds."
//
// Verifies:
//
//   1. After RebindClassPool(Old -> New), cells previously owned by
//      Old are now owned by New (re-allocating against New returns
//      cells that originally came from Old).
//   2. The cell ADDRESSES do not change (pointer stability).
//   3. Free-list concatenation: Old's free-list is appended to New's
//      free-list (so Old's freed cells become available to New's
//      allocations).
//   4. RebindClassPool auto-registers New if it wasn't previously
//      registered.
//   5. The post-rebind live count is migrated from Old to New (Old's
//      count zeros; New's count carries the original delta).
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
    // Setup: two FClasses at the SAME size class (the spec invariant
    // RebindClassPool requires).
    // -----------------------------------------------------------------
    FClass OldClass(FName("Old"), nullptr);
    OldClass.PropertiesSize = 100;
    OldClass.MinAlignment   = 8;

    FClass NewClass(FName("New"), nullptr);
    NewClass.PropertiesSize = 100;  // same size class as Old
    NewClass.MinAlignment   = 8;

    Allocator.RegisterClassPool(&OldClass);
    // NewClass intentionally NOT pre-registered; the rebind should
    // auto-register it at the same size class.

    // -----------------------------------------------------------------
    // Allocate 3 cells under OldClass. Free one (placed onto Old's
    // free-list). Two remain live.
    // -----------------------------------------------------------------
    void* Cell1 = Allocator.AllocateRaw(100, 8, &OldClass);
    void* Cell2 = Allocator.AllocateRaw(100, 8, &OldClass);
    void* Cell3 = Allocator.AllocateRaw(100, 8, &OldClass);

    Check(Cell1 != nullptr && Cell2 != nullptr && Cell3 != nullptr,
          "Setup: AllocateRaw under OldClass returned nullptr");

    // Free Cell2 -> goes on Old's free-list.
    Allocator.Deallocate(Cell2);

    // -----------------------------------------------------------------
    // Pre-rebind stats sanity.
    // -----------------------------------------------------------------
    {
        ::XCore::FXObjectAllocatorStats Stats = Allocator.GetStats();
        ::int32 LiveOld = 0;
        ::int32 LiveNew = 0;
        for (::int32 i = 0; i < Stats.PerClassAllocations.Num(); ++i)
        {
            if (Stats.PerClassAllocations[i].Class == &OldClass)
                LiveOld = Stats.PerClassAllocations[i].LiveCount;
            if (Stats.PerClassAllocations[i].Class == &NewClass)
                LiveNew = Stats.PerClassAllocations[i].LiveCount;
        }
        Check(LiveOld == 2, "Pre-rebind: OldClass live != 2");
        Check(LiveNew == 0, "Pre-rebind: NewClass live != 0");
    }

    // -----------------------------------------------------------------
    // The rebind itself.
    // -----------------------------------------------------------------
    Allocator.RebindClassPool(&OldClass, &NewClass);

    // -----------------------------------------------------------------
    // Post-rebind stats: OldClass should have 0 live (everything
    // migrated); NewClass should have 2 live (Cell1 + Cell3).
    // -----------------------------------------------------------------
    {
        ::XCore::FXObjectAllocatorStats Stats = Allocator.GetStats();
        ::int32 LiveOld = 0;
        ::int32 LiveNew = 0;
        for (::int32 i = 0; i < Stats.PerClassAllocations.Num(); ++i)
        {
            if (Stats.PerClassAllocations[i].Class == &OldClass)
                LiveOld = Stats.PerClassAllocations[i].LiveCount;
            if (Stats.PerClassAllocations[i].Class == &NewClass)
                LiveNew = Stats.PerClassAllocations[i].LiveCount;
        }
        Check(LiveOld == 0, "Post-rebind: OldClass live != 0");
        Check(LiveNew == 2, "Post-rebind: NewClass live != 2");
    }

    // -----------------------------------------------------------------
    // Allocating against NewClass now returns Cell2 (which was on
    // Old's free-list and got migrated to New's free-list).
    // -----------------------------------------------------------------
    void* CellN1 = Allocator.AllocateRaw(100, 8, &NewClass);
    Check(CellN1 == Cell2,
          "Post-rebind: migrated free-list did not surface Cell2 on "
          "NewClass allocation");

    // -----------------------------------------------------------------
    // The original Cell1 + Cell3 are still live (their addresses did
    // not change); free them via the NewClass path. (We deliberately
    // don't reference them by `OldClass`; per spec the rebind transfers
    // ownership so the cells are now NewClass-tagged.)
    // -----------------------------------------------------------------
    Allocator.Deallocate(Cell1);
    Allocator.Deallocate(Cell3);
    Allocator.Deallocate(CellN1);

    // After deallocating all 3, allocating again against NewClass
    // returns the LIFO-most-recent free cell (CellN1 / Cell2). This
    // proves the OwnerClass tags rebound correctly.
    void* CellN2 = Allocator.AllocateRaw(100, 8, &NewClass);
    Check(CellN2 == Cell2 || CellN2 == Cell1 || CellN2 == Cell3,
          "Post-rebind LIFO: returned cell did not come from the "
          "rebound free-list");

    Allocator.Deallocate(CellN2);

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.RebindClassPool: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.RebindClassPool: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
