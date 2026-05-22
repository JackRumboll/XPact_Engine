// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/CodepointAt.cpp -- O(n) codepoint access + UTF-8 errors.
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // ASCII round-trip.
    {
        ::XCore::FString S("abc");
        auto R0 = S.CodepointAt(0);
        auto R1 = S.CodepointAt(1);
        auto R2 = S.CodepointAt(2);
        if (!R0.has_value() || R0.value() != U'a') { std::fprintf(stderr, "FAIL: cp(0)\n"); return 1; }
        if (!R1.has_value() || R1.value() != U'b') { std::fprintf(stderr, "FAIL: cp(1)\n"); return 1; }
        if (!R2.has_value() || R2.value() != U'c') { std::fprintf(stderr, "FAIL: cp(2)\n"); return 1; }
    }

    // Out-of-range index.
    {
        ::XCore::FString S("abc");
        auto R = S.CodepointAt(3);
        if (R.has_value()) { std::fprintf(stderr, "FAIL: cp(3) returned value\n"); return 1; }
        if (R.error() != ::XCore::FStringError::IndexOutOfRange) { std::fprintf(stderr, "FAIL: cp(3) error\n"); return 1; }
    }

    // Negative index.
    {
        ::XCore::FString S("abc");
        auto R = S.CodepointAt(-1);
        if (R.has_value()) { std::fprintf(stderr, "FAIL: cp(-1) returned value\n"); return 1; }
    }

    // Empty string.
    {
        ::XCore::FString S;
        auto R = S.CodepointAt(0);
        if (R.has_value()) { std::fprintf(stderr, "FAIL: cp(0) on empty returned value\n"); return 1; }
        if (R.error() != ::XCore::FStringError::EmptyString) { std::fprintf(stderr, "FAIL: cp(0) on empty error\n"); return 1; }
    }

    // UTF-8 multibyte: "A" + "ä" + "中"
    {
        const unsigned char Buf[] = { 'A', 0xC3,0xA4, 0xE4,0xB8,0xAD };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));

        auto R0 = S.CodepointAt(0);
        if (!R0.has_value() || R0.value() != U'A')      { std::fprintf(stderr, "FAIL: utf8 cp(0)\n"); return 1; }
        auto R1 = S.CodepointAt(1);
        if (!R1.has_value() || R1.value() != 0x00E4U)   { std::fprintf(stderr, "FAIL: utf8 cp(1)\n"); return 1; }
        auto R2 = S.CodepointAt(2);
        if (!R2.has_value() || R2.value() != 0x4E2DU)   { std::fprintf(stderr, "FAIL: utf8 cp(2)\n"); return 1; }
    }

    // Malformed UTF-8: lone continuation byte.
    {
        const unsigned char Buf[] = { 0x80, 'a' };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        auto R = S.CodepointAt(0);
        if (R.has_value()) { std::fprintf(stderr, "FAIL: malformed produced value\n"); return 1; }
        if (R.error() != ::XCore::FStringError::InvalidUtf8) { std::fprintf(stderr, "FAIL: malformed error type\n"); return 1; }
    }

    // Genuine U+FFFD in source should be returned as a value, not an error.
    {
        // U+FFFD = EF BF BD
        const unsigned char Buf[] = { 0xEF, 0xBF, 0xBD };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        auto R = S.CodepointAt(0);
        if (!R.has_value()) { std::fprintf(stderr, "FAIL: genuine FFFD treated as error\n"); return 1; }
        if (R.value() != 0xFFFDU) { std::fprintf(stderr, "FAIL: genuine FFFD value\n"); return 1; }
    }

    return 0;
}
