// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/NotFoundReturnsNullptr.cpp -- defensive gate.
// =====================================================================
//
// EXERCISED PATHS:
//
//   * Find* on an unregistered name returns nullptr (no crash, no
//     assert, no UB).
//
//   * Find* on a registered name in the WRONG kind's map returns
//     nullptr (e.g., RegisterClass("Foo") then FindStruct("Foo") ->
//     nullptr).
//
//   * Find* with NAME_None always returns nullptr.
//
//   * Get*OwningModule on an unregistered name returns NAME_None.
//
//   * Empty registry: every Find* returns nullptr; every Get*Count
//     returns 0.
//
// =====================================================================

#include "XReflectionRuntime.h"

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FEnum.h"
#include "Reflection/FInterface.h"
#include "Reflection/FName.h"
#include "Reflection/FScriptStruct.h"
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
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FEnum;
    using ::XCore::Reflect::FFieldVariant;
    using ::XCore::Reflect::FInterface;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FScriptStruct;
    using ::XCore::Reflect::FStruct;
    using ::XCore::Reflect::XReflectionRuntime;

    ::XCore::HAL::FMemory::__Init();

    XReflectionRuntime::EmptyForTesting();

    // Empty registry: every Find* returns nullptr.
    Check(XReflectionRuntime::FindClass(FName("Anything"))        == nullptr, "Empty registry FindClass != nullptr");
    Check(XReflectionRuntime::FindStruct(FName("Anything"))       == nullptr, "Empty registry FindStruct != nullptr");
    Check(XReflectionRuntime::FindScriptStruct(FName("Anything")) == nullptr, "Empty registry FindScriptStruct != nullptr");
    Check(XReflectionRuntime::FindEnum(FName("Anything"))         == nullptr, "Empty registry FindEnum != nullptr");
    Check(XReflectionRuntime::FindInterface(FName("Anything"))    == nullptr, "Empty registry FindInterface != nullptr");

    // NAME_None always returns nullptr (sentinel-rejection contract).
    Check(XReflectionRuntime::FindClass(FName())        == nullptr, "FindClass(NAME_None) != nullptr");
    Check(XReflectionRuntime::FindStruct(FName())       == nullptr, "FindStruct(NAME_None) != nullptr");
    Check(XReflectionRuntime::FindScriptStruct(FName()) == nullptr, "FindScriptStruct(NAME_None) != nullptr");
    Check(XReflectionRuntime::FindEnum(FName())         == nullptr, "FindEnum(NAME_None) != nullptr");
    Check(XReflectionRuntime::FindInterface(FName())    == nullptr, "FindInterface(NAME_None) != nullptr");

    // Counts are zero on empty registry.
    Check(XReflectionRuntime::GetClassCount() == 0,        "Empty registry ClassCount != 0");
    Check(XReflectionRuntime::GetStructCount() == 0,       "Empty registry StructCount != 0");
    Check(XReflectionRuntime::GetScriptStructCount() == 0, "Empty registry ScriptStructCount != 0");
    Check(XReflectionRuntime::GetEnumCount() == 0,         "Empty registry EnumCount != 0");
    Check(XReflectionRuntime::GetInterfaceCount() == 0,    "Empty registry InterfaceCount != 0");

    // Get*OwningModule on absent name returns NAME_None.
    Check(XReflectionRuntime::GetClassOwningModule(FName("None")).IsNone(),
          "GetClassOwningModule on absent name did not return NAME_None");
    Check(XReflectionRuntime::GetStructOwningModule(FName("None")).IsNone(),
          "GetStructOwningModule on absent name did not return NAME_None");
    Check(XReflectionRuntime::GetEnumOwningModule(FName("None")).IsNone(),
          "GetEnumOwningModule on absent name did not return NAME_None");
    Check(XReflectionRuntime::GetInterfaceOwningModule(FName("None")).IsNone(),
          "GetInterfaceOwningModule on absent name did not return NAME_None");

    // -----------------------------------------------------------------
    // Cross-kind isolation: registering an FClass with name "Foo" does
    // NOT make FindStruct("Foo") resolve (the maps are independent).
    // -----------------------------------------------------------------
    {
        FClass C(FName("CrossKindFoo"), nullptr);
        Check(XReflectionRuntime::RegisterClass(&C), "RegisterClass failed");
        Check(XReflectionRuntime::FindClass(FName("CrossKindFoo")) == &C,
              "FindClass did not resolve the just-registered class");
        Check(XReflectionRuntime::FindStruct(FName("CrossKindFoo")) == nullptr,
              "FindStruct returned non-null for an FName registered only as a class");
        Check(XReflectionRuntime::FindScriptStruct(FName("CrossKindFoo")) == nullptr,
              "FindScriptStruct returned non-null for an FName registered only as a class");
        Check(XReflectionRuntime::FindEnum(FName("CrossKindFoo")) == nullptr,
              "FindEnum returned non-null for an FName registered only as a class");
        Check(XReflectionRuntime::FindInterface(FName("CrossKindFoo")) == nullptr,
              "FindInterface returned non-null for an FName registered only as a class");
    }

    XReflectionRuntime::EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.NotFoundReturnsNullptr: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.NotFoundReturnsNullptr: PASS\n";
    return 0;
}
