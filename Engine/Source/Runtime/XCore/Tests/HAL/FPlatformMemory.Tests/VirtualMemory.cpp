// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformMemory.Tests/VirtualMemory.cpp -- VM lifecycle test.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.1. Verifies the full
// Reserve -> Commit -> write -> Decommit -> Release lifecycle:
//
//   1. Reserve 1 MiB of virtual address space.
//   2. Commit the first 4 KiB page.
//   3. Verify the committed bytes are zero (Win64 VirtualAlloc and
//      Linux mmap MAP_ANONYMOUS both guarantee zero-on-commit).
//   4. Write a pattern into the committed page.
//   5. Read back the pattern.
//   6. Decommit the page.
//   7. Release the range.
//
// Each step is gated by an XPACT_CHECK-equivalent return-1-on-failure
// pattern (we cannot use XPACT_CHECK directly from a standalone test
// without the assertion-helper TU; the test prints to stderr and
// returns non-zero exit code).
//
// =====================================================================

#include "HAL/FPlatformMemory.h"

#include <cstddef>
#include <cstdint>
#include <iostream>

int main()
{
    using ::XCore::HAL::FPlatformMemory;

    constexpr std::size_t RESERVE_BYTES = 1u << 20;  // 1 MiB
    constexpr std::size_t COMMIT_BYTES  = 4096;      // 1 page

    // --- 1. Reserve.
    void* Base = FPlatformMemory::ReserveVirtual(RESERVE_BYTES);
    if (Base == nullptr)
    {
        std::cerr << "FAIL: ReserveVirtual(1 MiB) returned nullptr\n";
        return 1;
    }

    // --- 2. Commit first page.
    if (!FPlatformMemory::CommitVirtual(Base, COMMIT_BYTES))
    {
        std::cerr << "FAIL: CommitVirtual returned false\n";
        FPlatformMemory::ReleaseVirtual(Base, RESERVE_BYTES);
        return 1;
    }

    // --- 3. Verify zero-on-commit.
    auto* Bytes = static_cast<unsigned char*>(Base);
    for (std::size_t i = 0; i < COMMIT_BYTES; ++i)
    {
        if (Bytes[i] != 0)
        {
            std::cerr << "FAIL: committed page not zero at offset " << i
                      << " (value=" << static_cast<int>(Bytes[i]) << ")\n";
            FPlatformMemory::DecommitVirtual(Base, COMMIT_BYTES);
            FPlatformMemory::ReleaseVirtual(Base, RESERVE_BYTES);
            return 1;
        }
    }

    // --- 4. Write a pattern.
    for (std::size_t i = 0; i < COMMIT_BYTES; ++i)
    {
        Bytes[i] = static_cast<unsigned char>(i & 0xFF);
    }

    // --- 5. Read back.
    for (std::size_t i = 0; i < COMMIT_BYTES; ++i)
    {
        const unsigned char Expected = static_cast<unsigned char>(i & 0xFF);
        if (Bytes[i] != Expected)
        {
            std::cerr << "FAIL: pattern read-back mismatch at offset " << i
                      << " (got " << static_cast<int>(Bytes[i])
                      << " expected " << static_cast<int>(Expected) << ")\n";
            FPlatformMemory::DecommitVirtual(Base, COMMIT_BYTES);
            FPlatformMemory::ReleaseVirtual(Base, RESERVE_BYTES);
            return 1;
        }
    }

    // --- 6. Decommit.
    if (!FPlatformMemory::DecommitVirtual(Base, COMMIT_BYTES))
    {
        std::cerr << "FAIL: DecommitVirtual returned false\n";
        FPlatformMemory::ReleaseVirtual(Base, RESERVE_BYTES);
        return 1;
    }

    // NOTE: a read after decommit traps on Win64 (PAGE_NOACCESS) and
    // on Linux (PROT_NONE after mprotect). We intentionally do NOT
    // exercise the trap path here -- the test runner cannot recover
    // from a SIGSEGV. The "trap on decommit access" property is
    // verified by code review of the per-platform decommit body.

    // --- 7. Release.
    if (!FPlatformMemory::ReleaseVirtual(Base, RESERVE_BYTES))
    {
        std::cerr << "FAIL: ReleaseVirtual returned false\n";
        return 1;
    }

    std::cout << "VirtualMemory: PASS\n";
    return 0;
}
