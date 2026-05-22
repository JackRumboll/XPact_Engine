// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/Append.cpp -- Append + operator+/+= + SSO->heap promotion.
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Basic Append.
    {
        ::XCore::FString S("hello");
        S.Append(::XCore::FString(" world"));
        if (S.LenBytes() != 11) { std::fprintf(stderr, "FAIL: append LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "hello world", 11) != 0) { std::fprintf(stderr, "FAIL: append bytes\n"); return 1; }
    }

    // Append C-string.
    {
        ::XCore::FString S("abc");
        S.Append("def");
        if (S.LenBytes() != 6) { std::fprintf(stderr, "FAIL: append c-str LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "abcdef", 6) != 0) { std::fprintf(stderr, "FAIL: append c-str bytes\n"); return 1; }
    }

    // Append byte buffer + len.
    {
        ::XCore::FString S("X");
        const char* B = "YZW";
        S.Append(B, 2);
        if (S.LenBytes() != 3) { std::fprintf(stderr, "FAIL: append buf LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "XYZ", 3) != 0) { std::fprintf(stderr, "FAIL: append buf bytes\n"); return 1; }
    }

    // SSO -> heap promotion via Append.
    // Start at SSO (40 bytes), append 20 bytes -> total 60, must promote.
    {
        const char* S40 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        ::XCore::FString S(S40);
        if (S.LenBytes() != 40) { std::fprintf(stderr, "FAIL: pre-append LenBytes\n"); return 1; }
        const char* B20 = "BBBBBBBBBBBBBBBBBBBB";
        S.Append(B20);
        if (S.LenBytes() != 60) { std::fprintf(stderr, "FAIL: promote LenBytes=%d\n", S.LenBytes()); return 1; }
        const char* Expected = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABBBBBBBBBBBBBBBBBBBB";
        if (std::memcmp(S.ToUtf8Ptr(), Expected, 60) != 0) { std::fprintf(stderr, "FAIL: promote bytes\n"); return 1; }
    }

    // operator+
    {
        ::XCore::FString A("hi");
        ::XCore::FString B(" there");
        ::XCore::FString C = A + B;
        if (C.LenBytes() != 8) { std::fprintf(stderr, "FAIL: operator+ LenBytes\n"); return 1; }
        if (std::memcmp(C.ToUtf8Ptr(), "hi there", 8) != 0) { std::fprintf(stderr, "FAIL: operator+ bytes\n"); return 1; }
        // Source strings unchanged.
        if (A.LenBytes() != 2) { std::fprintf(stderr, "FAIL: operator+ mutated lhs\n"); return 1; }
        if (B.LenBytes() != 6) { std::fprintf(stderr, "FAIL: operator+ mutated rhs\n"); return 1; }
    }

    // operator+=
    {
        ::XCore::FString S("foo");
        S += ::XCore::FString("bar");
        if (S.LenBytes() != 6) { std::fprintf(stderr, "FAIL: += LenBytes\n"); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "foobar", 6) != 0) { std::fprintf(stderr, "FAIL: += bytes\n"); return 1; }
    }

    // Append null / zero len no-ops.
    {
        ::XCore::FString S("keep");
        S.Append(static_cast<const char*>(nullptr));
        if (S.LenBytes() != 4) { std::fprintf(stderr, "FAIL: append null mutated\n"); return 1; }
        S.Append("xyz", 0);
        if (S.LenBytes() != 4) { std::fprintf(stderr, "FAIL: append zero-len mutated\n"); return 1; }
    }

    return 0;
}
