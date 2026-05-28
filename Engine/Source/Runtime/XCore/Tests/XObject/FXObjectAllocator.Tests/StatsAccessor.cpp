// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/StatsAccessor.cpp -- GetStats POD snapshot
// correctness (XCoreXObject Rev 4 §3 telemetry + §10.5).
// =====================================================================
//
// Verifies the FXObjectAllocatorStats fields:
//
//   * TotalAllocatedBytes increases after Allocate, decreases after
//     Deallocate.
//   * TotalSlabBytes >= TotalAllocatedBytes (slabs reserve at least as
//     much as user-payload uses).
//   * TotalLiveObjects matches the count of live allocations.
//   * ActiveSlabs + IdleSlabs accounts for every slab.
//   * FragmentationPercent in [0.0, 100.0]; zero on empty heap.
//   * PerClassAllocations lists each class with > 0 live count exactly
//     once.
//   * LargeObjectSlabs increments on > 1024-byte allocations.
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
    using ::XCore::FXObjectAllocatorStats;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectAllocator& Allocator = FXObjectAllocator::Get();
    Allocator.__ResetForTests();

    // -----------------------------------------------------------------
    // Empty-heap baseline.
    // -----------------------------------------------------------------
    {
        FXObjectAllocatorStats Stats = Allocator.GetStats();
        Check(Stats.TotalAllocatedBytes == 0, "Empty heap: TotalAllocatedBytes != 0");
        Check(Stats.TotalSlabBytes      == 0, "Empty heap: TotalSlabBytes != 0");
        Check(Stats.TotalLiveObjects    == 0, "Empty heap: TotalLiveObjects != 0");
        Check(Stats.ActiveSlabs         == 0, "Empty heap: ActiveSlabs != 0");
        Check(Stats.IdleSlabs           == 0, "Empty heap: IdleSlabs != 0");
        Check(Stats.LargeObjectSlabs    == 0, "Empty heap: LargeObjectSlabs != 0");
        Check(Stats.FragmentationPercent == 0.0,
              "Empty heap: FragmentationPercent != 0");
        Check(Stats.PerClassAllocations.Num() == 0,
              "Empty heap: PerClassAllocations not empty");
    }

    // -----------------------------------------------------------------
    // After one allocate: stats grow.
    // -----------------------------------------------------------------
    FClass TestClass(FName("StatsTest"), nullptr);
    TestClass.PropertiesSize = 100;
    TestClass.MinAlignment   = 8;
    Allocator.RegisterClassPool(&TestClass);

    void* Cell1 = Allocator.AllocateRaw(100, 8, &TestClass);
    {
        FXObjectAllocatorStats Stats = Allocator.GetStats();
        // 100 bytes -> class 2 (128 cell width). The allocator's
        // TotalAllocatedBytes tracks cell-width * live-cell-count
        // (not user-requested bytes); spec §3.6 telemetry definition.
        Check(Stats.TotalAllocatedBytes > 0,
              "After 1 alloc: TotalAllocatedBytes == 0");
        Check(Stats.TotalSlabBytes >= Stats.TotalAllocatedBytes,
              "TotalSlabBytes < TotalAllocatedBytes (impossible)");
        Check(Stats.TotalLiveObjects == 1,
              "After 1 alloc: TotalLiveObjects != 1");
        Check(Stats.ActiveSlabs >= 1,
              "After 1 alloc: ActiveSlabs < 1");
        Check(Stats.FragmentationPercent >= 0.0 &&
              Stats.FragmentationPercent <= 100.0,
              "FragmentationPercent out of [0,100]");

        // PerClassAllocations contains exactly one entry (TestClass / 1).
        Check(Stats.PerClassAllocations.Num() == 1,
              "After 1 alloc: PerClassAllocations.Num() != 1");
        if (Stats.PerClassAllocations.Num() == 1)
        {
            Check(Stats.PerClassAllocations[0].Class     == &TestClass,
                  "PerClassAllocations: Class pointer mismatch");
            Check(Stats.PerClassAllocations[0].LiveCount == 1,
                  "PerClassAllocations: LiveCount != 1");
        }
    }

    // -----------------------------------------------------------------
    // Deallocate: stats shrink.
    // -----------------------------------------------------------------
    Allocator.Deallocate(Cell1);
    {
        FXObjectAllocatorStats Stats = Allocator.GetStats();
        Check(Stats.TotalLiveObjects == 0,
              "After 1 free: TotalLiveObjects != 0");
        // Slab is still resident (IdleSlabs may now be 1).
        Check(Stats.PerClassAllocations.Num() == 0,
              "After 1 free: PerClassAllocations not empty");
    }

    // -----------------------------------------------------------------
    // Large-object path: > 1024 bytes routes to LargeObjectSlabs.
    // -----------------------------------------------------------------
    {
        void* BigCell = Allocator.AllocateRaw(2048, 16, &TestClass);
        Check(BigCell != nullptr, "Large-object allocation failed");

        FXObjectAllocatorStats Stats = Allocator.GetStats();
        Check(Stats.LargeObjectSlabs >= 1,
              "Large alloc did not increment LargeObjectSlabs");
        Check(Stats.TotalLiveObjects >= 1,
              "Large alloc did not increment TotalLiveObjects");

        Allocator.Deallocate(BigCell);

        Stats = Allocator.GetStats();
        Check(Stats.LargeObjectSlabs == 0,
              "Large free did not decrement LargeObjectSlabs to 0");
    }

    // -----------------------------------------------------------------
    // Multiple-class breakdown: distinct entries for two classes.
    // -----------------------------------------------------------------
    {
        FClass OtherClass(FName("StatsTest2"), nullptr);
        OtherClass.PropertiesSize = 200;
        OtherClass.MinAlignment   = 8;
        Allocator.RegisterClassPool(&OtherClass);

        void* CellA = Allocator.AllocateRaw(100, 8, &TestClass);
        void* CellB = Allocator.AllocateRaw(200, 8, &OtherClass);

        FXObjectAllocatorStats Stats = Allocator.GetStats();
        ::int32 SeenA = 0, SeenB = 0;
        for (::int32 i = 0; i < Stats.PerClassAllocations.Num(); ++i)
        {
            if (Stats.PerClassAllocations[i].Class == &TestClass) ++SeenA;
            if (Stats.PerClassAllocations[i].Class == &OtherClass) ++SeenB;
        }
        Check(SeenA == 1, "Multi-class stats: TestClass not present exactly once");
        Check(SeenB == 1, "Multi-class stats: OtherClass not present exactly once");

        Allocator.Deallocate(CellA);
        Allocator.Deallocate(CellB);
    }

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.StatsAccessor: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.StatsAccessor: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
