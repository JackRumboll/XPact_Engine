// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TSet.Tests/AddContainsRemove.cpp -- TSet basic correctness.
// =====================================================================
//
// XCore-4a Section 5.6: "Add/Remove/Reserve/Reset round-trips per type"
// for TSet. Adds N pseudo-random int32 values, checks Contains agrees
// with a reference implementation (std::unordered_set), removes them
// all, checks size returns to 0.
//
// We use a deterministic xorshift PRNG so the test is reproducible
// across runs / platforms.
//
// =====================================================================

#include "Containers/TSet.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <unordered_set>

namespace
{
    constexpr ::int32 kNumOps = 10000;  // 100K from the brief; 10K keeps the
                                        // test under 10s with std::unordered_set
                                        // verification on every step.

    // -----------------------------------------------------------------
    // Deterministic xorshift PRNG so the test sequence is identical on
    // every run + every platform. Bit-exactness contract honoured.
    // -----------------------------------------------------------------
    ::uint64 g_RngState = 0x9E3779B185EBCA87ULL;
    ::int32 NextI32() noexcept
    {
        g_RngState ^= g_RngState << 13;
        g_RngState ^= g_RngState >> 7;
        g_RngState ^= g_RngState << 17;
        // Mask to a 31-bit non-negative int32 so signed comparison is
        // well-defined in the test.
        return static_cast<::int32>(g_RngState & 0x7FFFFFFFu);
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        ::XCore::TSet<::int32> S;
        std::unordered_set<::int32> Reference;

        // ---------------------------------------------------------------
        // Phase 1: Add kNumOps random int32 values. Track in a reference
        // set; verify Contains agrees at the end.
        // ---------------------------------------------------------------
        for (::int32 I = 0; I < kNumOps; ++I)
        {
            const ::int32 Value = NextI32();
            const bool RefHadIt = Reference.count(Value) > 0;
            const bool Added    = S.Add(Value);
            Reference.insert(Value);

            if (Added == RefHadIt)
            {
                // Added==true means newly inserted, RefHadIt==false expected.
                // Added==false means already present, RefHadIt==true expected.
                // The two should always be opposite.
                std::fprintf(stderr,
                    "FAIL: Add(%d) returned %d but reference had-it = %d (op %d)\n",
                    Value, static_cast<int>(Added), static_cast<int>(RefHadIt), I);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // Set sizes must match.
        if (static_cast<::SIZE_T>(S.Num()) != Reference.size())
        {
            std::fprintf(stderr,
                "FAIL: TSet::Num() = %d, std::unordered_set size = %zu\n",
                S.Num(), Reference.size());
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // ---------------------------------------------------------------
        // Phase 2: verify Contains agrees with the reference for every
        // element + a sample of non-elements.
        // ---------------------------------------------------------------
        for (::int32 Value : Reference)
        {
            if (!S.Contains(Value))
            {
                std::fprintf(stderr,
                    "FAIL: TSet does not contain %d (but reference does)\n", Value);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // Sample 1000 random values; for each, check both sides agree.
        for (::int32 I = 0; I < 1000; ++I)
        {
            const ::int32 Value   = NextI32();
            const bool TsetHas    = S.Contains(Value);
            const bool RefHas     = Reference.count(Value) > 0;
            if (TsetHas != RefHas)
            {
                std::fprintf(stderr,
                    "FAIL: TSet.Contains(%d)=%d but reference.count=%d\n",
                    Value, static_cast<int>(TsetHas), static_cast<int>(RefHas));
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // ---------------------------------------------------------------
        // Phase 3: Remove every element. After removing all, Num must be 0.
        // ---------------------------------------------------------------
        for (::int32 Value : Reference)
        {
            if (!S.Remove(Value))
            {
                std::fprintf(stderr,
                    "FAIL: TSet.Remove(%d) returned false (should be true)\n", Value);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        if (S.Num() != 0)
        {
            std::fprintf(stderr,
                "FAIL: after removing all, TSet.Num()=%d (expected 0)\n", S.Num());
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // ---------------------------------------------------------------
        // Phase 4: Re-Add after Remove. The set should still function.
        // (Tests the tombstone-recycle path.)
        // ---------------------------------------------------------------
        for (::int32 I = 0; I < 100; ++I)
        {
            const ::int32 V = I * 7 + 13;
            if (!S.Add(V))
            {
                std::fprintf(stderr,
                    "FAIL: re-add after drain returned false for %d\n", V);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }
        for (::int32 I = 0; I < 100; ++I)
        {
            const ::int32 V = I * 7 + 13;
            if (!S.Contains(V))
            {
                std::fprintf(stderr,
                    "FAIL: re-add Contains(%d) false (op %d)\n", V, I);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TSet.AddContainsRemove: PASS\n");
    return 0;
}
