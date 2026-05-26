// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/ToStringRoundTrip.cpp -- bytewise round-trip.
// =====================================================================
//
// XCore-4b Rev 3 §4.4 ToString contract:
//   * SerialNumber == 0 returns the interned bytes verbatim.
//   * SerialNumber > 0 returns "{base}_{N}" formatted via FString.
//
// This verifies the round-trip property: for any input string S that
// is NOT itself a numbered-suffix form ("Actor", "MyVar123Suffix"),
// FName(S).ToString().Equals(S) is true. For numbered forms ("Actor_5"),
// the round-trip preserves base + suffix exactly.
// =====================================================================

#include "Reflection/FName.h"
#include "HAL/FMemory.h"
#include "Containers/FString.h"

#include <cstdio>
#include <cstring>

namespace
{
    bool RoundTrip(const char* Input)
    {
        ::XCore::Reflect::FName N(Input);
        ::XCore::FString S = N.ToString();
        const ::SIZE_T InputLen = std::strlen(Input);
        if (static_cast<::SIZE_T>(S.LenBytes()) != InputLen)
        {
            std::fprintf(stderr, "FAIL: round-trip length mismatch on '%s' (got %d, expected %zu)\n",
                         Input, S.LenBytes(), InputLen);
            return false;
        }
        if (std::memcmp(S.ToUtf8Ptr(), Input, InputLen) != 0)
        {
            std::fprintf(stderr, "FAIL: round-trip bytes mismatch on '%s' (got '%s')\n",
                         Input, S.ToUtf8Cstr());
            return false;
        }
        return true;
    }
} // anonymous

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Plain identifiers.
    if (!RoundTrip("Actor"))           return 1;
    if (!RoundTrip("MyClass"))         return 1;
    if (!RoundTrip("X"))               return 1;
    if (!RoundTrip("PrivateInternal")) return 1;

    // Numbered forms.
    if (!RoundTrip("Actor_1"))         return 1;
    if (!RoundTrip("Actor_42"))        return 1;
    if (!RoundTrip("Inst_9999"))       return 1;

    // Non-numbered forms with underscores in them (the parser must
    // NOT mis-split these).
    if (!RoundTrip("My_Variable"))     return 1;
    if (!RoundTrip("Foo_Bar_Baz"))     return 1;
    // Trailing underscore alone is not numbered (no digits).
    if (!RoundTrip("MyVar_"))          return 1;
    // Leading zeros are rejected so the literal survives.
    if (!RoundTrip("Var_007"))         return 1;

    // Empty (NAME_None) round-trip.
    {
        ::XCore::Reflect::FName N;
        ::XCore::FString S = N.ToString();
        if (S.LenBytes() != 4 || std::memcmp(S.ToUtf8Ptr(), "None", 4) != 0)
        {
            std::fprintf(stderr, "FAIL: NAME_None.ToString() != 'None'\n");
            return 1;
        }
    }

    // ToString followed by FromString returns the same FName.
    {
        ::XCore::Reflect::FName Original("Player_15");
        ::XCore::FString Mid = Original.ToString();
        ::XCore::Reflect::FName Recreated(Mid.ToUtf8Ptr(), Mid.LenBytes());
        if (Original != Recreated)
        {
            std::fprintf(stderr, "FAIL: ToString->FromString round-trip not idempotent for 'Player_15'\n");
            return 1;
        }
        if (Recreated.GetSerialNumber() != 15)
        {
            std::fprintf(stderr, "FAIL: recreated SerialNumber = %u (expected 15)\n",
                         Recreated.GetSerialNumber());
            return 1;
        }
    }

    return 0;
}
