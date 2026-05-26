// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/SizeofAndAlignof.cpp -- ABI lock verification for all
// Phase 4b.5 descriptor types (XCore-4b §7 + §11.3).
// =====================================================================
//
// Verifies sizeof / alignof / member offsets at runtime in a separate
// TU (not just at the header static_assert sites) to catch any
// toolchain divergence.
//
// Phase 4b.5 audit-corrected sizes (see FStruct.h SPEC DRIFT NOTICE):
//
//   FRepRecord                16  (matches spec)
//   FEnumValue                16  (matches spec)
//   FFunctionDescriptor       16  (matches spec)
//   FCppStructOpsFakeVTable  136  (matches spec)
//   FStruct                  112  (spec said 104; actual TArray=24)
//   FScriptStruct            128  (spec said 120; FStruct base = 112)
//   FClass                   224  (spec said 200; FStruct base = 112 +
//                                  ClassReps/NetFields TArray = 24 each)
//   FEnum                     72  (spec said 64; Values TArray = 24)
//   FInterface                64  (spec said 56; InterfaceFunctions = 24)
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/FCppStructOpsFakeVTable.h"
#include "Reflection/FClass.h"
#include "Reflection/FEnum.h"
#include "Reflection/FInterface.h"
#include "Reflection/FRepRecord.h"
#include "Reflection/FScriptStruct.h"
#include "Reflection/FStruct.h"

