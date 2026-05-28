// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XCoreDelegates_OnHotReload.Tests/SubscribeBroadcast.cpp
// (XCoreXObject Rev 4 §9.2 + Phase 5.j).
// =====================================================================
//
// Verifies the three hot-reload bracket delegates (Start, Complete,
// Abort) share a uniform multicast delegate shape:
//   * Subscribe returns a valid handle.
//   * Broadcast invokes every subscriber with the right context.
//   * Unsubscribe removes the callback.
//   * Re-entry safety: a callback that Subscribes during its own
//     invocation does NOT fire in the current Broadcast.
//   * GetSubscriberCount tracks add / remove correctly.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "XObject/XCoreDelegates_OnHotReload.h"

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

    void TestSingleDelegate(
        ::XCore::CoreDelegates::FOnHotReloadDelegate& Delegate,
        const char* DelegateName)
    {
        using ::XCore::CoreDelegates::FOnHotReloadDelegate;
        using ::XCore::CoreDelegates::FHotReloadContext;

        Delegate.__ResetForTests();
        Check(Delegate.GetSubscriberCount() == 0,
              "Initial subscriber count should be 0.");

        // -------------------------------------------------------------
        // Test 1: Broadcast on empty delegate -> no callbacks invoked,
        // no crash.
        // -------------------------------------------------------------
        FHotReloadContext EmptyCtx{};
        Delegate.Broadcast(EmptyCtx);

        // -------------------------------------------------------------
        // Test 2: Subscribe + Broadcast fires the callback with the
        // correct context.
        // -------------------------------------------------------------
        int HitCount = 0;
        ::std::int64_t ObservedReplaceCount  = -1;
        ::std::int64_t ObservedInstanceCount = -1;
        auto Cb = [&HitCount, &ObservedReplaceCount, &ObservedInstanceCount](
            const FHotReloadContext& Ctx) noexcept
        {
            ++HitCount;
            ObservedReplaceCount  = Ctx.ReplacedClassCount;
            ObservedInstanceCount = Ctx.TotalInstancesRebound;
        };
        const auto Handle = Delegate.Subscribe(Cb);
        Check(Handle != FOnHotReloadDelegate::kInvalidHandle,
              "Subscribe should issue a non-invalid handle.");
        Check(Delegate.GetSubscriberCount() == 1,
              "Subscriber count should be 1 after Subscribe.");

        FHotReloadContext TestCtx;
        TestCtx.ReplacedClassCount    = 42;
        TestCtx.TotalInstancesRebound = 1000;
        Delegate.Broadcast(TestCtx);

        Check(HitCount == 1,
              "Callback should be invoked exactly once after Broadcast.");
        Check(ObservedReplaceCount == 42,
              "Callback should observe ReplacedClassCount = 42.");
        Check(ObservedInstanceCount == 1000,
              "Callback should observe TotalInstancesRebound = 1000.");

        // -------------------------------------------------------------
        // Test 3: Multiple subscribers all receive Broadcast.
        // -------------------------------------------------------------
        int Hit2 = 0;
        auto Cb2 = [&Hit2](const FHotReloadContext&) noexcept
        {
            ++Hit2;
        };
        const auto H2 = Delegate.Subscribe(Cb2);
        Check(H2 != Handle, "Each Subscribe should issue a unique handle.");
        Check(Delegate.GetSubscriberCount() == 2,
              "Subscriber count should be 2 after second Subscribe.");

        HitCount = 0;
        Hit2 = 0;
        Delegate.Broadcast(TestCtx);
        Check(HitCount == 1, "First subscriber should fire.");
        Check(Hit2     == 1, "Second subscriber should fire.");

        // -------------------------------------------------------------
        // Test 4: Unsubscribe removes the callback.
        // -------------------------------------------------------------
        Check(Delegate.Unsubscribe(Handle),
              "Unsubscribe should return true on valid handle.");
        Check(Delegate.GetSubscriberCount() == 1,
              "Subscriber count should be 1 after Unsubscribe.");
        Check(!Delegate.Unsubscribe(Handle),
              "Second Unsubscribe with same handle should return false.");
        Check(!Delegate.Unsubscribe(FOnHotReloadDelegate::kInvalidHandle),
              "Unsubscribe with kInvalidHandle should return false.");

        HitCount = 0;
        Hit2 = 0;
        Delegate.Broadcast(TestCtx);
        Check(HitCount == 0,
              "After Unsubscribe, first callback should NOT fire.");
        Check(Hit2 == 1,
              "Second callback should still fire.");

        // -------------------------------------------------------------
        // Test 5: Re-entry safety -- a callback that Subscribes during
        // its own invocation does NOT fire in the current Broadcast.
        // -------------------------------------------------------------
        Delegate.__ResetForTests();
        Check(Delegate.GetSubscriberCount() == 0,
              "Post-reset subscriber count should be 0.");

        int  Cb3Hits   = 0;
        int  CbNewHits = 0;
        auto CbReentry = [&Cb3Hits, &CbNewHits, &Delegate](
            const FHotReloadContext&) noexcept
        {
            ++Cb3Hits;
            if (Cb3Hits == 1)
            {
                auto NewCb = [&CbNewHits](const FHotReloadContext&) noexcept
                {
                    ++CbNewHits;
                };
                (void)Delegate.Subscribe(NewCb);
            }
        };
        (void)Delegate.Subscribe(CbReentry);
        Delegate.Broadcast(TestCtx);
        Check(Cb3Hits == 1,
              "Reentry-subscriber fires once in current broadcast.");
        Check(CbNewHits == 0,
              "Newly-subscribed callback does NOT fire in current broadcast.");

        Delegate.Broadcast(TestCtx);
        Check(CbNewHits == 1,
              "Newly-subscribed callback fires on the NEXT broadcast.");

        Delegate.__ResetForTests();
        std::cout << "  Tested delegate: " << DelegateName << "\n";
    }
}

int main()
{
    using ::XCore::CoreDelegates::GetOnHotReloadStart;
    using ::XCore::CoreDelegates::GetOnHotReloadComplete;
    using ::XCore::CoreDelegates::GetOnHotReloadAbort;

    ::XCore::HAL::FMemory::__Init();

    // Run the suite against all three singleton delegates.
    TestSingleDelegate(GetOnHotReloadStart(),    "OnHotReloadStart");
    TestSingleDelegate(GetOnHotReloadComplete(), "OnHotReloadComplete");
    TestSingleDelegate(GetOnHotReloadAbort(),    "OnHotReloadAbort");

    // Verify the three delegates are distinct singletons.
    Check(&GetOnHotReloadStart()    != &GetOnHotReloadComplete(),
          "OnHotReloadStart and OnHotReloadComplete must be distinct instances.");
    Check(&GetOnHotReloadStart()    != &GetOnHotReloadAbort(),
          "OnHotReloadStart and OnHotReloadAbort must be distinct instances.");
    Check(&GetOnHotReloadComplete() != &GetOnHotReloadAbort(),
          "OnHotReloadComplete and OnHotReloadAbort must be distinct instances.");

    if (g_FailureCount == 0)
    {
        std::cout << "XCoreDelegates_OnHotReload.SubscribeBroadcast: PASS\n";
        return 0;
    }
    std::cerr << "XCoreDelegates_OnHotReload.SubscribeBroadcast: "
              << g_FailureCount << " FAIL(s)\n";
    return 1;
}
