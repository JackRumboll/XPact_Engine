// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// IConsoleManager.Tests/SetByPriority.cpp -- cascade ordering.
// =====================================================================
//
// XCore-4a Rev 3, Section 1.3 locked decision 10 + Section 9.1 +
// Section 9.7 ("SetByPriority cascade: weaker setter cannot overwrite
// stronger value; priority history queryable in Dev.").
//
// Cascade verification:
//   * Default < Config < Commandline < Code < Console
//   * Weaker overwriting stronger is ignored.
//   * Same-priority recent setter wins (>= not strict >).
//
// =====================================================================

#include "HAL/IConsoleManager.h"
#include "HAL/IConsoleVariable.h"
#include "HAL/ECVarFlags.h"
#include "HAL/ECVarSetByPriority.h"

#include <iostream>

namespace
{
    using EPrio = ::XCore::Misc::ECVarSetByPriority;

    int RunSetByPriority()
    {
        auto& Manager = ::XCore::Misc::IConsoleManager::Get();
        auto* CVar = Manager.RegisterInt(
            "r.SetByPriority.Test", 1, "cascade test",
            ::XCore::Misc::ECVarFlags::Default);
        if (CVar == nullptr)
        {
            std::cerr << "FAIL: Registration returned nullptr\n";
            return 1;
        }

        // Default value visible (priority Default).
        if (CVar->GetInt() != 1)
        {
            std::cerr << "FAIL: registration default not visible (got " << CVar->GetInt() << ")\n";
            return 1;
        }

        // Config can overwrite Default.
        CVar->SetInt(10, EPrio::Config);
        if (CVar->GetInt() != 10)
        {
            std::cerr << "FAIL: Config didn't overwrite Default (got " << CVar->GetInt() << ")\n";
            return 1;
        }

        // Default cannot overwrite Config.
        CVar->SetInt(99, EPrio::Default);
        if (CVar->GetInt() != 10)
        {
            std::cerr << "FAIL: Default overwrote Config (got " << CVar->GetInt() << ")\n";
            return 1;
        }

        // Commandline > Config.
        CVar->SetInt(20, EPrio::Commandline);
        if (CVar->GetInt() != 20)
        {
            std::cerr << "FAIL: Commandline didn't overwrite Config\n";
            return 1;
        }

        // Config < Commandline; ignored.
        CVar->SetInt(99, EPrio::Config);
        if (CVar->GetInt() != 20)
        {
            std::cerr << "FAIL: Config overwrote Commandline\n";
            return 1;
        }

        // Code > Commandline.
        CVar->SetInt(30, EPrio::Code);
        if (CVar->GetInt() != 30)
        {
            std::cerr << "FAIL: Code didn't overwrite Commandline\n";
            return 1;
        }

        // Console (highest).
        CVar->SetInt(40, EPrio::Console);
        if (CVar->GetInt() != 40)
        {
            std::cerr << "FAIL: Console didn't overwrite Code\n";
            return 1;
        }

        // Nothing can overwrite Console (except Console itself).
        CVar->SetInt(99, EPrio::Default);
        if (CVar->GetInt() != 40)
        {
            std::cerr << "FAIL: Default overwrote Console\n";
            return 1;
        }
        CVar->SetInt(99, EPrio::Commandline);
        if (CVar->GetInt() != 40)
        {
            std::cerr << "FAIL: Commandline overwrote Console\n";
            return 1;
        }

        // Same-priority recent setter wins (>= not strict >).
        CVar->SetInt(50, EPrio::Console);
        if (CVar->GetInt() != 50)
        {
            std::cerr << "FAIL: Same-priority recent setter didn't win\n";
            return 1;
        }

        std::cout << "PASS: SetByPriority cascade (5-level ordering verified)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunSetByPriority();
}
