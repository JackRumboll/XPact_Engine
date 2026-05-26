// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/NumberedNames.cpp -- Actor_N suffix discipline.
// =====================================================================
//
// XCore-4b Rev 3 §4.4 ("Numbered-name support").
//
// Acceptance gate A5 (numbered-name parse + ToString round-trip):
//   * FName("Actor_5") yields the same Index as FName("Actor") with
//     SerialNumber == 5.
//   * ToString() of FName("Actor", 7) produces "Actor_7" bytes exactly.
//   * Leading zeros and overflow are rejected.
//   * WithNumber() shares the base Index without re-interning.
// =====================================================================

#include "Reflection/FName.h"
#include "HAL/FMemory.h"
#include "Containers/FString.h"

#include <cstdio>
#include <cstring>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;

    // -----------------------------------------------------------------
    // FromString recognises "Actor_5" -> base "Actor" + SerialNumber 5.
    // -----------------------------------------------------------------
    {
        FName Plain("Actor");
        FName Numbered("Actor_5");

        if (Plain.GetIndex() != Numbered.GetIndex())
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor_5\") base Index differs from FName(\"Actor\")\n");
            return 1;
        }
        if (Numbered.GetSerialNumber() != 5)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor_5\").GetSerialNumber() = %u (expected 5)\n",
                         Numbered.GetSerialNumber());
            return 1;
        }
        if (Plain.GetSerialNumber() != 0)
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\").GetSerialNumber() = %u (expected 0)\n",
                         Plain.GetSerialNumber());
            return 1;
        }
        if (!Numbered.IsNumbered())
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor_5\").IsNumbered() returned false\n");
            return 1;
        }
        if (Plain.IsNumbered())
        {
            std::fprintf(stderr, "FAIL: FName(\"Actor\").IsNumbered() returned true\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // ToString round-trip: "Actor_7" -> ToString() == "Actor_7".
    // -----------------------------------------------------------------
    {
        FName N("Actor_7");
        ::XCore::FString S = N.ToString();
        if (S.LenBytes() != 7)
        {
            std::fprintf(stderr, "FAIL: ToString(\"Actor_7\").LenBytes() = %d (expected 7)\n", S.LenBytes());
            return 1;
        }
        if (std::memcmp(S.ToUtf8Ptr(), "Actor_7", 7) != 0)
        {
            std::fprintf(stderr, "FAIL: ToString(\"Actor_7\") byte mismatch\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // WithNumber: same Index, different SerialNumber.
    // -----------------------------------------------------------------
    {
        FName Base("Item");
        FName Item3 = FName::WithNumber(Base, 3);

        if (Item3.GetIndex() != Base.GetIndex())
        {
            std::fprintf(stderr, "FAIL: WithNumber Index differs from base\n");
            return 1;
        }
        if (Item3.GetSerialNumber() != 3)
        {
            std::fprintf(stderr, "FAIL: WithNumber(3).GetSerialNumber() = %u (expected 3)\n",
                         Item3.GetSerialNumber());
            return 1;
        }

        // ToString round-trip.
        ::XCore::FString S = Item3.ToString();
        if (S.LenBytes() != 6 || std::memcmp(S.ToUtf8Ptr(), "Item_3", 6) != 0)
        {
            std::fprintf(stderr, "FAIL: WithNumber(3).ToString() = '%s' (expected 'Item_3')\n",
                         S.ToUtf8Cstr());
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Different SerialNumbers compare unequal.
    // -----------------------------------------------------------------
    {
        FName A1("Foo_1");
        FName A2("Foo_2");
        if (A1 == A2)
        {
            std::fprintf(stderr, "FAIL: Foo_1 == Foo_2\n");
            return 1;
        }
        if (A1.GetIndex() != A2.GetIndex())
        {
            std::fprintf(stderr, "FAIL: Foo_1 / Foo_2 have different base Index\n");
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Leading-zero rejection: "Actor_05" is NOT a numbered name.
    // -----------------------------------------------------------------
    {
        FName N("Actor_05");
        if (N.GetSerialNumber() != 0)
        {
            std::fprintf(stderr, "FAIL: \"Actor_05\" was parsed as numbered (got Serial = %u)\n",
                         N.GetSerialNumber());
            return 1;
        }
        if (N.GetBaseLength() != 8)
        {
            std::fprintf(stderr, "FAIL: \"Actor_05\" base length = %d (expected 8 -- leading zero rejected)\n",
                         N.GetBaseLength());
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Single zero "Foo_0" -- SerialNumber == 0 means no suffix per spec,
    // so the parser rejects "_0" too and treats the whole name as literal.
    // -----------------------------------------------------------------
    {
        FName N("Foo_0");
        if (N.GetSerialNumber() != 0)
        {
            std::fprintf(stderr, "FAIL: \"Foo_0\" got Serial = %u (expected 0; treated as literal)\n",
                         N.GetSerialNumber());
            return 1;
        }
        if (N.GetBaseLength() != 5)
        {
            std::fprintf(stderr, "FAIL: \"Foo_0\" base length = %d (expected 5)\n", N.GetBaseLength());
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // ToString of NAME_None returns "None".
    // -----------------------------------------------------------------
    {
        FName N;
        ::XCore::FString S = N.ToString();
        if (S.LenBytes() != 4 || std::memcmp(S.ToUtf8Ptr(), "None", 4) != 0)
        {
            std::fprintf(stderr, "FAIL: NAME_None.ToString() = '%s' (expected 'None')\n", S.ToUtf8Cstr());
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // (Utf8, ByteLen, Suffix) ctor: explicit suffix without parsing.
    // -----------------------------------------------------------------
    {
        FName N("Pawn", 4, 42);
        if (N.GetSerialNumber() != 42)
        {
            std::fprintf(stderr, "FAIL: FName(\"Pawn\", 4, 42).GetSerialNumber() = %u (expected 42)\n",
                         N.GetSerialNumber());
            return 1;
        }
        if (N.GetBaseLength() != 4)
        {
            std::fprintf(stderr, "FAIL: FName(\"Pawn\", 4, 42).GetBaseLength() = %d (expected 4)\n",
                         N.GetBaseLength());
            return 1;
        }
        ::XCore::FString S = N.ToString();
        if (S.LenBytes() != 7 || std::memcmp(S.ToUtf8Ptr(), "Pawn_42", 7) != 0)
        {
            std::fprintf(stderr, "FAIL: FName(\"Pawn\", 4, 42).ToString() = '%s' (expected 'Pawn_42')\n",
                         S.ToUtf8Cstr());
            return 1;
        }
    }

    return 0;
}
