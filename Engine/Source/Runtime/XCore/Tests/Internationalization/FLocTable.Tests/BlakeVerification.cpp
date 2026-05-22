// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLocTable.Tests/BlakeVerification.cpp -- BLAKE3 tamper-detection test.
// =====================================================================
//
// XCore-4a Rev 3 Section 18 OPEN-5 + Section 11.9 BLAKE3 contract.
//
// Verifies that:
//   * A valid loctable loads cleanly.
//   * A tampered loctable (any byte in the payload flipped) fails
//     verification and returns FParseError::Malformed.
//
// The tamper test exercises the load-bearing integrity-check property
// of the OPEN-5 RECOMMENDED format: any single-byte corruption in the
// payload region produces a BLAKE3-digest mismatch.
//
// =====================================================================

#include "Internationalization/FLocTableLoader.h"
#include "Internationalization/FLocTable.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>
#include <cstring>

extern "C" int XPact_GenerateSampleLoctables(const char* OutputDir);

int main()
{
    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);

    // Generate the sample fixtures.
    const char* Dir = ".";
    if (XPact_GenerateSampleLoctables(Dir) != 0)
    {
        std::fprintf(stderr, "[BlakeVerification] FAIL: fixture generation failed\n");
        return 1;
    }

    // ---------- Test 1: valid loctable loads cleanly ----------
    {
        ::XCore::FString Path(Dir);
        Path.Append("/en-US.loctable");

        auto Result = ::XCore::Loc::FLocTableLoader::LoadFromFile(Path);
        if (!Result.has_value())
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: valid loctable failed to load: error=%d\n",
                static_cast<int>(Result.error()));
            return 1;
        }
        if (Result.value().Num() < 5)
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: valid loctable has too few entries: %d\n",
                Result.value().Num());
            return 1;
        }
    }

    // ---------- Test 2: tampered file fails verification ----------
    {
        ::XCore::FString OrigPath(Dir);
        OrigPath.Append("/en-US.loctable");
        ::XCore::FString TamperPath(Dir);
        TamperPath.Append("/en-US.tampered.loctable");

        // Read original.
        std::FILE* Fp = std::fopen(OrigPath.ToUtf8Cstr(), "rb");
        if (Fp == nullptr)
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: cannot open original\n");
            return 1;
        }
        std::fseek(Fp, 0, SEEK_END);
        long FileLen = std::ftell(Fp);
        std::fseek(Fp, 0, SEEK_SET);
        void* Buf = ::XCore::HAL::FMemory::MallocOrAbort(
            static_cast<::SIZE_T>(FileLen), 1,
            ::XCore::HAL::FMemTag::Localization);
        ::SIZE_T BytesRead = std::fread(Buf, 1, static_cast<::SIZE_T>(FileLen), Fp);
        std::fclose(Fp);
        if (BytesRead != static_cast<::SIZE_T>(FileLen))
        {
            std::fprintf(stderr, "[BlakeVerification] FAIL: read short\n");
            ::XCore::HAL::FMemory::Free(Buf);
            return 1;
        }

        // Tamper: flip a byte in the payload region (offset 50,
        // safely past the 40-byte fixed header and 8-byte
        // EntryCount).
        ::uint8* Bytes = static_cast<::uint8*>(Buf);
        Bytes[50] ^= 0xFF;

        // Write tampered file.
        Fp = std::fopen(TamperPath.ToUtf8Cstr(), "wb");
        if (Fp == nullptr)
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: cannot write tamper file\n");
            ::XCore::HAL::FMemory::Free(Buf);
            return 1;
        }
        std::fwrite(Bytes, 1, static_cast<::SIZE_T>(FileLen), Fp);
        std::fclose(Fp);
        ::XCore::HAL::FMemory::Free(Buf);

        // Load tampered: must fail.
        auto Result = ::XCore::Loc::FLocTableLoader::LoadFromFile(TamperPath);
        if (Result.has_value())
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: tampered loctable loaded successfully (should have failed)\n");
            return 1;
        }
        if (Result.error() != ::XCore::FParseError::Malformed)
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: tampered loctable error code wrong: %d\n",
                static_cast<int>(Result.error()));
            return 1;
        }

        // Cleanup: remove the tamper file so it doesn't pollute
        // subsequent test runs.
        std::remove(TamperPath.ToUtf8Cstr());
    }

    // ---------- Test 3: magic-byte tamper fails ----------
    {
        ::XCore::FString OrigPath(Dir);
        OrigPath.Append("/en-US.loctable");
        ::XCore::FString TamperPath(Dir);
        TamperPath.Append("/en-US.badmagic.loctable");

        std::FILE* Fp = std::fopen(OrigPath.ToUtf8Cstr(), "rb");
        std::fseek(Fp, 0, SEEK_END);
        long FileLen = std::ftell(Fp);
        std::fseek(Fp, 0, SEEK_SET);
        void* Buf = ::XCore::HAL::FMemory::MallocOrAbort(
            static_cast<::SIZE_T>(FileLen), 1,
            ::XCore::HAL::FMemTag::Localization);
        std::fread(Buf, 1, static_cast<::SIZE_T>(FileLen), Fp);
        std::fclose(Fp);

        ::uint8* Bytes = static_cast<::uint8*>(Buf);
        Bytes[0] = 0x00; // corrupt magic.

        Fp = std::fopen(TamperPath.ToUtf8Cstr(), "wb");
        std::fwrite(Bytes, 1, static_cast<::SIZE_T>(FileLen), Fp);
        std::fclose(Fp);
        ::XCore::HAL::FMemory::Free(Buf);

        auto Result = ::XCore::Loc::FLocTableLoader::LoadFromFile(TamperPath);
        if (Result.has_value())
        {
            std::fprintf(stderr,
                "[BlakeVerification] FAIL: bad-magic loctable loaded (should have failed)\n");
            return 1;
        }

        std::remove(TamperPath.ToUtf8Cstr());
    }

    std::fprintf(stdout, "PASS: BlakeVerification\n");
    return 0;
}
