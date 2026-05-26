// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FProperty.Tests/SizeofAndAlignof.cpp -- FProperty + FFakeVTable +
// 16 primitive subclasses ABI lock verification (acceptance gate B1 +
// B3 + B7 for Phase 4b.4a).
// =====================================================================
//
// XCore-4b Rev 3, Section 5.3 + Section 5.4 + Section 11.2 (Stage B
// addendum layout table).
//
// Verifies sizeof / alignof at runtime in a separate TU (not just at
// the header static_assert sites) to catch any toolchain divergence
// (e.g., MSVC vs Clang struct-layout differences across translation
// units, which the header alone might not surface).
//
// =====================================================================

#include "Reflection/EClassCastFlags.h"
#include "Reflection/EPropertyFlags.h"
#include "Reflection/FBoolProperty.h"
#include "Reflection/FByteProperty.h"
#include "Reflection/FDoubleProperty.h"
#include "Reflection/FEnumProperty.h"
#include "Reflection/FFakeVTable.h"
#include "Reflection/FFloatProperty.h"
#include "Reflection/FInt16Property.h"
#include "Reflection/FInt64Property.h"
#include "Reflection/FInt8Property.h"
#include "Reflection/FIntProperty.h"
#include "Reflection/FNameProperty.h"
#include "Reflection/FProperty.h"
#include "Reflection/FStrProperty.h"
#include "Reflection/FTextProperty.h"
#include "Reflection/FUInt16Property.h"
#include "Reflection/FUInt32Property.h"
#include "Reflection/FUInt64Property.h"

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
    // FFakeVTable ABI lock (§5.4 + §11.2).
    // -----------------------------------------------------------------
    Check(sizeof(FFakeVTable)  == 128, "sizeof(FFakeVTable) != 128");
    Check(alignof(FFakeVTable) == 8,   "alignof(FFakeVTable) != 8");
    Check(offsetof(FFakeVTable, Capabilities)    == 0,
          "offsetof(FFakeVTable, Capabilities) != 0");
    Check(offsetof(FFakeVTable, _reservedHeader) == 4,
          "offsetof(FFakeVTable, _reservedHeader) != 4");
    Check(offsetof(FFakeVTable, Slots)           == 8,
          "offsetof(FFakeVTable, Slots) != 8");
    Check(sizeof(FFakeVTable::Slots) == 15 * 8,
          "FFakeVTable::Slots size != 120 (15 * 8)");

    // ESlot count.
    Check(static_cast<std::uint32_t>(ESlot::Count) == 15,
          "ESlot::Count != 15");

    // -----------------------------------------------------------------
    // EPropertyFlags ABI lock.
    // -----------------------------------------------------------------
    Check(sizeof(EPropertyFlags) == 8,
          "sizeof(EPropertyFlags) != 8");
    Check(std::is_same_v<std::underlying_type_t<EPropertyFlags>, std::uint64_t>,
          "EPropertyFlags underlying type must be uint64");

    // -----------------------------------------------------------------
    // FProperty base ABI lock (§5.3 + §11.2; locked at 104 bytes).
    // -----------------------------------------------------------------
    Check(sizeof(FProperty)  == 104, "sizeof(FProperty) != 104");
    Check(alignof(FProperty) == 8,   "alignof(FProperty) != 8");

    // Per-member offsets.
    Check(offsetof(FProperty, ArrayDim)                      == 32,
          "FProperty::ArrayDim @ != 32");
    Check(offsetof(FProperty, ElementSize)                   == 36,
          "FProperty::ElementSize @ != 36");
    Check(offsetof(FProperty, Offset)                        == 40,
          "FProperty::Offset @ != 40");
    Check(offsetof(FProperty, PropertyFlags)                 == 48,
          "FProperty::PropertyFlags @ != 48");
    Check(offsetof(FProperty, RepIndex)                      == 56,
          "FProperty::RepIndex @ != 56");
    Check(offsetof(FProperty, BlueprintReplicationCondition) == 58,
          "FProperty::BlueprintReplicationCondition @ != 58");
    Check(offsetof(FProperty, RepNotifyFunc)                 == 64,
          "FProperty::RepNotifyFunc @ != 64");
    Check(offsetof(FProperty, BlueprintFlags)                == 72,
          "FProperty::BlueprintFlags @ != 72");
    Check(offsetof(FProperty, EditFlags)                     == 76,
          "FProperty::EditFlags @ != 76");
    Check(offsetof(FProperty, PropertyLinkNext)              == 80,
          "FProperty::PropertyLinkNext @ != 80");
    Check(offsetof(FProperty, DestructorLinkNext)            == 88,
          "FProperty::DestructorLinkNext @ != 88");
    Check(offsetof(FProperty, DispatchTable)                 == 96,
          "FProperty::DispatchTable @ != 96");

    // -----------------------------------------------------------------
    // Per-subclass ABI lock (§11.2 layout table).
    //
    // Subclass payloads start at offset 104 (the byte after
    // FProperty::DispatchTable).
    // -----------------------------------------------------------------

    // 104-byte subclasses: no payload beyond FProperty base.
    Check(sizeof(FInt8Property)   == 104, "sizeof(FInt8Property) != 104");
    Check(sizeof(FInt16Property)  == 104, "sizeof(FInt16Property) != 104");
    Check(sizeof(FIntProperty)    == 104, "sizeof(FIntProperty) != 104");
    Check(sizeof(FInt64Property)  == 104, "sizeof(FInt64Property) != 104");
    Check(sizeof(FUInt16Property) == 104, "sizeof(FUInt16Property) != 104");
    Check(sizeof(FUInt32Property) == 104, "sizeof(FUInt32Property) != 104");
    Check(sizeof(FUInt64Property) == 104, "sizeof(FUInt64Property) != 104");
    Check(sizeof(FFloatProperty)  == 104, "sizeof(FFloatProperty) != 104");
    Check(sizeof(FDoubleProperty) == 104, "sizeof(FDoubleProperty) != 104");
    Check(sizeof(FNameProperty)   == 104, "sizeof(FNameProperty) != 104");
    Check(sizeof(FStrProperty)    == 104, "sizeof(FStrProperty) != 104");
    Check(sizeof(FTextProperty)   == 104, "sizeof(FTextProperty) != 104");

    // 112-byte subclasses: 8-byte payload.
    Check(sizeof(FByteProperty) == 112, "sizeof(FByteProperty) != 112");
    Check(offsetof(FByteProperty, UnderlyingEnum) == 104,
          "FByteProperty::UnderlyingEnum @ != 104");

    // 120-byte subclasses (FBoolProperty + FEnumProperty).
    Check(sizeof(FBoolProperty) == 120, "sizeof(FBoolProperty) != 120");
    Check(offsetof(FBoolProperty, FieldSize)  == 104,
          "FBoolProperty::FieldSize @ != 104");
    Check(offsetof(FBoolProperty, ByteOffset) == 105,
          "FBoolProperty::ByteOffset @ != 105");
    Check(offsetof(FBoolProperty, ByteMask)   == 106,
          "FBoolProperty::ByteMask @ != 106");
    Check(offsetof(FBoolProperty, FieldMask)  == 107,
          "FBoolProperty::FieldMask @ != 107");
    Check(offsetof(FBoolProperty, SetBitFunc) == 112,
          "FBoolProperty::SetBitFunc @ != 112");

    Check(sizeof(FEnumProperty) == 120, "sizeof(FEnumProperty) != 120");
    Check(offsetof(FEnumProperty, UnderlyingProp) == 104,
          "FEnumProperty::UnderlyingProp @ != 104");
    Check(offsetof(FEnumProperty, Enum)           == 112,
          "FEnumProperty::Enum @ != 112");

    // -----------------------------------------------------------------
    // Alignment locks (every FProperty subclass is alignas(8)).
    // -----------------------------------------------------------------
    Check(alignof(FInt8Property)   == 8, "alignof(FInt8Property) != 8");
    Check(alignof(FInt16Property)  == 8, "alignof(FInt16Property) != 8");
    Check(alignof(FIntProperty)    == 8, "alignof(FIntProperty) != 8");
    Check(alignof(FInt64Property)  == 8, "alignof(FInt64Property) != 8");
    Check(alignof(FUInt16Property) == 8, "alignof(FUInt16Property) != 8");
    Check(alignof(FUInt32Property) == 8, "alignof(FUInt32Property) != 8");
    Check(alignof(FUInt64Property) == 8, "alignof(FUInt64Property) != 8");
    Check(alignof(FFloatProperty)  == 8, "alignof(FFloatProperty) != 8");
    Check(alignof(FDoubleProperty) == 8, "alignof(FDoubleProperty) != 8");
    Check(alignof(FNameProperty)   == 8, "alignof(FNameProperty) != 8");
    Check(alignof(FStrProperty)    == 8, "alignof(FStrProperty) != 8");
    Check(alignof(FTextProperty)   == 8, "alignof(FTextProperty) != 8");
    Check(alignof(FByteProperty)   == 8, "alignof(FByteProperty) != 8");
    Check(alignof(FBoolProperty)   == 8, "alignof(FBoolProperty) != 8");
    Check(alignof(FEnumProperty)   == 8, "alignof(FEnumProperty) != 8");

    // -----------------------------------------------------------------
    // POD-ness traits. Every FProperty subclass is trivially copyable
    // and trivially destructible (per Phase 4b.4a header static_asserts).
    // -----------------------------------------------------------------
    Check(std::is_trivially_copyable_v<FProperty>,
          "FProperty must be trivially copyable");
    Check(std::is_trivially_destructible_v<FProperty>,
          "FProperty must be trivially destructible");

    if (g_FailureCount > 0)
    {
        std::cerr << "FProperty.SizeofAndAlignof: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FProperty.SizeofAndAlignof: PASS\n";
    return 0;
}
