// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnClassReplaced.Tests/SubscribeUnsubscribe.cpp
// (XCoreXObject Rev 4 §10.10.1 + Phase 5.k).
// =====================================================================
//
// Verifies the multicast delegate's subscription bookkeeping:
//   * Subscribe returns a non-zero handle.
//   * GetSubscriberCount tracks Subscribe + Unsubscribe.
//   * Unsubscribe with the issued handle returns true; second
//     Unsubscribe with the same handle returns false.
//   * Unsubscribe with kInvalidHandle returns false.
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

    ::XCore::HAL::FMemory::__Init();

    FOnClassReplacedDelegate& Delegate = GetOnClassReplaced();
    Delegate.__ResetForTests();

    Check(Delegate.GetSubscriberCount() == 0,
          "Initial subscriber count should be 0.");

    // -----------------------------------------------------------------
    // Test 1: Subscribe returns a non-zero handle.
    // -----------------------------------------------------------------
    int LocalCounter = 0;
    auto Cb1 = [&LocalCounter](
        const ::XCore::Reflect::FClass*,
        const ::XCore::Reflect::FClass*) noexcept
    {
        ++LocalCounter;
    };
    const auto H1 = Delegate.Subscribe(Cb1);
    Check(H1 != FOnClassReplacedDelegate::kInvalidHandle,
          "Subscribe should return a non-invalid handle.");
    Check(Delegate.GetSubscriberCount() == 1,
          "Subscriber count should be 1 after Subscribe.");

    // -----------------------------------------------------------------
    // Test 2: Second Subscribe -> count goes to 2; handles distinct.
    // -----------------------------------------------------------------
    auto Cb2 = [&LocalCounter](
        const ::XCore::Reflect::FClass*,
        const ::XCore::Reflect::FClass*) noexcept
    {
        LocalCounter += 10;
    };
    const auto H2 = Delegate.Subscribe(Cb2);
    Check(H2 != FOnClassReplacedDelegate::kInvalidHandle,
          "Second Subscribe should return a non-invalid handle.");
    Check(H1 != H2,
          "Each Subscribe should return a UNIQUE handle.");
    Check(Delegate.GetSubscriberCount() == 2,
          "Subscriber count should be 2 after second Subscribe.");

    // -----------------------------------------------------------------
    // Test 3: Unsubscribe returns true on first call.
    // -----------------------------------------------------------------
    const bool Unsub1 = Delegate.Unsubscribe(H1);
    Check(Unsub1, "Unsubscribe(H1) should return true on first call.");
    Check(Delegate.GetSubscriberCount() == 1,
          "Subscriber count should be 1 after first Unsubscribe.");

    // -----------------------------------------------------------------
    // Test 4: Re-Unsubscribe same handle returns false.
    // -----------------------------------------------------------------
    const bool Unsub1Again = Delegate.Unsubscribe(H1);
    Check(!Unsub1Again,
          "Re-Unsubscribe(H1) should return false (already gone).");
    Check(Delegate.GetSubscriberCount() == 1,
          "Subscriber count unchanged after re-Unsubscribe.");

    // -----------------------------------------------------------------
    // Test 5: Unsubscribe(kInvalidHandle) returns false.
    // -----------------------------------------------------------------
    const bool UnsubInvalid =
        Delegate.Unsubscribe(FOnClassReplacedDelegate::kInvalidHandle);
    Check(!UnsubInvalid,
          "Unsubscribe(kInvalidHandle) should return false.");

    // -----------------------------------------------------------------
    // Test 6: Unsubscribe(H2) cleans up.
    // -----------------------------------------------------------------
    Delegate.Unsubscribe(H2);
    Check(Delegate.GetSubscriberCount() == 0,
          "Subscriber count should be 0 after final Unsubscribe.");

    Delegate.__ResetForTests();

    if (g_FailureCount == 0)
    {
        std::cout << "XCoreDelegates_OnClassReplaced.SubscribeUnsubscribe: PASS\n";
        return 0;
    }
    std::cerr << "XCoreDelegates_OnClassReplaced.SubscribeUnsubscribe: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
