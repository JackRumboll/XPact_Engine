// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// X6_HotReloadXObjectKeySurvives.cpp -- Foundation Prototype X6
// acceptance: hot-reload class replacement preserves XObjectKey
// resolution for 100% of instances.
// =====================================================================
//
// X6 acceptance (spec §13.2):
//   "hot-reload class replacement preserves XObjectKey resolution for
//    100% of instances in a synthetic 10k-instance test."
//
// This Phase 5.l acceptance wraps the Phase 5.j FXObjectHotReloadCoord
// inator.Tests/XObjectKeyResolutionSurvives.cpp test which ALREADY
// ships the canonical X6 verification at the 10k-instance scale (spec
// target). The Foundation Prototype harness re-runs the same scenario
// under the X6 banner so the per-criterion test runner output reflects
// X6 coverage explicitly.
//
// JUDGEMENT CALL (Phase 5.l X6 wrapper). Re-using the existing test
// scenario (rather than re-implementing it in a new TU) is the
// principled choice per the Prime Directive: the Phase 5.j test IS
// the X6 implementation. The wrapper acknowledges this without
// inflating the codebase.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectHotReloadCoordinator.h"
#include "XObject/FXObjectHotReloadState.h"
#include "XObject/XObject.h"
#include "XObject/XObjectKey.h"

#include <vector>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::FHotReloadThreadEnumeration;
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectHotReloadCoordinator;
    using ::XCore::XObject;
    using ::XCore::XObjectKey;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    Phase5L::ResetAllForTests();

    // -----------------------------------------------------------------
    // Build OldClass + NewClass (matching the Phase 5.j test pattern).
    // -----------------------------------------------------------------
    FClass OldClass(FName(), nullptr);
    FClass NewClass(FName(), nullptr);
    FXObjectAllocator::Get().RegisterClassPool(&OldClass);

    constexpr ::int32 kInstanceCount = 10000;
    std::vector<XObject> Instances(kInstanceCount);
    std::vector<XObjectKey> Keys;
    Keys.reserve(kInstanceCount);
    std::vector<XObject*> ExpectedPointers;
    ExpectedPointers.reserve(kInstanceCount);

    // -----------------------------------------------------------------
    // Allocate 10k synthetic instances.
    // -----------------------------------------------------------------
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        ::uint32 Serial = 0;
        const ::int32 Index = FXObjectArray::Get().ReserveSlot(&Serial);
        Instances[I].ClassPrivate  = &OldClass;
        Instances[I].InternalIndex = Index;
        Instances[I].SerialNumber  = Serial;
        FXObjectArray::Get().BindObject(Index, &Instances[I]);

        Keys.emplace_back(&Instances[I]);
        ExpectedPointers.push_back(&Instances[I]);
    }

    P5L_CHECK(FXObjectArray::Get().NumLive() == kInstanceCount,
              "X6: FXObjectArray should hold 10000 live instances "
              "pre-replacement");

    // -----------------------------------------------------------------
    // Pre-replacement: every Key resolves to its instance.
    // -----------------------------------------------------------------
    ::int32 PreResolveCount = 0;
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        if (Keys[I].Resolve() == ExpectedPointers[I])
        {
            ++PreResolveCount;
        }
    }
    P5L_CHECK(PreResolveCount == kInstanceCount,
              "X6: pre-replacement Key resolution incomplete");

    // -----------------------------------------------------------------
    // Run the cascade (BeginQuiesce + ApplyClassReplacement).
    // -----------------------------------------------------------------
    FHotReloadThreadEnumeration Enum{};
    FXObjectHotReloadCoordinator::Get().BeginHotReloadQuiesce(Enum);
    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            &OldClass, &NewClass);
        P5L_CHECK(R.has_value(),
                  "X6: ApplyClassReplacement failed on the 10k instances");
    }

    // -----------------------------------------------------------------
    // X6 ACCEPTANCE: post-replacement, 100% of Keys still resolve
    // to the same XObject pointer (non-moving GC invariant per spec
    // §6.4 + Rev 2 FIX-A-MED-23).
    // -----------------------------------------------------------------
    ::int32 IdentityMatchCount = 0;
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        XObject* Resolved = Keys[I].Resolve();
        if (Resolved == ExpectedPointers[I])
        {
            ++IdentityMatchCount;
        }
    }
    const double SurvivalRate =
        100.0 * static_cast<double>(IdentityMatchCount)
              / static_cast<double>(kInstanceCount);

    P5L_CHECK(IdentityMatchCount == kInstanceCount,
              "X6: post-replacement Key resolution survival rate is "
              "below 100%");

    std::cout << "X6: " << IdentityMatchCount << "/" << kInstanceCount
              << " keys survived class replacement ("
              << SurvivalRate << "%).\n";

    // Cleanup.
    FXObjectHotReloadCoordinator::Get().__ResetForTests();
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        FXObjectArray::Get().FreeEntry(Instances[I].InternalIndex);
    }

    return P5L_REPORT_PASS("FoundationPrototype.X6_HotReloadXObjectKeySurvives");
}
