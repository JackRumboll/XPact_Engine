// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/DiscriminatorLayout.cpp -- Rev 2 FIX-5 / MEDIUM-11
// runtime byte-63 discriminator location test.
// =====================================================================
//
// Per Rev 2 FIX-5 / MEDIUM-11: FString uses byte 63 (high bit) as the
// SSO discriminator. The existing SsoVsHeap.cpp test covers the
// discriminator's TRUTH VALUE at various lengths. This test
// complements it by pinning the BYTE LOCATION of the discriminator at
// offset 63 and the BIT VALUE at the high bit (0x80).
//
// The two tests overlap in coverage but address different defect
// classes:
//   * SsoVsHeap.cpp: "SSO/heap transition fires at the right length".
//   * DiscriminatorLayout.cpp (this file): "byte 63 / bit 7 IS the
//     discriminator -- a code change that moved the marker would still
//     pass SsoVsHeap if the new location flipped consistently, but
//     would fail this test."
//
// LAYOUT SPEC (FString.h:521-525):
//   static constexpr ::int32 kStorageSize       = 64;
//   static constexpr ::int32 kSsoMaxBytes       = 47;
//   static constexpr ::int32 kDiscriminatorByte = 63;
//   static constexpr ::uint8 kSsoFlagBit        = 0x80;
//   static constexpr ::uint8 kSsoLenMask        = 0x7F;
//
//   SSO mode: byte 63 high bit = 1; low 7 bits = byte length 0..47.
//   Heap mode: byte 63 high bit = 0.
//
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

