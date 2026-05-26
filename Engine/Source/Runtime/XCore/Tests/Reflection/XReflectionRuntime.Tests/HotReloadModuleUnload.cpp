// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/HotReloadModuleUnload.cpp -- module-unload gate.
// =====================================================================
//
// XCore-4b Rev 4, dispatch task: "register 5 classes from 'ModuleA',
// unregister ModuleA, verify all 5 are FindClass-nullptr after."
//
// EXERCISED PATHS:
//
//   * OnModuleLoad stamps the thread-local current-module slot.
//   * Subsequent Register* calls carry the OwningModule stamp.
//   * GetClassOwningModule returns the stamped FName.
//   * OnModuleUnload purges every descriptor whose OwningModule
//     matches the unloading module.
//   * Find* on the purged descriptors returns nullptr.
//   * Descriptors registered BEFORE OnModuleLoad (no current module)
//     carry NAME_None as OwningModule and survive OnModuleUnload.
//   * Descriptors registered for a DIFFERENT module survive an
//     unrelated OnModuleUnload.
//   * The full FClass / FStruct / FEnum / FInterface kind matrix is
//     covered (each kind's module-stamping + purging).
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

    const FName ModuleA = FName("ModuleA");
    const FName ModuleB = FName("ModuleB");

    // -----------------------------------------------------------------
    // Initial: GetCurrentModuleName returns NAME_None (no module loaded).
    // -----------------------------------------------------------------
    Check(XReflectionRuntime::GetCurrentModuleName().IsNone(),
          "Initial GetCurrentModuleName != NAME_None");

    // -----------------------------------------------------------------
    // Pre-module registration: registers BEFORE OnModuleLoad carry
    // NAME_None as OwningModule (engine-core / pre-bootstrap path).
    //
    // Storage for these fixtures must outlive the module-unload calls
    // below so we can verify they survive. FClass is non-copyable +
    // non-movable; we hold them via unique_ptr so vector::reserve works.
    // -----------------------------------------------------------------
    std::vector<std::unique_ptr<FClass>> PreModuleClasses;
    PreModuleClasses.reserve(2);
    PreModuleClasses.emplace_back(std::make_unique<FClass>(FName("PreModule_A"), nullptr));
    PreModuleClasses.emplace_back(std::make_unique<FClass>(FName("PreModule_B"), nullptr));

    Check(XReflectionRuntime::RegisterClass(PreModuleClasses[0].get()),
          "RegisterClass(PreModule_A) failed");
    Check(XReflectionRuntime::RegisterClass(PreModuleClasses[1].get()),
          "RegisterClass(PreModule_B) failed");

    Check(XReflectionRuntime::GetClassOwningModule(FName("PreModule_A")).IsNone(),
          "PreModule_A OwningModule != NAME_None");

    // -----------------------------------------------------------------
    // OnModuleLoad(ModuleA): registrations now stamp ModuleA.
    // -----------------------------------------------------------------
    XReflectionRuntime::OnModuleLoad(ModuleA);
    Check(XReflectionRuntime::GetCurrentModuleName() == ModuleA,
          "GetCurrentModuleName != ModuleA after OnModuleLoad");

    // Register 5 classes from ModuleA.
    std::vector<std::unique_ptr<FClass>> ModuleAClasses;
    ModuleAClasses.reserve(5);
    for (int I = 0; I < 5; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "ModuleA_Class_%d", I);
        ModuleAClasses.emplace_back(std::make_unique<FClass>(FName(NameBuf), nullptr));
        Check(XReflectionRuntime::RegisterClass(ModuleAClasses[I].get()),
              "RegisterClass(ModuleA_*) failed");
    }

    // Also register one of each other kind for full-matrix coverage.
    FStruct      ModuleAStruct     (FName("ModuleA_Struct"),       nullptr);
    FScriptStruct ModuleAScript    (FName("ModuleA_ScriptStruct"), nullptr, 0u, nullptr);
    FEnum        ModuleAEnum       (FFieldVariant(), FName("ModuleA_Enum"));
    FInterface   ModuleAInterface  (FFieldVariant(), FName("ModuleA_Interface"));

    Check(XReflectionRuntime::RegisterStruct(&ModuleAStruct),         "RegisterStruct from ModuleA failed");
    Check(XReflectionRuntime::RegisterScriptStruct(&ModuleAScript),   "RegisterScriptStruct from ModuleA failed");
    Check(XReflectionRuntime::RegisterEnum(&ModuleAEnum),             "RegisterEnum from ModuleA failed");
    Check(XReflectionRuntime::RegisterInterface(&ModuleAInterface),   "RegisterInterface from ModuleA failed");

    // Owning-module stamp.
    Check(XReflectionRuntime::GetClassOwningModule(FName("ModuleA_Class_0")) == ModuleA,
          "ModuleA_Class_0 OwningModule != ModuleA");
    Check(XReflectionRuntime::GetStructOwningModule(FName("ModuleA_Struct")) == ModuleA,
          "ModuleA_Struct OwningModule != ModuleA");
    Check(XReflectionRuntime::GetEnumOwningModule(FName("ModuleA_Enum")) == ModuleA,
          "ModuleA_Enum OwningModule != ModuleA");
    Check(XReflectionRuntime::GetInterfaceOwningModule(FName("ModuleA_Interface")) == ModuleA,
          "ModuleA_Interface OwningModule != ModuleA");

    // -----------------------------------------------------------------
    // OnModuleUnload(ModuleB): no-op for ModuleA's entries (different
    // module).
    // -----------------------------------------------------------------
    XReflectionRuntime::OnModuleUnload(ModuleB);
    for (int I = 0; I < 5; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "ModuleA_Class_%d", I);
        Check(XReflectionRuntime::FindClass(FName(NameBuf)) == ModuleAClasses[I].get(),
              "ModuleA class disappeared after unrelated module unload");
    }

    // PreModule registrations survive.
    Check(XReflectionRuntime::FindClass(FName("PreModule_A")) == PreModuleClasses[0].get(),
          "PreModule_A disappeared after OnModuleUnload(ModuleB)");

    // -----------------------------------------------------------------
    // OnModuleUnload(ModuleA): purges all 5 ModuleA classes + struct +
    // scriptstruct + enum + interface.
    // -----------------------------------------------------------------
    XReflectionRuntime::OnModuleUnload(ModuleA);

    for (int I = 0; I < 5; ++I)
    {
        char NameBuf[32];
        std::snprintf(NameBuf, sizeof(NameBuf), "ModuleA_Class_%d", I);
        Check(XReflectionRuntime::FindClass(FName(NameBuf)) == nullptr,
              "ModuleA class still resolves after OnModuleUnload(ModuleA)");
    }

    Check(XReflectionRuntime::FindStruct(FName("ModuleA_Struct")) == nullptr,
          "ModuleA_Struct still resolves after OnModuleUnload(ModuleA)");
    Check(XReflectionRuntime::FindScriptStruct(FName("ModuleA_ScriptStruct")) == nullptr,
          "ModuleA_ScriptStruct still resolves after OnModuleUnload(ModuleA)");
    Check(XReflectionRuntime::FindEnum(FName("ModuleA_Enum")) == nullptr,
          "ModuleA_Enum still resolves after OnModuleUnload(ModuleA)");
    Check(XReflectionRuntime::FindInterface(FName("ModuleA_Interface")) == nullptr,
          "ModuleA_Interface still resolves after OnModuleUnload(ModuleA)");

    // PreModule registrations survive ModuleA unload (their
    // OwningModule is NAME_None, not ModuleA).
    Check(XReflectionRuntime::FindClass(FName("PreModule_A")) == PreModuleClasses[0].get(),
          "PreModule_A disappeared after OnModuleUnload(ModuleA)");
    Check(XReflectionRuntime::FindClass(FName("PreModule_B")) == PreModuleClasses[1].get(),
          "PreModule_B disappeared after OnModuleUnload(ModuleA)");

    // -----------------------------------------------------------------
    // Current-module slot is cleared (defensive; the bracket should
    // always nest properly).
    // -----------------------------------------------------------------
    Check(XReflectionRuntime::GetCurrentModuleName().IsNone(),
          "Current module not cleared after OnModuleUnload(ModuleA)");

    // -----------------------------------------------------------------
    // Re-load: a subsequent OnModuleLoad/Register* sequence works
    // cleanly (the registry is back to "post-unload" state, not
    // "corrupted").
    // -----------------------------------------------------------------
    XReflectionRuntime::OnModuleLoad(ModuleA);
    FClass Reloaded(FName("ModuleA_Reload"), nullptr);
    Check(XReflectionRuntime::RegisterClass(&Reloaded),
          "Re-register after unload failed");
    Check(XReflectionRuntime::FindClass(FName("ModuleA_Reload")) == &Reloaded,
          "FindClass on re-registered class failed");
    Check(XReflectionRuntime::GetClassOwningModule(FName("ModuleA_Reload")) == ModuleA,
          "Re-registered class OwningModule != ModuleA");

    // -----------------------------------------------------------------
    // OnModuleLoad(NAME_None) is rejected.
    // -----------------------------------------------------------------
    XReflectionRuntime::OnModuleUnload(ModuleA);
    XReflectionRuntime::OnModuleLoad(FName{});
    Check(XReflectionRuntime::GetCurrentModuleName().IsNone(),
          "OnModuleLoad(NAME_None) set the current module despite rejection");

    XReflectionRuntime::EmptyForTesting();

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.HotReloadModuleUnload: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.HotReloadModuleUnload: PASS\n";
    return 0;
}
