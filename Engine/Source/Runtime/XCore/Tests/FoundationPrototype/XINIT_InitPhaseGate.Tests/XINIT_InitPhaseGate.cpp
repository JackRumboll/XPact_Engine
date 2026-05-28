// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XINIT_InitPhaseGate.cpp -- Foundation Prototype X-INIT acceptance:
// NewObject called before PostStaticInit asserts (Dev) / aborts
// (Shipping).
// =====================================================================
//
// X-INIT acceptance (spec §13.2; Rev 2 added per FIX-A-CRIT-5):
//   "init-phase gate. Test that NewObject called before
//    EInitPhase::PostStaticInit asserts (Dev) / aborts (Shipping).
//    Pass criterion: bootstrap-ordering bugs caught at first NewObject
//    call site."
//
// This test exercises the Try* variant (which surfaces the violation
// as kInvalidPhase rather than aborting) so the negative path is
// observable. The infallible NewObject path's XPACT_CHECK abort would
// terminate the test process; observation of the abort is the death-
// test pattern which requires a process-fork harness (not shipped in
// Phase 5.l per the dispatch task's "skip tests for surfaces that
// aren't implemented yet" allowance for runtime-fork harnesses).
//
// =====================================================================

#include "../Phase5LCommon.h"

#include "HAL/XInitPhase.h"
#include "XObject/NewObject.h"

#include "Reflection/FClass.h"
#include "Reflection/FName.h"

namespace
{
    int g_FailureCount = 0;
}

int main()
{
    using ::XCore::HAL::EInitPhase;
    using ::XCore::HAL::EngineInitPhase;
    using ::XCore::ENewObjectError;
    using ::XCore::EObjectFlags;
    using ::XCore::TryNewObjectImpl;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // X-INIT NEGATIVE: pre-PostStaticInit Try*NewObject* returns
    // kInvalidPhase. We do NOT call ResetAllForTests() here because
    // it advances to PostStaticInit; we need the pre-PostStaticInit
    // state for this assertion. We reset the singletons manually.
    // -----------------------------------------------------------------

    // The XInitPhase ladder is monotonic: it can only advance. If a
    // prior test in the same .exe advanced past PreStaticInit, the
    // pre-PostStaticInit gate cannot be retested in-process. The
    // canonical Phase 5.l harness compiles each test as its own .exe
    // so this is the FRESH-PROCESS state (per the XBT per-fixture
    // pattern documented in the existing XCore.Tests.Build.toml
    // header).
    if (EngineInitPhase() < EInitPhase::PostStaticInit)
    {
        // Construct a minimal FClass so the Try* path's "class is
        // not nullptr" gate passes (so the test reads the InitPhase
        // gate path, not the kInvalidClass path).
        FClass TestClass(FName("XINITTestClass"), nullptr);

        const auto Result = TryNewObjectImpl(
            &TestClass,
            /*Outer=*/nullptr,
            /*Name=*/FName(),
            /*Flags=*/EObjectFlags::None,
            /*Archetype=*/nullptr);

        P5L_CHECK(!Result.has_value(),
                  "X-INIT: TryNewObjectImpl pre-PostStaticInit returned "
                  "a value (expected kInvalidPhase error)");
        if (!Result.has_value())
        {
            P5L_CHECK(Result.error() == ENewObjectError::kInvalidPhase,
                      "X-INIT: TryNewObjectImpl pre-PostStaticInit "
                      "returned wrong error code (expected kInvalidPhase)");
        }
    }
    else
    {
        std::cout << "X-INIT: SKIP (init-phase already advanced) -- "
                     "pre-PostStaticInit gate cannot be tested in-process; "
                     "fresh-process .exe per-fixture pattern required.\n";
    }

    // -----------------------------------------------------------------
    // X-INIT POSITIVE: post-PostStaticInit Try*NewObject* succeeds.
    //
    // Reset the singletons then advance to PostStaticInit so the rest
    // of the test surfaces the happy path. ResetAllForTests advances
    // automatically.
    // -----------------------------------------------------------------
    Phase5L::ResetAllForTests();

    P5L_CHECK(EngineInitPhase() >= EInitPhase::PostStaticInit,
              "X-INIT: failed to advance to PostStaticInit");

    {
        FClass TestClass(FName("XINITTestClass2"), nullptr);
        TestClass.PropertiesSize = sizeof(::XCore::XObject);
        TestClass.MinAlignment   = alignof(::XCore::XObject);

        ::XCore::FXObjectAllocator::Get().RegisterClassPool(&TestClass);

        const auto Result = TryNewObjectImpl(
            &TestClass,
            /*Outer=*/nullptr,
            /*Name=*/FName(),
            /*Flags=*/EObjectFlags::None,
            /*Archetype=*/nullptr);

        P5L_CHECK(Result.has_value(),
                  "X-INIT: TryNewObjectImpl post-PostStaticInit failed "
                  "(expected happy-path success)");
        if (Result.has_value() && Result.value() != nullptr)
        {
            Phase5L::ReleaseAndDeallocate(Result.value());
        }
    }

    return P5L_REPORT_PASS("FoundationPrototype.XINIT_InitPhaseGate");
}
