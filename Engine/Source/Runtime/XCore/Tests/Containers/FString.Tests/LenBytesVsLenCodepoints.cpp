// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/LenBytesVsLenCodepoints.cpp -- locked decision 3.
// =====================================================================
//
// Verifies the LenBytes vs LenCodepoints distinction is honoured.
// Tests multibyte UTF-8 strings (Latin supplement, CJK, emoji).
// Per locked decision 3, callers must be explicit about the unit.

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // ASCII: LenBytes == LenCodepoints.
    {
        ::XCore::FString S("hello");
        if (S.LenBytes()      != 5) { std::fprintf(stderr, "FAIL: ascii LenBytes=%d\n", S.LenBytes()); return 1; }
        if (S.LenCodepoints() != 5) { std::fprintf(stderr, "FAIL: ascii LenCodepoints=%d\n", S.LenCodepoints()); return 1; }
    }

    // Latin-1 supplement: each codepoint is 2 UTF-8 bytes.
    // Three codepoints: U+00E4 (ä), U+00F6 (ö), U+00FC (ü)
    // UTF-8 bytes: C3 A4 C3 B6 C3 BC -- 6 bytes, 3 codepoints.
    {
        const unsigned char Buf[] = { 0xC3,0xA4, 0xC3,0xB6, 0xC3,0xBC };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        if (S.LenBytes()      != 6) { std::fprintf(stderr, "FAIL: latin LenBytes=%d\n", S.LenBytes()); return 1; }
        if (S.LenCodepoints() != 3) { std::fprintf(stderr, "FAIL: latin LenCodepoints=%d\n", S.LenCodepoints()); return 1; }
    }

    // CJK: each codepoint is 3 UTF-8 bytes.
    // Two codepoints: U+4E2D (中), U+6587 (文)
    // UTF-8 bytes: E4 B8 AD E6 96 87 -- 6 bytes, 2 codepoints.
    {
        const unsigned char Buf[] = { 0xE4,0xB8,0xAD, 0xE6,0x96,0x87 };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        if (S.LenBytes()      != 6) { std::fprintf(stderr, "FAIL: cjk LenBytes=%d\n", S.LenBytes()); return 1; }
        if (S.LenCodepoints() != 2) { std::fprintf(stderr, "FAIL: cjk LenCodepoints=%d\n", S.LenCodepoints()); return 1; }
    }

    // Emoji: U+1F600 (😀) is 4 UTF-8 bytes (supplementary plane).
    // Two of them: 8 bytes, 2 codepoints.
    {
        const unsigned char Buf[] = { 0xF0,0x9F,0x98,0x80,  0xF0,0x9F,0x98,0x80 };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        if (S.LenBytes()      != 8) { std::fprintf(stderr, "FAIL: emoji LenBytes=%d\n", S.LenBytes()); return 1; }
        if (S.LenCodepoints() != 2) { std::fprintf(stderr, "FAIL: emoji LenCodepoints=%d\n", S.LenCodepoints()); return 1; }
    }

    // Mixed: ASCII + Latin + CJK + emoji.
    // "A" (1 byte 1 cp) + "ä" (2 bytes 1 cp) + "中" (3 bytes 1 cp) + emoji (4 bytes 1 cp)
    // = 10 bytes, 4 codepoints.
    {
        const unsigned char Buf[] = {
            'A',
            0xC3, 0xA4,
            0xE4, 0xB8, 0xAD,
            0xF0, 0x9F, 0x98, 0x80
        };
        ::XCore::FString S(reinterpret_cast<const char*>(Buf), sizeof(Buf));
        if (S.LenBytes()      != 10) { std::fprintf(stderr, "FAIL: mixed LenBytes=%d\n", S.LenBytes()); return 1; }
        if (S.LenCodepoints() != 4)  { std::fprintf(stderr, "FAIL: mixed LenCodepoints=%d\n", S.LenCodepoints()); return 1; }
    }

    // Empty: both 0.
    {
        ::XCore::FString S;
        if (S.LenBytes()      != 0) { std::fprintf(stderr, "FAIL: empty LenBytes\n"); return 1; }
        if (S.LenCodepoints() != 0) { std::fprintf(stderr, "FAIL: empty LenCodepoints\n"); return 1; }
    }

    return 0;
}
