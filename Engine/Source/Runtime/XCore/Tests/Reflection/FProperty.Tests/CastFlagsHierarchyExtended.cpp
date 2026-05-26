// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/CastFlagsHierarchyExtended.cpp -- per-subclass
// CastFlags + IsA chain through FProperty parent for the 13 new
// Phase 4b.4b subclasses (XCore-4b §5.2 + §5.5).
// =====================================================================
//
// Mirrors the Phase 4b.4a CastFlagsHierarchy.cpp pattern. For each
// of the 13 new subclasses:
//
//   1. CastFlags include kFProperty parent gate AND the subclass's
//      own dedicated bit.
//   2. The 5 object-reference family members (FObjectProperty,
//      FWeakObjectProperty, FSoftObjectProperty, FClassProperty,
//      FSoftClassProperty) additionally include kFObjectPropertyBase.
//   3. The other 8 subclasses (FInterfaceProperty, FStructProperty,
//      FArrayProperty, FMapProperty, FSetProperty, FDelegateProperty,
//      FMulticastInlineDelegate, FMulticastSparseDelegate) do NOT
//      include kFObjectPropertyBase (per XPact's flat hierarchy).
//   4. IsA(FProperty::StaticClass()) returns true via FFieldClass::
//      IsChildOf SuperClass-chain walk (Phase 4b.4a fast path via
//      CastFlags AND).
//   5. Every subclass's StaticClass()->FakeVTable is non-null.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FArrayProperty.h"
#include "Reflection/FClassProperty.h"
#include "Reflection/FDelegateProperty.h"
#include "Reflection/FInterfaceProperty.h"
#include "Reflection/FMapProperty.h"
#include "Reflection/FMulticastInlineDelegateProperty.h"
#include "Reflection/FMulticastSparseDelegateProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FObjectProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FSetProperty.h"
#include "Reflection/FSoftClassProperty.h"
#include "Reflection/FSoftObjectProperty.h"
#include "Reflection/FStructProperty.h"
#include "Reflection/FWeakObjectProperty.h"

