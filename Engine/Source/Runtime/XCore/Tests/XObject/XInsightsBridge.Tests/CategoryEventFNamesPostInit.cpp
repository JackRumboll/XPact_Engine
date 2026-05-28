// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XInsightsBridge.Tests/CategoryEventFNamesPostInit.cpp -- verify the
// FName accessors land post-init (XCoreXObject Rev 4 §10.12 +
// Phase 5.k).
// =====================================================================
//
// Verifies:
//   * Calling PrewarmAtPostStaticInit interns every accessor.
//   * Each Category / Event / Key accessor returns a valid FName
//     (Index != 0; ToString matches the expected string).
//   * Accessor results are stable across multiple calls (same Index).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FName.h"
#include "XObject/XInsightsEvents.h"

#include <cstring>
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
    namespace Events = ::XCore::HAL::XInsightsEvents;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Test 1: Prewarm does not crash.
    // -----------------------------------------------------------------
    {
        Events::PrewarmAtPostStaticInit();
        Check(true, "PrewarmAtPostStaticInit completed without crash.");
    }

    // -----------------------------------------------------------------
    // Test 2: Categories are non-None and the expected strings.
    // -----------------------------------------------------------------
    {
        const FName& Cat = Events::CategoryGC();
        Check(!Cat.IsNone(), "CategoryGC should not be NAME_None.");
        Check(::std::strcmp(Cat.GetBaseBytes(), "XCoreXObject.GC") == 0,
              "CategoryGC base bytes should be 'XCoreXObject.GC'.");
    }
    {
        const FName& Cat = Events::CategoryAllocator();
        Check(!Cat.IsNone(),
              "CategoryAllocator should not be NAME_None.");
        Check(::std::strcmp(Cat.GetBaseBytes(), "XCoreXObject.Allocator") == 0,
              "CategoryAllocator base bytes mismatch.");
    }
    {
        const FName& Cat = Events::CategoryHotReload();
        Check(!Cat.IsNone(),
              "CategoryHotReload should not be NAME_None.");
        Check(::std::strcmp(Cat.GetBaseBytes(), "XCoreXObject.HotReload") == 0,
              "CategoryHotReload base bytes mismatch.");
    }

    // -----------------------------------------------------------------
    // Test 3: GC events.
    // -----------------------------------------------------------------
    {
        Check(::std::strcmp(Events::GC::CycleComplete().GetBaseBytes(),
                            "CycleComplete") == 0,
              "GC::CycleComplete name mismatch.");
        Check(::std::strcmp(Events::GC::FullScanFallback().GetBaseBytes(),
                            "FullScanFallback") == 0,
              "GC::FullScanFallback name mismatch.");
        Check(::std::strcmp(Events::GC::RememberedSetSaturation().GetBaseBytes(),
                            "RememberedSetSaturation") == 0,
              "GC::RememberedSetSaturation name mismatch.");
    }

    // -----------------------------------------------------------------
    // Test 4: Allocator events.
    // -----------------------------------------------------------------
    {
        Check(::std::strcmp(Events::Allocator::AllocationFailure().GetBaseBytes(),
                            "AllocationFailure") == 0,
              "Allocator::AllocationFailure name mismatch.");
        Check(::std::strcmp(Events::Allocator::PoolGrew().GetBaseBytes(),
                            "PoolGrew") == 0,
              "Allocator::PoolGrew name mismatch.");
        Check(::std::strcmp(Events::Allocator::PoolReleaseAtScenarioBoundary().GetBaseBytes(),
                            "PoolReleaseAtScenarioBoundary") == 0,
              "Allocator::PoolReleaseAtScenarioBoundary name mismatch.");
        Check(::std::strcmp(Events::Allocator::FragmentationThresholdCrossed().GetBaseBytes(),
                            "FragmentationThresholdCrossed") == 0,
              "Allocator::FragmentationThresholdCrossed name mismatch.");
    }

    // -----------------------------------------------------------------
    // Test 5: HotReload events.
    // -----------------------------------------------------------------
    {
        Check(::std::strcmp(Events::HotReload::CascadeApplied().GetBaseBytes(),
                            "CascadeApplied") == 0,
              "HotReload::CascadeApplied name mismatch.");
        Check(::std::strcmp(Events::HotReload::ClassReplaced().GetBaseBytes(),
                            "ClassReplaced") == 0,
              "HotReload::ClassReplaced name mismatch.");
        Check(::std::strcmp(Events::HotReload::QuiesceWaited().GetBaseBytes(),
                            "QuiesceWaited") == 0,
              "HotReload::QuiesceWaited name mismatch.");
    }

    // -----------------------------------------------------------------
    // Test 6: Stability -- repeated calls return the same FName.
    // -----------------------------------------------------------------
    {
        const FName A = Events::GC::CycleComplete();
        const FName B = Events::GC::CycleComplete();
        Check(A.GetIndex() == B.GetIndex(),
              "GC::CycleComplete should be index-stable across calls.");
        Check(A == B,
              "GC::CycleComplete should be == across calls.");
    }

    // -----------------------------------------------------------------
    // Test 7: Some payload-key sanity.
    // -----------------------------------------------------------------
    {
        Check(::std::strcmp(Events::Keys::SizeClass().GetBaseBytes(),
                            "sizeClass") == 0,
              "Keys::SizeClass name mismatch.");
        Check(::std::strcmp(Events::Keys::DirtyCardCount().GetBaseBytes(),
                            "dirtyCardCount") == 0,
              "Keys::DirtyCardCount name mismatch.");
        Check(::std::strcmp(Events::Keys::SaturationPct().GetBaseBytes(),
                            "saturationPct") == 0,
              "Keys::SaturationPct name mismatch.");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "XInsightsBridge.CategoryEventFNamesPostInit: PASS\n";
        return 0;
    }
    std::cerr << "XInsightsBridge.CategoryEventFNamesPostInit: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
