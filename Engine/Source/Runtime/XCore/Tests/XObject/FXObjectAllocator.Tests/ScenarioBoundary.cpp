// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectAllocator.Tests/ScenarioBoundary.cpp -- Begin/EndScenario
// Boundary scope bookkeeping (XCoreXObject Rev 4 §3.6).
// =====================================================================
//
// Spec §3.6: BeginScenarioBoundary pushes a scope; allocations within
// the scope are tagged with the scope's scenario name. EndScenarioBoundary
// pops the scope.
//
// PHASE 5.b SCOPE per the FXObjectAllocator header gating note:
//
//   "Phase 5.b ships the scope bookkeeping + the per-cell tagging;
//    the actual mark-region clearing requires the FXObjectCollector
//    (Phase 5.h) so the SCAN over 'escaped references' can run.
//    Until then EndScenarioBoundary is documentation-only -- the scope
//    name is captured for telemetry; the heap-side action is a no-op
//    until 5.h provides the reachability oracle."
//
// This test therefore verifies only the SCOPE BOOKKEEPING:
//
//   1. Begin + End is a no-op (no XPACT_CHECK violation).
//   2. Nested Begin + Begin + End + End is balanced.
//   3. Allocations inside the scope are valid; they do NOT crash on
//      scope close (the cells stay live; Deallocate works as expected).
//
// The mark-region-clearing action gated on Phase 5.h is SKIPPED per
// the dispatch task's "skip tests for surfaces that aren't implemented
// yet" allowance. The skip is documented inline.
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

    FClass TestClass(FName("ScenarioTest"), nullptr);
    TestClass.PropertiesSize = 100;
    TestClass.MinAlignment   = 8;
    Allocator.RegisterClassPool(&TestClass);

    // -----------------------------------------------------------------
    // Test 1: Simple Begin + End balance.
    // -----------------------------------------------------------------
    {
        Allocator.BeginScenarioBoundary();

        // Allocate inside the scope.
        void* Cell = Allocator.AllocateRaw(100, 8, &TestClass);
        Check(Cell != nullptr, "In-scope AllocateRaw returned nullptr");

        // Close the scope. Per the Phase 5.b gating note, this is a
        // counter-bump no-op for the heap (the FXObjectCollector
        // reachability oracle is needed for the actual release).
        Allocator.EndScenarioBoundary(FName("TestScenario1"));

        // The cell is still live (Phase 5.b is doc-only on scope end);
        // we deallocate explicitly to clean up.
        Allocator.Deallocate(Cell);
    }

    // -----------------------------------------------------------------
    // Test 2: Nested Begin + Begin + End + End.
    // -----------------------------------------------------------------
    {
        Allocator.BeginScenarioBoundary();
        void* Cell1 = Allocator.AllocateRaw(100, 8, &TestClass);

        Allocator.BeginScenarioBoundary();
        void* Cell2 = Allocator.AllocateRaw(100, 8, &TestClass);

        Check(Cell1 != nullptr && Cell2 != nullptr,
              "Nested-scope AllocateRaw returned nullptr");
        Check(Cell1 != Cell2,
              "Nested-scope AllocateRaw returned the same cell twice");

        Allocator.EndScenarioBoundary(FName("InnerScenario"));
        Allocator.EndScenarioBoundary(FName("OuterScenario"));

        Allocator.Deallocate(Cell1);
        Allocator.Deallocate(Cell2);
    }

    // -----------------------------------------------------------------
    // Test 3: Multiple sequential scopes (Begin + End + Begin + End)
    // each succeed.
    // -----------------------------------------------------------------
    {
        for (int I = 0; I < 4; ++I)
        {
            Allocator.BeginScenarioBoundary();
            void* Cell = Allocator.AllocateRaw(100, 8, &TestClass);
            Check(Cell != nullptr, "Sequential-scope alloc returned nullptr");
            Allocator.EndScenarioBoundary(FName());
            Allocator.Deallocate(Cell);
        }
    }

    // -----------------------------------------------------------------
    // Phase 5.h gated path (SKIPPED). The actual mark-region-clearing
    // on scope close requires the FXObjectCollector reachability
    // oracle. Phase 5.b documents the API; the test for the release
    // action will land alongside Phase 5.h.
    //
    // SKIP rationale: the s_XObjectAllocator hook returns memory; the
    // collector that detects "this scope's cells are unreferenced" has
    // not been written. Calling EndScenarioBoundary today is a no-op
    // for the heap; verifying that no-op behaviour is what Test 1-3
    // above cover. Verifying the FUTURE batch-clear behaviour requires
    // FXObjectCollector and is out-of-scope for Phase 5.b.
    // -----------------------------------------------------------------
    std::cout << "SKIP: scenario-boundary batch-clear (Phase 5.h "
                 "FXObjectCollector dependency)\n";

    Allocator.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectAllocator.ScenarioBoundary: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectAllocator.ScenarioBoundary: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
}
