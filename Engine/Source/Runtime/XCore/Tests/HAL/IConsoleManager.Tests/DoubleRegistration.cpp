// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// IConsoleManager.Tests/DoubleRegistration.cpp -- M-7 stub.
// =====================================================================
//
// XCore-4a Rev 3, Section 9.5 fix M-7 ("Build-time double-registration
// error"): XBT pre-scans the codebase for FAutoConsoleVariable
// constructions and emits a build error if two share the same name.
// The runtime content-hash keying is defense-in-depth (the second
// Register* with the same name returns the existing CVar).
//
// PHASE 1F STUB: the XBT build-time scan is Phase 1g's work. This
// test exercises the runtime defense-in-depth path: a second
// RegisterInt with an already-registered name returns the existing
// CVar (idempotent registration), not a fresh CVar with the new
// default value.
//
// =====================================================================

#include "HAL/IConsoleManager.h"
#include "HAL/IConsoleVariable.h"
#include "HAL/ECVarFlags.h"

#include <iostream>

namespace
{
    int RunDoubleRegistration()
    {
        auto& Manager = ::XCore::Misc::IConsoleManager::Get();

        ::XCore::Misc::IConsoleVariable* First = Manager.RegisterInt(
            "r.DoubleReg.Test", 100, "first registration",
            ::XCore::Misc::ECVarFlags::Default);
        if (First == nullptr)
        {
            std::cerr << "FAIL: first registration returned nullptr\n";
            return 1;
        }
        if (First->GetInt() != 100)
        {
            std::cerr << "FAIL: first GetInt expected 100, got " << First->GetInt() << "\n";
            return 1;
        }

        // Second registration with same name -- should return the
        // existing CVar (idempotent), NOT a fresh CVar.
        ::XCore::Misc::IConsoleVariable* Second = Manager.RegisterInt(
            "r.DoubleReg.Test", 200, "second registration",
            ::XCore::Misc::ECVarFlags::Default);
        if (Second != First)
        {
            std::cerr << "FAIL: second registration produced a fresh CVar; expected the existing pointer\n";
            return 1;
        }
        // The default of the second call was 200 but the existing
        // CVar's value should still be 100 (the second Register
        // does NOT mutate the existing value).
        if (Second->GetInt() != 100)
        {
            std::cerr << "FAIL: second Register mutated the value; expected 100 got " << Second->GetInt() << "\n";
            return 1;
        }

        std::cout << "PASS: DoubleRegistration (idempotent runtime defense-in-depth verified;\n"
                     "      XBT build-time scan ships in Phase 1g per spec fix M-7)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunDoubleRegistration();
}
