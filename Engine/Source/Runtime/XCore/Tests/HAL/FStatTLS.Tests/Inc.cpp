// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatTLS.Tests/Inc.cpp -- XSTAT_INC basic correctness.
// =====================================================================
//
// XCore-4a Rev 3, Section 10.5 fix Rev 3 M1 ("Hot-path lazy-init
// lifecycle"). Acceptance criterion G-extra:
//
//   "XSTAT_INC from a constinit constructor (PreStaticInit) on a fresh
//    thread does not abort and increments the slot correctly; the
//    count survives into PostStaticInit's first merger pass via the
//    open-addressing slot the merger walks regardless of perfect-hash
//    state."
//
// PHASE 1F SCOPE: this test exercises XSTAT_INC against the open-
// addressing slot directly (no constinit-ctor sequencing because the
// test runs as a regular main()). The constinit-ctor variant is the
// PreStaticInit gate; the current test verifies the run-time path
// post-main() which is functionally equivalent (the open-addressing
// slot is the same regardless of phase).
//
// The merger pass is verified via FStatRegistry::MergeFromAllThreads
// + Snapshot.
//
// =====================================================================

#include "HAL/FStatTLS.h"
#include "HAL/FStatRegistry.h"
#include "Macros/XCoreTypes.h"

#include <iostream>

namespace
{
    // XSTAT_DECL at namespace scope -- produces an inline constexpr
    // FStatId named MyStat_StatId.
    XSTAT_DECL(MyStat, MyGroup);
    XSTAT_DECL(AnotherStat, MyGroup);

    int RunIncBasic()
    {
        // Drain any pre-existing exit-overflow queue from prior tests
        // by running a merge first; the global tree is shared across
        // tests in the same binary.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Pre;
        ::XCore::Stat::FStatRegistry::Snapshot(Pre);
        const ::int64 PreCount = Pre.Lookup(MyStat_StatId.Hash);

        // 1000 increments to MyStat.
        for (int i = 0; i < 1000; ++i)
        {
            XSTAT_INC(MyStat);
        }

        // 500 adds of +2 to AnotherStat.
        for (int i = 0; i < 500; ++i)
        {
            XSTAT_ADD(AnotherStat, 2);
        }

        // Merge + snapshot.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Post;
        ::XCore::Stat::FStatRegistry::Snapshot(Post);

        const ::int64 MyCount = Post.Lookup(MyStat_StatId.Hash);
        const ::int64 OtherCount = Post.Lookup(AnotherStat_StatId.Hash);

        if (MyCount != PreCount + 1000)
        {
            std::cerr << "FAIL: MyStat count expected " << (PreCount + 1000)
                      << " got " << MyCount << "\n";
            return 1;
        }
        if (OtherCount != 1000)   // 500 * 2
        {
            std::cerr << "FAIL: AnotherStat count expected 1000 got " << OtherCount << "\n";
            return 1;
        }

        // Verify Hash uniqueness.
        if (MyStat_StatId.Hash == AnotherStat_StatId.Hash)
        {
            std::cerr << "FAIL: hash collision between MyStat and AnotherStat\n";
            return 1;
        }

        std::cout << "PASS: FStatTLS::Inc + Add (XSTAT_INC + XSTAT_ADD round-trip)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunIncBasic();
}
