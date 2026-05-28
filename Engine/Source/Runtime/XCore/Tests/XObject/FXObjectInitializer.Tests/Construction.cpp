// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.Tests/Construction.cpp -- XCoreXObject Rev 4 §8.4.
// =====================================================================
//
// Verifies the ctor binds Target / Class / Archetype + initialises
// the bPostInitFired flag false + InstancingGraph empty.
//
// =====================================================================

#include "XObject/FXObjectInitializer.h"
#include "XObject/XObject.h"
#include "Reflection/FClass.h"
#include "Reflection/FName.h"

#include "HAL/FMemory.h"

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
    using ::XCore::FXObjectInitializer;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    ::XCore::HAL::FMemory::__Init();

    // Construct a minimal FClass (no lifecycle table; no constructor
    // fn). The Phase 5.d Initializer only requires Class to be non-null
    // and Class->LifecycleTable to short-circuit at dispatch.
    FClass TestClass(FName("XTestClass"), nullptr);

    XObject Target;
    Target.ClassPrivate = &TestClass;

    // ----- Construct with default-archetype (nullptr). -----
    {
        FXObjectInitializer Initializer(&Target, &TestClass);

        Check(Initializer.GetTarget()    == &Target,    "GetTarget() != &Target");
        Check(Initializer.GetClass()     == &TestClass, "GetClass() != &TestClass");
        Check(Initializer.GetArchetype() == nullptr,    "GetArchetype() != nullptr");
        Check(!Initializer.HasFiredPostInit(),
              "fresh Initializer: HasFiredPostInit() == true");
        Check(Initializer.GetInstancingGraph().IsEmpty(),
              "fresh Initializer: InstancingGraph not empty");
    }

    // ----- Construct with explicit archetype. -----
    {
        XObject ArchetypeObj;
        ArchetypeObj.ClassPrivate = &TestClass;

        FXObjectInitializer Initializer(&Target, &TestClass, &ArchetypeObj);

        Check(Initializer.GetTarget()    == &Target,        "with-archetype: GetTarget() wrong");
        Check(Initializer.GetClass()     == &TestClass,     "with-archetype: GetClass() wrong");
        Check(Initializer.GetArchetype() == &ArchetypeObj,  "with-archetype: GetArchetype() wrong");
        Check(!Initializer.HasFiredPostInit(),
              "with-archetype: HasFiredPostInit() == true");
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectInitializer.Construction: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectInitializer.Construction: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
