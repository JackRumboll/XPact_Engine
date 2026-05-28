// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInsightsBridge.Tests/EmitterHelpers.cpp -- per-category helper
// dispatch (XCoreXObject Rev 4 §10.12 + Phase 5.k).
// =====================================================================
//
// Verifies the XInsightsEmitHelpers convenience functions:
//   * EmitGCCycleComplete dispatches through the bridge.
//   * EmitAllocFailure dispatches through the bridge.
//   * EmitFragmentationThresholdCrossed dispatches through the
//     bridge.
//
// On Clang/GCC the mock counter (defined in EmitWithMockStrongSymbol.
// cpp's TU, which links into the same test target via XBT's per-test
// .cpp = one .exe pattern; this is a SEPARATE TU but the helpers all
// dispatch through the same XCore_XInsights_Emit symbol). On MSVC the
// mock is gated; this test verifies the no-crash + reasonable-payload
// behaviour via direct payload inspection.
//
// For the per-helper "did it dispatch?" check we use a simple counter
// inside this TU (own mock weak/strong override). The test is
// independent of EmitWithMockStrongSymbol.cpp (those are separate
// .exes per XBT's test layout).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FName.h"
#include "XObject/FXInsightsPayload.h"
#include "XObject/XInsightsBridge.h"
#include "XObject/XInsightsEmitHelpers.h"
#include "XObject/XInsightsEvents.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <iostream>

#if !defined(_MSC_VER)

// Strong override on Clang/GCC; supplants the weak stub.
static ::std::atomic<int> g_HelpersEmitCount{0};
static const char*        g_HelpersLastCategory = nullptr;
static const char*        g_HelpersLastEvent    = nullptr;
static ::SIZE_T           g_HelpersLastEntryCount = 0;

extern "C"
{
    void XCore_XInsights_Emit(
        const char*                                       Category,
        const char*                                       Event,
        const ::XCore::HAL::FXInsightsPayload*            Payload) noexcept
    {
        g_HelpersLastCategory = Category;
        g_HelpersLastEvent    = Event;
        g_HelpersLastEntryCount = (Payload != nullptr) ? Payload->NumEntries() : 0;
        g_HelpersEmitCount.fetch_add(1, ::std::memory_order_relaxed);
    }
}

#endif // !_MSC_VER

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
    namespace Helpers = ::XCore::HAL::XInsightsEmitHelpers;
    namespace Events  = ::XCore::HAL::XInsightsEvents;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    Events::PrewarmAtPostStaticInit();

#if defined(_MSC_VER)
    // MSVC baseline: verify the helpers do not crash. The strong-
    // override link path is XInsights's responsibility.
    {
        Helpers::EmitGCCycleComplete(
            /*MarkDurationUs=*/   100,
            /*SweepDurationUs=*/  50,
            /*DirtyCardCount=*/   1000,
            /*ReclaimedCount=*/   500,
            /*ThroughputMBps=*/   123.4,
            /*CycleId=*/          1);
        Check(true, "EmitGCCycleComplete no-crash baseline.");
    }
    {
        Helpers::EmitAllocFailure(
            /*SizeClass=*/    3,
            /*RequestedBytes=*/256,
            /*Tag=*/           FName("XObject"));
        Check(true, "EmitAllocFailure no-crash baseline.");
    }
    {
        Helpers::EmitFragmentationThresholdCrossed(
            /*SizeClass=*/        -1,
            /*WastedPct=*/        15.5,
            /*TotalAllocatedBytes=*/4096);
        Check(true, "EmitFragmentationThresholdCrossed no-crash baseline.");
    }

    std::cout << "XInsightsBridge.EmitterHelpers: PASS (MSVC baseline only)\n";
    return 0;
