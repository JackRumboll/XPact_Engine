// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectHotReloadCoordinator.Tests/BeginQuiesce.cpp
// (XCoreXObject Rev 4 §9.2 + Phase 5.j).
// =====================================================================
//
// Verifies the BeginHotReloadQuiesce side-effects:
//   * g_XHotReloadInProgress transitions to true.
//   * g_XGCAcceptDrains transitions to false (SATB drain paused).
//   * IsQuiesceActive() returns true.
//   * The OnHotReloadStart delegate fires exactly once with an empty
//     FHotReloadContext.
//   * ParkHook fires for every active role in the enumeration; nullptr
//     slots are skipped.
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

    // Park-hook counters. Globals because ParkHook is a plain function
    // pointer; capturing a lambda is not possible.
    int g_GameThreadParkCount   = 0;
    int g_RenderThreadParkCount = 0;
    int g_AudioThreadParkCount  = 0;

    void GameThreadPark()   noexcept { ++g_GameThreadParkCount; }
    void RenderThreadPark() noexcept { ++g_RenderThreadParkCount; }
    void AudioThreadPark()  noexcept { ++g_AudioThreadParkCount; }
}

int main()
{
    using ::XCore::FXObjectHotReloadCoordinator;
    using ::XCore::FHotReloadThreadEnumeration;
    using ::XCore::EHotReloadThreadRole;
    using ::XCore::IsHotReloadInProgress;
    using ::XCore::g_XHotReloadInProgress;
    using ::XCore::g_XGCAcceptDrains;
    using ::XCore::CoreDelegates::FOnHotReloadDelegate;
    using ::XCore::CoreDelegates::FHotReloadContext;
    using ::XCore::CoreDelegates::GetOnHotReloadStart;

    ::XCore::HAL::FMemory::__Init();

    // Reset all dependent singletons to a clean baseline.
    ::XCore::FXObjectArray::Get().__ResetForTests();
    ::XCore::FXObjectAllocator::Get().__ResetForTests();
    ::XCore::FXObjectGCCardTable::Get().__ResetForTests();
    ::XCore::FXObjectCollector::Get().__ResetForTests();
    FXObjectHotReloadCoordinator::Get().__ResetForTests();
    GetOnHotReloadStart().__ResetForTests();

    // -----------------------------------------------------------------
    // Pre-condition baselines.
    // -----------------------------------------------------------------
    Check(!IsHotReloadInProgress(),
          "Pre: g_XHotReloadInProgress should be false.");
    Check(g_XGCAcceptDrains.load(::std::memory_order_acquire) == true,
          "Pre: g_XGCAcceptDrains should be true.");
    Check(!FXObjectHotReloadCoordinator::Get().IsQuiesceActive(),
          "Pre: coordinator IsQuiesceActive should be false.");

    // -----------------------------------------------------------------
    // Subscribe a callback to verify OnHotReloadStart fires.
    // -----------------------------------------------------------------
    int StartHitCount  = 0;
    ::std::int64_t ObservedReplaceCount = -1;
    ::std::int64_t ObservedInstanceCount = -1;
    const auto Handle = GetOnHotReloadStart().Subscribe(
        [&StartHitCount, &ObservedReplaceCount, &ObservedInstanceCount](
            const FHotReloadContext& Ctx) noexcept
        {
            ++StartHitCount;
            ObservedReplaceCount  = Ctx.ReplacedClassCount;
            ObservedInstanceCount = Ctx.TotalInstancesRebound;
        });
    Check(Handle != FOnHotReloadDelegate::kInvalidHandle,
          "Subscribe should issue a valid handle.");

    // -----------------------------------------------------------------
    // Set up the FHotReloadThreadEnumeration with three Park hooks.
    // GameThread + RenderThread + AudioThread have ParkHooks; other
    // roles are nullptr (skipped).
    // -----------------------------------------------------------------
    FHotReloadThreadEnumeration Enum;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kGameThread)].ParkHook   = &GameThreadPark;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kRenderThread)].ParkHook = &RenderThreadPark;
    Enum.Hooks[static_cast<::std::size_t>(EHotReloadThreadRole::kAudioThread)].ParkHook  = &AudioThreadPark;
    // The other 7 slots remain nullptr.

    // -----------------------------------------------------------------
    // Open the quiesce window.
    // -----------------------------------------------------------------
    FXObjectHotReloadCoordinator::Get().BeginHotReloadQuiesce(Enum);

    // -----------------------------------------------------------------
    // Post-quiesce assertions.
    // -----------------------------------------------------------------
    Check(IsHotReloadInProgress(),
          "Post-Begin: g_XHotReloadInProgress should be true.");
    Check(g_XGCAcceptDrains.load(::std::memory_order_acquire) == false,
          "Post-Begin: g_XGCAcceptDrains should be false (SATB paused).");
    Check(FXObjectHotReloadCoordinator::Get().IsQuiesceActive(),
          "Post-Begin: coordinator IsQuiesceActive should be true.");

    // Per-cascade counters reset to 0.
    Check(FXObjectHotReloadCoordinator::Get().GetReplacedClassCount() == 0,
          "Post-Begin: replaced class count should be 0.");
    Check(FXObjectHotReloadCoordinator::Get().GetTotalInstancesRebound() == 0,
          "Post-Begin: total instances rebound should be 0.");

    // ParkHook fires for the three active roles.
    Check(g_GameThreadParkCount   == 1, "GameThreadPark should fire exactly once.");
    Check(g_RenderThreadParkCount == 1, "RenderThreadPark should fire exactly once.");
    Check(g_AudioThreadParkCount  == 1, "AudioThreadPark should fire exactly once.");

    // OnHotReloadStart fired exactly once with empty context.
    Check(StartHitCount == 1,
          "OnHotReloadStart should fire exactly once.");
    Check(ObservedReplaceCount == 0,
          "OnHotReloadStart context: ReplacedClassCount should be 0.");
    Check(ObservedInstanceCount == 0,
          "OnHotReloadStart context: TotalInstancesRebound should be 0.");

    // -----------------------------------------------------------------
    // Cleanup: reset for next test. (We don't run FinishHotReload
    // Cascade here because that's a separate test; __ResetForTests
    // clears the flags directly.)
    // -----------------------------------------------------------------
    FXObjectHotReloadCoordinator::Get().__ResetForTests();
    GetOnHotReloadStart().Unsubscribe(Handle);

    Check(!IsHotReloadInProgress(),
          "Post-reset: g_XHotReloadInProgress should be false.");
    Check(g_XGCAcceptDrains.load(::std::memory_order_acquire) == true,
          "Post-reset: g_XGCAcceptDrains should be true.");

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectHotReloadCoordinator.BeginQuiesce: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectHotReloadCoordinator.BeginQuiesce: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
