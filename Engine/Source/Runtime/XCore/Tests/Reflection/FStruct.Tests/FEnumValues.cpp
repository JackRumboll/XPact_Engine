// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStruct.Tests/FEnumValues.cpp -- FEnum::FindByName + FindByValue
// (XCore-4b §7.4).
// =====================================================================
//
// Constructs an FEnum with 5 enumerators and verifies:
//
//   1. FindByName resolves each declared enumerator to its numeric
//      value.
//   2. FindByName returns false for an unknown name.
//   3. FindByValue resolves each numeric value to its declared name.
//   4. FindByValue returns false for an unknown value.
//   5. NumEnumerators returns 5.
//   6. CppForm slot accepts FName("EnumClass").
//
// =====================================================================

#include "HAL/FMemory.h"
#include "Reflection/EEnumFlags.h"
#include "Reflection/FEnum.h"
#include "Reflection/FFieldVariant.h"
#include "Reflection/FName.h"

#include <cstdint>
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
    using namespace ::XCore::Reflect;

    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // Build a synthetic enum: 5 enumerators with non-contiguous
    // numeric values (mirroring `enum class EColor : int { Red=1,
    // Green=2, Blue=4, Yellow=8, Magenta=16 }`).
    // -----------------------------------------------------------------
    FEnum Color(FFieldVariant{}, FName("EColor"), EEnumFlags::ENUM_Flags);

    Color.Values.Add(FEnumValue(FName("Red"),     1));
    Color.Values.Add(FEnumValue(FName("Green"),   2));
    Color.Values.Add(FEnumValue(FName("Blue"),    4));
    Color.Values.Add(FEnumValue(FName("Yellow"),  8));
    Color.Values.Add(FEnumValue(FName("Magenta"), 16));

    Color.CppForm = FName("EnumClass");

    // -----------------------------------------------------------------
    // NumEnumerators + EnumFlags + CppForm accessors.
    // -----------------------------------------------------------------
    Check(Color.NumEnumerators() == 5, "NumEnumerators != 5");
    Check(Color.GetEnumFlags() == EEnumFlags::ENUM_Flags,
          "GetEnumFlags != ENUM_Flags");
    Check(Color.GetCppForm() == FName("EnumClass"),
          "GetCppForm != FName(\"EnumClass\")");

    // -----------------------------------------------------------------
    // FindByName: every declared enumerator resolves correctly.
    // -----------------------------------------------------------------
    {
        ::int64_t V = 0;
        Check(Color.FindByName(FName("Red"),     V), "FindByName(\"Red\") failed");
        Check(V == 1, "FindByName(\"Red\") wrong value");
    }
    {
        ::int64_t V = 0;
        Check(Color.FindByName(FName("Green"),   V), "FindByName(\"Green\") failed");
        Check(V == 2, "FindByName(\"Green\") wrong value");
    }
    {
        ::int64_t V = 0;
        Check(Color.FindByName(FName("Blue"),    V), "FindByName(\"Blue\") failed");
        Check(V == 4, "FindByName(\"Blue\") wrong value");
    }
    {
        ::int64_t V = 0;
        Check(Color.FindByName(FName("Yellow"),  V), "FindByName(\"Yellow\") failed");
        Check(V == 8, "FindByName(\"Yellow\") wrong value");
    }
    {
        ::int64_t V = 0;
        Check(Color.FindByName(FName("Magenta"), V), "FindByName(\"Magenta\") failed");
        Check(V == 16, "FindByName(\"Magenta\") wrong value");
    }

    // -----------------------------------------------------------------
    // FindByName: unknown name returns false.
    // -----------------------------------------------------------------
    {
        ::int64_t V = 999;
        Check(!Color.FindByName(FName("Cyan"), V),
              "FindByName(\"Cyan\") erroneously true");
    }

    // -----------------------------------------------------------------
    // FindByValue: every declared value resolves correctly.
    // -----------------------------------------------------------------
    {
        FName N;
        Check(Color.FindByValue(1,  N), "FindByValue(1) failed");
        Check(N == FName("Red"),  "FindByValue(1) wrong name");
    }
    {
        FName N;
        Check(Color.FindByValue(2,  N), "FindByValue(2) failed");
        Check(N == FName("Green"), "FindByValue(2) wrong name");
    }
    {
        FName N;
        Check(Color.FindByValue(4,  N), "FindByValue(4) failed");
        Check(N == FName("Blue"),  "FindByValue(4) wrong name");
    }
    {
        FName N;
        Check(Color.FindByValue(8,  N), "FindByValue(8) failed");
        Check(N == FName("Yellow"), "FindByValue(8) wrong name");
    }
    {
        FName N;
        Check(Color.FindByValue(16, N), "FindByValue(16) failed");
        Check(N == FName("Magenta"), "FindByValue(16) wrong name");
    }

    // -----------------------------------------------------------------
    // FindByValue: unknown value (composed bitflag).
    // -----------------------------------------------------------------
    {
        FName N;
        Check(!Color.FindByValue(3, N),
              "FindByValue(3 = Red|Green) erroneously matched a single enumerator");
    }

    // -----------------------------------------------------------------
    // StaticClass + IsA<FEnum>.
    // -----------------------------------------------------------------
    Check(FEnum::StaticClass() != nullptr, "FEnum::StaticClass() returned nullptr");
    Check(Color.IsA(FEnum::StaticClass()), "Color is not an FEnum");

    if (g_FailureCount > 0)
    {
        std::cerr << "FStruct.FEnumValues: FAIL ("
                  << g_FailureCount << " failures)\n";
        return 1;
    }
    std::cout << "FStruct.FEnumValues: PASS\n";
    return 0;
}
