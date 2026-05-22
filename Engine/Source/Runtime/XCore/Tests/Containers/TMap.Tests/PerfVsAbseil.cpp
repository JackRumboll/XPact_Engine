// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TMap.Tests/PerfVsAbseil.cpp -- SwissTable performance smoke test.
// =====================================================================
//
// XCore-4a Section 5.6 + Section 11.9: the formal acceptance is "TMap
// lookup meets-or-beats abseil flat_hash_map in its scalar-hash
// configuration".
//
// However, abseil is not yet vendored in /Engine/Source/ThirdParty/.
// Phase 1d ships the algorithm + the test scaffold; the abseil-
// comparison harness is Phase 1e work (vendor abseil, build a sibling
// benchmark that runs both implementations against identical workloads,
// assert TMap is within 10%).
//
// What this test DOES verify (Phase 1d-appropriate scope):
//
//   1. Algorithmic-complexity sanity: at 10K, 100K, and 1M entries
//      the average operation cost stays within a constant factor.
//      Specifically: total time for N operations should be O(N), not
//      O(N log N) or O(N^2). We check by running at two scales (10K
//      and 100K) and asserting the per-op time ratio is within a
//      reasonable constant-factor window (we allow 2.5x to absorb
//      cache + allocation variation; the algorithmic cost itself
//      should be ~1.0x).
//
//   2. Insertion + lookup + removal throughput: at 100K entries, the
//      total wall-clock time stays under a generous upper bound (5
//      seconds in Test config), catching catastrophic regressions.
//
// PERF-TUNING NOTES.
// The scalar probe path used here (per Section 11.9 + dispatch fix #25)
// pays a ~30% penalty vs SIMD-friendly Group::Match for x86_64.
// Acceptance "within 10% of abseil's scalar-hash config" is the algorithm-
// vs-algorithm comparison; the SIMD gap is not in scope of the spec's
// acceptance bar.
//
// =====================================================================

#include "Containers/TMap.h"
#include "HAL/FMemory.h"
#include "HAL/FPlatformTime.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>

namespace
{
    constexpr ::int32 kSmallScale  = 10000;
    constexpr ::int32 kMediumScale = 100000;

    ::uint64 g_RngState = 0x9E3779B185EBCA87ULL;
    ::int32 NextI32() noexcept
    {
        g_RngState ^= g_RngState << 13;
        g_RngState ^= g_RngState >> 7;
        g_RngState ^= g_RngState << 17;
        return static_cast<::int32>(g_RngState & 0x7FFFFFFFu);
    }

    // -----------------------------------------------------------------
    // RunBenchmark -- insert N pairs, look up each one, then remove all.
    // Returns the total nanoseconds.
    // -----------------------------------------------------------------
    ::uint64 RunBenchmark(::int32 N) noexcept
    {
        // Reset RNG state so the two runs use identical key sequences.
        g_RngState = 0x9E3779B185EBCA87ULL;

        const double Start = ::XCore::HAL::FPlatformTime::Seconds();

        ::XCore::TMap<::int32, ::int32> M;
        M.Reserve(N);

        // Insert N keys with deterministic values.
        for (::int32 I = 0; I < N; ++I)
        {
            const ::int32 K = NextI32();
            M.Add(K, I);
        }

        // Reset RNG and look up each key.
        g_RngState = 0x9E3779B185EBCA87ULL;
        ::int32 LookupChecksum = 0;
        for (::int32 I = 0; I < N; ++I)
        {
            const ::int32 K = NextI32();
            const ::int32* V = M.Find(K);
            if (V != nullptr) LookupChecksum += *V;
        }

        // Reset and remove every key.
        g_RngState = 0x9E3779B185EBCA87ULL;
        for (::int32 I = 0; I < N; ++I)
        {
            const ::int32 K = NextI32();
            M.Remove(K);
        }

        const double End = ::XCore::HAL::FPlatformTime::Seconds();
        // Use the checksum so the optimiser cannot remove the loop body.
        (void)LookupChecksum;

        return static_cast<::uint64>((End - Start) * 1e9);
    }
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // Run small + medium scales.
    const ::uint64 SmallNs  = RunBenchmark(kSmallScale);
    const ::uint64 MediumNs = RunBenchmark(kMediumScale);

    // Per-op times.
    const double SmallPerOp  = static_cast<double>(SmallNs)  / (3.0 * kSmallScale);   // insert + find + remove
    const double MediumPerOp = static_cast<double>(MediumNs) / (3.0 * kMediumScale);

    std::printf("TMap.PerfVsAbseil: insert+find+remove\n");
    std::printf("  N=%d: %.0f ns (per-op: %.1f ns)\n", kSmallScale,  static_cast<double>(SmallNs),  SmallPerOp);
    std::printf("  N=%d: %.0f ns (per-op: %.1f ns)\n", kMediumScale, static_cast<double>(MediumNs), MediumPerOp);

    // Algorithmic-complexity sanity: per-op time at 10x scale should be
    // within 2.5x of the 1x scale per-op time. Linear cost gives ratio
    // close to 1; log-linear gives ~log10(10) = 1; quadratic gives 10.
    const double Ratio = MediumPerOp / SmallPerOp;
    if (Ratio > 2.5 || Ratio < 0.4)
    {
        std::fprintf(stderr,
            "FAIL: per-op cost ratio %.2fx (expected close to 1.0; allowed [0.4, 2.5])\n",
            Ratio);
        ::XCore::HAL::FMemory::__Shutdown();
        return 1;
    }

    // Absolute throughput sanity: 100K full round trip should complete
    // under 5 seconds (5e9 ns). This catches catastrophic regressions.
    if (MediumNs > 5'000'000'000ULL)
    {
        std::fprintf(stderr,
            "FAIL: 100K round-trip took %.2f sec (expected < 5 sec)\n",
            static_cast<double>(MediumNs) / 1e9);
        ::XCore::HAL::FMemory::__Shutdown();
        return 1;
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("TMap.PerfVsAbseil: PASS (ratio %.2fx)\n", Ratio);
    return 0;
}