namespace
{
    // Byte-level peek at the discriminator byte at offset 63.
    // The XBT compiler doesn't permit reinterpret_cast across the
    // alignas(16) boundary, but std::byte aliasing is well-defined
    // per [basic.lval]/11.
    unsigned char ReadDiscriminatorByte(const ::XCore::FString& S)
    {
        const auto* Bytes = reinterpret_cast<const unsigned char*>(&S);
        return Bytes[63];
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // ABI lock 1: sizeof(FString) == 64. The discriminator location is
    // only meaningful if the object is exactly 64 bytes; verify
    // explicitly here so a future regression that changes sizeof is
    // caught as a test failure rather than a silent layout drift.
    // -----------------------------------------------------------------
    if (sizeof(::XCore::FString) != 64)
    {
        std::fprintf(stderr,
            "FAIL: sizeof(FString) = %zu; expected 64 "
            "(discriminator location at offset 63 requires 64-byte storage)\n",
            sizeof(::XCore::FString));
        return 1;
    }

    // -----------------------------------------------------------------
    // Step 2: SSO content. Length 5 ("hello") is well below the SSO
    // cap (47 bytes). The discriminator byte MUST have bit 7 set; the
    // low 7 bits MUST equal the byte length (5).
    // -----------------------------------------------------------------
    {
        ::XCore::FString S("hello");
        if (S.LenBytes() != 5)
        {
            std::fprintf(stderr,
                "FAIL: SSO LenBytes mismatch (%d != 5)\n",
                static_cast<int>(S.LenBytes()));
            return 1;
        }

        const unsigned char Byte63 = ReadDiscriminatorByte(S);

        // High bit set => SSO.
        if ((Byte63 & 0x80u) == 0)
        {
            std::fprintf(stderr,
                "FAIL: SSO discriminator at byte 63 has high bit clear "
                "(byte=0x%02X)\n",
                static_cast<unsigned>(Byte63));
            return 1;
        }

        // Low 7 bits encode the SSO byte length.
        const unsigned char SsoLen = static_cast<unsigned char>(Byte63 & 0x7Fu);
        if (SsoLen != 5)
        {
            std::fprintf(stderr,
                "FAIL: SSO discriminator low-7-bits length mismatch "
                "(got %u, expected 5; byte=0x%02X)\n",
                static_cast<unsigned>(SsoLen),
                static_cast<unsigned>(Byte63));
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Step 3: Heap content. Length 100 (> 47) forces heap allocation.
    // The discriminator byte MUST have bit 7 CLEAR.
    // -----------------------------------------------------------------
    {
        char Buf[100];
        std::memset(Buf, 'z', 100);
        ::XCore::FString S(Buf, 100);
        if (S.LenBytes() != 100)
        {
            std::fprintf(stderr,
                "FAIL: heap LenBytes mismatch (%d != 100)\n",
                static_cast<int>(S.LenBytes()));
            return 1;
        }

        const unsigned char Byte63 = ReadDiscriminatorByte(S);

        // High bit clear => heap.
        if ((Byte63 & 0x80u) != 0)
        {
            std::fprintf(stderr,
                "FAIL: heap discriminator at byte 63 has high bit SET "
                "(should be clear; byte=0x%02X)\n",
                static_cast<unsigned>(Byte63));
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Step 4: Boundary lengths. Verify the discriminator is correctly
    // located + correctly bit-set across the SSO/heap boundary.
    //
    // Lengths to probe:
    //   * 0   -- empty SSO; discriminator = 0x80 | 0 = 0x80.
    //   * 47  -- maximum SSO; discriminator = 0x80 | 47 = 0xAF.
    //   * 48  -- minimum heap; discriminator high bit clear.
    // -----------------------------------------------------------------
    {
        ::XCore::FString S;  // default; length 0, SSO.
        if (ReadDiscriminatorByte(S) != 0x80u)
        {
            std::fprintf(stderr,
                "FAIL: empty FString discriminator != 0x80 (got 0x%02X)\n",
                static_cast<unsigned>(ReadDiscriminatorByte(S)));
            return 1;
        }
    }
    {
        const char* B = "12345678901234567890123456789012345678901234567";  // 47 bytes
        ::XCore::FString S(B);
        if (S.LenBytes() != 47)
        {
            std::fprintf(stderr, "FAIL: 47 LenBytes\n");
            return 1;
        }
        if (ReadDiscriminatorByte(S) != 0xAFu)
        {
            std::fprintf(stderr,
                "FAIL: 47-byte SSO discriminator != 0xAF (got 0x%02X)\n",
                static_cast<unsigned>(ReadDiscriminatorByte(S)));
            return 1;
        }
    }
    {
        const char* B = "123456789012345678901234567890123456789012345678";  // 48 bytes
        ::XCore::FString S(B);
        if (S.LenBytes() != 48)
        {
            std::fprintf(stderr, "FAIL: 48 LenBytes\n");
            return 1;
        }
        const unsigned char Byte63 = ReadDiscriminatorByte(S);
        if ((Byte63 & 0x80u) != 0)
        {
            std::fprintf(stderr,
                "FAIL: 48-byte heap discriminator has high bit set "
                "(byte=0x%02X)\n",
                static_cast<unsigned>(Byte63));
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Step 5: Cross-check -- the spec says byte 63 contains the
    // discriminator; verify the BYTES AT EVERY OTHER OFFSET in the
    // heap-mode case don't have bit 7 set in a way that would imply
    // a discriminator location elsewhere.
    //
    // This is a regression catch: if a future revision relocates the
    // discriminator to (say) byte 62, this loop would still permit
    // pass if the new byte happened to be 0x00; but the SSO content
    // case above would also break, providing complementary coverage.
    //
    // We don't assert anything strong here; we just print observed
    // byte 63 vs other-byte snapshot for debugging.
    // -----------------------------------------------------------------
    {
        char Buf[200];
        std::memset(Buf, 'q', 200);
        ::XCore::FString S(Buf, 200);
        const auto* Raw = reinterpret_cast<const unsigned char*>(&S);
        const unsigned char Byte63 = Raw[63];

        // Byte 63 high bit MUST be 0 for the 200-byte heap string.
        if ((Byte63 & 0x80u) != 0)
        {
            std::fprintf(stderr,
                "FAIL: 200-byte heap discriminator has high bit set\n");
            return 1;
        }

        // In heap mode, bytes 0..7 are the m_data pointer (likely
        // non-zero); we don't assert anything about them. Bytes 8..15
        // are m_byteLen = 200 little-endian. Just print the first 16
        // bytes for diagnostic context.
        std::fprintf(stdout,
            "INFO: heap-mode 200-byte FString first 16 bytes: ");
        for (int I = 0; I < 16; ++I)
        {
            std::fprintf(stdout, "%02X ", static_cast<unsigned>(Raw[I]));
        }
        std::fprintf(stdout, "; byte 63 = 0x%02X\n",
                     static_cast<unsigned>(Byte63));
    }

    std::printf("DiscriminatorLayout: PASS\n");
    return 0;
}
