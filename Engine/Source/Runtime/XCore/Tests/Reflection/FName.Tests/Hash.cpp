// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/Hash.cpp -- GetTypeHash determinism.
// =====================================================================
//
// XCore-4b Rev 3 §4.5 ("Hash function + equality") + §4.4 ("Hash
// determinism: FName(\"MyName\").GetTypeHash() returns the bit-exact
// same uint64 on Win64 x86_64, Linux x86_64, Android ARM64").
//
// The cross-platform bit-exactness is asserted via FXxh3's own
// determinism contract (XCore-4a §11.9). This unit test verifies:
//
//   * The same FName produces the same hash on repeated calls.
//   * Different FNames produce different hashes (probabilistic; assert
//     no collision across a small fixture set).
//   * GetTypeHash(NAME_None) is a known canonical value (FXxh3-64 of
//     the all-zero 8-byte sequence -- the same byte input that produces
//     the FName({0, 0}) byte pattern).
//
// The cross-platform vector ("Win64 == Linux == Android" hashes) lives
// in Tests/Determinism/FNameHashVectors.csv per spec A3; this test is
// the per-platform sanity gate.
// =====================================================================

#include "Reflection/FName.h"
#include "Hash/FXxh3.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstdint>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::GetTypeHash;

    // -----------------------------------------------------------------
    // GetTypeHash(NAME_None) equals FXxh3-64 of the 8 zero bytes.
    // -----------------------------------------------------------------
    {
        const ::uint8 ZeroBytes[8] = {0, 0, 0, 0, 0, 0, 0, 0};
        const ::uint64 Expected = ::XCore::Hash::FXxh3::Hash64(ZeroBytes, 8, /*Seed=*/0);

        FName N;
        const ::uint64 Got = GetTypeHash(N);
        if (Got != Expected)
        {
            std::fprintf(stderr, "FAIL: GetTypeHash(NAME_None) = 0x%llx, expected 0x%llx\n",
                         static_cast<unsigned long long>(Got),
                         static_cast<unsigned long long>(Expected));
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Repeatability: same FName, two calls, same hash.
    // -----------------------------------------------------------------
    {
        FName N("Actor");
        const ::uint64 H1 = GetTypeHash(N);
        const ::uint64 H2 = GetTypeHash(N);
        if (H1 != H2)
        {
            std::fprintf(stderr, "FAIL: GetTypeHash(FName(\"Actor\")) not stable across two calls (0x%llx vs 0x%llx)\n",
                         static_cast<unsigned long long>(H1),
                         static_cast<unsigned long long>(H2));
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Distinct FNames produce distinct hashes (small fixture; full
    // determinism CI lives in FNameHashVectors.csv per A3).
    // -----------------------------------------------------------------
    {
        FName A("Alpha");
        FName B("Beta");
        FName G("Gamma");

        const ::uint64 HA = GetTypeHash(A);
        const ::uint64 HB = GetTypeHash(B);
        const ::uint64 HG = GetTypeHash(G);

        if (HA == HB || HA == HG || HB == HG)
        {
            std::fprintf(stderr, "FAIL: hash collision among Alpha/Beta/Gamma: HA=0x%llx HB=0x%llx HG=0x%llx\n",
                         static_cast<unsigned long long>(HA),
                         static_cast<unsigned long long>(HB),
                         static_cast<unsigned long long>(HG));
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Same base, different SerialNumber: different hashes.
    // The two FNames share Index but differ on SerialNumber; the 8-byte
    // handle hash thus differs.
    // -----------------------------------------------------------------
    {
        FName N("X");
        FName N1 = FName::WithNumber(N, 1);
        FName N2 = FName::WithNumber(N, 2);

        const ::uint64 H0 = GetTypeHash(N);
        const ::uint64 H1 = GetTypeHash(N1);
        const ::uint64 H2 = GetTypeHash(N2);

        if (H0 == H1 || H0 == H2 || H1 == H2)
        {
            std::fprintf(stderr, "FAIL: same-base hashes collide across SerialNumber\n");
            return 1;
        }
    }

    return 0;
}
