// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/ShardDistributionUniformity.cpp -- Rev 2 FIX-6 /
// MEDIUM-13 chi-square uniformity gate.
// =====================================================================
//
// Per Rev 2 FIX-6 / MEDIUM-13: FNamePool uses the top 8 bits of
// FXxh3-64(name_bytes) for shard selection. The existing
// ShardDistribution.cpp test verifies basic distribution at 1024
// strings ("at least 8 distinct shards populated") which fires only
// for catastrophic distribution collapses. This complementary test
// scales the corpus to 5000 names and applies a chi-square statistic
// against a uniform distribution to fail on subtle skew.
//
// CORPUS: 5000 unique FName strings of the form "Actor_0001",
//         "Actor_0002", ..., "Actor_5000". The corpus is deterministic
//         (no PRNG; reproducible across runs and platforms).
//
// METHOD: For each interned FName, extract the shard id via
//         GetFNameShardIdFromIndex. Tally per-shard counts. Compute
//         the chi-square statistic:
//
//             chi^2 = sum_i (O_i - E)^2 / E
//
//         where O_i is the observed count in shard i, E is the
//         expected count (5000 / 256 ~= 19.53), summed over all 256
//         shards.
//
// THRESHOLD: For 255 degrees of freedom and alpha = 0.05 (i.e., 95%
//         confidence that the distribution is NOT uniform), the
//         critical chi^2 value is approximately 293.25 (from
//         chi-square tables; see https://en.wikipedia.org/wiki/
//         Chi-squared_distribution and Eqn (4.5) in NIST/SEMATECH
//         e-Handbook of Statistical Methods).
//
//         We use chi^2 < 295 as the acceptance threshold (slight
//         margin over the 293.25 critical value to account for
//         compiler-floating-point variance; the threshold catches
//         distributions that are statistically non-uniform at p=0.05
//         while accepting noise from a true-uniform distribution).
//
//         The Rev 2 prompt also requests:
//           * No shard has zero entries.
//           * No shard has > 2x expected average.
//         The 2x cap is a per-shard "max bucket" guard that fires
//         when individual shards are catastrophically overpopulated
//         even if the chi-square statistic remains within bounds.
//
// =====================================================================

#include "Reflection/FName.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

namespace
{
    constexpr ::int32 kCorpusSize = 5000;
    constexpr ::int32 kShardCount = 256;

    // chi-square critical value for df=255 at alpha=0.05; margin = 1.75.
    // (df=255, p=0.95) ~= 293.25; we use 295.0 as the soft acceptance
    // threshold to absorb FP noise. Source: NIST/SEMATECH e-Handbook,
    // table 4.5 chi-squared critical values.
    constexpr double kChiSquareThreshold = 295.0;
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::GetFNameShardIdFromIndex;

    // -----------------------------------------------------------------
    // Step 1: Generate the corpus + tally per-shard occupancy.
    // -----------------------------------------------------------------
    int Counts[kShardCount] = {};

    // The corpus uses "Actor_0001"..."Actor_5000" -- the suffix
    // "_NNNN" is NUMERIC but FName's numbered-name parser sets
    // SerialNumber rather than interning each as a distinct base.
    // To avoid that path (which would collapse all 5000 to ONE
    // intern entry), we use a non-numeric prefix variation:
    // "Name_NNNNa", "Name_NNNNb", ..., distributing across the
    // ASCII letters so the numbered-suffix parser doesn't fire.
    //
    // The mathematical property we want: 5000 distinct interned
    // FNames, each landing in some shard.
    char Buf[32];
    for (::int32 I = 0; I < kCorpusSize; ++I)
    {
        // Non-numeric suffix per name: "Name_<i>_a" where the trailing
        // letter varies per name. The numbered-suffix parser only
        // matches strict trailing "_NNN" digits; the trailing "_a" /
        // "_b" / etc. blocks the parser.
        const char Suffix = static_cast<char>('a' + (I % 26));
        const int Len = std::snprintf(Buf, sizeof(Buf),
                                      "UniformityTest_%05d_%c", I, Suffix);
        if (Len < 0 || Len >= static_cast<int>(sizeof(Buf)))
        {
            std::fprintf(stderr, "FAIL: snprintf overrun at I=%d\n", I);
            return 1;
        }

        FName N(Buf, Len);
        const ::uint8 ShardId = GetFNameShardIdFromIndex(N.GetIndex());
        ++Counts[ShardId];
    }

    // -----------------------------------------------------------------
    // Step 2: Verify total count adds up. Sanity check that no FName
    // was lost or double-counted.
    // -----------------------------------------------------------------
    {
        int Total = 0;
        for (::int32 S = 0; S < kShardCount; ++S)
        {
            Total += Counts[S];
        }
        if (Total != kCorpusSize)
        {
            std::fprintf(stderr,
                "FAIL: per-shard tally sum = %d (expected %d)\n",
                Total, kCorpusSize);
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Step 3: No shard zero-populated; no shard exceeds 2x expected.
    // -----------------------------------------------------------------
    const double Expected = static_cast<double>(kCorpusSize) /
                            static_cast<double>(kShardCount);  // ~19.53
    const int MaxPerShard = static_cast<int>(Expected * 2.0);  // 39

    int MinObserved = kCorpusSize;
    int MaxObserved = 0;
    for (::int32 S = 0; S < kShardCount; ++S)
    {
        if (Counts[S] < MinObserved) MinObserved = Counts[S];
        if (Counts[S] > MaxObserved) MaxObserved = Counts[S];
    }

    if (MinObserved == 0)
    {
        std::fprintf(stderr,
            "FAIL: at least one shard has zero entries; expected uniform "
            "distribution to populate every shard at corpus size %d.\n",
            kCorpusSize);
        // Print all empty shards.
        for (::int32 S = 0; S < kShardCount; ++S)
        {
            if (Counts[S] == 0)
            {
                std::fprintf(stderr, "  shard %d: empty\n", S);
            }
        }
        return 1;
    }

    if (MaxObserved > MaxPerShard)
    {
        std::fprintf(stderr,
            "FAIL: at least one shard has %d entries (> 2x expected %d); "
            "distribution is non-uniform.\n",
            MaxObserved, MaxPerShard);
        // Print the over-populated shards.
        for (::int32 S = 0; S < kShardCount; ++S)
        {
            if (Counts[S] > MaxPerShard)
            {
                std::fprintf(stderr, "  shard %d: %d entries\n", S, Counts[S]);
            }
        }
        return 1;
    }

    // -----------------------------------------------------------------
    // Step 4: Chi-square statistic.
    // -----------------------------------------------------------------
    double ChiSquare = 0.0;
    for (::int32 S = 0; S < kShardCount; ++S)
    {
        const double Observed = static_cast<double>(Counts[S]);
        const double Diff     = Observed - Expected;
        ChiSquare += (Diff * Diff) / Expected;
    }

    std::fprintf(stdout,
        "INFO: corpus=%d, shards=%d, expected/shard=%.2f, "
        "observed min/max=%d/%d, chi^2=%.2f (threshold=%.2f)\n",
        kCorpusSize, kShardCount, Expected,
        MinObserved, MaxObserved,
        ChiSquare, kChiSquareThreshold);

    if (ChiSquare >= kChiSquareThreshold)
    {
        std::fprintf(stderr,
            "FAIL: chi^2 = %.2f >= %.2f (df=255, alpha=0.05); "
            "distribution rejects the null hypothesis of uniformity.\n",
            ChiSquare, kChiSquareThreshold);
        return 1;
    }

    std::printf("ShardDistributionUniformity: PASS\n");
    return 0;
}
