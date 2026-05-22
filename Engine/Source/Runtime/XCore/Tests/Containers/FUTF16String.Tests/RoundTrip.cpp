// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FUTF16String.Tests/RoundTrip.cpp -- FString <-> FUTF16String byte-exact.
// =====================================================================
//
// Verifies the hand-rolled UTF-8 <-> UTF-16 converter (used on all
// platforms in Phase 1d; the Win32 WideCharToMultiByte path is wired
// in Phase 2). The round-trip property: every valid UTF-8 input
// produces a UTF-16 buffer that decodes back to byte-exact UTF-8.
//
// Inputs sampled: ASCII, Latin-1 supplement, CJK, and supplementary-
// plane emoji.
//
// =====================================================================

#include "Containers/FUTF16String.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

namespace
{
    bool RoundTrip(const char* Utf8, ::int32 Len, const char* TestName)
    {
        ::XCore::FString Original(Utf8, Len);
        ::XCore::FUTF16String Wide = ::XCore::FUTF16String::FromFString(Original);
        ::XCore::FString Recovered = ::XCore::FUTF16String::ToFString(Wide);
        if (Recovered.LenBytes() != Original.LenBytes())
        {
            std::fprintf(stderr, "FAIL: %s recovered len=%d original len=%d\n",
                TestName, Recovered.LenBytes(), Original.LenBytes());
            return false;
        }
        if (std::memcmp(Recovered.ToUtf8Ptr(), Utf8, static_cast<std::size_t>(Len)) != 0)
        {
            std::fprintf(stderr, "FAIL: %s bytes mismatch\n", TestName);
            return false;
        }
        return true;
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // ASCII.
    {
        const char* S = "hello world";
        if (!RoundTrip(S, 11, "ASCII")) return 1;
    }

    // Latin: "äöü".
    {
        const unsigned char Buf[] = { 0xC3,0xA4, 0xC3,0xB6, 0xC3,0xBC };
        if (!RoundTrip(reinterpret_cast<const char*>(Buf), sizeof(Buf), "Latin")) return 1;
    }

    // CJK: "中文".
    {
        const unsigned char Buf[] = { 0xE4,0xB8,0xAD, 0xE6,0x96,0x87 };
        if (!RoundTrip(reinterpret_cast<const char*>(Buf), sizeof(Buf), "CJK")) return 1;
    }

    // Emoji (supplementary plane).
    {
        const unsigned char Buf[] = { 0xF0,0x9F,0x98,0x80, 0xF0,0x9F,0x98,0x81 };
        if (!RoundTrip(reinterpret_cast<const char*>(Buf), sizeof(Buf), "Emoji")) return 1;
    }

    // Mixed.
    {
        const unsigned char Buf[] = {
            'A',
            0xC3, 0xA4,
            0xE4, 0xB8, 0xAD,
            0xF0, 0x9F, 0x98, 0x80
        };
        if (!RoundTrip(reinterpret_cast<const char*>(Buf), sizeof(Buf), "Mixed")) return 1;
    }

    // Empty.
    {
        if (!RoundTrip("", 0, "Empty")) return 1;
    }

    // FUTF16String basic invariants.
    {
        ::XCore::FString Orig("hi");
        ::XCore::FUTF16String W = ::XCore::FUTF16String::FromFString(Orig);
        if (W.WideLen() != 2) { std::fprintf(stderr, "FAIL: WideLen != 2\n"); return 1; }
        if (W.WideCStr()[2] != L'\0') { std::fprintf(stderr, "FAIL: null term\n"); return 1; }
    }

    return 0;
}
