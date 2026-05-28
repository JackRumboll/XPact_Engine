// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/TryNewObjectInvalidClass.cpp -- Rev 3 FIX-M-R2-1
// kInvalidClass + kAbstractClass error surfaces.
// =====================================================================
//
// XCoreXObject Rev 4 §3.5 + Rev 3 FIX-M-R2-1: TryNewObjectImpl
// surfaces pre-condition failures as ENewObjectError values rather
// than aborting.
//
// =====================================================================

#include "XObject/FXObjectArray.h"
#include "XObject/NewObject.h"
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
    using ::XCore::EObjectFlags;
    using ::XCore::ENewObjectError;
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::EClassFlags;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    // ----- nullptr Class -> kInvalidClass -----
    {
        auto Result = ::XCore::TryNewObjectImpl(
            /*Class=*/ nullptr, nullptr, FName("X"),
            EObjectFlags::None, nullptr);

        Check(!Result.has_value(),
              "nullptr Class: TryNewObject returned a value");
        if (!Result.has_value())
        {
            Check(Result.error() == ENewObjectError::kInvalidClass,
                  "nullptr Class: error != kInvalidClass");
        }
    }

    // ----- Abstract Class -> kAbstractClass -----
    {
        FClass AbstractClass(FName("XAbstract"), nullptr,
                             EClassFlags::CLASS_Abstract);

        auto Result = ::XCore::TryNewObjectImpl(
            &AbstractClass, nullptr, FName("X"),
            EObjectFlags::None, nullptr);

        Check(!Result.has_value(),
              "abstract Class: TryNewObject returned a value");
        if (!Result.has_value())
        {
            Check(Result.error() == ENewObjectError::kAbstractClass,
                  "abstract Class: error != kAbstractClass");
        }
    }

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.TryNewObjectInvalidClass: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.TryNewObjectInvalidClass: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
