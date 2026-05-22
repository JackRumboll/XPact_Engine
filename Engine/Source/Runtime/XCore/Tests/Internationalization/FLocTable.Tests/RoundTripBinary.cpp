// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FLocTable.Tests/RoundTripBinary.cpp -- save+load byte-exact test.
// =====================================================================
//
// XCore-4a Rev 3 Section 18 OPEN-5.
//
// Verifies that:
//   * A TArray<FLocEntry> serialised via SaveToFile and re-loaded via
//     LoadFromFile produces a byte-exact reproduction of the original
//     entries.
//   * The serialized file is byte-exact deterministic given identical
//     inputs (a property required for the OPEN-5 bit-exactness contract
//     across Win64 / Linux / Android).
//
// =====================================================================

#include "Internationalization/FLocTableLoader.h"
#include "Internationalization/FLocTable.h"
#include "HAL/FMemory.h"
#include "HAL/XInitPhase.h"

#include <cstdio>
#include <cstring>

namespace
{
    using FLocEntry = ::XCore::Loc::FLocTableLoader::FLocEntry;

    static FLocEntry MakeEntry(const char* Ns, const char* Key, const char* Val)
    {
        FLocEntry E;
        E.Namespace = ::XCore::FString(Ns);
        E.Key       = ::XCore::FString(Key);
        E.Value     = ::XCore::FString(Val);
        return E;
    }
} // namespace

int main()
{
    using namespace ::XCore;
    using namespace ::XCore::Loc;

    ::XCore::HAL::FMemory::__Init();
    ::XCore::HAL::__AdvanceInitPhase(::XCore::HAL::EInitPhase::PostStaticInit);

    // ----- Build a small entries set -----
    TArray<FLocEntry, DefaultAllocator> InEntries(
        DefaultAllocator(::XCore::HAL::FMemTag::Localization));
    InEntries.Emplace(MakeEntry("Ns1", "K1", "Value1"));
    InEntries.Emplace(MakeEntry("Ns1", "K2", "Value2"));
    InEntries.Emplace(MakeEntry("AnotherNs", "K1", "Different"));
    InEntries.Emplace(MakeEntry("", "", ""));  // empty-field edge case
    InEntries.Emplace(MakeEntry("UTF-8", "Hello", "\xe3\x81\x93\xe3\x82\x93\xe3\x81\xab\xe3\x81\xa1\xe3\x81\xaf"));

    // ----- Save to disk -----
    const ::XCore::FString Path("./roundtrip.loctable");
    if (!FLocTableLoader::SaveToFile(Path, InEntries))
    {
        std::fprintf(stderr, "[RoundTrip] FAIL: SaveToFile failed\n");
        return 1;
    }

    // ----- Load back -----
    auto Result = FLocTableLoader::LoadFromFile(Path);
    if (!Result.has_value())
    {
        std::fprintf(stderr, "[RoundTrip] FAIL: LoadFromFile failed err=%d\n",
                     static_cast<int>(Result.error()));
        return 1;
    }
    auto& OutEntries = Result.value();

    // ----- Compare entry-by-entry -----
    if (OutEntries.Num() != InEntries.Num())
    {
        std::fprintf(stderr,
            "[RoundTrip] FAIL: entry count mismatch in=%d out=%d\n",
            InEntries.Num(), OutEntries.Num());
        return 1;
    }

    for (::int32 I = 0; I < InEntries.Num(); ++I)
    {
        const FLocEntry& InE  = InEntries[I];
        const FLocEntry& OutE = OutEntries[I];

        if (!(InE.Namespace == OutE.Namespace))
        {
            std::fprintf(stderr,
                "[RoundTrip] FAIL: entry %d namespace mismatch\n", I);
            return 1;
        }
        if (!(InE.Key == OutE.Key))
        {
            std::fprintf(stderr,
                "[RoundTrip] FAIL: entry %d key mismatch\n", I);
            return 1;
        }
        if (!(InE.Value == OutE.Value))
        {
            std::fprintf(stderr,
                "[RoundTrip] FAIL: entry %d value mismatch\n", I);
            return 1;
        }
    }

    // ----- Save twice and assert byte-exact (determinism) -----
    const ::XCore::FString Path2("./roundtrip2.loctable");
    if (!FLocTableLoader::SaveToFile(Path2, InEntries))
    {
        std::fprintf(stderr, "[RoundTrip] FAIL: SaveToFile #2 failed\n");
        return 1;
    }

    // Read both files and byte-compare.
    auto ReadAll = [](const char* P, ::SIZE_T& OutLen) -> ::uint8* {
        std::FILE* Fp = std::fopen(P, "rb");
        if (!Fp) return nullptr;
        std::fseek(Fp, 0, SEEK_END);
        long L = std::ftell(Fp);
        std::fseek(Fp, 0, SEEK_SET);
        ::uint8* Buf = static_cast<::uint8*>(
            ::XCore::HAL::FMemory::MallocOrAbort(
                static_cast<::SIZE_T>(L), 1,
                ::XCore::HAL::FMemTag::Localization));
        std::fread(Buf, 1, static_cast<::SIZE_T>(L), Fp);
        std::fclose(Fp);
        OutLen = static_cast<::SIZE_T>(L);
        return Buf;
    };

    ::SIZE_T Len1, Len2;
    ::uint8* B1 = ReadAll(Path.ToUtf8Cstr(),  Len1);
    ::uint8* B2 = ReadAll(Path2.ToUtf8Cstr(), Len2);

    if (Len1 != Len2 || std::memcmp(B1, B2, Len1) != 0)
    {
        std::fprintf(stderr,
            "[RoundTrip] FAIL: two saves not byte-exact (Len1=%zu Len2=%zu)\n",
            Len1, Len2);
        ::XCore::HAL::FMemory::Free(B1);
        ::XCore::HAL::FMemory::Free(B2);
        return 1;
    }

    ::XCore::HAL::FMemory::Free(B1);
    ::XCore::HAL::FMemory::Free(B2);

    // Cleanup.
    std::remove(Path.ToUtf8Cstr());
    std::remove(Path2.ToUtf8Cstr());

    std::fprintf(stdout, "PASS: RoundTripBinary\n");
    return 0;
}
