// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.Tests/DrainPendingEagerCDOs.cpp -- X-INIT-EAGER gate.
// =====================================================================
//
// XCoreXObject Rev 4 Rev 3 FIX-H-R2-6 (X-INIT-EAGER): the Drain walk
// materialises every queued CDO + the queue drops to zero. The
// returned count equals the number of materialised CDOs.
//
// =====================================================================

#include "XObject/CDOManagement.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "Reflection/EClassFlags.h"
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
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::EClassFlags;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();
    ::XCore::__ResetCDOsForTests();

    // Empty drain returns 0.
    Check(::XCore::DrainPendingEagerCDOs() == 0,
          "empty drain returned non-zero");

    // Queue 4 eager classes.
    FClass Eager1(FName("XEager1"), nullptr, EClassFlags::CLASS_EagerCDO);
    FClass Eager2(FName("XEager2"), nullptr, EClassFlags::CLASS_EagerCDO);
    FClass Eager3(FName("XEager3"), nullptr, EClassFlags::CLASS_EagerCDO);
    FClass Eager4(FName("XEager4"), nullptr, EClassFlags::CLASS_EagerCDO);

    ::XCore::EnqueueEagerCDO(&Eager1);
    ::XCore::EnqueueEagerCDO(&Eager2);
    ::XCore::EnqueueEagerCDO(&Eager3);
    ::XCore::EnqueueEagerCDO(&Eager4);

    Check(::XCore::GetPendingEagerCDOCount() == 4,
          "pre-drain count != 4");

    // Drain.
    const ::int32 Drained = ::XCore::DrainPendingEagerCDOs();
    Check(Drained == 4, "drain count != 4");
    Check(::XCore::GetPendingEagerCDOCount() == 0,
          "post-drain count != 0 (queue not cleared)");

    // Every class's CDO was materialised.
    Check(Eager1.GetCDO() != nullptr, "Eager1 CDO not constructed");
    Check(Eager2.GetCDO() != nullptr, "Eager2 CDO not constructed");
    Check(Eager3.GetCDO() != nullptr, "Eager3 CDO not constructed");
    Check(Eager4.GetCDO() != nullptr, "Eager4 CDO not constructed");

    if (g_FailureCount == 0)
    {
        std::cout << "CDOManagement.DrainPendingEagerCDOs: PASS\n";
        return 0;
    }
    std::cerr << "CDOManagement.DrainPendingEagerCDOs: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
