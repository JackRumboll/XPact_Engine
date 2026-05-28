// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// CDOManagement.Tests/CDOFlags.cpp -- spec §8.2 CDO flag posture.
// =====================================================================
//
// Verifies the constructed CDO carries the documented flag set:
//   * RF_ClassDefaultObject -- distinguishes the CDO from instances.
//   * RF_ArchetypeObject    -- the CDO is the canonical archetype.
//   * RF_Public             -- visible across package boundaries.
//   * RF_MarkAsRootSet      -- pinned in the GC root set.
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
    using ::XCore::EObjectFlags;
    using ::XCore::HasAnyObjectFlags;
    using ::XCore::FXObjectArray;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);
    FXObjectArray::Get().__ResetForTests();
    ::XCore::__ResetCDOsForTests();

    FClass TestClass(FName("XCDOFlags"), nullptr);
    const XObject* const CDO = ::XCore::GetClassDefaultObject(&TestClass);

    Check(CDO != nullptr, "CDO construction failed");
    if (CDO == nullptr)
    {
        std::cerr << "CDOManagement.CDOFlags: FAIL\n";
        return 1;
    }

    const EObjectFlags Flags = CDO->GetObjectFlags();

    Check(HasAnyObjectFlags(Flags, EObjectFlags::ClassDefaultObject),
          "CDO missing ClassDefaultObject flag");
    Check(HasAnyObjectFlags(Flags, EObjectFlags::ArchetypeObject),
          "CDO missing ArchetypeObject flag");
    Check(HasAnyObjectFlags(Flags, EObjectFlags::Public),
          "CDO missing Public flag");
    Check(HasAnyObjectFlags(Flags, EObjectFlags::MarkAsRootSet),
          "CDO missing MarkAsRootSet flag");

    // The CDO should NOT have Transient (per the spec; CDOs are
    // serialised as the template default).
    Check(!HasAnyObjectFlags(Flags, EObjectFlags::Transient),
          "CDO has Transient flag set (should be persistent)");

    if (g_FailureCount == 0)
    {
        std::cout << "CDOManagement.CDOFlags: PASS\n";
        return 0;
    }
    std::cerr << "CDOManagement.CDOFlags: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
