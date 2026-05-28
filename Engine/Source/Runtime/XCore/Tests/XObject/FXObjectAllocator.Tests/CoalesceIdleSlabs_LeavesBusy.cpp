// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/CoalesceIdleSlabs_LeavesBusy.cpp -- idle-
// slab coalescence MUST preserve slabs with any live cell
// (XCoreXObject Rev 4 §3.6 + FIX-A-MED-24; Phase 5.i).
// =====================================================================
//
// The Phase 5.b body of CoalesceIdleSlabs releases ONLY fully-empty
// slabs (LiveCellCount == 0). Phase 5.i preserves that gate + adds
// the bytes-returned reporting + the IdleSlabCoalesced telemetry
// emit.
//
// This test verifies: a slab with at least ONE live cell is NEVER
// released by CoalesceIdleSlabs. The verification surface:
//
//   1. Allocate one cell in a fresh size class. The slab is committed
//      with 1 live cell + N-1 unassigned cells (where N is the per-
//      slab cell count, ~170 for class 5 at 384 bytes).
//
//   2. Call CoalesceIdleSlabs. Returned bytes should be 0 (the busy
//      slab is NOT released).
//
//   3. Verify the previously-allocated cell is still valid (we can
//      Deallocate it without crashing -- it must still be tracked).
//
//   4. Free the cell + call CoalesceIdleSlabs again -- NOW the slab
//      becomes idle (LiveCellCount == 0) + the slab IS released
//      (returned bytes >= 1 slab worth).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectAllocator.h"

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
    // Use a fresh size class (class 6: 512 bytes; chosen to be
    // distinct from the other CoalesceIdleSlabs tests so the slab
    // state is fully owned by this test).
    // -----------------------------------------------------------------
    FClass TestClass(FName("CoalesceLeavesBusyTest"), nullptr);
    TestClass.PropertiesSize = 500;   // routes to class 6 (512)
    TestClass.MinAlignment   = 8;
    Allocator.RegisterClassPool(&TestClass);

    // Allocate ONE cell. This commits a fresh slab (no prior slab
    // existed for this class).
    void* LiveCell = Allocator.AllocateRaw(500, 8, &TestClass);
    Check(LiveCell != nullptr, "Setup: AllocateRaw returned nullptr");

    // -----------------------------------------------------------------
    // Call CoalesceIdleSlabs. The slab has 1 live cell so it MUST NOT
    // be released. The returned bytes-value must be 0 for this
    // class's slabs (any other class's idle slabs from prior test
    // state were already cleared by __ResetForTests).
    // -----------------------------------------------------------------
    const ::int64 BytesReturned = Allocator.CoalesceIdleSlabs();
    Check(BytesReturned == 0,
          "CoalesceIdleSlabs released a busy slab (LiveCellCount > 0)");

    // -----------------------------------------------------------------
    // Verify the cell is still valid: we can Deallocate it (which
    // requires the slab to be in the SizePool's slab list).
    // -----------------------------------------------------------------
    Allocator.Deallocate(LiveCell);

    // -----------------------------------------------------------------
    // Now CoalesceIdleSlabs SHOULD release the slab (LiveCellCount
    // dropped to 0 at the Deallocate). Bytes >= 1 slab.
    // -----------------------------------------------------------------
    const ::int64 BytesReturnedPostFree = Allocator.CoalesceIdleSlabs();
    Check(BytesReturnedPostFree >= (::int64(128) << 10),
          "Post-Free CoalesceIdleSlabs did not release the now-idle "
          "slab (expected >= 128 KB for class 6)");

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.CoalesceIdleSlabs_LeavesBusy: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.CoalesceIdleSlabs_LeavesBusy: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
