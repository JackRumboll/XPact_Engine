// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectHotReloadCoordinator.Tests/XObjectKeyResolutionSurvives.cpp
// (XCoreXObject Rev 4 §6.4 + §11.4 acceptance X6 + Phase 5.j).
// =====================================================================
//
// THE X6 ACCEPTANCE TEST:
//
// Spec §11.4 X6: "hot-reload class replacement preserves XObjectKey
// resolution for 100% of instances in a synthetic 10k-instance test."
//
// Per the XCoreXObject Rev 4 §6.4 trailing prose: XObjectKey's
// {InternalIndex, SerialNumber} survives class replacement because
// the rebind protocol preserves both fields (only ClassPrivate is
// atomically stored; the slot is NOT released, the SerialNumber is
// NOT bumped).
//
// =====================================================================
//
// SCALE NOTE:
//
// The spec calls for a 10k-instance synthetic. This test uses 10000
// instances (the full spec-targeted scale). The instances are
// stack-allocated as an array of XObjects; binding 10k entries into
// FXObjectArray's storage takes ~100k cycles total on contemporary
// hardware. Total test run time on a typical workstation is <100 ms.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectHotReloadCoordinator.h"
#include "XObject/FXObjectHotReloadState.h"
#include "XObject/XObject.h"
#include "XObject/XObjectKey.h"

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
    using ::XCore::XObject;
    using ::XCore::XObjectKey;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXObjectHotReloadCoordinator;
    using ::XCore::FHotReloadThreadEnumeration;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();
    FXObjectHotReloadCoordinator::Get().__ResetForTests();

    // -----------------------------------------------------------------
    // Set up OldClass + NewClass.
    // -----------------------------------------------------------------
    FClass OldClass(FName{}, /*Super=*/nullptr);
    FClass NewClass(FName{}, /*Super=*/nullptr);
    FXObjectAllocator::Get().RegisterClassPool(&OldClass);

    // -----------------------------------------------------------------
    // Allocate the 10k synthetic instances (spec-targeted scale).
    //
    // Use heap-allocated vector because 10k * sizeof(XObject) = ~560 KB
    // which fits comfortably in stack of any reasonable build BUT the
    // vector-on-heap posture is cleaner + matches the typical test
    // harness pattern.
    // -----------------------------------------------------------------
    constexpr ::int32 kInstanceCount = 10000;
    std::vector<XObject> Instances(kInstanceCount);

    // Capture an XObjectKey for each instance BEFORE the replacement.
    // Per spec §6.4: the key holds {InternalIndex, SerialNumber}
    // captured at this point in time.
    std::vector<XObjectKey> Keys;
    Keys.reserve(kInstanceCount);

    // Also capture each instance's underlying pointer for the
    // post-replacement comparison.
    std::vector<XObject*> ExpectedPointers;
    ExpectedPointers.reserve(kInstanceCount);

    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        ::uint32 Serial = 0;
        const ::int32 Index = FXObjectArray::Get().ReserveSlot(&Serial);
        Instances[I].ClassPrivate  = &OldClass;
        Instances[I].InternalIndex = Index;
        Instances[I].SerialNumber  = Serial;
        FXObjectArray::Get().BindObject(Index, &Instances[I]);

        // Construct XObjectKey from the live identity tuple.
        Keys.emplace_back(&Instances[I]);
        ExpectedPointers.push_back(&Instances[I]);
    }

    Check(FXObjectArray::Get().NumLive() == kInstanceCount,
          "FXObjectArray should hold 10000 live instances pre-replacement.");

    // -----------------------------------------------------------------
    // Pre-replacement: every key resolves to its instance.
    // -----------------------------------------------------------------
    ::int32 PreResolveCount = 0;
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        XObject* Resolved = Keys[I].Resolve();
        if (Resolved == ExpectedPointers[I])
        {
            ++PreResolveCount;
        }
    }
    Check(PreResolveCount == kInstanceCount,
          "Pre-replacement: all 10000 keys should resolve to their instances.");

    // -----------------------------------------------------------------
    // Run the cascade.
    // -----------------------------------------------------------------
    FHotReloadThreadEnumeration Enum{};
    FXObjectHotReloadCoordinator::Get().BeginHotReloadQuiesce(Enum);

    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            &OldClass, &NewClass);
        Check(R.has_value(),
              "ApplyClassReplacement should succeed on the 10k instances.");
    }

    // -----------------------------------------------------------------
    // POST-REPLACEMENT: the X6 acceptance check.
    //
    // 100% of pre-captured XObjectKeys MUST resolve to the same
    // XObject pointer they resolved to before. The pointer is
    // preserved per §6.4 non-moving GC commitment; the
    // {InternalIndex, SerialNumber} pair is unchanged across the
    // class swap.
    // -----------------------------------------------------------------
    ::int32 PostResolveCount = 0;
    ::int32 IdentityMatchCount = 0;
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        XObject* Resolved = Keys[I].Resolve();
        if (Resolved != nullptr)
        {
            ++PostResolveCount;
            if (Resolved == ExpectedPointers[I])
            {
                ++IdentityMatchCount;
            }
        }
    }

    Check(PostResolveCount == kInstanceCount,
          "Post-replacement: all 10000 keys must still resolve to non-null.");
    Check(IdentityMatchCount == kInstanceCount,
          "Post-replacement: all 10000 keys must resolve to the SAME "
          "XObject pointer (non-moving invariant).");

    // -----------------------------------------------------------------
    // The X6 acceptance: 100% survival rate.
    // -----------------------------------------------------------------
    const double SurvivalRate =
        100.0 * static_cast<double>(IdentityMatchCount)
              / static_cast<double>(kInstanceCount);
    Check(SurvivalRate >= 100.0,
          "X6 acceptance: XObjectKey resolution survival rate must be 100%.");

    std::cout << "X6 acceptance: " << IdentityMatchCount << "/" << kInstanceCount
              << " keys survived class replacement ("
              << SurvivalRate << "%).\n";

    // -----------------------------------------------------------------
    // Cleanup.
    // -----------------------------------------------------------------
    FXObjectHotReloadCoordinator::Get().__ResetForTests();
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        FXObjectArray::Get().FreeEntry(Instances[I].InternalIndex);
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectHotReloadCoordinator.XObjectKeyResolutionSurvives: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectHotReloadCoordinator.XObjectKeyResolutionSurvives: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
