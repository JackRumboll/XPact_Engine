// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/PhaseGuardFires.cpp -- X-INIT acceptance gate.
// =====================================================================
//
// XCoreXObject Rev 4 §3.5 + Rev 2 FIX-A-CRIT-5 (X-INIT): NewObject
// called before EInitPhase::PostStaticInit returns
// kInvalidPhase via TryNewObjectImpl (the infallible NewObjectImpl
// asserts in Dev/Debug).
//
// The test exercises the fallible path so the failure mode is
// observable without aborting the process.
//
// =====================================================================

#include "XObject/NewObject.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

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
    using ::XCore::EObjectFlags;
    using ::XCore::ENewObjectError;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // Phase is PreStaticInit by default; the gate should fire.
    // (Don't call __AdvanceInitPhase here.)
    Check(::XCore::HAL::EngineInitPhase() < ::XCore::HAL::EInitPhase::PostStaticInit,
          "test pre-condition: EngineInitPhase already >= PostStaticInit");

    FClass TestClass(FName("XPreInit"), nullptr);

    auto Result = ::XCore::TryNewObjectImpl(
        &TestClass, nullptr, FName("InstPre"),
        EObjectFlags::None, nullptr);

    Check(!Result.has_value(),
          "Pre-PostStaticInit: TryNewObject returned a value");
    if (!Result.has_value())
    {
        Check(Result.error() == ENewObjectError::kInvalidPhase,
              "Pre-PostStaticInit: error != kInvalidPhase");
    }

    // After advancing to PostStaticInit, NewObject succeeds.
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);

    auto PostInitResult = ::XCore::TryNewObjectImpl(
        &TestClass, nullptr, FName("InstPost"),
        EObjectFlags::None, nullptr);
    Check(PostInitResult.has_value(),
          "Post-PostStaticInit: TryNewObject returned an error");

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.PhaseGuardFires: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.PhaseGuardFires: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
