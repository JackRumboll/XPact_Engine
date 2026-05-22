// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TMap.Tests/AddFindRemove.cpp -- TMap key/value round-trip.
// =====================================================================
//
// XCore-4a Section 5.6 + Section 5.5 row 4 (TMap divergence). Adds N
// (Key, Value) pairs, verifies Find returns the put Value, exercises
// the overwrite path, then drains via Remove.
//
// We use a deterministic xorshift PRNG for the key set so the test is
// reproducible across runs / platforms.
//
// =====================================================================

#include "Containers/TMap.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <unordered_map>

namespace
{
    constexpr ::int32 kNumOps = 5000;

    ::uint64 g_RngState = 0xC2B2AE3D27D4EB4FULL;
    ::int32 NextI32() noexcept
    {
        g_RngState ^= g_RngState << 13;
        g_RngState ^= g_RngState >> 7;
        g_RngState ^= g_RngState << 17;
        return static_cast<::int32>(g_RngState & 0x7FFFFFFFu);
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    {
        ::XCore::TMap<::int32, ::int32> M;
        std::unordered_map<::int32, ::int32> Ref;

        // ---------------------------------------------------------------
        // Phase 1: insert kNumOps pairs. Track in std::unordered_map.
        // ---------------------------------------------------------------
        for (::int32 I = 0; I < kNumOps; ++I)
        {
            const ::int32 K = NextI32();
            const ::int32 V = NextI32();
            M.Add(K, V);
            Ref[K] = V;
        }

        // ---------------------------------------------------------------
        // Phase 2: Find/Contains agree with the reference for every key.
        // ---------------------------------------------------------------
        for (const auto& Kv : Ref)
        {
            if (!M.Contains(Kv.first))
            {
                std::fprintf(stderr, "FAIL: TMap.Contains(%d) returned false\n", Kv.first);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }

            const ::int32* Got = M.Find(Kv.first);
            if (Got == nullptr || *Got != Kv.second)
            {
                std::fprintf(stderr,
                    "FAIL: TMap.Find(%d) = %s (expected %d)\n",
                    Kv.first,
                    Got == nullptr ? "nullptr" : "<value mismatch>",
                    Kv.second);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        // Set sizes must match.
        if (static_cast<::SIZE_T>(M.Num()) != Ref.size())
        {
            std::fprintf(stderr,
                "FAIL: TMap.Num() = %d, std::unordered_map size = %zu\n",
                M.Num(), Ref.size());
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }

        // ---------------------------------------------------------------
        // Phase 3: overwrite. Insert (K, V') where K already exists.
        // ---------------------------------------------------------------
        {
            ::int32 PickedKey = -1;
            for (const auto& Kv : Ref) { PickedKey = Kv.first; break; }
            const ::int32 NewValue = 0xDEADBEEF;
            M.Add(PickedKey, NewValue);
            const ::int32* AfterOverwrite = M.Find(PickedKey);
            if (AfterOverwrite == nullptr || *AfterOverwrite != NewValue)
            {
                std::fprintf(stderr,
                    "FAIL: overwrite of key %d: Find returned %s\n",
                    PickedKey,
                    AfterOverwrite == nullptr ? "nullptr" : "<value mismatch>");
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
            // Restore for the drain below.
            M.Add(PickedKey, Ref[PickedKey]);
        }

        // ---------------------------------------------------------------
        // Phase 4: operator[] inserts default value when absent.
        // ---------------------------------------------------------------
        {
            const ::int32 K = 0x7FFFFFF0;  // unlikely to collide with RNG
            // Make sure K is absent.
            M.Remove(K);
            const ::int32 GotDefault = M[K];
            if (GotDefault != 0)
            {
                std::fprintf(stderr,
                    "FAIL: operator[]({fresh}) returned %d (expected 0=default)\n",
                    GotDefault);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
            // Now write via operator[].
            M[K] = 99;
            if (M[K] != 99)
            {
                std::fprintf(stderr, "FAIL: operator[] write/read inconsistent\n");
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
            M.Remove(K);
        }

        // ---------------------------------------------------------------
        // Phase 5: Drain via Remove. Num must return to 0.
        // ---------------------------------------------------------------
        for (const auto& Kv : Ref)
        {
            if (!M.Remove(Kv.first))
            {
                std::fprintf(stderr,
                    "FAIL: TMap.Remove(%d) returned false (should be true)\n",
                    Kv.first);
                ::XCore::HAL::FMemory::__Shutdown();
                return 1;
            }
        }

        if (M.Num() != 0)
        {
            std::fprintf(stderr,
                "FAIL: post-drain TMap.Num()=%d (expected 0)\n", M.Num());
            ::XCore::HAL::FMemory::__Shutdown();
            return 1;
        }
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TMap.AddFindRemove: PASS\n");
    return 0;
}
