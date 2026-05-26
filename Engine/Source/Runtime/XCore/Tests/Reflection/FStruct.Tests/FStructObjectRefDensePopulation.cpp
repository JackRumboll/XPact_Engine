// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FStructObjectRefDensePopulation.cpp -- FStruct::Link
// populates ObjectRefProperties from properties whose
// ContainsObjectReference returns true (XCore-4b §7.1 + FIX-13).
// =====================================================================
//
// Builds an FStruct with a mix of:
//
//   1 x FIntProperty       (primitive; ContainsObjectReference = false)
//   1 x FObjectProperty    (strong XObject*; ContainsObjectReference = true)
//   1 x FFloatProperty     (primitive; ContainsObjectReference = false)
//   1 x FDelegateProperty  (delegate; ContainsObjectReference = true)
//
// Calls FStruct::Link and verifies:
//
//   1. ObjectRefProperties contains exactly 2 entries (the
//      FObjectProperty + the FDelegateProperty).
//   2. The order matches the ChildProperties walk (FObject first,
//      Delegate second, given declaration order).
//   3. Primitives are not in ObjectRefProperties.
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FDelegateProperty.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FObjectProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FStruct.h"

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
    using namespace ::XCore::Reflect;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Build the struct + 4 properties.
    // -----------------------------------------------------------------
    FStruct Struct(FName("TestStruct"), nullptr);

    FIntProperty       IntProp  (FFieldVariant{}, FName("Score"));
    FObjectProperty    ObjProp  (FFieldVariant{}, FName("Owner"));
    FFloatProperty     FltProp  (FFieldVariant{}, FName("Speed"));
    FDelegateProperty  DelProp  (FFieldVariant{}, FName("OnDeath"));

    // Thread: head=IntProp -> ObjProp -> FltProp -> DelProp -> nullptr.
    Struct.ChildProperties = &IntProp;
    IntProp.SetNext(&ObjProp);
    ObjProp.SetNext(&FltProp);
    FltProp.SetNext(&DelProp);
    DelProp.SetNext(nullptr);

    // -----------------------------------------------------------------
    // Pre-Link: ObjectRefProperties is empty.
    // -----------------------------------------------------------------
    Check(Struct.GetObjectRefProperties().Num() == 0,
          "ObjectRefProperties not empty pre-Link");

    // -----------------------------------------------------------------
    // Link.
    // -----------------------------------------------------------------
    Struct.Link();

    // -----------------------------------------------------------------
    // ObjectRefProperties should contain ObjProp + DelProp only.
    //
    // FObjectProperty: ContainsObjectReference slot returns true
    // (strong XObject* IS a GC root).
    //
    // FDelegateProperty: ContainsObjectReference slot returns true
    // (the delegate Target field IS a GC root).
    //
    // FIntProperty + FFloatProperty: ContainsObjectReference slot is
    // not populated; the FProperty wrapper returns false. They do NOT
    // appear in ObjectRefProperties.
    // -----------------------------------------------------------------
    const auto& Refs = Struct.GetObjectRefProperties();
    Check(Refs.Num() == 2,
          "ObjectRefProperties count != 2 (expected ObjProp + DelProp)");

    if (Refs.Num() == 2)
    {
        // ChildProperties walk order: ObjProp comes before DelProp.
        Check(Refs[0] == &ObjProp,
              "ObjectRefProperties[0] != &ObjProp (expected first encountered)");
        Check(Refs[1] == &DelProp,
              "ObjectRefProperties[1] != &DelProp (expected second encountered)");
    }

    // -----------------------------------------------------------------
    // Verify primitives are NOT in ObjectRefProperties.
    // -----------------------------------------------------------------
    for (::int32 Idx = 0; Idx < Refs.Num(); ++Idx)
    {
        Check(Refs[Idx] != &IntProp, "IntProp erroneously in ObjectRefProperties");
        Check(Refs[Idx] != &FltProp, "FltProp erroneously in ObjectRefProperties");
    }

    // -----------------------------------------------------------------
    // Idempotency: Link again -> same set.
    // -----------------------------------------------------------------
    Struct.Link();
    Check(Struct.GetObjectRefProperties().Num() == 2,
          "ObjectRefProperties count != 2 after second Link");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FStructObjectRefDensePopulation: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FStructObjectRefDensePopulation: PASS\n";
    return 0;
}
