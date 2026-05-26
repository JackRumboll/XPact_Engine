// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/CastFlagsHierarchy.cpp -- per-subclass CastFlags +
// IsA chain through FProperty parent (XCore-4b §5.2 + §5.5).
// =====================================================================
//
// Each of the 15 primitive subclasses owns a dedicated bit in
// EClassCastFlags. The IsA test verifies:
//
//   1. Each subclass's CastFlags include kFProperty (the parent bit)
//      AND its own dedicated bit.
//   2. IsA(FProperty::StaticClass()) returns true for every subclass
//      instance.
//   3. IsA(SameClass::StaticClass()) returns true.
//   4. IsA(DifferentSiblingClass::StaticClass()) returns false.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FBoolProperty.h"
#include "Reflection/FByteProperty.h"
#include "Reflection/FDoubleProperty.h"
#include "Reflection/FEnumProperty.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FInt16Property.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FInt8Property.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FNameProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FStrProperty.h"
#include "Reflection/FTextProperty.h"
#include "Reflection/FUInt16Property.h"
#include "Reflection/FUInt32Property.h"
#include "Reflection/FUInt64Property.h"

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

    using ::XCore::Reflect::EClassCastFlags;
    using ::XCore::Reflect::FFieldClass;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FProperty;
    using ::XCore::Reflect::HasAllCastFlags;
    using ::XCore::Reflect::HasAnyCastFlags;

    template <typename SubclassT>
    void CheckSubclass(const char* TypeName, EClassCastFlags ExpectedBit)
    {
        // Construct an instance.
        SubclassT Instance(FFieldVariant{}, FName("F"));

        const FFieldClass* SubClass = SubclassT::StaticClass();
        Check(SubClass != nullptr,
              (std::string(TypeName) + "::StaticClass() returned nullptr").c_str());

        // SubclassFlags must include BOTH the parent kFProperty bit AND
        // the subclass's own bit.
        const EClassCastFlags SubFlags = SubClass->GetCastFlags();
        Check(HasAllCastFlags(SubFlags, EClassCastFlags::kFProperty),
              (std::string(TypeName) + " CastFlags missing kFProperty parent bit").c_str());
        Check(HasAllCastFlags(SubFlags, ExpectedBit),
              (std::string(TypeName) + " CastFlags missing own bit").c_str());

        // Instance->GetClass() returns the subclass.
        Check(Instance.GetClass() == SubClass,
              (std::string(TypeName) + " GetClass() != StaticClass()").c_str());

        // IsA fast path: IsA(FProperty::StaticClass()) succeeds via
        // SuperClass walk; in Phase 4b.4a the walk via
        // FFieldClass::IsChildOf does the iteration.
        Check(Instance.IsA(FProperty::StaticClass()),
              (std::string(TypeName) + " IsA(FProperty) returned false").c_str());

        // IsA(self) succeeds.
        Check(Instance.IsA(SubClass),
              (std::string(TypeName) + " IsA(self) returned false").c_str());
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Trigger lazy-init of the base FProperty class name.
    (void)::XCore::Reflect::GetFPropertyStaticClass();

    CheckSubclass<::XCore::Reflect::FBoolProperty>  ("FBoolProperty",   EClassCastFlags::kFBoolProperty);
    CheckSubclass<::XCore::Reflect::FByteProperty>  ("FByteProperty",   EClassCastFlags::kFByteProperty);
    CheckSubclass<::XCore::Reflect::FInt8Property>  ("FInt8Property",   EClassCastFlags::kFInt8Property);
    CheckSubclass<::XCore::Reflect::FInt16Property> ("FInt16Property",  EClassCastFlags::kFInt16Property);
    CheckSubclass<::XCore::Reflect::FIntProperty>   ("FIntProperty",    EClassCastFlags::kFIntProperty);
    CheckSubclass<::XCore::Reflect::FInt64Property> ("FInt64Property",  EClassCastFlags::kFInt64Property);
    CheckSubclass<::XCore::Reflect::FUInt16Property>("FUInt16Property", EClassCastFlags::kFUInt16Property);
    CheckSubclass<::XCore::Reflect::FUInt32Property>("FUInt32Property", EClassCastFlags::kFUInt32Property);
    CheckSubclass<::XCore::Reflect::FUInt64Property>("FUInt64Property", EClassCastFlags::kFUInt64Property);
    CheckSubclass<::XCore::Reflect::FFloatProperty> ("FFloatProperty",  EClassCastFlags::kFFloatProperty);
    CheckSubclass<::XCore::Reflect::FDoubleProperty>("FDoubleProperty", EClassCastFlags::kFDoubleProperty);
    CheckSubclass<::XCore::Reflect::FStrProperty>   ("FStrProperty",    EClassCastFlags::kFStrProperty);
    CheckSubclass<::XCore::Reflect::FNameProperty>  ("FNameProperty",   EClassCastFlags::kFNameProperty);
    CheckSubclass<::XCore::Reflect::FTextProperty>  ("FTextProperty",   EClassCastFlags::kFTextProperty);
    CheckSubclass<::XCore::Reflect::FEnumProperty>  ("FEnumProperty",   EClassCastFlags::kFEnumProperty);

    // Cross-hierarchy negative test: an FIntProperty instance is NOT
    // an FBoolProperty.
    {
        ::XCore::Reflect::FIntProperty IntInstance(FFieldVariant{}, FName("F"));
        Check(!IntInstance.IsA(::XCore::Reflect::FBoolProperty::StaticClass()),
              "FIntProperty instance IsA(FBoolProperty) returned true");
        Check(!IntInstance.IsA(::XCore::Reflect::FFloatProperty::StaticClass()),
              "FIntProperty instance IsA(FFloatProperty) returned true");
    }

    // -----------------------------------------------------------------
    // Verify each subclass's StaticClass()->FakeVTable is non-null
    // (the discipline that every FProperty subclass has a populated
    // dispatch table in .rodata).
    // -----------------------------------------------------------------
    Check(::XCore::Reflect::FInt8Property::StaticClass()->GetFakeVTable() != nullptr,
          "FInt8Property::StaticClass()->FakeVTable is nullptr");
    Check(::XCore::Reflect::FIntProperty::StaticClass()->GetFakeVTable() != nullptr,
          "FIntProperty::StaticClass()->FakeVTable is nullptr");
    Check(::XCore::Reflect::FFloatProperty::StaticClass()->GetFakeVTable() != nullptr,
          "FFloatProperty::StaticClass()->FakeVTable is nullptr");
    Check(::XCore::Reflect::FBoolProperty::StaticClass()->GetFakeVTable() != nullptr,
          "FBoolProperty::StaticClass()->FakeVTable is nullptr");
    Check(::XCore::Reflect::FNameProperty::StaticClass()->GetFakeVTable() != nullptr,
          "FNameProperty::StaticClass()->FakeVTable is nullptr");

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.CastFlagsHierarchy: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.CastFlagsHierarchy: PASS\n";
    return 0;
}
