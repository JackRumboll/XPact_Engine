// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/ABILock.cpp -- §11 ABI lock for the registry.
// =====================================================================
//
// XCore-4b Rev 4, Section 11 (Toolchain Contract Addendum) does NOT
// list XReflectionRuntime among the byte-layout-frozen types -- the
// class is opaque (all-static methods, no public data members), so
// there is no byte-layout contract to lock the way FName / FProperty /
// FClass / FStruct / etc. have.
//
// What IS testable at compile + runtime:
//
//   * XReflectionRuntime is non-polymorphic (no virtual methods).
//     This is the hot-reload safety contract (§9: descriptors and the
//     registry must not have vtables).
//
//   * XReflectionRuntime is non-constructible (the ctor is deleted).
//     The class is purely a namespace of static methods.
//
//   * The class has empty-base size (1 byte by C++ object-identity
//     rule). Sub-byte aggregate is not testable, but sizeof == 1 is
//     the documented expectation for an all-static class.
//
//   * The function-pointer signatures of Find* / Register* /
//     Unregister* / OnModule* / Get*Count are stable (the addresses
//     are stable across ABI revisions of XCore-4b). This is
//     compile-time-checked here: any signature change makes this
//     test fail to compile.
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
    using ::XCore::Reflect::FClass;
    using ::XCore::Reflect::FEnum;
    using ::XCore::Reflect::FInterface;
    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::FScriptStruct;
    using ::XCore::Reflect::FStruct;
    using ::XCore::Reflect::XReflectionRuntime;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Compile-time + runtime checks on the class's identity properties.
    // -----------------------------------------------------------------

    // No virtual methods (hot-reload safety; F3).
    static_assert(!std::is_polymorphic_v<XReflectionRuntime>,
                  "XReflectionRuntime must not be polymorphic (no virtuals; hot-reload safety)");
    Check(!std::is_polymorphic_v<XReflectionRuntime>,
          "XReflectionRuntime runtime polymorphism check failed");

    // Non-default-constructible (the registry is namespace-shaped).
    static_assert(!std::is_default_constructible_v<XReflectionRuntime>,
                  "XReflectionRuntime must not be default-constructible");
    static_assert(!std::is_copy_constructible_v<XReflectionRuntime>,
                  "XReflectionRuntime must not be copy-constructible");
    static_assert(!std::is_move_constructible_v<XReflectionRuntime>,
                  "XReflectionRuntime must not be move-constructible");

    // Empty-class size: per C++ object-identity rule, an empty class
    // has sizeof >= 1. The expected value is exactly 1 byte on every
    // supported toolchain.
    static_assert(sizeof(XReflectionRuntime) == 1,
                  "XReflectionRuntime sizeof != 1 (empty-class baseline)");

    // -----------------------------------------------------------------
    // Function-pointer signature locks. Compile-time check via
    // pointer-to-member-function type assignments.
    //
    // Each line forces the compiler to confirm the signature; any
    // change to a method's return type or parameter list breaks
    // compilation here loudly.
    // -----------------------------------------------------------------

    using FindClassFn         = const FClass*        (*)(FName) noexcept;
    using FindStructFn        = const FStruct*       (*)(FName) noexcept;
    using FindScriptStructFn  = const FScriptStruct* (*)(FName) noexcept;
    using FindEnumFn          = const FEnum*         (*)(FName) noexcept;
    using FindInterfaceFn     = const FInterface*    (*)(FName) noexcept;

    FindClassFn         pFindClass        = &XReflectionRuntime::FindClass;
    FindStructFn        pFindStruct       = &XReflectionRuntime::FindStruct;
    FindScriptStructFn  pFindScriptStruct = &XReflectionRuntime::FindScriptStruct;
    FindEnumFn          pFindEnum         = &XReflectionRuntime::FindEnum;
    FindInterfaceFn     pFindInterface    = &XReflectionRuntime::FindInterface;

    Check(pFindClass        != nullptr, "FindClass signature lock failed");
    Check(pFindStruct       != nullptr, "FindStruct signature lock failed");
    Check(pFindScriptStruct != nullptr, "FindScriptStruct signature lock failed");
    Check(pFindEnum         != nullptr, "FindEnum signature lock failed");
    Check(pFindInterface    != nullptr, "FindInterface signature lock failed");

    using RegisterClassFn        = bool (*)(const FClass*)        noexcept;
    using RegisterStructFn       = bool (*)(const FStruct*)       noexcept;
    using RegisterScriptStructFn = bool (*)(const FScriptStruct*) noexcept;
    using RegisterEnumFn         = bool (*)(const FEnum*)         noexcept;
    using RegisterInterfaceFn    = bool (*)(const FInterface*)    noexcept;

    RegisterClassFn        pRegisterClass        = &XReflectionRuntime::RegisterClass;
    RegisterStructFn       pRegisterStruct       = &XReflectionRuntime::RegisterStruct;
    RegisterScriptStructFn pRegisterScriptStruct = &XReflectionRuntime::RegisterScriptStruct;
    RegisterEnumFn         pRegisterEnum         = &XReflectionRuntime::RegisterEnum;
    RegisterInterfaceFn    pRegisterInterface    = &XReflectionRuntime::RegisterInterface;

    Check(pRegisterClass        != nullptr, "RegisterClass signature lock failed");
    Check(pRegisterStruct       != nullptr, "RegisterStruct signature lock failed");
    Check(pRegisterScriptStruct != nullptr, "RegisterScriptStruct signature lock failed");
    Check(pRegisterEnum         != nullptr, "RegisterEnum signature lock failed");
    Check(pRegisterInterface    != nullptr, "RegisterInterface signature lock failed");

    using OnModuleLoadFn   = void  (*)(FName) noexcept;
    using OnModuleUnloadFn = void  (*)(FName) noexcept;
    OnModuleLoadFn   pOnModuleLoad   = &XReflectionRuntime::OnModuleLoad;
    OnModuleUnloadFn pOnModuleUnload = &XReflectionRuntime::OnModuleUnload;
    Check(pOnModuleLoad   != nullptr, "OnModuleLoad signature lock failed");
    Check(pOnModuleUnload != nullptr, "OnModuleUnload signature lock failed");

    using GetClassCountFn = ::int32 (*)() noexcept;
    GetClassCountFn pGetClassCount = &XReflectionRuntime::GetClassCount;
    Check(pGetClassCount != nullptr, "GetClassCount signature lock failed");

    // -----------------------------------------------------------------
    // FName + descriptor types: spot-check the §11 layouts are still
    // honoured (these are the load-bearing ABI locks the registry
    // depends on).
    // -----------------------------------------------------------------
    static_assert(sizeof(FName) == 8,
                  "FName sizeof != 8; ABI broken; registry's TMap key changes");
    static_assert(alignof(FName) == 4,
                  "FName alignof != 4; ABI broken");
    // FStruct / FClass / FEnum / FInterface sizes per Phase 4b.5
    // audit-corrected values + Phase 5.a' Contract Rev 13.9 micro-bump
    // (FStruct +8 RefSchema; FClass-specific +8 LifecycleTable; +16
    // net in FClass total) per XCoreXObject Rev 4 §11.2 / §11.3.
    static_assert(sizeof(FStruct) == 120,         "FStruct sizeof != 120 (Rev 13.9 micro-bump)");
    static_assert(sizeof(FClass) == 240,          "FClass sizeof != 240 (Rev 13.9 micro-bump)");
    static_assert(sizeof(FScriptStruct) == 136,   "FScriptStruct sizeof != 136 (Rev 13.9 cascade)");
    static_assert(sizeof(FEnum) == 72,            "FEnum sizeof != 72 (audit-corrected)");
    static_assert(sizeof(FInterface) == 64,       "FInterface sizeof != 64 (audit-corrected)");

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.ABILock: FAIL (" << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.ABILock: PASS\n";
    return 0;
}