#include <cstddef>
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
    // FRepRecord (spec-locked 16 bytes; matches Phase 4b.5 actual).
    // -----------------------------------------------------------------
    Check(sizeof(FRepRecord) == 16, "sizeof(FRepRecord) != 16");
    Check(alignof(FRepRecord) == 8, "alignof(FRepRecord) != 8");
    Check(offsetof(FRepRecord, Property) == 0,  "FRepRecord::Property offset != 0");
    Check(offsetof(FRepRecord, Index)    == 8,  "FRepRecord::Index offset != 8");
    Check(offsetof(FRepRecord, _pad)     == 12, "FRepRecord::_pad offset != 12");

    // -----------------------------------------------------------------
    // FEnumValue (spec-locked 16 bytes).
    // -----------------------------------------------------------------
    Check(sizeof(FEnumValue) == 16, "sizeof(FEnumValue) != 16");
    Check(alignof(FEnumValue) == 8, "alignof(FEnumValue) != 8");
    Check(offsetof(FEnumValue, Name)  == 0, "FEnumValue::Name offset != 0");
    Check(offsetof(FEnumValue, Value) == 8, "FEnumValue::Value offset != 8");

    // -----------------------------------------------------------------
    // FFunctionDescriptor (spec-locked 16 bytes).
    // -----------------------------------------------------------------
    Check(sizeof(FFunctionDescriptor) == 16, "sizeof(FFunctionDescriptor) != 16");
    Check(alignof(FFunctionDescriptor) == 8, "alignof(FFunctionDescriptor) != 8");
    Check(offsetof(FFunctionDescriptor, Name)          == 0, "FFunctionDescriptor::Name offset != 0");
    Check(offsetof(FFunctionDescriptor, SignatureHash) == 8, "FFunctionDescriptor::SignatureHash offset != 8");

    // -----------------------------------------------------------------
    // FCppStructOpsFakeVTable (spec-locked 136 bytes).
    // -----------------------------------------------------------------
    Check(sizeof(FCppStructOpsFakeVTable)  == 136, "sizeof(FCppStructOpsFakeVTable) != 136");
    Check(alignof(FCppStructOpsFakeVTable) == 8,   "alignof(FCppStructOpsFakeVTable) != 8");
    Check(offsetof(FCppStructOpsFakeVTable, Capabilities)    == 0, "Capabilities offset != 0");
    Check(offsetof(FCppStructOpsFakeVTable, _reservedHeader) == 4, "_reservedHeader offset != 4");
    Check(offsetof(FCppStructOpsFakeVTable, Slots)           == 8, "Slots offset != 8");
    Check(sizeof(FCppStructOpsFakeVTable::Slots) == 16 * 8, "Slots[16] size != 128");
    Check(static_cast<std::uint32_t>(ECppOpSlot::Count) == 16, "ECppOpSlot::Count != 16");

    // -----------------------------------------------------------------
    // FStruct (audit-corrected 112 bytes; spec said 104).
    // -----------------------------------------------------------------
    Check(sizeof(FStruct)  == 112, "sizeof(FStruct) != 112");
    Check(alignof(FStruct) == 8,   "alignof(FStruct) != 8");
    Check(offsetof(FStruct, NamePrivate)        ==   0, "FStruct::NamePrivate offset != 0");
    Check(offsetof(FStruct, SuperStruct)        ==   8, "FStruct::SuperStruct offset != 8");
    Check(offsetof(FStruct, ChildProperties)    ==  16, "FStruct::ChildProperties offset != 16");
    Check(offsetof(FStruct, PropertiesSize)     ==  24, "FStruct::PropertiesSize offset != 24");
    Check(offsetof(FStruct, MinAlignment)       ==  28, "FStruct::MinAlignment offset != 28");
    Check(offsetof(FStruct, StructFlags)        ==  30, "FStruct::StructFlags offset != 30");
    Check(offsetof(FStruct, PropertyLink)       ==  32, "FStruct::PropertyLink offset != 32");
    Check(offsetof(FStruct, DestructorLink)     ==  40, "FStruct::DestructorLink offset != 40");
    Check(offsetof(FStruct, PostConstructLink)  ==  48, "FStruct::PostConstructLink offset != 48");
    Check(offsetof(FStruct, ObjectRefProperties)==  56, "FStruct::ObjectRefProperties offset != 56");
    Check(offsetof(FStruct, SchemaHash)         ==  80, "FStruct::SchemaHash offset != 80 (audit)");
    Check(offsetof(FStruct, SchemaVersion)      ==  88, "FStruct::SchemaVersion offset != 88 (audit)");
    Check(offsetof(FStruct, UnversionedSchema)  ==  96, "FStruct::UnversionedSchema offset != 96 (audit)");
    Check(offsetof(FStruct, SerializeStructFn)  == 104, "FStruct::SerializeStructFn offset != 104 (audit)");

    // -----------------------------------------------------------------
    // FScriptStruct (audit-corrected 128 bytes; spec said 120).
    // -----------------------------------------------------------------
    Check(sizeof(FScriptStruct)  == 128, "sizeof(FScriptStruct) != 128");
    Check(alignof(FScriptStruct) == 8,   "alignof(FScriptStruct) != 8");
    Check(offsetof(FScriptStruct, Capabilities)     == 112, "FScriptStruct::Capabilities offset != 112 (audit)");
    Check(offsetof(FScriptStruct, _padCapabilities) == 116, "FScriptStruct::_padCapabilities offset != 116");
    Check(offsetof(FScriptStruct, CppOpsTable)      == 120, "FScriptStruct::CppOpsTable offset != 120 (audit)");

    // -----------------------------------------------------------------
    // FClass (audit-corrected 224 bytes; spec said 200).
    // -----------------------------------------------------------------
    Check(sizeof(FClass)  == 224, "sizeof(FClass) != 224");
    Check(alignof(FClass) == 8,   "alignof(FClass) != 8");
    Check(offsetof(FClass, ClassConstructorFn)          == 112, "FClass::ClassConstructorFn offset != 112 (audit)");
    Check(offsetof(FClass, ClassVTableHelperCtorCaller) == 120, "FClass::ClassVTableHelperCtorCaller offset != 120");
    Check(offsetof(FClass, ClassDefaultObject)          == 128, "FClass::ClassDefaultObject offset != 128");
    Check(offsetof(FClass, ClassFlags)                  == 136, "FClass::ClassFlags offset != 136");
    Check(offsetof(FClass, ClassCastFlags)              == 144, "FClass::ClassCastFlags offset != 144");
    Check(offsetof(FClass, ClassWithin)                 == 152, "FClass::ClassWithin offset != 152");
    Check(offsetof(FClass, FirstOwnedClassRep)          == 160, "FClass::FirstOwnedClassRep offset != 160");
    Check(offsetof(FClass, ClassRepCount)               == 164, "FClass::ClassRepCount offset != 164");
    Check(offsetof(FClass, ClassReps)                   == 168, "FClass::ClassReps offset != 168");
    Check(offsetof(FClass, NetFields)                   == 192, "FClass::NetFields offset != 192 (audit)");
    Check(offsetof(FClass, ClassConfigName)             == 216, "FClass::ClassConfigName offset != 216");

    // -----------------------------------------------------------------
    // FEnum (audit-corrected 72 bytes; spec said 64).
    // -----------------------------------------------------------------
    Check(sizeof(FEnum)  == 72, "sizeof(FEnum) != 72");
    Check(alignof(FEnum) == 8,  "alignof(FEnum) != 8");
    Check(offsetof(FEnum, EnumFlags) == 32, "FEnum::EnumFlags offset != 32");
    Check(offsetof(FEnum, Values)    == 40, "FEnum::Values offset != 40");
    Check(offsetof(FEnum, CppForm)   == 64, "FEnum::CppForm offset != 64 (audit)");

    // -----------------------------------------------------------------
    // FInterface (audit-corrected 64 bytes; spec said 56).
    // -----------------------------------------------------------------
    Check(sizeof(FInterface)  == 64, "sizeof(FInterface) != 64");
    Check(alignof(FInterface) == 8,  "alignof(FInterface) != 8");
    Check(offsetof(FInterface, InterfaceFunctions) == 32, "FInterface::InterfaceFunctions offset != 32");
    Check(offsetof(FInterface, InterfaceFlags)     == 56, "FInterface::InterfaceFlags offset != 56 (audit)");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.SizeofAndAlignof: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.SizeofAndAlignof: PASS\n";
    return 0;
}
