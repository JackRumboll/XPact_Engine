// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/CountApi.cpp -- diagnostic-count gate.
// =====================================================================
//
// EXERCISED PATHS:
//
//   * GetClassCount / GetStructCount / GetScriptStructCount /
//     GetEnumCount / GetInterfaceCount return the correct count after
//     a sequence of registrations.
//
//   * Counts increment + decrement correctly across Register* and
//     Unregister* sequences.
//
//   * FScriptStruct registrations increment BOTH the FScriptStruct
//     count AND the FStruct count (because the FStruct view of the
//     FScriptStruct lands in the AllStructs array as well).
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

#include <cstdio>
#include <iostream>
#include <memory>
#include <vector>

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

    // -----------------------------------------------------------------
    // Initial state: every count == 0.
    // -----------------------------------------------------------------
    Check(XReflectionRuntime::GetClassCount() == 0,        "Initial ClassCount != 0");
    Check(XReflectionRuntime::GetStructCount() == 0,       "Initial StructCount != 0");
    Check(XReflectionRuntime::GetScriptStructCount() == 0, "Initial ScriptStructCount != 0");
    Check(XReflectionRuntime::GetEnumCount() == 0,         "Initial EnumCount != 0");
    Check(XReflectionRuntime::GetInterfaceCount() == 0,    "Initial InterfaceCount != 0");

    // -----------------------------------------------------------------
    // Register 7 classes, 3 structs, 2 script structs, 5 enums, 4 interfaces.
    //
    // The descriptor types are non-copyable + non-movable so we hold
    // them via unique_ptr to enable vector::reserve.
    // -----------------------------------------------------------------
    std::vector<std::unique_ptr<FClass>> Classes;
    Classes.reserve(7);
    for (int I = 0; I < 7; ++I)
    {
        char Buf[32];
        std::snprintf(Buf, sizeof(Buf), "C_%d", I);
        Classes.emplace_back(std::make_unique<FClass>(FName(Buf), nullptr));
        Check(XReflectionRuntime::RegisterClass(Classes[I].get()), "RegisterClass failed");
    }
    Check(XReflectionRuntime::GetClassCount() == 7, "ClassCount != 7 after 7 RegisterClass");

    std::vector<std::unique_ptr<FStruct>> Structs;
    Structs.reserve(3);
    for (int I = 0; I < 3; ++I)
    {
        char Buf[32];
        std::snprintf(Buf, sizeof(Buf), "S_%d", I);
        Structs.emplace_back(std::make_unique<FStruct>(FName(Buf), nullptr));
        Check(XReflectionRuntime::RegisterStruct(Structs[I].get()), "RegisterStruct failed");
    }
    Check(XReflectionRuntime::GetStructCount() == 3, "StructCount != 3 after 3 RegisterStruct");

    std::vector<std::unique_ptr<FScriptStruct>> ScriptStructs;
    ScriptStructs.reserve(2);
    for (int I = 0; I < 2; ++I)
    {
        char Buf[32];
        std::snprintf(Buf, sizeof(Buf), "SS_%d", I);
        ScriptStructs.emplace_back(std::make_unique<FScriptStruct>(FName(Buf), nullptr, 0u, nullptr));
        Check(XReflectionRuntime::RegisterScriptStruct(ScriptStructs[I].get()),
              "RegisterScriptStruct failed");
    }
    Check(XReflectionRuntime::GetScriptStructCount() == 2,
          "ScriptStructCount != 2 after 2 RegisterScriptStruct");
    // FStruct count includes both FStruct and FScriptStruct entries.
    Check(XReflectionRuntime::GetStructCount() == 5,
          "StructCount != 5 after 3 FStruct + 2 FScriptStruct");

    std::vector<std::unique_ptr<FEnum>> Enums;
    Enums.reserve(5);
    for (int I = 0; I < 5; ++I)
    {
        char Buf[32];
        std::snprintf(Buf, sizeof(Buf), "E_%d", I);
        Enums.emplace_back(std::make_unique<FEnum>(FFieldVariant(), FName(Buf)));
        Check(XReflectionRuntime::RegisterEnum(Enums[I].get()), "RegisterEnum failed");
    }
    Check(XReflectionRuntime::GetEnumCount() == 5, "EnumCount != 5 after 5 RegisterEnum");

    std::vector<std::unique_ptr<FInterface>> Interfaces;
    Interfaces.reserve(4);
    for (int I = 0; I < 4; ++I)
    {
        char Buf[32];
        std::snprintf(Buf, sizeof(Buf), "I_%d", I);
        Interfaces.emplace_back(std::make_unique<FInterface>(FFieldVariant(), FName(Buf)));
        Check(XReflectionRuntime::RegisterInterface(Interfaces[I].get()), "RegisterInterface failed");
    }
    Check(XReflectionRuntime::GetInterfaceCount() == 4,
          "InterfaceCount != 4 after 4 RegisterInterface");

    // -----------------------------------------------------------------
    // Unregister sequence: counts decrement correctly.
    // -----------------------------------------------------------------
    XReflectionRuntime::UnregisterClass(FName("C_0"));
    Check(XReflectionRuntime::GetClassCount() == 6, "ClassCount != 6 after UnregisterClass(C_0)");
    XReflectionRuntime::UnregisterClass(FName("C_1"));
    Check(XReflectionRuntime::GetClassCount() == 5, "ClassCount != 5 after UnregisterClass(C_1)");

    XReflectionRuntime::UnregisterStruct(FName("S_0"));
    Check(XReflectionRuntime::GetStructCount() == 4, "StructCount != 4 after UnregisterStruct(S_0)");

    XReflectionRuntime::UnregisterScriptStruct(FName("SS_0"));
    Check(XReflectionRuntime::GetScriptStructCount() == 1,
          "ScriptStructCount != 1 after UnregisterScriptStruct(SS_0)");
    // Struct count also decrements (the FScriptStruct's FStruct view is purged).
    Check(XReflectionRuntime::GetStructCount() == 3,
          "StructCount != 3 after UnregisterScriptStruct(SS_0)");

    XReflectionRuntime::UnregisterEnum(FName("E_0"));
    Check(XReflectionRuntime::GetEnumCount() == 4, "EnumCount != 4 after UnregisterEnum(E_0)");

    XReflectionRuntime::UnregisterInterface(FName("I_0"));
    Check(XReflectionRuntime::GetInterfaceCount() == 3,
          "InterfaceCount != 3 after UnregisterInterface(I_0)");

    // -----------------------------------------------------------------
    // Unregister of absent name is a no-op (count unchanged).
    // -----------------------------------------------------------------
    XReflectionRuntime::UnregisterClass(FName("DoesNotExist"));
    Check(XReflectionRuntime::GetClassCount() == 5,
          "Unregister of absent name changed ClassCount");

    // Unregister of NAME_None is a no-op (sentinel rejection).
    XReflectionRuntime::UnregisterClass(FName());
    Check(XReflectionRuntime::GetClassCount() == 5,
          "UnregisterClass(NAME_None) changed ClassCount");

    XReflectionRuntime::EmptyForTesting();
    Check(XReflectionRuntime::GetClassCount() == 0,
          "EmptyForTesting did not zero ClassCount");

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.CountApi: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.CountApi: PASS\n";
    return 0;
}
