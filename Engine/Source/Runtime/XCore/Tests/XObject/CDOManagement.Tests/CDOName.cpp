// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.Tests/CDOName.cpp -- spec §8.2 name convention.
// =====================================================================
//
// Verifies the CDO's FName follows "Default__<ClassName>" convention.
// Tests both ComposeCDOName (the helper) and the actual constructed
// CDO's NamePrivate.
//
// =====================================================================

#include "XObject/CDOManagement.h"
#include "XObject/FXObjectArray.h"
#include "XObject/XObject.h"
#include "Containers/FString.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstring>
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

    FClass TestClass(FName("XCDOName"), nullptr);

    // ComposeCDOName direct test.
    const FName Composed = ::XCore::ComposeCDOName(&TestClass);
    const ::XCore::FString ComposedStr = Composed.ToString();

    Check(std::strcmp(ComposedStr.ToUtf8Cstr(), "Default__XCDOName") == 0,
          "ComposeCDOName != Default__XCDOName");

    // The CDO's NamePrivate matches.
    const XObject* const CDO = ::XCore::GetClassDefaultObject(&TestClass);
    Check(CDO != nullptr, "CDO construction failed");
    if (CDO != nullptr)
    {
        const FName CDOName = CDO->GetFName();
        const ::XCore::FString CDONameStr = CDOName.ToString();
        Check(std::strcmp(CDONameStr.ToUtf8Ptr(), "Default__XCDOName") == 0,
              "CDO NamePrivate != Default__XCDOName");
    }

    // Nullptr Class -> NAME_None.
    const FName NoneComposed = ::XCore::ComposeCDOName(nullptr);
    Check(NoneComposed.IsNone(),
          "ComposeCDOName(nullptr) != NAME_None");

    if (g_FailureCount == 0)
    {
        std::cout << "CDOManagement.CDOName: PASS\n";
        return 0;
    }
    std::cerr << "CDOManagement.CDOName: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
