// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/ReferencePropertiesSizeof.cpp -- ABI lock for the
// 13 reference/container/delegate FProperty subclasses (Phase 4b.4b
// acceptance gates B1 + B3).
// =====================================================================
//
// XCore-4b Rev 3, Section 11.2 layout table -- verifies sizeof /
// alignof / per-member offsets at runtime in a separate TU (not just
// at the header static_assert sites) to catch any toolchain divergence
// across translation units.
//
// =====================================================================

#include "Reflection/FArrayProperty.h"
#include "Reflection/FClassProperty.h"
#include "Reflection/FDelegateProperty.h"
#include "Reflection/FInterfaceProperty.h"
#include "Reflection/FMapProperty.h"
#include "Reflection/FMulticastInlineDelegateProperty.h"
#include "Reflection/FMulticastSparseDelegateProperty.h"
#include "Reflection/FObjectProperty.h"
#include "Reflection/FSetProperty.h"
#include "Reflection/FSoftClassProperty.h"
#include "Reflection/FSoftObjectProperty.h"
#include "Reflection/FStructProperty.h"
#include "Reflection/FWeakObjectProperty.h"

#include "Reflection/FSoftObjectPath.h"
#include "Reflection/FWeakObjectPtr.h"

#include <cstddef>
#include <iostream>
#include <type_traits>

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
    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // Placeholder value types (System 5 will swap in full impls; the
    // 8-byte slot must be preserved).
    // -----------------------------------------------------------------
    Check(sizeof(FSoftObjectPath)  == 8, "sizeof(FSoftObjectPath) != 8");
    Check(alignof(FSoftObjectPath) == 8, "alignof(FSoftObjectPath) != 8");
    Check(sizeof(FWeakObjectPtr)   == 8, "sizeof(FWeakObjectPtr) != 8");
    Check(alignof(FWeakObjectPtr)  == 8, "alignof(FWeakObjectPtr) != 8");

    // -----------------------------------------------------------------
    // 112-byte subclasses (104 FProperty + 8 payload):
    //   FObjectProperty, FWeakObjectProperty, FSoftObjectProperty,
    //   FClassProperty, FSoftClassProperty, FInterfaceProperty,
    //   FStructProperty, FSetProperty, FDelegateProperty,
    //   FMulticastInlineDelegateProperty,
    //   FMulticastSparseDelegateProperty.
    // -----------------------------------------------------------------

    Check(sizeof(FObjectProperty)        == 112, "sizeof(FObjectProperty) != 112");
    Check(alignof(FObjectProperty)       == 8,   "alignof(FObjectProperty) != 8");
    Check(offsetof(FObjectProperty, PropertyClass) == 104,
          "FObjectProperty::PropertyClass @ != 104");

    Check(sizeof(FWeakObjectProperty)    == 112, "sizeof(FWeakObjectProperty) != 112");
    Check(alignof(FWeakObjectProperty)   == 8,   "alignof(FWeakObjectProperty) != 8");
    Check(offsetof(FWeakObjectProperty, PropertyClass) == 104,
          "FWeakObjectProperty::PropertyClass @ != 104");

    Check(sizeof(FSoftObjectProperty)    == 112, "sizeof(FSoftObjectProperty) != 112");
    Check(alignof(FSoftObjectProperty)   == 8,   "alignof(FSoftObjectProperty) != 8");
    Check(offsetof(FSoftObjectProperty, PropertyClass) == 104,
          "FSoftObjectProperty::PropertyClass @ != 104");

    Check(sizeof(FClassProperty)         == 112, "sizeof(FClassProperty) != 112");
    Check(alignof(FClassProperty)        == 8,   "alignof(FClassProperty) != 8");
    Check(offsetof(FClassProperty, MetaClass) == 104,
          "FClassProperty::MetaClass @ != 104");

    Check(sizeof(FSoftClassProperty)     == 112, "sizeof(FSoftClassProperty) != 112");
    Check(alignof(FSoftClassProperty)    == 8,   "alignof(FSoftClassProperty) != 8");
    Check(offsetof(FSoftClassProperty, MetaClass) == 104,
          "FSoftClassProperty::MetaClass @ != 104");

    Check(sizeof(FInterfaceProperty)     == 112, "sizeof(FInterfaceProperty) != 112");
    Check(alignof(FInterfaceProperty)    == 8,   "alignof(FInterfaceProperty) != 8");
    Check(offsetof(FInterfaceProperty, InterfaceClass) == 104,
          "FInterfaceProperty::InterfaceClass @ != 104");

    Check(sizeof(FStructProperty)        == 112, "sizeof(FStructProperty) != 112");
    Check(alignof(FStructProperty)       == 8,   "alignof(FStructProperty) != 8");
    Check(offsetof(FStructProperty, Struct) == 104,
          "FStructProperty::Struct @ != 104");

    Check(sizeof(FSetProperty)           == 112, "sizeof(FSetProperty) != 112");
    Check(alignof(FSetProperty)          == 8,   "alignof(FSetProperty) != 8");
    Check(offsetof(FSetProperty, ElementProp) == 104,
          "FSetProperty::ElementProp @ != 104");

    Check(sizeof(FDelegateProperty)      == 112, "sizeof(FDelegateProperty) != 112");
    Check(alignof(FDelegateProperty)     == 8,   "alignof(FDelegateProperty) != 8");
    Check(offsetof(FDelegateProperty, SignatureFunction) == 104,
          "FDelegateProperty::SignatureFunction @ != 104");

    Check(sizeof(FMulticastInlineDelegateProperty)  == 112,
          "sizeof(FMulticastInlineDelegateProperty) != 112");
    Check(alignof(FMulticastInlineDelegateProperty) == 8,
          "alignof(FMulticastInlineDelegateProperty) != 8");
    Check(offsetof(FMulticastInlineDelegateProperty, SignatureFunction) == 104,
          "FMulticastInlineDelegateProperty::SignatureFunction @ != 104");

    Check(sizeof(FMulticastSparseDelegateProperty)  == 112,
          "sizeof(FMulticastSparseDelegateProperty) != 112");
    Check(alignof(FMulticastSparseDelegateProperty) == 8,
          "alignof(FMulticastSparseDelegateProperty) != 8");
    Check(offsetof(FMulticastSparseDelegateProperty, SignatureFunction) == 104,
          "FMulticastSparseDelegateProperty::SignatureFunction @ != 104");

    // -----------------------------------------------------------------
    // 120-byte: FArrayProperty (104 FProperty + 8 Inner + 4 ArrayFlags
    // + 4 pad).
    // -----------------------------------------------------------------
    Check(sizeof(FArrayProperty)         == 120, "sizeof(FArrayProperty) != 120");
    Check(alignof(FArrayProperty)        == 8,   "alignof(FArrayProperty) != 8");
    Check(offsetof(FArrayProperty, Inner)      == 104,
          "FArrayProperty::Inner @ != 104");
    Check(offsetof(FArrayProperty, ArrayFlags) == 112,
          "FArrayProperty::ArrayFlags @ != 112");

    // -----------------------------------------------------------------
    // 128-byte: FMapProperty (104 FProperty + 8 KeyProp + 8 ValueProp
    // + 4 MapFlags + 4 pad).
    // -----------------------------------------------------------------
    Check(sizeof(FMapProperty)           == 128, "sizeof(FMapProperty) != 128");
    Check(alignof(FMapProperty)          == 8,   "alignof(FMapProperty) != 8");
    Check(offsetof(FMapProperty, KeyProp)   == 104,
          "FMapProperty::KeyProp @ != 104");
    Check(offsetof(FMapProperty, ValueProp) == 112,
          "FMapProperty::ValueProp @ != 112");
    Check(offsetof(FMapProperty, MapFlags)  == 120,
          "FMapProperty::MapFlags @ != 120");

    // -----------------------------------------------------------------
    // POD-ness traits. Every subclass is trivially copyable +
    // destructible.
    // -----------------------------------------------------------------
    Check(std::is_trivially_copyable_v<FObjectProperty>,
          "FObjectProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FWeakObjectProperty>,
          "FWeakObjectProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FSoftObjectProperty>,
          "FSoftObjectProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FClassProperty>,
          "FClassProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FSoftClassProperty>,
          "FSoftClassProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FInterfaceProperty>,
          "FInterfaceProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FStructProperty>,
          "FStructProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FArrayProperty>,
          "FArrayProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FMapProperty>,
          "FMapProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FSetProperty>,
          "FSetProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FDelegateProperty>,
          "FDelegateProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FMulticastInlineDelegateProperty>,
          "FMulticastInlineDelegateProperty must be trivially copyable");
    Check(std::is_trivially_copyable_v<FMulticastSparseDelegateProperty>,
          "FMulticastSparseDelegateProperty must be trivially copyable");

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.ReferencePropertiesSizeof: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.ReferencePropertiesSizeof: PASS\n";
    return 0;
}
