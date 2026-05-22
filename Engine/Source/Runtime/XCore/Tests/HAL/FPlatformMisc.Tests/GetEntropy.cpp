// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformMisc.Tests/GetEntropy.cpp -- runtime entropy sanity check.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1 + Section 17.4 acceptance criterion
// D-extra-3 (entropy primitive smoke test).
//
// Sanity-level check: request 256 bytes from GetEntropy and verify the
// output is not all-zeros (a true CSPRNG returns 256 zero bytes with
// probability 2^-2048; observing all-zero is overwhelmingly evidence
// the syscall failed silently). Full entropy verification (NIST SP
// 800-90B test suite) is a separate certification concern.
//
// Phase 1b: this is the first FPlatformMisc test that exercises an
// actual platform-syscall body. The Phase 1a equivalent (Surface.cpp)
// was compile-only.
//
// =====================================================================

#include "HAL/FPlatformMisc.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iostream>

int main()
{
    using ::XCore::HAL::FPlatformMisc;

    // Request 256 bytes.
    unsigned char Buf[256];
    std::memset(Buf, 0, sizeof(Buf));

    FPlatformMisc::GetEntropy(Buf, sizeof(Buf));

    // Verify at least one byte is non-zero.
    bool bAnyNonZero = false;
    for (std::size_t i = 0; i < sizeof(Buf); ++i)
    {
        if (Buf[i] != 0)
        {
            bAnyNonZero = true;
            break;
        }
    }

    if (!bAnyNonZero)
    {
        std::cerr << "FAIL: GetEntropy returned all-zero 256-byte buffer\n";
        return 1;
    }

    // Defensive check: verify the buffer is not all-identical (a
    // pathological deterministic implementation that returns
    // 0x42-fill would pass the non-zero test but is obviously not
    // entropy). Count the unique byte values.
    bool bSeen[256] = { false };
    for (std::size_t i = 0; i < sizeof(Buf); ++i)
    {
        bSeen[Buf[i]] = true;
    }
    int UniqueCount = 0;
    for (int v = 0; v < 256; ++v)
    {
        if (bSeen[v])
        {
            ++UniqueCount;
        }
    }

    // A truly random 256-byte sample sees ~163 unique values on
    // average; the probability of <50 unique values is vanishingly
    // small. Use 50 as a generous lower bound.
    if (UniqueCount < 50)
    {
        std::cerr << "FAIL: GetEntropy returned only " << UniqueCount
                  << " unique byte values; entropy likely broken\n";
        return 1;
    }

    std::cout << "GetEntropy: PASS (" << UniqueCount << " unique bytes)\n";
    return 0;
}
