// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/ContainsObjectReferenceMatrix.cpp -- comprehensive
// ContainsObjectReference dispatch verification across all FProperty
// subclasses (XCore-4b §5.4 + FIX-13; Phase 4b.4b).
// =====================================================================
//
// This is the GC-scan reachability gate. For every FProperty subclass,
// verify that ContainsObjectReference returns the expected truth value
// so FStruct::ObjectRefProperties population (FIX-13) at FClass::Link
// time produces the correct GC-root set.
//
// EXPECTED TRUTH TABLE:
//
//   Primitive (Phase 4b.4a)               -> FALSE (slot unpopulated;
//                                                    wrapper returns false)
//     FInt8/16/32/64, FUInt16/32/64,
//     FFloat, FDouble, FByte, FBool,
//     FName, FStr, FText, FEnum
//
//   Strong object refs (Phase 4b.4b)      -> TRUE
//     FObjectProperty, FClassProperty,
//     FInterfaceProperty
//
//   Weak / soft refs (Phase 4b.4b)        -> FALSE (NOT GC roots)
//     FWeakObjectProperty,
//     FSoftObjectProperty,
//     FSoftClassProperty
//
//   Delegates (Phase 4b.4b)               -> TRUE (Target field is FObject*)
//     FDelegateProperty,
//     FMulticastInlineDelegateProperty,
//     FMulticastSparseDelegateProperty
//
//   Containers (Phase 4b.4b)              -> TRUE conservative
//     FArrayProperty, FMapProperty,
//     FSetProperty, FStructProperty
//     (The typed walker checks Inner / Key / Value / Element / Struct
//      for the precise answer; the slot returns conservative-true at
//      Phase 4b.4b to ensure no GC roots are missed. FStructProperty
//      is the exception -- it returns FALSE pending Phase 4b.5 FStruct
//      ObjectRefProperties hook-up, as the dispatch shape doesn't
//      carry per-instance struct context at Phase 4b.4b.)
//
// =====================================================================

#include "Containers/TArray.h"
#include "HAL/FMemory.h"

#include "Reflection/FArrayProperty.h"
#include "Reflection/FBoolProperty.h"
#include "Reflection/FByteProperty.h"
#include "Reflection/FClassProperty.h"
#include "Reflection/FDelegateProperty.h"
#include "Reflection/FDoubleProperty.h"
#include "Reflection/FEnumProperty.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FInt16Property.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FInt8Property.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FInterfaceProperty.h"
#include "Reflection/FMapProperty.h"
#include "Reflection/FMulticastInlineDelegateProperty.h"
#include "Reflection/FMulticastSparseDelegateProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FNameProperty.h"
#include "Reflection/FObjectProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FSetProperty.h"
#include "Reflection/FSoftClassProperty.h"
#include "Reflection/FSoftObjectProperty.h"
#include "Reflection/FStrProperty.h"
#include "Reflection/FStructProperty.h"
#include "Reflection/FTextProperty.h"
#include "Reflection/FUInt16Property.h"
#include "Reflection/FUInt32Property.h"
#include "Reflection/FUInt64Property.h"
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

    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FStructProperty;

    template <typename PropT>
    void CheckContainsObjectRef(const char* TypeName, bool bExpected)
    {
        PropT Prop(FFieldVariant{}, FName("F"));

        ::XCore::TArray<const FStructProperty*> Encountered;
        const bool bActual = Prop.ContainsObjectReference(Encountered);
        if (bActual != bExpected)
        {
            Check(false,
                  (std::string(TypeName) + ".ContainsObjectReference returned "
                   + (bActual ? "true" : "false") + ", expected "
                   + (bExpected ? "true" : "false")).c_str());
        }
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using namespace ::XCore::Reflect;

    // -----------------------------------------------------------------
    // Primitive types -- FALSE.
    // (Phase 4b.4a populates these with NO ContainsObjectReference
    // slot; the wrapper returns false for unpopulated slots.)
    // -----------------------------------------------------------------
    CheckContainsObjectRef<FInt8Property>  ("FInt8Property",   false);
    CheckContainsObjectRef<FInt16Property> ("FInt16Property",  false);
    CheckContainsObjectRef<FIntProperty>   ("FIntProperty",    false);
    CheckContainsObjectRef<FInt64Property> ("FInt64Property",  false);
    CheckContainsObjectRef<FUInt16Property>("FUInt16Property", false);
    CheckContainsObjectRef<FUInt32Property>("FUInt32Property", false);
    CheckContainsObjectRef<FUInt64Property>("FUInt64Property", false);
    CheckContainsObjectRef<FFloatProperty> ("FFloatProperty",  false);
    CheckContainsObjectRef<FDoubleProperty>("FDoubleProperty", false);
    CheckContainsObjectRef<FByteProperty>  ("FByteProperty",   false);
    CheckContainsObjectRef<FBoolProperty>  ("FBoolProperty",   false);
    CheckContainsObjectRef<FNameProperty>  ("FNameProperty",   false);
    CheckContainsObjectRef<FStrProperty>   ("FStrProperty",    false);
    CheckContainsObjectRef<FTextProperty>  ("FTextProperty",   false);
    CheckContainsObjectRef<FEnumProperty>  ("FEnumProperty",   false);

    // -----------------------------------------------------------------
    // Strong object refs -- TRUE.
    // -----------------------------------------------------------------
    CheckContainsObjectRef<FObjectProperty>   ("FObjectProperty",    true);
    CheckContainsObjectRef<FClassProperty>    ("FClassProperty",     true);
    CheckContainsObjectRef<FInterfaceProperty>("FInterfaceProperty", true);

    // -----------------------------------------------------------------
    // Weak / soft refs -- FALSE (not GC roots).
    // -----------------------------------------------------------------
    CheckContainsObjectRef<FWeakObjectProperty>("FWeakObjectProperty", false);
    CheckContainsObjectRef<FSoftObjectProperty>("FSoftObjectProperty", false);
    CheckContainsObjectRef<FSoftClassProperty> ("FSoftClassProperty",  false);

    // -----------------------------------------------------------------
    // Delegates -- TRUE.
    // -----------------------------------------------------------------
    CheckContainsObjectRef<FDelegateProperty>                 ("FDelegateProperty",                 true);
    CheckContainsObjectRef<FMulticastInlineDelegateProperty>  ("FMulticastInlineDelegateProperty",  true);
    CheckContainsObjectRef<FMulticastSparseDelegateProperty>  ("FMulticastSparseDelegateProperty",  true);

    // -----------------------------------------------------------------
    // Containers (FArray / FMap / FSet) -- TRUE conservative.
    // The typed walker uses Inner->ContainsObjectReference for the
    // precise answer; the slot returns conservative-true.
    // -----------------------------------------------------------------
    CheckContainsObjectRef<FArrayProperty>("FArrayProperty", true);
    CheckContainsObjectRef<FMapProperty>  ("FMapProperty",   true);
    CheckContainsObjectRef<FSetProperty>  ("FSetProperty",   true);

    // -----------------------------------------------------------------
    // FStructProperty -- FALSE pending Phase 4b.5.
    //
    // At Phase 4b.4b, FStructProperty's slot returns false because the
    // dispatch shape doesn't carry per-instance struct context. Phase
    // 4b.5 will rewire to delegate to Struct->ObjectRefProperties.
    // Documented in FStructProperty.cpp.
    // -----------------------------------------------------------------
    CheckContainsObjectRef<FStructProperty>("FStructProperty", false);

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.ContainsObjectReferenceMatrix: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.ContainsObjectReferenceMatrix: PASS\n";
    return 0;
}
