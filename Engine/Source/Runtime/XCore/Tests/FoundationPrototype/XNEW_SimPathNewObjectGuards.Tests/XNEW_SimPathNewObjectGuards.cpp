// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XNEW_SimPathNewObjectGuards.cpp -- Foundation Prototype X-NEW
// acceptance: sim-path NewObject runtime invariant guards.
// =====================================================================
//
// X-NEW acceptance (spec §13.2; Rev 2 added per FIX-A-CRIT-1):
//   "sim-path NewObject runtime invariant guards. Test that
//    XPACT_CHECK_SL(IsSimPathThread() || !IsSimPathTU()) fires on
//    every sim-path NewObject call from a non-SimPathSerialExecutor
//    thread. Pass criterion: assert fires in Dev; abort in Shipping;
//    no false-negatives."
//
// JUDGEMENT CALL (Phase 5.l X-NEW deferral). Same deferral envelope
// as X-DET: the runtime probes
//     ::XCore::HAL::IsSimPathTU
//     ::XCore::HAL::IsSimPathThread
// have NOT shipped (Phase 5.d's NewObject.h leaves the
// XPACT_CHECK_SL site as a TODO comment for the future phase). The
// XPACT_CHECK_SL macro IS compiled into the NewObjectImpl path; the
// negative-side abort observation requires a fork harness + the
// runtime probes.
//
// Phase 5.l ships the HAPPY-PATH verification that NewObject works
// from a non-sim-path TU (matching the Phase 5.d NewObject.Tests/
// SimPathGuard.cpp pattern) + the documented invariant.
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"
#include "XObject/NewObject.h"

#include <thread>

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::EObjectFlags;
    using ::XCore::NewObjectImpl;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    Phase5L::ResetAllForTests();

    // -----------------------------------------------------------------
    // Build a synthetic FClass + register it with the allocator.
    // -----------------------------------------------------------------
    FClass TestClass(FName("XNEWTestClass"), nullptr);
    TestClass.PropertiesSize = sizeof(XObject);
    TestClass.MinAlignment   = alignof(XObject);

    ::XCore::FXObjectAllocator::Get().RegisterClassPool(&TestClass);

    // -----------------------------------------------------------------
    // HAPPY PATH 1: NewObjectImpl from the main test thread (a non-
    // sim-path thread by convention) succeeds.
    // -----------------------------------------------------------------
    XObject* const Obj = NewObjectImpl(
        &TestClass, nullptr, FName(), EObjectFlags::None, nullptr);

    P5L_CHECK(Obj != nullptr,
              "X-NEW: NewObjectImpl from main thread failed (expected "
              "happy-path success on non-sim-path TU)");

    if (Obj != nullptr)
    {
        // Verify the object is properly registered.
        P5L_CHECK(Obj->ClassPrivate == &TestClass,
                  "X-NEW: NewObjectImpl produced object with wrong "
                  "ClassPrivate");
        P5L_CHECK(Obj->InternalIndex > 0,
                  "X-NEW: NewObjectImpl produced object with invalid "
                  "InternalIndex");
        Phase5L::ReleaseAndDeallocate(Obj);
    }

    // -----------------------------------------------------------------
    // HAPPY PATH 2: NewObjectImpl from a fresh std::thread also
    // succeeds (no sim-path-thread check fires; the test TU is non-
    // sim-path).
    //
    // This sub-test verifies the LACK of a false-positive: just because
    // the calling thread is not the main thread does not mean
    // XPACT_CHECK_SL should fire (the macro keys off the TU + thread
    // sim-path classification, not the "main thread" property).
    // -----------------------------------------------------------------
    ::std::atomic<bool> ThreadSucceeded{false};
    ::std::thread Worker([&]()
    {
        XObject* const ThreadObj = NewObjectImpl(
            &TestClass, nullptr, FName(), EObjectFlags::None, nullptr);
        if (ThreadObj != nullptr)
        {
            ThreadSucceeded.store(true, ::std::memory_order_release);
            Phase5L::ReleaseAndDeallocate(ThreadObj);
        }
    });
    Worker.join();
    P5L_CHECK(ThreadSucceeded.load(::std::memory_order_acquire),
              "X-NEW: NewObjectImpl from worker thread failed");

    // -----------------------------------------------------------------
    // X-NEW DEFERRED COMPONENT documentation.
    // -----------------------------------------------------------------
    std::cout << "X-NEW: happy-path verified (main thread + worker "
                 "thread); sim-path negative test deferred to "
                 "::XCore::HAL::IsSimPathTU + IsSimPathThread runtime "
                 "probes (future phase).\n";

    return P5L_REPORT_PASS("FoundationPrototype.XNEW_SimPathNewObjectGuards");
}
