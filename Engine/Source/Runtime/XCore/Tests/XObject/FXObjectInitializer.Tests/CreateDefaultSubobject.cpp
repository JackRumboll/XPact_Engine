// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXObjectInitializer.Tests/CreateDefaultSubobject.cpp -- spec §8.4.
// =====================================================================
//
// Verifies that CreateDefaultSubobjectImpl produces a sub-object with:
//   * Outer = Target (the initializer's just-constructed object)
//   * Name  = the supplied FName
//   * Class = the supplied FClass*
//   * Flags include DefaultSubObject (mandatory per spec §8.4)
//
// Phase 5.d uses the type-erased CreateDefaultSubobjectImpl entry
// (the templated CreateDefaultSubobject<T> requires T::StaticClass()
// which is XHT-emitted in Phase 5.f+; the impl entry takes the FClass
// directly).
//
// =====================================================================

#include "XObject/FXObjectInitializer.h"
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
    using ::XCore::FXObjectInitializer;
    using ::XCore::FXObjectArray;
    using ::XCore::EObjectFlags;
    using ::XCore::HasAnyObjectFlags;
    using ::XCore::XObject;
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FName;

    // Bring the engine up to PostStaticInit so NewObject's phase gate
    // passes. FMemory + FXObjectArray + FXObjectAllocator come up
    // lazily.
    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);

    FXObjectArray::Get().__ResetForTests();

    FClass ParentClass(FName("XParent"), nullptr);
    FClass SubClass(FName("XSub"), nullptr);

    // Construct a parent XObject. We do this via NewObjectImpl so the
    // parent has a valid InternalIndex + SerialNumber for the
    // sub-object's Outer chain.
    XObject* const Parent = ::XCore::NewObjectImpl(
        &ParentClass,
        /*Outer=*/nullptr,
        FName("ParentInstance"),
        EObjectFlags::None,
        /*Archetype=*/nullptr);

    Check(Parent != nullptr, "Parent NewObject failed");
    if (Parent == nullptr)
    {
        std::cerr << "FXObjectInitializer.CreateDefaultSubobject: FAIL\n";
        return 1;
    }

    // Create an Initializer for Parent + invoke CreateDefaultSubobject.
    XObject* SubobjectPtr = nullptr;
    {
        FXObjectInitializer Initializer(Parent, &ParentClass);

        const FName SubobjectName("MyComponent");
        SubobjectPtr = Initializer.CreateDefaultSubobjectImpl(
            &SubClass, SubobjectName, /*bTransient=*/false);

        Check(SubobjectPtr != nullptr,
              "CreateDefaultSubobjectImpl returned nullptr");
        if (SubobjectPtr != nullptr)
        {
            Check(SubobjectPtr->GetOuter() == Parent,
                  "sub-object Outer != Parent");
            Check(SubobjectPtr->GetClass() == &SubClass,
                  "sub-object Class != SubClass");
            Check(SubobjectPtr->GetFName() == SubobjectName,
                  "sub-object Name != provided FName");
            Check(HasAnyObjectFlags(SubobjectPtr->GetObjectFlags(),
                                    EObjectFlags::DefaultSubObject),
                  "sub-object DefaultSubObject flag not set");
            Check(!HasAnyObjectFlags(SubobjectPtr->GetObjectFlags(),
                                     EObjectFlags::Transient),
                  "sub-object Transient flag set despite bTransient=false");
        }
    }

    // After the Initializer destructs, the sub-object remains alive
    // (it's in FXObjectArray; the destructor only fires PostInit on
    // the Initializer's Target).
    Check(SubobjectPtr != nullptr && SubobjectPtr->GetClass() == &SubClass,
          "sub-object class lost after Initializer destruction");

    // ----- Transient variant -----
    {
        FXObjectInitializer Initializer(Parent, &ParentClass);
        XObject* const TransientSub = Initializer.CreateDefaultSubobjectImpl(
            &SubClass, FName("TransientComponent"), /*bTransient=*/true);
        Check(TransientSub != nullptr,
              "transient CreateDefaultSubobjectImpl returned nullptr");
        if (TransientSub != nullptr)
        {
            Check(HasAnyObjectFlags(TransientSub->GetObjectFlags(),
                                    EObjectFlags::Transient),
                  "transient sub-object Transient flag NOT set");
            Check(HasAnyObjectFlags(TransientSub->GetObjectFlags(),
                                    EObjectFlags::DefaultSubObject),
                  "transient sub-object DefaultSubObject flag NOT set");
        }
    }

    if (g_FailureCount == 0)
    {
        std::cout << "FXObjectInitializer.CreateDefaultSubobject: PASS\n";
        return 0;
    }
    std::cerr << "FXObjectInitializer.CreateDefaultSubobject: " << g_FailureCount << " FAIL(s)\n";
    return 1;
}
