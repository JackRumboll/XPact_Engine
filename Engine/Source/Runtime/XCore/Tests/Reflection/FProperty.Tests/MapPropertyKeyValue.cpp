// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/MapPropertyKeyValue.cpp -- FMapProperty distinct
// KeyProp / ValueProp dispatch (XCore-4b §5.5; Phase 4b.4b).
// =====================================================================
//
// Verifies:
//
//   1. FMapProperty.KeyProp and ValueProp are independently
//      accessible.
//   2. The two pointers store distinct values; the FMapProperty's
//      layout puts KeyProp at offset 104 and ValueProp at offset 112.
//   3. FMapProperty.ContainsObjectReference returns true conservatively
//      (the typed walker checks both KeyProp + ValueProp for precise
//      answer).
//   4. FMapProperty CastFlags include kFMapProperty + kFProperty.
//
// =====================================================================

#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "Reflection/EClassCastFlags.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FMapProperty.h"
#include "Reflection/FName.h"
#include "Reflection/FProperty.h"
#include "Reflection/FStrProperty.h"
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
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FInt64Property;
    using ::XCore::Reflect::FIntProperty;
    using ::XCore::Reflect::FMapProperty;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FProperty;
    using ::XCore::Reflect::FStrProperty;
    using ::XCore::Reflect::FStructProperty;
    using ::XCore::Reflect::HasAllCastFlags;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // (1) Default ctor leaves KeyProp / ValueProp at nullptr.
    // -----------------------------------------------------------------
    {
        FMapProperty MapProp(FFieldVariant{}, FName("M"));
        Check(MapProp.GetKeyProp()   == nullptr,
              "FMapProperty default KeyProp != nullptr");
        Check(MapProp.GetValueProp() == nullptr,
              "FMapProperty default ValueProp != nullptr");
    }

    // -----------------------------------------------------------------
    // (2) Set distinct Key / Value props and verify they're separate.
    //
    // Use FIntProperty as key, FStrProperty as value (heterogeneous
    // -- exactly the case the spec calls out for FMapProperty).
    // -----------------------------------------------------------------
    {
        FIntProperty KeyInt(FFieldVariant{}, FName("K"));
        FStrProperty ValStr(FFieldVariant{}, FName("V"));

        FMapProperty MapProp(FFieldVariant{}, FName("M"));
        MapProp.SetKeyProp(&KeyInt);
        MapProp.SetValueProp(&ValStr);

        Check(MapProp.GetKeyProp()   == &KeyInt,
              "FMapProperty SetKeyProp/GetKeyProp round-trip failed");
        Check(MapProp.GetValueProp() == &ValStr,
              "FMapProperty SetValueProp/GetValueProp round-trip failed");
        Check(MapProp.GetKeyProp()   != MapProp.GetValueProp(),
              "FMapProperty KeyProp and ValueProp must be DISTINCT");

        // Verify the layout offset: KeyProp at 104, ValueProp at 112.
        // (Already asserted in ReferencePropertiesSizeof.cpp; replicate
        // the runtime check here for the per-instance memory layout.)
        const auto KeyPropOffset =
            reinterpret_cast<const ::uint8*>(&MapProp.KeyProp)
            - reinterpret_cast<const ::uint8*>(&MapProp);
        Check(KeyPropOffset == 104,
              "FMapProperty::KeyProp not at byte offset 104");

        const auto ValPropOffset =
            reinterpret_cast<const ::uint8*>(&MapProp.ValueProp)
            - reinterpret_cast<const ::uint8*>(&MapProp);
        Check(ValPropOffset == 112,
              "FMapProperty::ValueProp not at byte offset 112");
    }

    // -----------------------------------------------------------------
    // (3) ContainsObjectReference dispatch slot population.
    // -----------------------------------------------------------------
    {
        FMapProperty MapProp(FFieldVariant{}, FName("M"));
        const auto* Vtable = MapProp.GetDispatchTable();
        Check(Vtable != nullptr, "FMapProperty DispatchTable is nullptr");
        Check(Vtable->HasSlot(ESlot::ContainsObjectReference),
              "FMapProperty.ContainsObjectReference slot is not populated");

        ::XCore::TArray<const FStructProperty*> Encountered;
        Check(MapProp.ContainsObjectReference(Encountered),
              "FMapProperty.ContainsObjectReference returned false "
              "(expected true as conservative answer at Phase 4b.4b)");
    }

    // -----------------------------------------------------------------
    // (4) FMapProperty CastFlags.
    // -----------------------------------------------------------------
    {
        const auto Flags = FMapProperty::StaticClass()->GetCastFlags();
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFMapProperty),
              "FMapProperty CastFlags missing own bit");
        Check(HasAllCastFlags(Flags, EClassCastFlags::kFProperty),
              "FMapProperty CastFlags missing kFProperty");
        Check(!HasAllCastFlags(Flags, EClassCastFlags::kFObjectPropertyBase),
              "FMapProperty CastFlags should NOT include kFObjectPropertyBase");
    }

    // -----------------------------------------------------------------
    // (5) MapFlags accessor round-trip.
    // -----------------------------------------------------------------
    {
        FMapProperty MapProp(FFieldVariant{}, FName("M"));
        Check(MapProp.GetMapFlags() == 0, "FMapProperty default MapFlags != 0");
        MapProp.SetMapFlags(0xABCDu);
        Check(MapProp.GetMapFlags() == 0xABCDu,
              "FMapProperty SetMapFlags / GetMapFlags round-trip failed");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.MapPropertyKeyValue: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.MapPropertyKeyValue: PASS\n";
    return 0;
}
