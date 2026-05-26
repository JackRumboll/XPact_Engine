// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/RegisterFindRoundTrip.cpp -- gate E1.
// =====================================================================
//
// XCore-4b Rev 4, Section 13 Acceptance gate E1:
//
//   "E1. FindClass(FName) returns the correct FClass for every
//        registered class in test fixtures (500+ classes)."
//
// This file exercises the basic round-trip across ALL five reflected
// kinds (FClass, FStruct, FScriptStruct, FEnum, FInterface) at small
// scale (5 fixtures per kind). The 500+ scale gate is exercised by
// ConcurrentReadStress.cpp + the future CI shard once a synthesised
// 500-class XHT-emit lands.
//
// EXERCISED PATHS:
//
//   * RegisterClass + FindClass round-trip preserves pointer identity.
//   * RegisterStruct + FindStruct.
//   * RegisterScriptStruct + FindScriptStruct + FindStruct (the
//     FScriptStruct is registered into BOTH maps).
//   * RegisterEnum + FindEnum.
//   * RegisterInterface + FindInterface.
//   * Find* on an unregistered name returns nullptr (not crash).
//   * Find* with NAME_None returns nullptr.
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

    // Start clean.
    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // FClass round-trip.
    // -----------------------------------------------------------------
    {
        FClass C1(FName("AXActor"), nullptr);
        FClass C2(FName("AXPawn"), &C1);
        FClass C3(FName("AXCharacter"), &C2);

        Check(XReflectionRuntime::RegisterClass(&C1), "RegisterClass(C1) returned false");
        Check(XReflectionRuntime::RegisterClass(&C2), "RegisterClass(C2) returned false");
        Check(XReflectionRuntime::RegisterClass(&C3), "RegisterClass(C3) returned false");

        Check(XReflectionRuntime::GetClassCount() == 3, "GetClassCount != 3 after RegisterClass x3");

        Check(XReflectionRuntime::FindClass(FName("AXActor"))     == &C1, "FindClass(AXActor) wrong");
        Check(XReflectionRuntime::FindClass(FName("AXPawn"))      == &C2, "FindClass(AXPawn) wrong");
        Check(XReflectionRuntime::FindClass(FName("AXCharacter")) == &C3, "FindClass(AXCharacter) wrong");

        // Find on unregistered name returns nullptr.
        Check(XReflectionRuntime::FindClass(FName("NotRegistered")) == nullptr,
              "FindClass on unregistered name did not return nullptr");

        // NAME_None returns nullptr.
        Check(XReflectionRuntime::FindClass(FName()) == nullptr,
              "FindClass(NAME_None) did not return nullptr");

        // Idempotent re-register: same pointer, returns true; count unchanged.
        Check(XReflectionRuntime::RegisterClass(&C1), "Idempotent re-register of C1 did not return true");
        Check(XReflectionRuntime::GetClassCount() == 3, "GetClassCount changed on idempotent re-register");
    }

    // -----------------------------------------------------------------
    // FStruct round-trip.
    // -----------------------------------------------------------------
    {
        FStruct S1(FName("FVector"), nullptr);
        FStruct S2(FName("FRotator"), nullptr);

        Check(XReflectionRuntime::RegisterStruct(&S1), "RegisterStruct(S1) returned false");
        Check(XReflectionRuntime::RegisterStruct(&S2), "RegisterStruct(S2) returned false");

        Check(XReflectionRuntime::GetStructCount() == 2, "GetStructCount != 2 after RegisterStruct x2");

        Check(XReflectionRuntime::FindStruct(FName("FVector"))  == &S1, "FindStruct(FVector) wrong");
        Check(XReflectionRuntime::FindStruct(FName("FRotator")) == &S2, "FindStruct(FRotator) wrong");

        Check(XReflectionRuntime::FindStruct(FName("FAbsent")) == nullptr,
              "FindStruct on unregistered name did not return nullptr");
    }

    // -----------------------------------------------------------------
    // FScriptStruct round-trip.
    //
    // RegisterScriptStruct populates BOTH the ScriptStructsByName map
    // AND the StructsByName map (because every FScriptStruct IS a
    // FStruct). The verification confirms both views resolve.
    // -----------------------------------------------------------------
    {
        FScriptStruct SS1(FName("FTransform"), nullptr, /*Capabilities*/ 0u, /*CppOpsTable*/ nullptr);
        FScriptStruct SS2(FName("FLinearColor"), nullptr, /*Capabilities*/ 0u, /*CppOpsTable*/ nullptr);

        Check(XReflectionRuntime::RegisterScriptStruct(&SS1),
              "RegisterScriptStruct(SS1) returned false");
        Check(XReflectionRuntime::RegisterScriptStruct(&SS2),
              "RegisterScriptStruct(SS2) returned false");

        Check(XReflectionRuntime::GetScriptStructCount() == 2,
              "GetScriptStructCount != 2 after RegisterScriptStruct x2");

        // Both views resolve.
        Check(XReflectionRuntime::FindScriptStruct(FName("FTransform")) == &SS1,
              "FindScriptStruct(FTransform) wrong");
        Check(XReflectionRuntime::FindScriptStruct(FName("FLinearColor")) == &SS2,
              "FindScriptStruct(FLinearColor) wrong");

        // FindStruct also resolves (FScriptStruct registered into both maps).
        Check(XReflectionRuntime::FindStruct(FName("FTransform"))
              == static_cast<const FStruct*>(&SS1),
              "FindStruct(FTransform) did not return the FScriptStruct's FStruct base");
        Check(XReflectionRuntime::FindStruct(FName("FLinearColor"))
              == static_cast<const FStruct*>(&SS2),
              "FindStruct(FLinearColor) did not return the FScriptStruct's FStruct base");

        // GetStructCount includes both FStruct (S1, S2) and FScriptStruct
        // entries (SS1, SS2) -- they all land in AllStructs.
        Check(XReflectionRuntime::GetStructCount() == 4,
              "GetStructCount != 4 after 2 FStruct + 2 FScriptStruct registrations");
    }

    // -----------------------------------------------------------------
    // FEnum round-trip.
    // -----------------------------------------------------------------
    {
        FEnum E1(FFieldVariant(), FName("ECollisionChannel"));
        FEnum E2(FFieldVariant(), FName("EAxis"));

        Check(XReflectionRuntime::RegisterEnum(&E1), "RegisterEnum(E1) returned false");
        Check(XReflectionRuntime::RegisterEnum(&E2), "RegisterEnum(E2) returned false");

        Check(XReflectionRuntime::GetEnumCount() == 2, "GetEnumCount != 2 after RegisterEnum x2");

        Check(XReflectionRuntime::FindEnum(FName("ECollisionChannel")) == &E1,
              "FindEnum(ECollisionChannel) wrong");
        Check(XReflectionRuntime::FindEnum(FName("EAxis")) == &E2,
              "FindEnum(EAxis) wrong");

        Check(XReflectionRuntime::FindEnum(FName("EAbsent")) == nullptr,
              "FindEnum on unregistered name did not return nullptr");
    }

    // -----------------------------------------------------------------
    // FInterface round-trip.
    // -----------------------------------------------------------------
    {
        FInterface I1(FFieldVariant(), FName("IInteractable"));
        FInterface I2(FFieldVariant(), FName("IDamageable"));

        Check(XReflectionRuntime::RegisterInterface(&I1), "RegisterInterface(I1) returned false");
        Check(XReflectionRuntime::RegisterInterface(&I2), "RegisterInterface(I2) returned false");

        Check(XReflectionRuntime::GetInterfaceCount() == 2,
              "GetInterfaceCount != 2 after RegisterInterface x2");

        Check(XReflectionRuntime::FindInterface(FName("IInteractable")) == &I1,
              "FindInterface(IInteractable) wrong");
        Check(XReflectionRuntime::FindInterface(FName("IDamageable")) == &I2,
              "FindInterface(IDamageable) wrong");

        Check(XReflectionRuntime::FindInterface(FName("IAbsent")) == nullptr,
              "FindInterface on unregistered name did not return nullptr");
    }

    // Clean up.
    XReflectionRuntime::EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.RegisterFindRoundTrip: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.RegisterFindRoundTrip: PASS\n";
    return 0;
}
