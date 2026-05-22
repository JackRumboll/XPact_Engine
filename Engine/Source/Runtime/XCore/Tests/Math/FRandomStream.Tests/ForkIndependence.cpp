// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FRandomStream.Tests/ForkIndependence.cpp -- Fork produces independent.
// =====================================================================
//
// XCore-4a Rev 3, Section 6.1.6: "the Fork operation uses splitmix64
// over the parent's current state to derive a child seed, so a forked
// stream is independent of subsequent parent draws while remaining
// deterministic."
//
// Properties verified:
//   1. Fork is deterministic: same seed + same call sequence ->
//      same fork outputs.
//   2. Fork is independent of the parent's subsequent draws:
//      the parent's sequence after the fork differs from the
//      child's, and the child's first draw is not equal to
//      the parent's next draw.
//   3. Two Fork calls from the same parent state produce DIFFERENT
//      children (because the first Fork advances the parent).
//
// =====================================================================

#include "Math/FRandomStream.h"

#include <cstdio>

int main()
{
    using ::XCore::Math::FRandomStream;

    // 1. Determinism: identical sequence reproduces.
    {
        FRandomStream A(0x1234567812345678ull);
        FRandomStream B(0x1234567812345678ull);
        FRandomStream AC = A.Fork();
        FRandomStream BC = B.Fork();
        // Parent post-fork must match.
        if (A.NextUInt64() != B.NextUInt64())
        {
            std::fprintf(stderr, "FAIL: parent post-fork divergence\n");
            return 1;
        }
        // Child must match.
        for (int I = 0; I < 100; ++I)
        {
            if (AC.NextUInt64() != BC.NextUInt64())
            {
                std::fprintf(stderr, "FAIL: forked-child divergence at step %d\n", I);
                return 1;
            }
        }
    }

    // 2. Parent + child are independent: their sequences differ.
    {
        FRandomStream P(0xABCDEF0123456789ull);
        FRandomStream C = P.Fork();
        bool SawDifference = false;
        for (int I = 0; I < 100; ++I)
        {
            if (P.NextUInt64() != C.NextUInt64())
            {
                SawDifference = true;
                break;
            }
        }
        if (!SawDifference)
        {
            std::fprintf(stderr, "FAIL: parent and forked child produced identical sequences\n");
            return 1;
        }
    }

    // 3. Two Fork calls produce different children.
    {
        FRandomStream P(0xABCDEF0123456789ull);
        FRandomStream C1 = P.Fork();
        FRandomStream C2 = P.Fork();
        bool SawDifference = false;
        for (int I = 0; I < 100; ++I)
        {
            if (C1.NextUInt64() != C2.NextUInt64())
            {
                SawDifference = true;
                break;
            }
        }
        if (!SawDifference)
        {
            std::fprintf(stderr, "FAIL: consecutive Fork calls produced identical children\n");
            return 1;
        }
    }

    return 0;
}