#include <iostream>
#include <string>

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

    template <typename SubclassT>
    void CheckSubclass(const char* TypeName, EClassCastFlags ExpectedBit,
                       bool bExpectObjectPropertyBase)
    {
        SubclassT Instance(FFieldVariant{}, FName("F"));

        const FFieldClass* SubClass = SubclassT::StaticClass();
        Check(SubClass != nullptr,
              (std::string(TypeName) + "::StaticClass() returned nullptr").c_str());

        // CastFlags must include kFProperty parent gate AND own bit.
        const EClassCastFlags Flags = SubClass->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              (std::string(TypeName) + " CastFlags missing kFProperty parent bit").c_str());
        Check(HasAllCastFlags(Flags, ExpectedBit),
              (std::string(TypeName) + " CastFlags missing own bit").c_str());

        // kFObjectPropertyBase parent gate expectation.
        if (bExpectObjectPropertyBase)
        {
            Check(HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
                  (std::string(TypeName) + " CastFlags missing kFObjectPropertyBase "
                   "(this subclass IS in the object-reference family)").c_str());
        }
        else
        {
            Check(!HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
                  (std::string(TypeName) + " CastFlags should NOT include kFObjectPropertyBase "
                   "(this subclass is NOT in the object-reference family)").c_str());
        }

        // Instance->GetClass() returns the subclass.
        Check(Instance.GetClass() == SubClass,
              (std::string(TypeName) + " GetClass() != StaticClass()").c_str());

        // IsA(FProperty::StaticClass()) via CastFlags fast path.
        Check(Instance.IsA(FProperty::StaticClass()),
              (std::string(TypeName) + " IsA(FProperty) returned false").c_str());

        // IsA(self) succeeds.
        Check(Instance.IsA(SubClass),
              (std::string(TypeName) + " IsA(self) returned false").c_str());

        // Per-subclass FakeVTable is non-null (the discipline that
        // every FProperty subclass has a populated dispatch table).
        Check(SubClass->GetFakeVTable() != nullptr,
              (std::string(TypeName) + " StaticClass()->FakeVTable is nullptr").c_str());

        // SuperClass points at FProperty's StaticClass.
        Check(SubClass->GetSuperClass() == FProperty::StaticClass(),
              (std::string(TypeName) + " SuperClass != FProperty").c_str());
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Trigger lazy-init of the base FProperty class name.
    (void)::XCore::Reflect::GetFPropertyStaticClass();

    // -----------------------------------------------------------------
    // Object-reference family (kFObjectPropertyBase set).
    // -----------------------------------------------------------------
    CheckSubclass<::XCore::Reflect::FObjectProperty>(
        "FObjectProperty",     EClassCastFlags::kFObjectProperty,     true);
    CheckSubclass<::XCore::Reflect::FWeakObjectProperty>(
        "FWeakObjectProperty", EClassCastFlags::kFWeakObjectProperty, true);
    CheckSubclass<::XCore::Reflect::FSoftObjectProperty>(
        "FSoftObjectProperty", EClassCastFlags::kFSoftObjectProperty, true);
    CheckSubclass<::XCore::Reflect::FClassProperty>(
        "FClassProperty",      EClassCastFlags::kFClassProperty,      true);
    CheckSubclass<::XCore::Reflect::FSoftClassProperty>(
        "FSoftClassProperty",  EClassCastFlags::kFSoftClassProperty,  true);

    // -----------------------------------------------------------------
    // Container family (kFObjectPropertyBase NOT set).
    // -----------------------------------------------------------------
    CheckSubclass<::XCore::Reflect::FStructProperty>(
        "FStructProperty",     EClassCastFlags::kFStructProperty,     false);
    CheckSubclass<::XCore::Reflect::FArrayProperty>(
        "FArrayProperty",      EClassCastFlags::kFArrayProperty,      false);
    CheckSubclass<::XCore::Reflect::FMapProperty>(
        "FMapProperty",        EClassCastFlags::kFMapProperty,        false);
    CheckSubclass<::XCore::Reflect::FSetProperty>(
        "FSetProperty",        EClassCastFlags::kFSetProperty,        false);

    // -----------------------------------------------------------------
    // Interface (kFObjectPropertyBase NOT set in XPact's flat hierarchy).
    // -----------------------------------------------------------------
    CheckSubclass<::XCore::Reflect::FInterfaceProperty>(
        "FInterfaceProperty",  EClassCastFlags::kFInterfaceProperty,  false);

    // -----------------------------------------------------------------
    // Delegate family (kFObjectPropertyBase NOT set).
    // -----------------------------------------------------------------
    CheckSubclass<::XCore::Reflect::FDelegateProperty>(
        "FDelegateProperty",
        EClassCastFlags::kFDelegateProperty,
        false);
    CheckSubclass<::XCore::Reflect::FMulticastInlineDelegateProperty>(
        "FMulticastInlineDelegateProperty",
        EClassCastFlags::kFMulticastInlineDelegateProperty,
        false);
    CheckSubclass<::XCore::Reflect::FMulticastSparseDelegateProperty>(
        "FMulticastSparseDelegateProperty",
        EClassCastFlags::kFMulticastSparseDelegateProperty,
        false);

    // -----------------------------------------------------------------
    // Cross-hierarchy negative tests.
    // -----------------------------------------------------------------
    {
        ::XCore::Reflect::FArrayProperty ArrInstance(FFieldVariant{}, FName("A"));
        Check(!ArrInstance.IsA(::XCore::Reflect::FMapProperty::StaticClass()),
              "FArrayProperty instance IsA(FMapProperty) returned true");
        Check(!ArrInstance.IsA(::XCore::Reflect::FObjectProperty::StaticClass()),
              "FArrayProperty instance IsA(FObjectProperty) returned true");
    }
    {
        ::XCore::Reflect::FObjectProperty ObjInstance(FFieldVariant{}, FName("O"));
        Check(!ObjInstance.IsA(::XCore::Reflect::FArrayProperty::StaticClass()),
              "FObjectProperty instance IsA(FArrayProperty) returned true");
        Check(!ObjInstance.IsA(::XCore::Reflect::FDelegateProperty::StaticClass()),
              "FObjectProperty instance IsA(FDelegateProperty) returned true");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.CastFlagsHierarchyExtended: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.CastFlagsHierarchyExtended: PASS\n";
    return 0;
}
