// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/IterationApi.cpp -- gate E4.
// =====================================================================
//
// XCore-4b Rev 4, Section 13 Acceptance gate E4:
//
//   "E4. IterateAllClasses visits every registered class exactly once;
//        iteration order is deterministic (registration order)."
//
// EXERCISED PATHS:
//
//   * IterateAllClasses visits every registered FClass exactly once.
//   * Iteration order matches registration order.
//   * IterateAllStructs / IterateAllEnums / IterateAllInterfaces
//     follow the same contract.
//   * On an empty registry, the visitor is not called.
//   * The stack-buffer fast path (count <= 32) and the heap-fallback
//     path (count > 32) both work; we test the heap fallback by
//     registering 40 classes (32 stack + 8 heap).
//
// =====================================================================

#include "XReflectionRuntime.h"

#include "HAL/FMemory.h"
#include "Reflection/FClass.h"
#include "Reflection/FEnum.h"
#include "Reflection/FInterface.h"
#include "Reflection/FName.h"
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
    using ::XCore::Reflect::FStruct;
    using ::XCore::Reflect::XReflectionRuntime;

    ::XCore::HAL::FMemory::__Init();

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // Empty-registry case: the visitor is not called.
    // -----------------------------------------------------------------
    {
        int CallCount = 0;
        XReflectionRuntime::IterateAllClasses([&](const FClass*) { ++CallCount; });
        Check(CallCount == 0, "IterateAllClasses on empty registry called the visitor");
    }

    // -----------------------------------------------------------------
    // Register 5 classes in a deterministic order; verify the visitor
    // sees them in the same order.
    //
    // FClass is non-copyable + non-movable; we hold them via
    // unique_ptr so the underlying std::vector can reserve capacity.
    // -----------------------------------------------------------------
    std::vector<std::unique_ptr<FClass>> SmallFixtures;
    SmallFixtures.reserve(5);
    SmallFixtures.emplace_back(std::make_unique<FClass>(FName("AXVehicle"),   nullptr));
    SmallFixtures.emplace_back(std::make_unique<FClass>(FName("AXPawn"),      nullptr));
    SmallFixtures.emplace_back(std::make_unique<FClass>(FName("AXActor"),     nullptr));
    SmallFixtures.emplace_back(std::make_unique<FClass>(FName("AXCharacter"), nullptr));
    SmallFixtures.emplace_back(std::make_unique<FClass>(FName("AXPlayer"),    nullptr));

    for (int I = 0; I < 5; ++I)
    {
        Check(XReflectionRuntime::RegisterClass(SmallFixtures[I].get()),
              "Small-fixture RegisterClass failed");
    }

    {
        std::vector<const FClass*> Visited;
        XReflectionRuntime::IterateAllClasses([&](const FClass* C) { Visited.push_back(C); });
        Check(Visited.size() == 5, "IterateAllClasses did not visit 5 classes");
        for (size_t I = 0; I < Visited.size(); ++I)
        {
            Check(Visited[I] == SmallFixtures[I].get(),
                  "IterateAllClasses order != registration order");
        }
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // Heap-fallback path: register 40 classes (> kStackCap of 32) and
    // verify all 40 are visited.
    // -----------------------------------------------------------------
    constexpr int kManyClasses = 40;
    std::vector<std::unique_ptr<FClass>> ManyFixtures;
    ManyFixtures.reserve(kManyClasses);
    for (int I = 0; I < kManyClasses; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "Many_%d", I);
        ManyFixtures.emplace_back(std::make_unique<FClass>(FName(NameBuf), nullptr));
        Check(XReflectionRuntime::RegisterClass(ManyFixtures[I].get()),
              "Many-fixture RegisterClass failed");
    }

    {
        std::vector<const FClass*> Visited;
        XReflectionRuntime::IterateAllClasses([&](const FClass* C) { Visited.push_back(C); });
        Check(static_cast<int>(Visited.size()) == kManyClasses,
              "Heap-fallback IterateAllClasses did not visit all 40 classes");
        for (int I = 0; I < kManyClasses; ++I)
        {
            Check(Visited[static_cast<size_t>(I)] == ManyFixtures[I].get(),
                  "Heap-fallback IterateAllClasses order != registration order");
        }
    }

    // Visit-exactly-once: count duplicates.
    {
        int DuplicateCount = 0;
        std::vector<const FClass*> Visited;
        XReflectionRuntime::IterateAllClasses([&](const FClass* C)
        {
            for (const FClass* Prev : Visited)
            {
                if (Prev == C)
                {
                    ++DuplicateCount;
                }
            }
            Visited.push_back(C);
        });
        Check(DuplicateCount == 0, "IterateAllClasses visited a class more than once");
    }

    XReflectionRuntime::EmptyForTesting();

    // -----------------------------------------------------------------
    // IterateAllStructs / IterateAllEnums / IterateAllInterfaces:
    // basic 3-fixture round-trip for each.
    // -----------------------------------------------------------------
    {
        std::vector<std::unique_ptr<FStruct>> Structs;
        Structs.reserve(3);
        Structs.emplace_back(std::make_unique<FStruct>(FName("FVector"),  nullptr));
        Structs.emplace_back(std::make_unique<FStruct>(FName("FRotator"), nullptr));
        Structs.emplace_back(std::make_unique<FStruct>(FName("FQuat"),    nullptr));

        for (int I = 0; I < 3; ++I)
        {
            Check(XReflectionRuntime::RegisterStruct(Structs[I].get()), "RegisterStruct failed");
        }

        std::vector<const FStruct*> Visited;
        XReflectionRuntime::IterateAllStructs([&](const FStruct* S) { Visited.push_back(S); });
        Check(Visited.size() == 3, "IterateAllStructs did not visit 3 structs");
        for (size_t I = 0; I < 3; ++I)
        {
            Check(Visited[I] == Structs[I].get(), "IterateAllStructs order != registration order");
        }
    }

    XReflectionRuntime::EmptyForTesting();

    {
        std::vector<std::unique_ptr<FEnum>> Enums;
        Enums.reserve(3);
        Enums.emplace_back(std::make_unique<FEnum>(FFieldVariant(), FName("ECollisionChannel")));
        Enums.emplace_back(std::make_unique<FEnum>(FFieldVariant(), FName("EAxis")));
        Enums.emplace_back(std::make_unique<FEnum>(FFieldVariant(), FName("EObjectTypeQuery")));

        for (int I = 0; I < 3; ++I)
        {
            Check(XReflectionRuntime::RegisterEnum(Enums[I].get()), "RegisterEnum failed");
        }

        std::vector<const FEnum*> Visited;
        XReflectionRuntime::IterateAllEnums([&](const FEnum* E) { Visited.push_back(E); });
        Check(Visited.size() == 3, "IterateAllEnums did not visit 3 enums");
        for (size_t I = 0; I < 3; ++I)
        {
            Check(Visited[I] == Enums[I].get(), "IterateAllEnums order != registration order");
        }
    }

    XReflectionRuntime::EmptyForTesting();

    {
        std::vector<std::unique_ptr<FInterface>> Interfaces;
        Interfaces.reserve(3);
        Interfaces.emplace_back(std::make_unique<FInterface>(FFieldVariant(), FName("IInteractable")));
        Interfaces.emplace_back(std::make_unique<FInterface>(FFieldVariant(), FName("IDamageable")));
        Interfaces.emplace_back(std::make_unique<FInterface>(FFieldVariant(), FName("ISaveable")));

        for (int I = 0; I < 3; ++I)
        {
            Check(XReflectionRuntime::RegisterInterface(Interfaces[I].get()), "RegisterInterface failed");
        }

        std::vector<const FInterface*> Visited;
        XReflectionRuntime::IterateAllInterfaces([&](const FInterface* IF) { Visited.push_back(IF); });
        Check(Visited.size() == 3, "IterateAllInterfaces did not visit 3 interfaces");
        for (size_t I = 0; I < 3; ++I)
        {
            Check(Visited[I] == Interfaces[I].get(),
                  "IterateAllInterfaces order != registration order");
        }
    }

    XReflectionRuntime::EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.IterationApi: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.IterationApi: PASS\n";
    return 0;
}
