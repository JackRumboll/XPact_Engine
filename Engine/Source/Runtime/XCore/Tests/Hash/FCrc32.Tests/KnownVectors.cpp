// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FCrc32.Tests/KnownVectors.cpp -- CRC-32C known-vector test.
// =====================================================================
//
// XCore-4a Section 11.9: validates the FCrc32::Compute output against
// canonical CRC-32C test vectors. CRC-32C (Castagnoli, polynomial
// 0x1EDC6F41) is widely standardized; the test vectors below come from
// RFC 3720 (iSCSI) Appendix B / RFC 9260 (SCTP) and the standard
// "check" vector used by every CRC-32C implementation.
//
// Verified expected outputs (canonical CRC-32C / Castagnoli):
//   ""              -> 0x00000000
//   "123456789"     -> 0xE3069283  (the standard CRC-32C "check" string)
//
// We test only well-known vectors to keep this self-checking; richer
// vector tables (RFC 3720 random data blocks) can be added if a
// regression suggests the implementation drifts. The two values above
// are sufficient for spot-checking the polynomial + initial state.
//
// =====================================================================

#include "Hash/FCrc32.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <cstring>

namespace
{
    struct FCrc32TestVector
    {
        const char* Input;
        ::SIZE_T    Length;
        ::uint32    Expected;
    };

    const FCrc32TestVector kVectors[] = {
        // Empty input.
        { "",          0,  0x00000000u },

        // Standard CRC-32C "check" string. This is the canonical
        // self-test vector used by every CRC-32C library; if our
        // implementation drifts (wrong polynomial, wrong init state,
        // wrong final XOR), this is what catches it.
        { "123456789", 9,  0xE3069283u },
    };
}

int main()
{
    int Failed = 0;

    for (const FCrc32TestVector& V : kVectors)
    {
        const ::uint32 Got = ::XCore::Hash::FCrc32::Compute(V.Input, V.Length);
        if (Got != V.Expected)
        {
            std::fprintf(stderr,
                "FAIL: FCrc32::Compute(\"%s\", %zu) = 0x%08X (expected 0x%08X)\n",
                V.Input, V.Length, Got, V.Expected);
            ++Failed;
        }
    }

    if (Failed > 0)
    {
        std::fprintf(stderr, "FCrc32.KnownVectors: %d failures\n", Failed);
        return 1;
    }

    std::printf("FCrc32.KnownVectors: PASS (%zu vectors)\n", sizeof(kVectors) / sizeof(kVectors[0]));
    return 0;
}
