// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.Tests/LazyCDOConstruction.cpp -- spec §8.1 + §8.2.
// =====================================================================
//
// Verifies GetClassDefaultObject:
//   * First call constructs the CDO.
//   * Second call returns the SAME pointer (cached via atomic).
//
// =====================================================================

#include "XObject/CDOManagement.h"
#include "XObject/FXObjectArray.h"
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
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();
    ::XCore::__ResetCDOsForTests();

    FClass TestClass(FName("XLazyCDO"), nullptr);
    Check(TestClass.GetCDO() == nullptr,
          "pre-construction: CDO slot non-null");

    // First call constructs.
    const XObject* const CDO1 = ::XCore::GetClassDefaultObject(&TestClass);
    Check(CDO1 != nullptr, "first GetClassDefaultObject returned nullptr");

    // Second call returns same pointer.
    const XObject* const CDO2 = ::XCore::GetClassDefaultObject(&TestClass);
    Check(CDO1 == CDO2,
          "second GetClassDefaultObject != first (cache broken)");

    // The FClass slot reflects the published CDO.
    Check(TestClass.GetCDO() != nullptr,
          "FClass::GetCDO returned nullptr after CDO construction");

    // Nullptr Class -> nullptr CDO.
    Check(::XCore::GetClassDefaultObject(nullptr) == nullptr,
          "GetClassDefaultObject(nullptr) != nullptr");

    if (g_FailureCount == 0)
    {
        std::cout << "CDOManagement.LazyCDOConstruction: PASS\n";
        return 0;
    }
    std::cerr << "CDOManagement.LazyCDOConstruction: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
