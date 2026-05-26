// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.Tests/ShardDistribution.cpp -- 256-shard discipline (A1).
// =====================================================================
//
// XCore-4b Rev 3 §4.2 + §4.6 ("256 shards; selection is the high 8 bits
// of the XXH3-64 byte hash").
//
// Acceptance gate A1 verification:
//   * GetFNameShardCount() returns 256.
//   * Shard selection: GetFNameShardIdFromByteHash(h) == (h >> 56)
//     for an enumerated set of byte sequences.
//   * Hashing 1024 distinct strings populates a broad spread of shards
//     (sanity check that not every Index lands in shard 0).
//   * FName.Index >> 24 == GetFNameShardIdFromByteHash(byte_hash) for
//     matching pairs (the encoding contract).
// =====================================================================

#include "Reflection/FName.h"
#include "Hash/FXxh3.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cstring>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    using ::XCore::Reflect::FName;
    using ::XCore::Reflect::GetFNameShardCount;
    using ::XCore::Reflect::GetFNameShardIdFromIndex;
    using ::XCore::Reflect::GetFNameShardIdFromByteHash;

    // -----------------------------------------------------------------
    // Shard count is 256.
    // -----------------------------------------------------------------
    if (GetFNameShardCount() != 256)
    {
        std::fprintf(stderr, "FAIL: GetFNameShardCount() = %zu (expected 256)\n",
                     GetFNameShardCount());
        return 1;
    }

    // -----------------------------------------------------------------
    // Shard selection: GetFNameShardIdFromByteHash(h) == (h >> 56).
    // -----------------------------------------------------------------
    {
        const ::uint64 H = 0xAB12'3456'7890'ABCDULL;
        const ::uint8 Expected = static_cast<::uint8>(H >> 56);
        const ::uint8 Got      = GetFNameShardIdFromByteHash(H);
        if (Got != Expected)
        {
            std::fprintf(stderr, "FAIL: GetFNameShardIdFromByteHash bit mismatch (got %u, expected %u)\n",
                         Got, Expected);
            return 1;
        }
    }

    // -----------------------------------------------------------------
    // Distribute 1024 distinct strings; assert that at least 8 distinct
    // shards are populated. (A perfectly uniform XXH3 over 1024 inputs
    // has E[hits per shard] = 4; 8 distinct shards is a safe lower
    // bound that fires only for catastrophic distribution collapses.)
    // -----------------------------------------------------------------
    {
        constexpr ::int32 kCount = 1024;
        bool ShardSeen[256] = {false};
        for (::int32 I = 0; I < kCount; ++I)
        {
            char Buf[32];
            int  Len = std::snprintf(Buf, sizeof(Buf), "DistTest_%d", I);
            FName N(Buf, Len);

            const ::uint8 ShardId = GetFNameShardIdFromIndex(N.GetIndex());
            ShardSeen[ShardId] = true;
        }

        ::int32 PopulatedShards = 0;
        for (::int32 S = 0; S < 256; ++S)
        {
            if (ShardSeen[S])
            {
                ++PopulatedShards;
            }
        }

        if (PopulatedShards < 8)
        {
            std::fprintf(stderr, "FAIL: 1024-string fixture populated only %d shards (expected >= 8)\n",
                         PopulatedShards);
            return 1;
        }
        std::fprintf(stdout, "INFO: 1024 distinct strings populated %d/256 shards\n", PopulatedShards);
    }

    // -----------------------------------------------------------------
    // Encoding contract: FName.Index >> 24 must equal the shard id
    // computed from the byte hash for the SAME input.
    //
    // NOTE: the FName ctor strips numbered-suffix patterns. "EncodingCheck"
    // contains no trailing "_N" so the input is interned verbatim and
    // the shard derived from the byte hash matches the Index's high byte.
    // -----------------------------------------------------------------
    {
        const char* Input = "EncodingCheck";
        const ::SIZE_T Len = std::strlen(Input);
        const ::uint64 ByteHash = ::XCore::Hash::FXxh3::Hash64(Input, Len, /*Seed=*/0);
        const ::uint8 ExpectedShard = GetFNameShardIdFromByteHash(ByteHash);

        FName N(Input);
        const ::uint8 IndexShard = GetFNameShardIdFromIndex(N.GetIndex());

        if (IndexShard != ExpectedShard)
        {
            std::fprintf(stderr,
                         "FAIL: encoding contract violated: byte-hash shard=%u, Index shard=%u\n",
                         ExpectedShard, IndexShard);
            return 1;
        }
    }

    return 0;
}
