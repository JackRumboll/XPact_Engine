// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FPlatformMisc.Tests/EntropyNIST.cpp -- Phase 1g D-extra-3:
// NIST SP 800-90B entropy-quality smoke test.
// =====================================================================
//
// XCore-4a Rev 3 Section 7.1 + Section 14 Step 4: the per-platform
// FPlatformMisc::GetEntropy must produce high-entropy bytes -- i.e.,
// the source must NOT be a fixed-pattern RNG or a low-entropy
// platform fallback.
//
// We approximate NIST SP 800-90B Section 6.3.1 "most common value
// estimate" entropy estimator over a 1 MiB sample:
//
//   1. Draw 1 MiB of GetEntropy output.
//   2. Tally the per-byte frequency table (256 buckets).
//   3. Compute the most-common-value's probability p_max.
//   4. Estimate min-entropy per byte: -log2(p_max).
//   5. Assert >= 7.5 bits/byte (CSRNG output should be ~7.99
//      bits/byte; the threshold is well below ideal but well above
//      any low-entropy source).
//
// The test catches catastrophic regressions (e.g., a stubbed
// GetEntropy returning zeros, a fallback to rand(), or a buggy
// per-platform path returning a fixed seed) without being so tight
// that statistical noise on a 1 MiB sample causes false failures.
//
// =====================================================================

#include "HAL/FPlatformMisc.h"
#include "Macros/XCoreTypes.h"

#include <array>
#include <cmath>
#include <cstdio>
#include <vector>

namespace
{
    constexpr ::SIZE_T kSampleBytes = 1024 * 1024;  // 1 MiB
    constexpr double   kMinEntropyThreshold = 7.5;  // bits/byte

    int RunEntropyNIST()
    {
        std::vector<unsigned char> Sample(kSampleBytes);
        ::XCore::HAL::FPlatformMisc::GetEntropy(Sample.data(), Sample.size());

        // Tally per-byte frequencies.
        std::array<::SIZE_T, 256> Freq{};
        Freq.fill(0);
        for (::SIZE_T I = 0; I < kSampleBytes; ++I)
        {
            ++Freq[Sample[I]];
        }

        // Most-common-value estimator.
        ::SIZE_T MaxCount = 0;
        for (::SIZE_T I = 0; I < 256; ++I)
        {
            if (Freq[I] > MaxCount)
            {
                MaxCount = Freq[I];
            }
        }

        const double PMax = static_cast<double>(MaxCount) /
                            static_cast<double>(kSampleBytes);

        // Min-entropy in bits per byte = -log2(p_max).
        const double MinEntropy = -std::log2(PMax);

        std::printf("INFO: 1 MiB GetEntropy sample: max byte frequency = %zu / %zu "
                    "(p_max = %.6f); estimated min-entropy = %.4f bits/byte.\n",
                    MaxCount, kSampleBytes, PMax, MinEntropy);

        if (MinEntropy < kMinEntropyThreshold)
        {
            std::fprintf(stderr,
                "FAIL: GetEntropy min-entropy estimate %.4f bits/byte is below "
                "the %.2f threshold. The CSRNG source may be stubbed or "
                "regressed; investigate FPlatformMisc::GetEntropy.\n",
                MinEntropy, kMinEntropyThreshold);
            return 1;
        }

        std::printf("PASS: GetEntropy min-entropy >= %.2f bits/byte over %zu samples\n",
                    kMinEntropyThreshold, kSampleBytes);
        return 0;
    }
}

int main()
{
    return RunEntropyNIST();
}
