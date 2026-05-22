// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/Construction.cpp -- ctor + assignment surface.
// =====================================================================
//
// Verifies default, C-string, C-string + len, copy, move ctors and
// assignments. Walks the 47-byte SSO boundary explicitly: strings
// of length 47 must be SSO; strings of length 48 must be heap.
//
// Since IsInSso is a private helper (per Section 11.1.4), this test
// observes mode INDIRECTLY: it constructs strings of various lengths
// and checks LenBytes / Equals / Data round-trips. The mode is
// confirmed by the separate SsoVsHeap.cpp test that inspects byte 63
// via the public storage view.
//
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Default ctor: empty string.
    {
        ::XCore::FString S;
        if (S.LenBytes() != 0) { std::fprintf(stderr, "FAIL: default ctor LenBytes != 0\n"); return 1; }
        if (!S.IsEmpty())      { std::fprintf(stderr, "FAIL: default ctor IsEmpty\n");      return 1; }
        if (S.LenCodepoints() != 0) { std::fprintf(stderr, "FAIL: default ctor LenCodepoints\n"); return 1; }
    }

    // Construct from ASCII C-string.
    {
        ::XCore::FString S("hello");
        if (S.LenBytes() != 5) { std::fprintf(stderr, "FAIL: ctor(hello) LenBytes=%d\n", S.LenBytes()); return 1; }
        if (S.LenCodepoints() != 5) { std::fprintf(stderr, "FAIL: ctor(hello) LenCodepoints=%d\n", S.LenCodepoints()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "hello", 5) != 0) { std::fprintf(stderr, "FAIL: ctor(hello) bytes mismatch\n"); return 1; }
    }

    // Construct from C-string + len.
    {
        const char* Buf = "abcdef";
        ::XCore::FString S(Buf, 3);
        if (S.LenBytes() != 3) { std::fprintf(stderr, "FAIL: ctor(buf,3) LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "abc", 3) != 0) { std::fprintf(stderr, "FAIL: ctor(buf,3) bytes mismatch\n"); return 1; }
    }

    // SSO boundary: 47 bytes is the max SSO size per spec.
    {
        // 47 'a's
        const char* S47 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ::XCore::FString S(S47);
        if (S.LenBytes() != 47) { std::fprintf(stderr, "FAIL: SSO(47) LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), S47, 47) != 0) { std::fprintf(stderr, "FAIL: SSO(47) bytes\n"); return 1; }
    }

    // Heap boundary: 48 bytes is the min heap size.
    {
        const char* S48 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ::XCore::FString S(S48);
        if (S.LenBytes() != 48) { std::fprintf(stderr, "FAIL: Heap(48) LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), S48, 48) != 0) { std::fprintf(stderr, "FAIL: Heap(48) bytes\n"); return 1; }
    }

    // Larger heap string.
    {
        char Buf[1024];
        for (int I = 0; I < 1024; ++I) Buf[I] = static_cast<char>('A' + (I % 26));
        ::XCore::FString S(Buf, 1024);
        if (S.LenBytes() != 1024) { std::fprintf(stderr, "FAIL: large heap LenBytes=%d\n", S.LenBytes()); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), Buf, 1024) != 0) { std::fprintf(stderr, "FAIL: large heap bytes\n"); return 1; }
    }

    // Copy ctor + Equals (SSO).
    {
        ::XCore::FString A("hello world");
        ::XCore::FString B(A);
        if (!A.Equals(B))   { std::fprintf(stderr, "FAIL: copy ctor Equals (sso)\n"); return 1; }
        if (B.LenBytes() != A.LenBytes()) { std::fprintf(stderr, "FAIL: copy ctor LenBytes (sso)\n"); return 1; }
    }

    // Copy ctor + Equals (heap).
    {
        char Buf[200];
        for (int I = 0; I < 200; ++I) Buf[I] = static_cast<char>('a' + (I % 26));
        ::XCore::FString A(Buf, 200);
        ::XCore::FString B(A);
        if (!A.Equals(B))   { std::fprintf(stderr, "FAIL: copy ctor Equals (heap)\n"); return 1; }
        if (B.LenBytes() != 200) { std::fprintf(stderr, "FAIL: copy ctor LenBytes (heap)\n"); return 1; }
    }

    // Move ctor.
    {
        ::XCore::FString A("moved value");
        const int OrigLen = A.LenBytes();
        ::XCore::FString B(std::move(A));
        if (B.LenBytes() != OrigLen) { std::fprintf(stderr, "FAIL: move ctor LenBytes\n"); return 1; }
        if (!A.IsEmpty()) { std::fprintf(stderr, "FAIL: move ctor source not empty\n"); return 1; }
    }

    // Copy assign.
    {
        ::XCore::FString A("source");
        ::XCore::FString B("destination");
        B = A;
        if (!B.Equals(A)) { std::fprintf(stderr, "FAIL: copy assign\n"); return 1; }
    }

    // Move assign.
    {
        ::XCore::FString A("src2");
        ::XCore::FString B("dst2");
        B = std::move(A);
        if (B.LenBytes() != 4) { std::fprintf(stderr, "FAIL: move assign LenBytes\n"); return 1; }
    }

    // Assign from C-string.
    {
        ::XCore::FString S("initial");
        S = "replaced";
        if (S.LenBytes() != 8) { std::fprintf(stderr, "FAIL: C-string assign LenBytes\n"); return 1; }
        if (std::memcmp(S.ToUtf8Ptr(), "replaced", 8) != 0) { std::fprintf(stderr, "FAIL: C-string assign bytes\n"); return 1; }
    }

    return 0;
}
