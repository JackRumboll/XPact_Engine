// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XReflectionRuntime.Tests/SingletonAccess.cpp -- gate E (basic).
// =====================================================================
//
// XCore-4b Rev 4, Section 10.
//
// EXERCISED PATHS:
//
//   * XReflectionRuntime is a class with all-static methods (no instance
//     handle exposed at the API surface). The singleton state lives
//     behind a function-local-static inside the .cpp; we cannot
//     directly observe its address, but we CAN observe that repeated
//     Find* / GetCount calls produce coherent state (a single
//     instance).
//
//   * The class is non-instantiable (its constructor is deleted) and
//     non-copyable / non-movable. This is verified via
//     std::is_constructible / std::is_copy_constructible /
//     std::is_move_constructible trait checks.
//
//   * EmptyForTesting + the count accessors round-trip cleanly: after
//     EmptyForTesting, every count == 0; the count accessors return
//     consistent values across repeated calls.
//
// =====================================================================

#include "XReflectionRuntime.h"

#include "HAL/FMemory.h"

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
    using ::XCore::Reflect::XReflectionRuntime;

    ::XCore::HAL::FMemory::__Init();

    // Trait checks: the class is non-instantiable and non-copyable.
    Check(!std::is_default_constructible_v<XReflectionRuntime>,
          "XReflectionRuntime is unexpectedly default-constructible");
    Check(!std::is_copy_constructible_v<XReflectionRuntime>,
          "XReflectionRuntime is unexpectedly copy-constructible");
    Check(!std::is_move_constructible_v<XReflectionRuntime>,
          "XReflectionRuntime is unexpectedly move-constructible");
    Check(!std::is_copy_assignable_v<XReflectionRuntime>,
          "XReflectionRuntime is unexpectedly copy-assignable");
    Check(!std::is_move_assignable_v<XReflectionRuntime>,
          "XReflectionRuntime is unexpectedly move-assignable");

    // Trait check: no virtual methods (hot-reload safety; gate F3).
    // A class with virtual methods is NOT trivially copyable AND has a
    // non-trivial constructor; XReflectionRuntime's constructor IS
    // deleted (not virtual), so we cannot use is_trivially_copyable.
    // Instead we test is_polymorphic, which is true iff the class
    // declares or inherits a virtual function.
    Check(!std::is_polymorphic_v<XReflectionRuntime>,
          "XReflectionRuntime is unexpectedly polymorphic (has virtual methods)");

    // Singleton coherence: EmptyForTesting clears state; counts == 0.
    XReflectionRuntime::EmptyForTesting();
    Check(XReflectionRuntime::GetClassCount() == 0,     "ClassCount != 0 after EmptyForTesting");
    Check(XReflectionRuntime::GetStructCount() == 0,    "StructCount != 0 after EmptyForTesting");
    Check(XReflectionRuntime::GetScriptStructCount()==0,"ScriptStructCount != 0 after EmptyForTesting");
    Check(XReflectionRuntime::GetEnumCount() == 0,      "EnumCount != 0 after EmptyForTesting");
    Check(XReflectionRuntime::GetInterfaceCount() == 0, "InterfaceCount != 0 after EmptyForTesting");

    // Repeated GetClassCount() returns the same value (no spurious
    // mutation between calls).
    {
        const ::int32 First  = XReflectionRuntime::GetClassCount();
        const ::int32 Second = XReflectionRuntime::GetClassCount();
        const ::int32 Third  = XReflectionRuntime::GetClassCount();
        Check(First == Second && Second == Third,
              "GetClassCount returned inconsistent values across repeated calls");
    }

    if (g_FailureCount > 0)
    {
        std::cerr << "XReflectionRuntime.SingletonAccess: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "XReflectionRuntime.SingletonAccess: PASS\n";
    return 0;
}
