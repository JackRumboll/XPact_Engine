// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// NewObject.Tests/BasicConstruction.cpp -- XCoreXObject Rev 4 §3.5.
// =====================================================================
//
// Verifies NewObjectImpl returns a valid XObject with all header
// fields populated:
//   * ClassPrivate    = supplied Class
//   * InternalIndex    -- assigned (positive)
//   * SerialNumber     -- assigned (positive; matches FXObjectArray)
//   * Outer            = supplied Outer
//   * NamePrivate      = supplied Name
//   * ObjectFlags      includes supplied flags AND clears NeedInitialization
//                       (post-PostInit dispatch).
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
    using ::XCore::HasAnyObjectFlags;
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();

    FClass TestClass(FName("XTest"), nullptr);

    const FName Name("BasicCtorInstance");
    XObject* const Obj = ::XCore::NewObjectImpl(
        &TestClass,
        /*Outer=*/nullptr,
        Name,
        EObjectFlags::Transient,
        /*Archetype=*/nullptr);

    Check(Obj != nullptr, "NewObjectImpl returned nullptr");
    if (Obj == nullptr)
    {
        std::cerr << "NewObject.BasicConstruction: FAIL\n";
        return 1;
    }

    Check(Obj->GetClass() == &TestClass,
          "GetClass != supplied Class");
    Check(Obj->GetOuter() == nullptr,
          "GetOuter != nullptr (top-level object)");
    Check(Obj->GetFName() == Name,
          "GetFName != supplied Name");
    Check(Obj->GetInternalIndex() > 0,
          "GetInternalIndex <= 0 (slot not bound)");
    Check(Obj->GetSerialNumber() > 0u,
          "GetSerialNumber == 0 (slot not initialised)");
    Check(HasAnyObjectFlags(Obj->GetObjectFlags(), EObjectFlags::Transient),
          "Transient flag not set");
    Check(!HasAnyObjectFlags(Obj->GetObjectFlags(), EObjectFlags::NeedInitialization),
          "NeedInitialization flag not cleared after PostInit");

    // The FXObjectArray entry should resolve to this object.
    XObject* const Resolved = FXObjectArray::Get().GetObjectAtIndex(
        Obj->GetInternalIndex(), Obj->GetSerialNumber());
    Check(Resolved == Obj,
          "FXObjectArray slot does not resolve back to constructed object");

    if (g_FailureCount == 0)
    {
        std::cout << "NewObject.BasicConstruction: PASS\n";
        return 0;
    }
    std::cerr << "NewObject.BasicConstruction: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
