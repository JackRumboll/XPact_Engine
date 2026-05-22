// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/SsoVsHeap.cpp -- SSO/heap discriminator + length boundary.
// =====================================================================
//
// Per Section 11.1.4 the discriminator is the high bit of byte 63.
// Since IsInSso is PRIVATE (deliberately encapsulated per fix Rev 3
// M2), this test observes the discriminator via a byte-level peek at
// the storage. This is the same technique XIL2CPP uses (per Section
// 11.1.5) so the test is the spec-blessed audit form.
//
// Layout reminder:
//   * SSO mode: byte 63 high bit = 1; low 7 bits = byte length 0..47.
//   * Heap mode: byte 63 high bit = 0.
//
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

namespace
{
    // Byte-level peek at the FString storage's discriminator.
    bool ObservedSso(const ::XCore::FString& S)
    {
        const auto* Bytes = reinterpret_cast<const unsigned char*>(&S);
        return (Bytes[63] & 0x80u) != 0;
    }
    unsigned char ObservedSsoLen(const ::XCore::FString& S)
    {
        const auto* Bytes = reinterpret_cast<const unsigned char*>(&S);
        return static_cast<unsigned char>(Bytes[63] & 0x7Fu);
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Length 0 -> SSO with len 0.
    {
        ::XCore::FString S;
        if (!ObservedSso(S)) { std::fprintf(stderr, "FAIL: len 0 not SSO\n"); return 1; }
        if (ObservedSsoLen(S) != 0) { std::fprintf(stderr, "FAIL: len 0 SSO len\n"); return 1; }
    }

    // Length 23 -> SSO.
    {
        const char* B = "12345678901234567890123";  // 23 chars
        ::XCore::FString S(B);
        if (S.LenBytes() != 23) { std::fprintf(stderr, "FAIL: 23 LenBytes\n"); return 1; }
        if (!ObservedSso(S)) { std::fprintf(stderr, "FAIL: 23 not SSO\n"); return 1; }
    }

    // Length 46 -> SSO.
    {
        const char* B = "1234567890123456789012345678901234567890123456";  // 46
        ::XCore::FString S(B);
        if (S.LenBytes() != 46) { std::fprintf(stderr, "FAIL: 46 LenBytes\n"); return 1; }
        if (!ObservedSso(S)) { std::fprintf(stderr, "FAIL: 46 not SSO\n"); return 1; }
    }

    // Length 47 -> SSO (boundary).
    {
        const char* B = "12345678901234567890123456789012345678901234567";  // 47
        ::XCore::FString S(B);
        if (S.LenBytes() != 47) { std::fprintf(stderr, "FAIL: 47 LenBytes\n"); return 1; }
        if (!ObservedSso(S)) { std::fprintf(stderr, "FAIL: 47 not SSO\n"); return 1; }
    }

    // Length 48 -> heap (boundary).
    {
        const char* B = "123456789012345678901234567890123456789012345678";  // 48
        ::XCore::FString S(B);
        if (S.LenBytes() != 48) { std::fprintf(stderr, "FAIL: 48 LenBytes\n"); return 1; }
        if (ObservedSso(S)) { std::fprintf(stderr, "FAIL: 48 still SSO\n"); return 1; }
    }

    // Length 100 -> heap.
    {
        char Buf[100];
        std::memset(Buf, 'a', 100);
        ::XCore::FString S(Buf, 100);
        if (S.LenBytes() != 100) { std::fprintf(stderr, "FAIL: 100 LenBytes\n"); return 1; }
        if (ObservedSso(S)) { std::fprintf(stderr, "FAIL: 100 still SSO\n"); return 1; }
    }

    // Length 1000 -> heap.
    {
        char Buf[1000];
        std::memset(Buf, 'b', 1000);
        ::XCore::FString S(Buf, 1000);
        if (S.LenBytes() != 1000) { std::fprintf(stderr, "FAIL: 1000 LenBytes\n"); return 1; }
        if (ObservedSso(S)) { std::fprintf(stderr, "FAIL: 1000 still SSO\n"); return 1; }
    }

    return 0;
}
