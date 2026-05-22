// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformMemory.Tests/Memcpy.cpp -- bypass-CRT memcpy correctness.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1. Verifies the bypass-CRT Memcpy wrapper
// produces byte-exact output for a 1 KiB pseudo-random source. The
// pseudo-random seed is fixed so the test is bit-reproducible across
// runs and platforms (NOT cryptographic; just an even distribution
// to maximise cache-line variation).
//
// =====================================================================

#include "HAL/FPlatformMemory.h"

#include <cstdint>
#include <cstring>  // baseline memcmp for the assert
#include <iostream>

int main()
{
    using ::XCore::HAL::FPlatformMemory;

    constexpr int BUF_BYTES = 1024;
    unsigned char Src[BUF_BYTES];
    unsigned char Dst[BUF_BYTES];

    // Fill Src with a fixed pseudo-random pattern via LCG (Numerical
    // Recipes constants; not cryptographic but deterministic across
    // platforms for the same seed).
    std::uint32_t State = 12345u;
    for (int i = 0; i < BUF_BYTES; ++i)
    {
        State = State * 1664525u + 1013904223u;
        Src[i] = static_cast<unsigned char>(State >> 24);
    }
    // Pre-fill Dst with a different pattern so a no-op Memcpy is
    // detectable.
    for (int i = 0; i < BUF_BYTES; ++i)
    {
        Dst[i] = 0xAA;
    }

    // Run the bypass-CRT Memcpy.
    void* Result = FPlatformMemory::Memcpy(Dst, Src, BUF_BYTES);
    if (Result != Dst)
    {
        std::cerr << "FAIL: Memcpy returned wrong pointer (expected Dst="
                  << static_cast<const void*>(Dst) << ", got " << Result << ")\n";
        return 1;
    }

    // Verify byte-exact match.
    if (std::memcmp(Src, Dst, BUF_BYTES) != 0)
    {
        // Find the first divergence for diagnostic detail.
        for (int i = 0; i < BUF_BYTES; ++i)
        {
            if (Src[i] != Dst[i])
            {
                std::cerr << "FAIL: Memcpy diverged at offset " << i
                          << " (Src=" << static_cast<int>(Src[i])
                          << ", Dst=" << static_cast<int>(Dst[i]) << ")\n";
                return 1;
            }
        }
    }

    // Edge case: 0-byte Memcpy returns Dst unchanged.
    unsigned char Z = 0x55;
    void* ZR = FPlatformMemory::Memcpy(&Z, Src, 0);
    if (ZR != &Z || Z != 0x55)
    {
        std::cerr << "FAIL: 0-byte Memcpy mutated destination\n";
        return 1;
    }

    std::cout << "Memcpy: PASS (1 KiB byte-exact)\n";
    return 0;
}
