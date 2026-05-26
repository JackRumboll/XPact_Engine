// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/ArrayPropertyTraversal.cpp -- FArrayProperty
// Inner-pointer wiring + ContainsObjectReference dispatch verification
// (XCore-4b §5.4 + §5.5; Phase 4b.4b acceptance gate B7 + FIX-13).
// =====================================================================
//
// Verifies:
//
//   1. FArrayProperty.Inner accessor round-trip.
//   2. FArrayProperty.ContainsObjectReference dispatch slot is
//      populated (Capabilities bit set; slot is non-null) and returns
//      true (conservative answer at Phase 4b.4b -- the typed walker
//      consults Inner for the precise answer).
//   3. The Inner FProperty's own ContainsObjectReference returns the
//      expected value:
//         a. FInt32 inner -> false (primitive, no object ref).
//         b. FObject inner -> true (the slot IS an XObject reference).
//   4. FArrayProperty CastFlags include kFArrayProperty + kFProperty.
//
// =====================================================================

#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FArrayProperty.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FObjectProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FStructProperty.h"

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
    using ::XCore::Reflect::EClassCastFlags;
    using ::XCore::Reflect::ESlot;
    using ::XCore::Reflect::FArrayProperty;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FIntProperty;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FObjectProperty;
    using ::XCore::Reflect::FProperty;
    using ::XCore::Reflect::FStructProperty;
    using ::XCore::Reflect::HasAllCastFlags;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // (1) Inner accessor round-trip.
    // -----------------------------------------------------------------
    {
        FArrayProperty ArrProp(FFieldVariant{}, FName("Arr"));
        Check(ArrProp.GetInner() == nullptr,
              "FArrayProperty default Inner != nullptr");

        FIntProperty IntInner(FFieldVariant{}, FName("Inner"));
        ArrProp.SetInner(&IntInner);
        Check(ArrProp.GetInner() == &IntInner,
              "FArrayProperty SetInner / GetInner round-trip failed");
    }

    // -----------------------------------------------------------------
    // (2) ContainsObjectReference dispatch slot population.
    // -----------------------------------------------------------------
    {
        FArrayProperty ArrProp(FFieldVariant{}, FName("Arr"));

        const auto* Vtable = ArrProp.GetDispatchTable();
        Check(Vtable != nullptr, "FArrayProperty DispatchTable is nullptr");
        Check(Vtable->HasSlot(ESlot::ContainsObjectReference),
              "FArrayProperty.ContainsObjectReference slot is not populated");

        // Conservative-true: the FArrayProperty dispatch slot returns
        // true unconditionally at Phase 4b.4b. The typed walker checks
        // Inner->ContainsObjectReference for the precise answer.
        ::XCore::TArray<const FStructProperty*> Encountered;
        Check(ArrProp.ContainsObjectReference(Encountered),
              "FArrayProperty.ContainsObjectReference returned false "
              "(expected true as conservative answer at Phase 4b.4b)");
    }

    // -----------------------------------------------------------------
    // (3a) Inner = FIntProperty: Inner->ContainsObjectReference -> false.
    //
    // FIntProperty's FFakeVTable has the ContainsObjectReference slot
    // UNPOPULATED (Capabilities bit clear; slot is nullptr). The
    // wrapper short-circuits and returns false. This is the precise
    // answer FStruct::ObjectRefProperties population (FIX-13) needs
    // to know.
    // -----------------------------------------------------------------
    {
        FIntProperty IntProp(FFieldVariant{}, FName("I"));
        const auto* Vtable = IntProp.GetDispatchTable();
        Check(Vtable != nullptr, "FIntProperty DispatchTable is nullptr");
        Check(!Vtable->HasSlot(ESlot::ContainsObjectReference),
              "FIntProperty.ContainsObjectReference slot SHOULD be "
              "unpopulated (primitive types have no object refs)");

        ::XCore::TArray<const FStructProperty*> Encountered;
        Check(!IntProp.ContainsObjectReference(Encountered),
              "FIntProperty.ContainsObjectReference returned true "
              "(primitives should NOT contain object refs)");
    }

    // -----------------------------------------------------------------
    // (3b) Inner = FObjectProperty: Inner->ContainsObjectReference -> true.
    // -----------------------------------------------------------------
    {
        FObjectProperty ObjProp(FFieldVariant{}, FName("O"));
        const auto* Vtable = ObjProp.GetDispatchTable();
        Check(Vtable != nullptr, "FObjectProperty DispatchTable is nullptr");
        Check(Vtable->HasSlot(ESlot::ContainsObjectReference),
              "FObjectProperty.ContainsObjectReference slot SHOULD be populated");

        ::XCore::TArray<const FStructProperty*> Encountered;
        Check(ObjProp.ContainsObjectReference(Encountered),
              "FObjectProperty.ContainsObjectReference returned false "
              "(object refs MUST be reported as roots)");
    }

    // -----------------------------------------------------------------
    // (4) FArrayProperty CastFlags.
    // -----------------------------------------------------------------
    {
        const auto Flags = FArrayProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFArrayProperty),
              "FArrayProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FArrayProperty CastFlags missing kFProperty");
        // FArrayProperty does NOT include kFObjectPropertyBase
        // (container, not object subtype).
        Check(!HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FArrayProperty CastFlags should NOT include kFObjectPropertyBase");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.ArrayPropertyTraversal: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.ArrayPropertyTraversal: PASS\n";
    return 0;
}
