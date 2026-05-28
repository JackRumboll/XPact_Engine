// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectHotReloadCoordinator.Tests/ApplyClassReplacement.cpp
// (XCoreXObject Rev 4 §9.2 + Phase 5.j).
// =====================================================================
//
// Verifies the per-class rebind operation:
//   * ApplyClassReplacement walks the FXObjectArray for instances of
//     OldClass and atomically rebinds ClassPrivate to NewClass.
//   * The OnClassReplaced delegate fires with (OldClass, NewClass).
//   * Per-cascade running totals (replacedClassCount + total
//     InstancesRebound) update correctly.
//   * Pre-condition checks: nullptr / identity / quiesce-not-active
//     return Err with the expected variant.
//
// Uses STACK XObjects (no allocator allocation). The mark cycle and
// allocator sub-pool rebind are exercised by other tests; this test
// focuses on the class-rebind walk.
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
#include "XObject/XCoreDelegates_OnClassReplaced.h"
#include "XObject/XCoreDelegates_OnHotReload.h"
#include "XObject/XObject.h"

#include <array>
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
    using ::XCore::XObject;
    using ::XCore::FXObjectArray;
    using ::XCore::FXObjectAllocator;
    using ::XCore::FXObjectCollector;
    using ::XCore::FXObjectGCCardTable;
    using ::XCore::FXObjectHotReloadCoordinator;
    using ::XCore::FHotReloadThreadEnumeration;
    using ::XCore::FHotReloadError;
    using ::XCore::EObjectFlags;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;
    using ::XCore::CoreDelegates::GetOnClassReplaced;
    using ::XCore::CoreDelegates::FOnClassReplacedDelegate;

    ::XCore::HAL::FMemory::__Init();

    FXObjectArray::Get().__ResetForTests();
    FXObjectAllocator::Get().__ResetForTests();
    FXObjectGCCardTable::Get().__ResetForTests();
    FXObjectCollector::Get().__ResetForTests();
    FXObjectHotReloadCoordinator::Get().__ResetForTests();
    GetOnClassReplaced().__ResetForTests();

    // -----------------------------------------------------------------
    // Set up two FClass instances (stack storage; FClass is the
    // descriptor type, NOT a heap-allocated object).
    // -----------------------------------------------------------------
    FClass OldClass(FName{}, /*Super=*/nullptr);
    FClass NewClass(FName{}, /*Super=*/nullptr);
    // OldClass and NewClass have distinct addresses; the rebind walk
    // compares pointers, so this is sufficient.

    // Register the OldClass sub-pool so RebindClassPool has something
    // to rebind (NewClass auto-registers).
    FXObjectAllocator::Get().RegisterClassPool(&OldClass);

    // -----------------------------------------------------------------
    // Construct 100 stack XObjects bound to OldClass via the
    // FXObjectArray (mirror of how XObject allocator-allocates +
    // BindObjects in production).
    // -----------------------------------------------------------------
    constexpr ::int32 kInstanceCount = 100;
    std::array<XObject, kInstanceCount> Instances{};
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        ::uint32 Serial = 0;
        const ::int32 Index = FXObjectArray::Get().ReserveSlot(&Serial);
        Instances[I].ClassPrivate  = &OldClass;
        Instances[I].InternalIndex = Index;
        Instances[I].SerialNumber  = Serial;
        FXObjectArray::Get().BindObject(Index, &Instances[I]);
    }

    Check(FXObjectArray::Get().NumLive() == kInstanceCount,
          "FXObjectArray should have 100 live instances after setup.");

    // -----------------------------------------------------------------
    // Pre-condition error checks (BEFORE opening the quiesce window).
    // -----------------------------------------------------------------
    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            nullptr, &NewClass);
        Check(!R.has_value() && R.error() == FHotReloadError::kNullOldClass,
              "ApplyClassReplacement(nullptr, NewClass) should return kNullOldClass.");
    }
    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            &OldClass, nullptr);
        Check(!R.has_value() && R.error() == FHotReloadError::kNullNewClass,
              "ApplyClassReplacement(OldClass, nullptr) should return kNullNewClass.");
    }
    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            &OldClass, &OldClass);
        Check(!R.has_value() && R.error() == FHotReloadError::kIdentityReplacement,
              "ApplyClassReplacement(OldClass, OldClass) should return kIdentityReplacement.");
    }
    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            &OldClass, &NewClass);
        Check(!R.has_value() && R.error() == FHotReloadError::kQuiesceNotActive,
              "ApplyClassReplacement without Begin should return kQuiesceNotActive.");
    }

    // -----------------------------------------------------------------
    // Open the quiesce window.
    // -----------------------------------------------------------------
    FHotReloadThreadEnumeration Enum{};
    FXObjectHotReloadCoordinator::Get().BeginHotReloadQuiesce(Enum);

    // -----------------------------------------------------------------
    // Subscribe to OnClassReplaced. The delegate fires once per
    // ApplyClassReplacement call.
    // -----------------------------------------------------------------
    int ReplaceHitCount = 0;
    const FClass* ObservedOld = nullptr;
    const FClass* ObservedNew = nullptr;
    const auto Handle = GetOnClassReplaced().Subscribe(
        [&ReplaceHitCount, &ObservedOld, &ObservedNew](
            const FClass* Old, const FClass* New) noexcept
        {
            ++ReplaceHitCount;
            ObservedOld = Old;
            ObservedNew = New;
        });
    Check(Handle != FOnClassReplacedDelegate::kInvalidHandle,
          "Subscribe should issue a valid handle.");

    // -----------------------------------------------------------------
    // Apply the replacement.
    // -----------------------------------------------------------------
    {
        auto R = FXObjectHotReloadCoordinator::Get().ApplyClassReplacement(
            &OldClass, &NewClass);
        Check(R.has_value(),
              "ApplyClassReplacement should succeed with valid args during quiesce.");
    }

    // -----------------------------------------------------------------
    // Verify every instance's ClassPrivate was rebound to NewClass.
    // -----------------------------------------------------------------
    ::int32 ReboundCount = 0;
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        if (Instances[I].ClassPrivate == &NewClass)
        {
            ++ReboundCount;
        }
        // No instance should still point at OldClass.
        Check(Instances[I].ClassPrivate != &OldClass,
              "Instance ClassPrivate should NOT still point at OldClass after rebind.");
    }
    Check(ReboundCount == kInstanceCount,
          "All instances should have ClassPrivate == NewClass after rebind.");

    // -----------------------------------------------------------------
    // Verify the HotReloadReplaced flag was set on every instance.
    // -----------------------------------------------------------------
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        Check(Instances[I].HasAnyFlags(EObjectFlags::HotReloadReplaced),
              "Instance should carry HotReloadReplaced flag post-rebind.");
    }

    // -----------------------------------------------------------------
    // OnClassReplaced delegate fired once with the right pointers.
    // -----------------------------------------------------------------
    Check(ReplaceHitCount == 1,
          "OnClassReplaced should fire exactly once per ApplyClass call.");
    Check(ObservedOld == &OldClass,
          "OnClassReplaced should observe OldClass.");
    Check(ObservedNew == &NewClass,
          "OnClassReplaced should observe NewClass.");

    // -----------------------------------------------------------------
    // Per-cascade running totals.
    // -----------------------------------------------------------------
    Check(FXObjectHotReloadCoordinator::Get().GetReplacedClassCount() == 1,
          "Replaced class count should be 1 after one ApplyClassReplacement.");
    Check(FXObjectHotReloadCoordinator::Get().GetTotalInstancesRebound() == kInstanceCount,
          "Total instances rebound should match the instance count.");

    // -----------------------------------------------------------------
    // Cleanup.
    // -----------------------------------------------------------------
    GetOnClassReplaced().Unsubscribe(Handle);
    FXObjectHotReloadCoordinator::Get().__ResetForTests();

    // Tear down the entries so FXObjectArray's __ResetForTests on the
    // next test doesn't observe stale pointers.
    for (::int32 I = 0; I < kInstanceCount; ++I)
    {
        FXObjectArray::Get().FreeEntry(Instances[I].InternalIndex);
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectHotReloadCoordinator.ApplyClassReplacement: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectHotReloadCoordinator.ApplyClassReplacement: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
