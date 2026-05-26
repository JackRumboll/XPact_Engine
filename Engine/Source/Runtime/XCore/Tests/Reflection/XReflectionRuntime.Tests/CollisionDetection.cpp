// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/CollisionDetection.cpp -- name-collision gate.
// =====================================================================
//
// XCore-4b Rev 4, §10.1 RegisterClass semantics:
//
//   "RegisterClass(FClass*) returns true if registered, false if Name
//    already registered (collision)."
//
// EXERCISED PATHS:
//
//   * Registering two distinct FClass pointers with the same FName:
//     second call returns false; state unchanged (existing pointer
//     still wins on Find).
//
//   * Idempotent re-register of the SAME pointer returns true; state
//     unchanged.
//
//   * Same flow for FStruct, FScriptStruct, FEnum, FInterface.
//
//   * Registering nullptr returns false (defensive).
//
//   * Registering a descriptor with NAME_None returns false (no-
//     named-types contract).
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

    // -----------------------------------------------------------------
    // FClass collision detection.
    // -----------------------------------------------------------------
    {
        FClass A(FName("AXShared"), nullptr);
        FClass B(FName("AXShared"), nullptr);   // same Name, different pointer

        Check(XReflectionRuntime::RegisterClass(&A),
              "First RegisterClass returned false");
        Check(!XReflectionRuntime::RegisterClass(&B),
              "Second RegisterClass (collision) did not return false");

        // Existing pointer (A) still wins on Find.
        Check(XReflectionRuntime::FindClass(FName("AXShared")) == &A,
              "FindClass(AXShared) did not return the originally-registered pointer");
        Check(XReflectionRuntime::GetClassCount() == 1,
              "GetClassCount != 1 after rejected collision");

        // Idempotent re-register of the SAME pointer returns true.
        Check(XReflectionRuntime::RegisterClass(&A),
              "Idempotent re-register of A returned false");
        Check(XReflectionRuntime::GetClassCount() == 1,
              "GetClassCount changed on idempotent re-register");
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // FStruct collision detection.
    // -----------------------------------------------------------------
    {
        FStruct A(FName("FShared"), nullptr);
        FStruct B(FName("FShared"), nullptr);

        Check(XReflectionRuntime::RegisterStruct(&A),  "First RegisterStruct returned false");
        Check(!XReflectionRuntime::RegisterStruct(&B), "Second RegisterStruct (collision) did not return false");
        Check(XReflectionRuntime::FindStruct(FName("FShared")) == &A,
              "FindStruct(FShared) did not return the originally-registered pointer");
        Check(XReflectionRuntime::GetStructCount() == 1,
              "GetStructCount != 1 after rejected collision");
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // FScriptStruct collision detection.
    // -----------------------------------------------------------------
    {
        FScriptStruct A(FName("FSharedScript"), nullptr, 0u, nullptr);
        FScriptStruct B(FName("FSharedScript"), nullptr, 0u, nullptr);

        Check(XReflectionRuntime::RegisterScriptStruct(&A),
              "First RegisterScriptStruct returned false");
        Check(!XReflectionRuntime::RegisterScriptStruct(&B),
              "Second RegisterScriptStruct (collision) did not return false");
        Check(XReflectionRuntime::FindScriptStruct(FName("FSharedScript")) == &A,
              "FindScriptStruct(FSharedScript) did not return the originally-registered pointer");
        // FStruct view should also still point at A.
        Check(XReflectionRuntime::FindStruct(FName("FSharedScript"))
              == static_cast<const FStruct*>(&A),
              "FindStruct(FSharedScript) did not return the FScriptStruct's FStruct base");
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // FEnum collision detection.
    // -----------------------------------------------------------------
    {
        FEnum A(FFieldVariant(), FName("EShared"));
        FEnum B(FFieldVariant(), FName("EShared"));

        Check(XReflectionRuntime::RegisterEnum(&A),  "First RegisterEnum returned false");
        Check(!XReflectionRuntime::RegisterEnum(&B), "Second RegisterEnum (collision) did not return false");
        Check(XReflectionRuntime::FindEnum(FName("EShared")) == &A,
              "FindEnum(EShared) did not return the originally-registered pointer");
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // FInterface collision detection.
    // -----------------------------------------------------------------
    {
        FInterface A(FFieldVariant(), FName("IShared"));
        FInterface B(FFieldVariant(), FName("IShared"));

        Check(XReflectionRuntime::RegisterInterface(&A),  "First RegisterInterface returned false");
        Check(!XReflectionRuntime::RegisterInterface(&B), "Second RegisterInterface (collision) did not return false");
        Check(XReflectionRuntime::FindInterface(FName("IShared")) == &A,
              "FindInterface(IShared) did not return the originally-registered pointer");
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // Nullptr inputs are rejected.
    // -----------------------------------------------------------------
    {
        Check(!XReflectionRuntime::RegisterClass(nullptr),       "RegisterClass(nullptr) did not return false");
        Check(!XReflectionRuntime::RegisterStruct(nullptr),      "RegisterStruct(nullptr) did not return false");
        Check(!XReflectionRuntime::RegisterScriptStruct(nullptr),"RegisterScriptStruct(nullptr) did not return false");
        Check(!XReflectionRuntime::RegisterEnum(nullptr),        "RegisterEnum(nullptr) did not return false");
        Check(!XReflectionRuntime::RegisterInterface(nullptr),   "RegisterInterface(nullptr) did not return false");
    }

    // -----------------------------------------------------------------
    // NAME_None descriptors are rejected.
    // -----------------------------------------------------------------
    {
        FClass NoneClass(FName(), nullptr);  // FName() == NAME_None
        Check(!XReflectionRuntime::RegisterClass(&NoneClass),
              "RegisterClass with NAME_None did not return false");

        FStruct NoneStruct(FName(), nullptr);
        Check(!XReflectionRuntime::RegisterStruct(&NoneStruct),
              "RegisterStruct with NAME_None did not return false");
    }

    XReflectionRuntime::EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.CollisionDetection: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.CollisionDetection: PASS\n";
    return 0;
}
