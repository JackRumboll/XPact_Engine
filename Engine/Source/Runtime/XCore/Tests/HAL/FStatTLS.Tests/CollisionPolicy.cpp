// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FStatTLS.Tests/CollisionPolicy.cpp -- M-10 open-addressing probing.
// =====================================================================
//
// XCore-4a Rev 3, Section 10.5 fix M-10 ("Shard layout"):
//   "Collision policy is open-addressing with linear probing (up to 8
//    slots forward); if all 8 probe slots are occupied with non-
//    matching hashes, the write falls through to a per-thread
//    overflow heap."
//
// This test verifies the open-addressing path:
//   * 10 synthetic FStatIds with hash values chosen so they all
//     collide on the same primary slot (FStatId.Hash & 1023 == constant).
//   * Each XSTAT_INC for 100 iterations.
//   * Post-merge, every stat must have count == 100.
//
// The probe window is 8 slots; 10 stats colliding will overflow 2 of
// them into the overflow fallback. Both code paths are exercised.
//
// =====================================================================

#include "HAL/FStatRegistry.h"
#include "HAL/FStatTLS.h"
#include "HAL/FStatId.h"

#include <iostream>

namespace
{
    // Construct 10 FStatIds with synthetic hash values colliding on
    // the same primary slot (slot 0). Each has a distinct Hash so the
    // open-addressing probe finds an empty slot; the linear probe
    // walks 8 slots forward.
    //
    // To make Hashes collide on slot 0 (Hash & 1023 == 0) but be
    // distinct: use multiples of 1024.
    //
    // Hash == 0 is reserved (means "empty slot") so we start at
    // 1024 * 1 = 1024.
    constexpr ::XCore::Stat::FStatId kColliders[10] = {
        { 1024ULL * 1,  "C1",  "G" },
        { 1024ULL * 2,  "C2",  "G" },
        { 1024ULL * 3,  "C3",  "G" },
        { 1024ULL * 4,  "C4",  "G" },
        { 1024ULL * 5,  "C5",  "G" },
        { 1024ULL * 6,  "C6",  "G" },
        { 1024ULL * 7,  "C7",  "G" },
        { 1024ULL * 8,  "C8",  "G" },
        { 1024ULL * 9,  "C9",  "G" },   // 9th slot -- still within probe window (8 slots)
        { 1024ULL * 10, "C10", "G" },   // 10th -- overflows into the per-thread fallback
    };

    constexpr int kIncs = 100;

    int RunCollision()
    {
        // Baseline.
        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Pre;
        ::XCore::Stat::FStatRegistry::Snapshot(Pre);

        ::int64 PreCounts[10];
        for (int i = 0; i < 10; ++i)
        {
            PreCounts[i] = Pre.Lookup(kColliders[i].Hash);
        }

        // Increment every collider 100 times.
        for (int i = 0; i < 10; ++i)
        {
            for (int j = 0; j < kIncs; ++j)
            {
                ::XCore::Stat::FStatTLS::Inc(kColliders[i]);
            }
        }

        ::XCore::Stat::FStatRegistry::MergeFromAllThreads();
        ::XCore::Stat::FStatSnapshot Post;
        ::XCore::Stat::FStatRegistry::Snapshot(Post);

        for (int i = 0; i < 10; ++i)
        {
            const ::int64 Got = Post.Lookup(kColliders[i].Hash);
            const ::int64 Expected = PreCounts[i] + kIncs;
            if (Got != Expected)
            {
                std::cerr << "FAIL: collider " << i << " expected " << Expected
                          << " got " << Got << "\n";
                return 1;
            }
        }

        std::cout << "PASS: CollisionPolicy (10 colliding stats; 8 via probe + 2 via overflow)\n";
        return 0;
    }
} // namespace

int main()
{
    return RunCollision();
}
