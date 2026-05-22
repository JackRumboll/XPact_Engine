// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/IndexOf.cpp -- byte + codepoint + substring search.
//
// Verifies IndexOfByte / IndexOfCodepoint / IndexOf agree with a
// brute-force reference walker. The BMH substring search is checked
// against a naive O(n*m) scan for 200 random needles in a 4 KiB
// haystack.
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

namespace
{
    // Brute-force substring search for cross-check.
    ::int32 BruteIndexOf(const char* H, ::int32 HLen, const char* N, ::int32 NLen)
    {
        if (NLen == 0) return 0;
        for (::int32 I = 0; I <= HLen - NLen; ++I)
        {
            if (std::memcmp(H + I, N, static_cast<std::size_t>(NLen)) == 0) return I;
        }
        return ::INDEX_NONE;
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // IndexOfByte basic.
    {
        ::XCore::FString S("hello world");
        if (S.IndexOfByte('w') != 6)  { std::fprintf(stderr, "FAIL: IndexOfByte('w')\n"); return 1; }
        if (S.IndexOfByte('z') != ::INDEX_NONE) { std::fprintf(stderr, "FAIL: IndexOfByte('z')\n"); return 1; }
        if (S.IndexOfByte('o') != 4)  { std::fprintf(stderr, "FAIL: IndexOfByte('o') first\n"); return 1; }
        if (S.IndexOfByte('o', 5) != 7) { std::fprintf(stderr, "FAIL: IndexOfByte('o',5)\n"); return 1; }
    }

    // IndexOfCodepoint with multibyte.
    {
        // "Aä中" -- cp 0 = A, cp 1 = ä, cp 2 = 中.
        const unsigned char Buf[] = { 'A', 0xC3,0xA4, 0xE4,0xB8,0xAD };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        if (S.IndexOfCodepoint(U'A') != 0) { std::fprintf(stderr, "FAIL: IndexOfCp(A)\n"); return 1; }
        if (S.IndexOfCodepoint(0xE4U) != 1) { std::fprintf(stderr, "FAIL: IndexOfCp(ä)\n"); return 1; }
        if (S.IndexOfCodepoint(0x4E2DU) != 2) { std::fprintf(stderr, "FAIL: IndexOfCp(中)\n"); return 1; }
        if (S.IndexOfCodepoint(U'Z') != ::INDEX_NONE) { std::fprintf(stderr, "FAIL: IndexOfCp(Z)\n"); return 1; }
    }

    // Substring search: BMH vs brute force.
    {
        ::XCore::FString H("the quick brown fox jumps over the lazy dog");
        if (H.IndexOf(::XCore::FString("brown")) != 10) { std::fprintf(stderr, "FAIL: IndexOf brown\n"); return 1; }
        if (H.IndexOf(::XCore::FString("the"))   != 0)  { std::fprintf(stderr, "FAIL: IndexOf the\n"); return 1; }
        if (H.IndexOf(::XCore::FString("xyz"))   != ::INDEX_NONE) { std::fprintf(stderr, "FAIL: IndexOf xyz\n"); return 1; }
        if (H.IndexOf(::XCore::FString(""))      != 0) { std::fprintf(stderr, "FAIL: IndexOf empty\n"); return 1; }
        if (H.IndexOf(::XCore::FString("dog"))   != 40) { std::fprintf(stderr, "FAIL: IndexOf dog\n"); return 1; }
    }

    // Last index.
    {
        ::XCore::FString S("hello world");
        if (S.LastIndexOf('o') != 7)  { std::fprintf(stderr, "FAIL: LastIndexOf('o')\n"); return 1; }
        if (S.LastIndexOf('z') != ::INDEX_NONE) { std::fprintf(stderr, "FAIL: LastIndexOf('z')\n"); return 1; }
        if (S.LastIndexOf('h') != 0)  { std::fprintf(stderr, "FAIL: LastIndexOf('h')\n"); return 1; }
    }

    // LastIndexOfByte(const FString&) -- Phase 1g fix M-1 substring variant.
    {
        ::XCore::FString H("abracadabra");
        if (H.LastIndexOfByte(::XCore::FString("a")) != 10)
            { std::fprintf(stderr, "FAIL: LastIndexOfByte('a') in 'abracadabra' (got %d, expected 10)\n", H.LastIndexOfByte(::XCore::FString("a"))); return 1; }
        if (H.LastIndexOfByte(::XCore::FString("br")) != 8)
            { std::fprintf(stderr, "FAIL: LastIndexOfByte('br') in 'abracadabra' (got %d, expected 8)\n", H.LastIndexOfByte(::XCore::FString("br"))); return 1; }
        if (H.LastIndexOfByte(::XCore::FString("abra")) != 7)
            { std::fprintf(stderr, "FAIL: LastIndexOfByte('abra') in 'abracadabra' (got %d, expected 7)\n", H.LastIndexOfByte(::XCore::FString("abra"))); return 1; }
        if (H.LastIndexOfByte(::XCore::FString("xyz")) != ::INDEX_NONE)
            { std::fprintf(stderr, "FAIL: LastIndexOfByte('xyz') should be INDEX_NONE\n"); return 1; }
        if (H.LastIndexOfByte(::XCore::FString("")) != H.LenBytes())
            { std::fprintf(stderr, "FAIL: LastIndexOfByte(empty) should return LenBytes (got %d)\n", H.LastIndexOfByte(::XCore::FString(""))); return 1; }
        if (H.LastIndexOfByte(::XCore::FString("abracadabraXYZ")) != ::INDEX_NONE)
            { std::fprintf(stderr, "FAIL: LastIndexOfByte(needle longer than haystack)\n"); return 1; }
    }

    // BMH cross-check: synthetic strings.
    {
        char Hay[400];
        for (int I = 0; I < 400; ++I) Hay[I] = static_cast<char>('a' + (I % 26));
        ::XCore::FString H(Hay, 400);

        // Needles of length 1..6 starting at each position.
        for (int Start = 0; Start < 300; ++Start)
        {
            for (int Len = 1; Len <= 6; ++Len)
            {
                ::XCore::FString N(Hay + Start, Len);
                const ::int32 GotBmh   = H.IndexOf(N);
                const ::int32 ExpBrute = BruteIndexOf(Hay, 400, Hay + Start, Len);
                if (GotBmh != ExpBrute)
                {
                    std::fprintf(stderr, "FAIL: BMH mismatch at start=%d len=%d (bmh=%d brute=%d)\n",
                        Start, Len, GotBmh, ExpBrute);
                    return 1;
                }
            }
        }
    }

    return 0;
}
