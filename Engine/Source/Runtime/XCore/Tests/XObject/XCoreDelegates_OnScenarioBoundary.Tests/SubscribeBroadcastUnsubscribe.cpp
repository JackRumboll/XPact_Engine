// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnScenarioBoundary.Tests/SubscribeBroadcastUnsubscribe.cpp
// (XCoreXObject Rev 4 §3.6 + §4.7; Phase 5.i).
// =====================================================================
//
// Round-trip verification of the FOnScenarioBoundaryDelegate
// multicast surface. Mirror of OnClassReplaced.Tests/
// SubscribeUnsubscribe + FireOnReplace combined: Phase 5.i ships ONE
// test that exercises the full Subscribe/Broadcast/Unsubscribe
// life-cycle to match the delegate's smaller surface (and to keep
// per-suite file count bounded).
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XCoreDelegates_OnScenarioBoundary.h"
#include "Reflection/FName.h"

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
    using ::XCore::CoreDelegates::FOnScenarioBoundaryDelegate;
    using ::XCore::CoreDelegates::FScenarioBoundaryContext;
    using ::XCore::CoreDelegates::EScenarioBoundaryPhase;
    using ::XCore::CoreDelegates::GetOnScenarioBoundary;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    FOnScenarioBoundaryDelegate& Delegate = GetOnScenarioBoundary();
    Delegate.__ResetForTests();

    // -----------------------------------------------------------------
    // Test 1: initial state is empty.
    // -----------------------------------------------------------------
    Check(Delegate.GetSubscriberCount() == 0,
          "Initial subscriber count should be 0.");

    // -----------------------------------------------------------------
    // Test 2: Broadcast on empty delegate is a no-op.
    // -----------------------------------------------------------------
    {
        const FScenarioBoundaryContext Ctx{
            FName("EmptyTestScenario"),
            EScenarioBoundaryPhase::kPreUnload,
        };
        Delegate.Broadcast(Ctx);
        Check(true, "Broadcast on empty delegate did not crash.");
    }

    // -----------------------------------------------------------------
    // Test 3: Subscribe returns a non-zero handle; Broadcast fires it
    // with the correct payload.
    // -----------------------------------------------------------------
    int                       HitCount       = 0;
    FName                     ObservedName;
    EScenarioBoundaryPhase    ObservedPhase  = EScenarioBoundaryPhase::kPostLoad;

    auto Cb = [&HitCount, &ObservedName, &ObservedPhase](
        const FScenarioBoundaryContext& Ctx) noexcept
    {
        ++HitCount;
        ObservedName  = Ctx.ScenarioName;
        ObservedPhase = Ctx.Phase;
    };

    const auto H1 = Delegate.Subscribe(Cb);
    Check(H1 != FOnScenarioBoundaryDelegate::kInvalidHandle,
          "Subscribe should issue a non-invalid handle.");
    Check(Delegate.GetSubscriberCount() == 1,
          "Subscriber count should be 1 after Subscribe.");

    {
        const FScenarioBoundaryContext Ctx{
            FName("MainTestScenario"),
            EScenarioBoundaryPhase::kPreUnload,
        };
        Delegate.Broadcast(Ctx);
    }
    Check(HitCount == 1,
          "Callback should fire exactly once on Broadcast.");
    Check(ObservedName == FName("MainTestScenario"),
          "Callback should observe the ScenarioName from the payload.");
    Check(ObservedPhase == EScenarioBoundaryPhase::kPreUnload,
          "Callback should observe the Phase from the payload.");

    // -----------------------------------------------------------------
    // Test 4: A second subscriber receives the same Broadcast (multi-
    // cast). Handles are distinct.
    // -----------------------------------------------------------------
    int Hit2 = 0;
    auto Cb2 = [&Hit2](const FScenarioBoundaryContext&) noexcept
    {
        ++Hit2;
    };
    const auto H2 = Delegate.Subscribe(Cb2);
    Check(H2 != FOnScenarioBoundaryDelegate::kInvalidHandle,
          "Second Subscribe should issue a non-invalid handle.");
    Check(H1 != H2, "Subscribe handles should be unique.");
    Check(Delegate.GetSubscriberCount() == 2,
          "Subscriber count should be 2 after second Subscribe.");

    HitCount = 0;
    Hit2     = 0;
    {
        const FScenarioBoundaryContext Ctx{
            FName("BothFireScenario"),
            EScenarioBoundaryPhase::kPostLoad,
        };
        Delegate.Broadcast(Ctx);
    }
    Check(HitCount == 1, "Cb should fire on the second Broadcast.");
    Check(Hit2     == 1, "Cb2 should also fire on the second Broadcast.");
    Check(ObservedPhase == EScenarioBoundaryPhase::kPostLoad,
          "Cb should observe the new phase value.");

    // -----------------------------------------------------------------
    // Test 5: Unsubscribe returns true on first call; subsequent
    // Broadcast does NOT invoke the unsubscribed callback.
    // -----------------------------------------------------------------
    Check(Delegate.Unsubscribe(H1),
          "Unsubscribe(H1) should return true.");
    Check(Delegate.GetSubscriberCount() == 1,
          "Subscriber count should be 1 after Unsubscribe.");

    HitCount = 0;
    Hit2     = 0;
    {
        const FScenarioBoundaryContext Ctx{
            FName(),
            EScenarioBoundaryPhase::kPreUnload,
        };
        Delegate.Broadcast(Ctx);
    }
    Check(HitCount == 0,
          "Cb (unsubscribed) should NOT fire on subsequent Broadcast.");
    Check(Hit2 == 1,
          "Cb2 (still subscribed) should fire.");

    // -----------------------------------------------------------------
    // Test 6: Unsubscribe of an already-removed handle returns false.
    // -----------------------------------------------------------------
    Check(!Delegate.Unsubscribe(H1),
          "Second Unsubscribe(H1) should return false.");

    // -----------------------------------------------------------------
    // Test 7: Unsubscribe(kInvalidHandle) returns false.
    // -----------------------------------------------------------------
    Check(!Delegate.Unsubscribe(FOnScenarioBoundaryDelegate::kInvalidHandle),
          "Unsubscribe(kInvalidHandle) should return false.");

    // -----------------------------------------------------------------
    // Test 8: Final Unsubscribe; count drops to 0.
    // -----------------------------------------------------------------
    Delegate.Unsubscribe(H2);
    Check(Delegate.GetSubscriberCount() == 0,
          "Subscriber count should be 0 after final Unsubscribe.");

    Delegate.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "XCoreDelegates_OnScenarioBoundary."
                     "SubscribeBroadcastUnsubscribe: PASS\n";
        return 0;
    }
    std::cerr << "XCoreDelegates_OnScenarioBoundary."
                 "SubscribeBroadcastUnsubscribe: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
