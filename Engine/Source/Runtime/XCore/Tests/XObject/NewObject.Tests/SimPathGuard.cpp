// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/SimPathGuard.cpp -- X-NEW acceptance gate documentation.
// =====================================================================
//
// XCoreXObject Rev 4 §3.5 + Rev 2 FIX-A-CRIT-1 (X-NEW): the
// XPACT_CHECK_SL(::XCore::HAL::IsSimPathThread() ||
//                !::XCore::HAL::IsSimPathTU())
// guard at the NewObject entry path enforces the sim-path runtime
// invariant.
//
// PHASE 5.d POSTURE: the runtime probes ::XCore::HAL::IsSimPathTU /
// IsSimPathThread are FUTURE PHASE deliverables. Phase 5.d ships the
// documentation of the invariant + the call-site marker (TODO comment
// in NewObject.h) but cannot exercise the negative-test path. This
// test documents the deferral + verifies the non-sim-path happy path
// (NewObject from a non-sim-path TU succeeds without firing any
// guard).
//
// =====================================================================

#include "XObject/FXObjectArray.h"
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
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    // The Phase 5.d posture: NewObject succeeds from a non-sim-path
    // TU (this TU is non-sim-path by default). The sim-path runtime
    // probe is not wired yet; the call site documents the invariant
    // and the test verifies the happy path.
    FClass TestClass(FName("XSimPath"), nullptr);

    XObject* const Obj = ::XCore::NewObjectImpl(
        &TestClass, nullptr, FName("Inst"), EObjectFlags::None, nullptr);
    Check(Obj != nullptr,
          "non-sim-path TU NewObject failed");
    Check(Obj != nullptr && Obj->GetClass() == &TestClass,
          "non-sim-path TU NewObject produced wrong class");

    // FUTURE PHASE NEGATIVE TEST: once ::XCore::HAL::IsSimPathTU /
    // IsSimPathThread ship (likely Phase 5.e+), extend this test to
    // verify the XPACT_CHECK_SL fires when called from a sim-path TU
    // off the SimPathSerialExecutor thread.

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.SimPathGuard: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.SimPathGuard: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
