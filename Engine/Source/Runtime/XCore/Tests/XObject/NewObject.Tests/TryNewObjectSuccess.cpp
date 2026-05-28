// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/TryNewObjectSuccess.cpp -- Rev 3 FIX-M-R2-1 happy path.
// =====================================================================
//
// XCoreXObject Rev 4 §3.5 + Rev 3 FIX-M-R2-1: TryNewObjectImpl
// returns Result<XObject*, ENewObjectError> with the success branch
// carrying the constructed XObject*.
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
    using ::XCore::ENewObjectError;
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    FClass TestClass(FName("XTryNew"), nullptr);

    auto Result = ::XCore::TryNewObjectImpl(
        &TestClass, nullptr, FName("InstTry"), EObjectFlags::None, nullptr);

    Check(Result.has_value(),
          "TryNewObjectImpl unexpectedly returned an error");
    if (Result.has_value())
    {
        XObject* const Obj = Result.value();
        Check(Obj != nullptr, "TryNewObject value is nullptr");
        Check(Obj->GetClass() == &TestClass,
              "TryNewObject class != requested");
        Check(Obj->GetFName() == FName("InstTry"),
              "TryNewObject name != requested");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.TryNewObjectSuccess: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.TryNewObjectSuccess: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
