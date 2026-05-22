// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// IConsoleManager.Tests/RegisterFind.cpp -- registration + lookup.
// =====================================================================
//
// XCore-4a Rev 3, Section 9.5 fix C-6 content-hash keying.
//
// Registers 1000 CVars; Find returns them; content-hash keying
// preserved across const char* pointer-identity diverging (Find called
// via a separately-allocated copy of the name string).
//
// =====================================================================

#include "HAL/IConsoleManager.h"
#include "HAL/IConsoleVariable.h"
#include "HAL/ECVarFlags.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <cstring>
#include <iostream>
#include <vector>

namespace
{
    constexpr int kNumCVars = 1000;

    int RunRegisterFind()
    {
        auto& Manager = ::XCore::Misc::IConsoleManager::Get();

        // Register 1000 int CVars with synthetic names. The names are
        // heap-allocated so they have engine-lifetime; the rodata-
        // pointer contract is preserved by the FAutoConsoleVariable
        // path normally, but the direct Register* surface accepts
        // any const char*.
        std::vector<char*> Names;
        Names.reserve(kNumCVars);

        for (int i = 0; i < kNumCVars; ++i)
        {
            char* Name = new char[32];
            std::snprintf(Name, 32, "r.Test.CVar%d", i);
            Names.push_back(Name);

            ::XCore::Misc::IConsoleVariable* CVar = Manager.RegisterInt(
                Name, i, "test cvar", ::XCore::Misc::ECVarFlags::Default);
            if (CVar == nullptr)
            {
                std::cerr << "FAIL: RegisterInt returned nullptr at i=" << i << "\n";
                return 1;
            }
            if (CVar->GetInt() != i)
            {
                std::cerr << "FAIL: GetInt mismatch at i=" << i
                          << " expected=" << i << " got=" << CVar->GetInt() << "\n";
                return 1;
            }
        }

        // Find every CVar back using a distinct char* (allocate a
        // separate buffer for each lookup) -- this is the content-
        // hash keying check (fix C-6): the const char* pointer
        // identity diverges between Register and Find, but the byte
        // content matches, so the hash matches.
        for (int i = 0; i < kNumCVars; ++i)
        {
            char Lookup[32];
            std::snprintf(Lookup, 32, "r.Test.CVar%d", i);
            // Verify Lookup is a different pointer than Names[i].
            if (Lookup == Names[i])
            {
                std::cerr << "FAIL: Lookup buffer aliased Names[" << i << "]\n";
                return 1;
            }
            ::XCore::Misc::IConsoleVariable* CVar = Manager.Find(Lookup);
            if (CVar == nullptr)
            {
                std::cerr << "FAIL: Find returned nullptr for r.Test.CVar" << i << "\n";
                return 1;
            }
            if (CVar->GetInt() != i)
            {
                std::cerr << "FAIL: Find result GetInt mismatch at i=" << i << "\n";
                return 1;
            }
        }

        // Find a non-existent name -> nullptr.
        if (Manager.Find("r.Test.DoesNotExist") != nullptr)
        {
            std::cerr << "FAIL: Find should return nullptr for unregistered name\n";
            return 1;
        }

        // Cleanup heap allocations (the CVars themselves are engine-
        // lifetime; only the temporary name buffers belong to this
        // test).
        for (char* Name : Names)
        {
            delete[] Name;
        }

        std::cout << "PASS: RegisterFind (1000 CVars round-trip, content-hash keying preserved)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunRegisterFind();
}