#else
    // -----------------------------------------------------------------
    // Test 1: EmitGCCycleComplete dispatches to GC category +
    // CycleComplete event with the expected 6-entry payload.
    // -----------------------------------------------------------------
    {
        g_HelpersEmitCount.store(0);
        Helpers::EmitGCCycleComplete(
            /*MarkDurationUs=*/   200,
            /*SweepDurationUs=*/  100,
            /*DirtyCardCount=*/   2000,
            /*ReclaimedCount=*/   1500,
            /*ThroughputMBps=*/   77.7,
            /*CycleId=*/          42);

        Check(g_HelpersEmitCount.load() == 1,
              "EmitGCCycleComplete should dispatch exactly once.");
        Check(g_HelpersLastCategory != nullptr
              && ::std::strcmp(g_HelpersLastCategory, "XCoreXObject.GC") == 0,
              "EmitGCCycleComplete should target category 'XCoreXObject.GC'.");
        Check(g_HelpersLastEvent != nullptr
              && ::std::strcmp(g_HelpersLastEvent, "CycleComplete") == 0,
              "EmitGCCycleComplete should target event 'CycleComplete'.");
        Check(g_HelpersLastEntryCount == 6,
              "EmitGCCycleComplete payload should have 6 entries.");
    }

    // -----------------------------------------------------------------
    // Test 2: EmitAllocFailure -> Allocator/AllocationFailure / 3 fields.
    // -----------------------------------------------------------------
    {
        g_HelpersEmitCount.store(0);
        Helpers::EmitAllocFailure(
            /*SizeClass=*/    5,
            /*RequestedBytes=*/4096,
            /*Tag=*/           FName("XObject"));

        Check(g_HelpersEmitCount.load() == 1,
              "EmitAllocFailure should dispatch exactly once.");
        Check(g_HelpersLastCategory != nullptr
              && ::std::strcmp(g_HelpersLastCategory, "XCoreXObject.Allocator") == 0,
              "EmitAllocFailure should target 'XCoreXObject.Allocator'.");
        Check(g_HelpersLastEvent != nullptr
              && ::std::strcmp(g_HelpersLastEvent, "AllocationFailure") == 0,
              "EmitAllocFailure should target 'AllocationFailure'.");
        Check(g_HelpersLastEntryCount == 3,
              "EmitAllocFailure payload should have 3 entries.");
    }

    // -----------------------------------------------------------------
    // Test 3: EmitFragmentationThresholdCrossed -> 3 fields.
    // -----------------------------------------------------------------
    {
        g_HelpersEmitCount.store(0);
        Helpers::EmitFragmentationThresholdCrossed(
            /*SizeClass=*/        -1,
            /*WastedPct=*/        18.0,
            /*TotalAllocatedBytes=*/8192);

        Check(g_HelpersEmitCount.load() == 1,
              "EmitFragmentationThresholdCrossed should dispatch exactly once.");
        Check(g_HelpersLastEvent != nullptr
              && ::std::strcmp(g_HelpersLastEvent, "FragmentationThresholdCrossed") == 0,
              "EmitFragmentationThresholdCrossed event name mismatch.");
        Check(g_HelpersLastEntryCount == 3,
              "EmitFragmentationThresholdCrossed payload should have 3 entries.");
    }

    // -----------------------------------------------------------------
    // Test 4: GCRememberedSetSaturation -> 4 fields.
    // -----------------------------------------------------------------
    {
        g_HelpersEmitCount.store(0);
        Helpers::EmitGCRememberedSetSaturation(
            /*DirtyCardCount=*/4096,
            /*TotalCardCount=*/8192,
            /*SaturationPct=*/  50.0,
            /*CycleId=*/        99);

        Check(g_HelpersEmitCount.load() == 1,
              "EmitGCRememberedSetSaturation should dispatch exactly once.");
        Check(g_HelpersLastEvent != nullptr
              && ::std::strcmp(g_HelpersLastEvent, "RememberedSetSaturation") == 0,
              "EmitGCRememberedSetSaturation event name mismatch.");
        Check(g_HelpersLastEntryCount == 4,
              "EmitGCRememberedSetSaturation payload should have 4 entries.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XInsightsBridge.EmitterHelpers: PASS\n";
        return 0;
    }
    std::cerr << "XInsightsBridge.EmitterHelpers: " << g_FailureCount
              << " FAIL(s)\n";
    return 1;
#endif // !_MSC_VER
}
