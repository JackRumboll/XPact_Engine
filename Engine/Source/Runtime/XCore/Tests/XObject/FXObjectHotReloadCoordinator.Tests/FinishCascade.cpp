// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectHotReloadCoordinator.Tests/FinishCascade.cpp
// (XCoreXObject Rev 4 §9.2 + Phase 5.j).
// =====================================================================
//
// Verifies the FinishHotReloadCascade side-effects:
//   * g_XHotReloadInProgress transitions back to false.
//   * g_XGCAcceptDrains transitions back to true (SATB drain resumed).
//   * IsQuiesceActive() returns false post-finish.
//   * The OnHotReloadComplete delegate fires exactly once with the
//     correct cascade totals.
//   * UnparkHooks fire for each role whose ParkHook was registered at
//     Begin.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/FXObjectArray.h"
#include "XObject/FXObjectAllocator.h"
#include "XObject/FXObjectCollector.h"
#include "XObject/FXObjectGCCardTable.h"
#include "XObject/FXObjectHotReloadCoordinator.h"
#include "XObject/FXObjectHotReloadState.h"
#include "XObject/XCoreDelegates_OnHotReload.h"
#include "XObject/XGCConcurrentState.h"

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

    int g_GameThreadParkCount     = 0;
    int g_GameThreadUnparkCount   = 0;
    int g_RenderThreadParkCount   = 0;
    int g_RenderThreadUnparkCount = 0;

    void GameThreadPark()     noexcept { ++g_GameThreadParkCount; }
    void GameThreadUnpark()   noexcept { ++g_GameThreadUnparkCount; }
    void RenderThreadPark()   noexcept { ++g_RenderThreadParkCount; }
    void RenderThreadUnpark() noexcept { ++g_RenderThreadUnparkCount; }
}

int main()
{
    using ::XCore::FXObjectHotReloadCoordinator;
    using ::XCore::FHotReloadThreadEnumeration;
    using ::XCore::EHotReloadThreadRole;
    using ::XCore::FClassReplacementMap;
    using ::XCore::IsHotReloadInProgress;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::CoreDelegates::FHotReloadContext;
    using ::XCore::CoreDelegates::FOnHotReloadDelegate;
    using ::XCore::CoreDelegates::GetOnHotReloadComplete;

    ::XCore::HAL::FMemory::__Init();

    ::XCore::FXObjectArray::Get().__ResetForTests();
    ::XCore::FXObjectAllocator::Get().__ResetForTests();
    ::XCore::FXObjectGCCardTable::Get().__ResetForTests();
    ::XCore::FXObjectCollector::Get().__ResetForTests();
    FXObjectHotReloadCoordinator::Get().__ResetForTests();
    GetOnHotReloadComplete().__ResetForTests();

    // -----------------------------------------------------------------
    // Subscribe to OnHotReloadComplete to capture the payload.
    // -----------------------------------------------------------------
    int CompleteHitCount = 0;
    ::std::int64_t ObservedReplaceCount  = -1;
    ::std::int64_t ObservedInstanceCount = -1;
    const auto Handle = GetOnHotReloadComplete().Subscribe(
        [&CompleteHitCount, &ObservedReplaceCount, &ObservedInstanceCount](
            const FHotReloadContext& Ctx) noexcept
        {
            ++CompleteHitCount;
            ObservedReplaceCount  = Ctx.ReplacedClassCount;
            ObservedInstanceCount = Ctx.TotalInstancesRebound;
        });
    Check(Handle != FOnHotReloadDelegate::kInvalidHandle,
          "Subscribe should issue a valid handle.");

    // -----------------------------------------------------------------
    // Set up the enumeration with Park + Unpark hook pairs.
    // -----------------------------------------------------------------
    FHotReloadThreadEnumeration Enum;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kGameThread)].ParkHook     = &GameThreadPark;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kGameThread)].UnparkHook   = &GameThreadUnpark;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kRenderThread)].ParkHook   = &RenderThreadPark;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kRenderThread)].UnparkHook = &RenderThreadUnpark;

    // -----------------------------------------------------------------
    // Open the quiesce window (verify side-effects).
    // -----------------------------------------------------------------
    FXObjectHotReloadCoordinator::Get().BeginHotReloadQuiesce(Enum);

    Check(IsHotReloadInProgress(), "Post-Begin: in-progress flag should be true.");
    Check(g_XGCAcceptDrains.load(::std::memory_order_acquire) == false,
          "Post-Begin: SATB drain should be paused.");
    Check(g_GameThreadParkCount   == 1, "Post-Begin: GameThreadPark should fire once.");
    Check(g_RenderThreadParkCount == 1, "Post-Begin: RenderThreadPark should fire once.");
    Check(g_GameThreadUnparkCount   == 0, "Post-Begin: GameThreadUnpark should NOT fire yet.");
    Check(g_RenderThreadUnparkCount == 0, "Post-Begin: RenderThreadUnpark should NOT fire yet.");

    // -----------------------------------------------------------------
    // Close the cascade. The empty FClassReplacementMap is the
    // XLiveCoding-side payload; for this test the cascade walked no
    // classes (no instances bound to a specific FClass), so the
    // running totals remain at 0 + 0.
    // -----------------------------------------------------------------
    FClassReplacementMap Map{};
    Map.Entries = nullptr;
    Map.Count   = 0;
    FXObjectHotReloadCoordinator::Get().FinishHotReloadCascade(Map);

    // -----------------------------------------------------------------
    // Post-Finish assertions.
    // -----------------------------------------------------------------
    Check(!IsHotReloadInProgress(),
          "Post-Finish: in-progress flag should be false.");
    Check(g_XGCAcceptDrains.load(::std::memory_order_acquire) == true,
          "Post-Finish: SATB drain should be resumed (g_XGCAcceptDrains true).");
    Check(!FXObjectHotReloadCoordinator::Get().IsQuiesceActive(),
          "Post-Finish: IsQuiesceActive should be false.");

    // UnparkHooks fired for the two active roles.
    Check(g_GameThreadUnparkCount   == 1, "Post-Finish: GameThreadUnpark should fire once.");
    Check(g_RenderThreadUnparkCount == 1, "Post-Finish: RenderThreadUnpark should fire once.");

    // OnHotReloadComplete fired exactly once with the running totals.
    Check(CompleteHitCount == 1,
          "Post-Finish: OnHotReloadComplete should fire exactly once.");
    Check(ObservedReplaceCount == 0,
          "Post-Finish: ReplacedClassCount == 0 (no ApplyClass in this test).");
    Check(ObservedInstanceCount == 0,
          "Post-Finish: TotalInstancesRebound == 0.");

    // -----------------------------------------------------------------
    // Double-Finish is a no-op (defensive check in the coordinator).
    // -----------------------------------------------------------------
    const int PrevHits = CompleteHitCount;
    FXObjectHotReloadCoordinator::Get().FinishHotReloadCascade(Map);
    Check(CompleteHitCount == PrevHits,
          "Double-Finish should be a no-op; OnHotReloadComplete should NOT re-fire.");

    // -----------------------------------------------------------------
    // Cleanup.
    // -----------------------------------------------------------------
    GetOnHotReloadComplete().Unsubscribe(Handle);
    FXObjectHotReloadCoordinator::Get().__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectHotReloadCoordinator.FinishCascade: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectHotReloadCoordinator.FinishCascade: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
