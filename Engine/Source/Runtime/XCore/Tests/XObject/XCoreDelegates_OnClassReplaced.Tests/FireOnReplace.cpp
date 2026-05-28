// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnClassReplaced.Tests/FireOnReplace.cpp
// (XCoreXObject Rev 4 §10.10.1 + Phase 5.k).
// =====================================================================
//
// Verifies the Broadcast fire-path:
//   * Manually fire Broadcast(old, new); subscribed callback receives
//     the call with the correct OldClass / NewClass pointers.
//   * Multiple subscribers all receive the call.
//   * After Unsubscribe, the callback does NOT receive subsequent
//     Broadcasts.
//   * Broadcast on empty delegate is a no-op.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XCoreDelegates_OnClassReplaced.h"
#include "Reflection/FClass.h"

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
    using ::XCore::CoreDelegates::FOnClassReplacedDelegate;
    using ::XCore::CoreDelegates::GetOnClassReplaced;
    using ::XCore::Reflect::FClass;

    ::XCore::HAL::FMemory::__Init();

    FOnClassReplacedDelegate& Delegate = GetOnClassReplaced();
    Delegate.__ResetForTests();

    // -----------------------------------------------------------------
    // Test 1: Broadcast on empty delegate -> no callbacks invoked,
    // no crash.
    // -----------------------------------------------------------------
    {
        Delegate.Broadcast(nullptr, nullptr);
        Check(true, "Broadcast on empty delegate is no-op.");
    }

    // -----------------------------------------------------------------
    // Test 2: Subscribe one callback; Broadcast fires it.
    // -----------------------------------------------------------------
    int  HitCount = 0;
    const FClass* ObservedOld = nullptr;
    const FClass* ObservedNew = nullptr;

    auto Cb = [&HitCount, &ObservedOld, &ObservedNew](
        const FClass* OldClass, const FClass* NewClass) noexcept
    {
        ++HitCount;
        ObservedOld = OldClass;
        ObservedNew = NewClass;
    };

    const auto Handle = Delegate.Subscribe(Cb);
    Check(Handle != FOnClassReplacedDelegate::kInvalidHandle,
          "Subscribe should issue a non-invalid handle.");

    // Use fake (non-null) FClass pointers; the delegate body does not
    // dereference them, so any non-null address works.
    const FClass* const FakeOld = reinterpret_cast<const FClass*>(0xAA00ull);
    const FClass* const FakeNew = reinterpret_cast<const FClass*>(0xBB00ull);

    Delegate.Broadcast(FakeOld, FakeNew);

    Check(HitCount == 1,
          "Callback should be invoked exactly once after Broadcast.");
    Check(ObservedOld == FakeOld,
          "Callback should observe the OldClass pointer.");
    Check(ObservedNew == FakeNew,
          "Callback should observe the NewClass pointer.");

    // -----------------------------------------------------------------
    // Test 3: Multiple subscribers all receive the same Broadcast.
    // -----------------------------------------------------------------
    int Hit2 = 0;
    auto Cb2 = [&Hit2](const FClass*, const FClass*) noexcept
    {
        ++Hit2;
    };
    const auto H2 = Delegate.Subscribe(Cb2);
    Check(H2 != Handle, "Each Subscribe should issue a unique handle.");

    HitCount = 0;
    Hit2     = 0;
    Delegate.Broadcast(FakeOld, FakeNew);
    Check(HitCount == 1,
          "Cb (first subscriber) should fire once.");
    Check(Hit2 == 1,
          "Cb2 (second subscriber) should fire once.");

    // -----------------------------------------------------------------
    // Test 4: Unsubscribe one; subsequent Broadcast does not invoke it.
    // -----------------------------------------------------------------
    Delegate.Unsubscribe(Handle);
    HitCount = 0;
    Hit2     = 0;
    Delegate.Broadcast(FakeOld, FakeNew);
    Check(HitCount == 0,
          "Cb (after Unsubscribe) should NOT fire.");
    Check(Hit2 == 1,
          "Cb2 (still subscribed) should fire.");

    // -----------------------------------------------------------------
    // Test 5: Multiple Broadcast calls accumulate.
    // -----------------------------------------------------------------
    Hit2 = 0;
    Delegate.Broadcast(FakeOld, FakeNew);
    Delegate.Broadcast(FakeOld, FakeNew);
    Delegate.Broadcast(FakeOld, FakeNew);
    Check(Hit2 == 3,
          "Cb2 should accumulate fires (3 Broadcasts -> 3 hits).");

    // -----------------------------------------------------------------
    // Test 6: Re-entry safety -- a callback that Subscribes during
    // its own invocation does NOT fire in the current Broadcast.
    // -----------------------------------------------------------------
    Delegate.Unsubscribe(H2);
    Delegate.__ResetForTests();
    Check(Delegate.GetSubscriberCount() == 0,
          "Pre-reentry-test state is clean.");

    int  Cb3Hits   = 0;
    int  CbNewHits = 0;
    auto CbReentry = [&Cb3Hits, &CbNewHits, &Delegate](
        const FClass*, const FClass*) noexcept
    {
        ++Cb3Hits;
        if (Cb3Hits == 1)
        {
            // First invocation: subscribe a NEW callback. The new
            // callback should NOT fire in the current Broadcast (the
            // snapshot-then-release pattern means the current iteration
            // already locked in the visible set).
            auto NewCb = [&CbNewHits](const FClass*, const FClass*) noexcept
            {
                ++CbNewHits;
            };
            (void)Delegate.Subscribe(NewCb);
        }
    };
    (void)Delegate.Subscribe(CbReentry);
    Delegate.Broadcast(FakeOld, FakeNew);
    Check(Cb3Hits == 1,
          "Reentry-subscriber fires once in current broadcast.");
    Check(CbNewHits == 0,
          "Newly-subscribed callback does NOT fire in current broadcast.");

    // Subsequent broadcast DOES fire the newly-subscribed callback.
    Delegate.Broadcast(FakeOld, FakeNew);
    Check(CbNewHits == 1,
          "Newly-subscribed callback fires on the NEXT broadcast.");

    Delegate.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "XCoreDelegates_OnClassReplaced.FireOnReplace: PASS\n";
        return 0;
    }
    std::cerr << "XCoreDelegates_OnClassReplaced.FireOnReplace: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
