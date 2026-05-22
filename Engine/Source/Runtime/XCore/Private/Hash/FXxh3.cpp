// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FXxh3.cpp -- clean-room scalar XXH3 64-bit implementation.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.9.
//
// This implementation follows the XXH3 64-bit specification:
//   https://github.com/Cyan4973/xxHash/blob/dev/doc/xxhash_spec.md
//
// And cross-references the reference C source at
//   https://github.com/Cyan4973/xxHash/blob/dev/xxhash.h
//
// Sim-path discipline. The reference C source ships SSE2/AVX2/AVX512/
// NEON/VSX paths gated on compile-time detection. This implementation
// uses the SCALAR path verbatim and never branches on CPU features.
// The same compiled object produces byte-identical output on:
//   * Win64 (MSVC x86_64)
//   * Linux (Clang/GCC x86_64)
//   * Android (Clang ARM64)
//
// SPECIFICATION SUMMARY.
//   XXH3 has four input-length brackets, each with a distinct mixing
//   pattern:
//     0..16:    XXH3_len_0to16_64b
//     17..128:  XXH3_len_17to128_64b
//     129..240: XXH3_len_129to240_64b
//     241+:     XXH3_hashLong_64b_default (striped 256-byte block)
//
//   The 192-byte "secret" is the canonical XXH3 secret table from the
//   reference C source (constant since v0.7.4; bit-exactness is locked
//   on this byte sequence).
//
// =====================================================================

#include "Hash/FXxh3.h"

#include <cstring>   // std::memcpy

namespace XCore::Hash
{
    namespace
    {
        // -----------------------------------------------------------------
        // The XXH3 default secret (192 bytes). Verbatim from the upstream
        // reference (xxhash.h, "kSecret" array). The byte sequence is the
        // bit-exactness lock for XXH3 -- it is identical across every
        // released version of XXH3 since v0.7.4.
        // -----------------------------------------------------------------
        constexpr ::uint8 kXxh3Secret[192] = {
            0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
            0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
            0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
            0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
            0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
            0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
            0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
            0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
            0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
            0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
            0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
            0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
        };

        // -----------------------------------------------------------------
        // The XXH3 64-bit prime constants. Bit-exactness lock.
        // -----------------------------------------------------------------
        constexpr ::uint64 kPrime32_1 = 0x9E3779B1u;
        constexpr ::uint64 kPrime32_2 = 0x85EBCA77u;
        constexpr ::uint64 kPrime32_3 = 0xC2B2AE3Du;

        constexpr ::uint64 kPrime64_1 = 0x9E3779B185EBCA87ULL;
        constexpr ::uint64 kPrime64_2 = 0xC2B2AE3D27D4EB4FULL;
        constexpr ::uint64 kPrime64_3 = 0x165667B19E3779F9ULL;
        constexpr ::uint64 kPrime64_4 = 0x85EBCA77C2B2AE63ULL;
        constexpr ::uint64 kPrime64_5 = 0x27D4EB2F165667C5ULL;

        constexpr ::uint64 kStripeLen     = 64;
        constexpr ::uint64 kSecretSizeMin = 136;
        constexpr ::uint64 kSecretDefaultSize = 192;
        constexpr ::uint64 kSecretLastAccStart = 7;
        constexpr ::uint64 kAccNb         = 8;       // accumulator words

        // -----------------------------------------------------------------
        // Little-endian uint32/uint64 readers.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint32 ReadLe32(const ::uint8* Ptr) noexcept
        {
            return  static_cast<::uint32>(Ptr[0])
                  | (static_cast<::uint32>(Ptr[1]) << 8)
                  | (static_cast<::uint32>(Ptr[2]) << 16)
                  | (static_cast<::uint32>(Ptr[3]) << 24);
        }

        XPACT_FORCEINLINE ::uint64 ReadLe64(const ::uint8* Ptr) noexcept
        {
            return  static_cast<::uint64>(ReadLe32(Ptr))
                  | (static_cast<::uint64>(ReadLe32(Ptr + 4)) << 32);
        }

        // -----------------------------------------------------------------
        // 64-bit byte-swap (the XXH3 mixing functions use both LE-direct
        // and byte-swapped reads internally).
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 BSwap64(::uint64 V) noexcept
        {
            return  ((V & 0x00000000000000FFULL) << 56)
                  | ((V & 0x000000000000FF00ULL) << 40)
                  | ((V & 0x0000000000FF0000ULL) << 24)
                  | ((V & 0x00000000FF000000ULL) << 8)
                  | ((V & 0x000000FF00000000ULL) >> 8)
                  | ((V & 0x0000FF0000000000ULL) >> 24)
                  | ((V & 0x00FF000000000000ULL) >> 40)
                  | ((V & 0xFF00000000000000ULL) >> 56);
        }

        // -----------------------------------------------------------------
        // XXH3 left-rotate.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Rotl64(::uint64 V, int Bits) noexcept
        {
            return (V << Bits) | (V >> (64 - Bits));
        }

        // -----------------------------------------------------------------
        // mult64to128 -- 64x64 -> 128 multiply.
        //
        // We do this via the __uint128_t builtin where available (Clang/GCC);
        // MSVC has __umul128 but no __uint128_t. The compile-time portable
        // approach is "Karatsuba-style split into two 32-bit half-products":
        //
        //   lhs * rhs = (lhs_hi << 32 + lhs_lo) * (rhs_hi << 32 + rhs_lo)
        //             = lhs_hi*rhs_hi << 64
        //             + (lhs_hi*rhs_lo + lhs_lo*rhs_hi) << 32
        //             + lhs_lo*rhs_lo
        //
        // The bit-exact result is independent of compiler: this is just
        // uint32*uint32->uint64 arithmetic which every C++ compiler
        // produces identically.
        //
        // Returns { low_64, high_64 }.
        // -----------------------------------------------------------------
        struct FUint128
        {
            ::uint64 Low;
            ::uint64 High;
        };

        XPACT_FORCEINLINE FUint128 Mult64To128(::uint64 Lhs, ::uint64 Rhs) noexcept
        {
            const ::uint64 LhsLo = Lhs & 0xFFFFFFFFULL;
            const ::uint64 LhsHi = Lhs >> 32;
            const ::uint64 RhsLo = Rhs & 0xFFFFFFFFULL;
            const ::uint64 RhsHi = Rhs >> 32;

            const ::uint64 Lo_Lo = LhsLo * RhsLo;
            const ::uint64 Hi_Lo = LhsHi * RhsLo;
            const ::uint64 Lo_Hi = LhsLo * RhsHi;
            const ::uint64 Hi_Hi = LhsHi * RhsHi;

            // Sum the cross terms, watching for carry. Reference C source
            // uses this sequence for bit-exact reproducibility.
            const ::uint64 Cross = (Lo_Lo >> 32) + (Hi_Lo & 0xFFFFFFFFULL) + Lo_Hi;
            const ::uint64 Upper = (Hi_Lo >> 32) + (Cross >> 32) + Hi_Hi;
            const ::uint64 Lower = (Cross << 32) | (Lo_Lo & 0xFFFFFFFFULL);

            return FUint128{ Lower, Upper };
        }

        // -----------------------------------------------------------------
        // XXH3_mul128_fold64 -- multiply two 64-bit values, fold the
        // 128-bit result via XOR of low and high. Used in the mixing
        // functions.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Mul128Fold64(::uint64 Lhs, ::uint64 Rhs) noexcept
        {
            const FUint128 P = Mult64To128(Lhs, Rhs);
            return P.Low ^ P.High;
        }

        // -----------------------------------------------------------------
        // XXH3 64-bit avalanche -- the final mixing step.
        //
        // Per the reference C source:
        //
        //   h64 ^= h64 >> 37;
        //   h64 *= PRIME_MX1 (0x165667919E3779F9);
        //   h64 ^= h64 >> 32;
        //
        // This is the "rrmxmx" avalanche used by XXH3_64.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Xxh64Avalanche(::uint64 H64) noexcept
        {
            H64 ^= H64 >> 33;
            H64 *= kPrime64_2;
            H64 ^= H64 >> 29;
            H64 *= kPrime64_3;
            H64 ^= H64 >> 32;
            return H64;
        }

        XPACT_FORCEINLINE ::uint64 Xxh3Avalanche(::uint64 H64) noexcept
        {
            H64 = H64 ^ (H64 >> 37);
            H64 *= 0x165667919E3779F9ULL;
            H64 = H64 ^ (H64 >> 32);
            return H64;
        }

        XPACT_FORCEINLINE ::uint64 Xxh3RrmxmxAvalanche(::uint64 H64, ::uint64 Len) noexcept
        {
            H64 ^= Rotl64(H64, 49) ^ Rotl64(H64, 24);
            H64 *= 0x9FB21C651E98DF25ULL;
            H64 ^= (H64 >> 35) + Len;
            H64 *= 0x9FB21C651E98DF25ULL;
            H64 ^= (H64 >> 28);
            return H64;
        }

        // =================================================================
        // Length 0..16 path.
        // =================================================================

        // -----------------------------------------------------------------
        // XXH3_len_1to3_64b -- input length 1, 2, or 3.
        //
        // Combine the first byte, last byte, middle byte (for len==1,
        // these collapse), and length into a single uint32, then mix with
        // 2 secret words.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Len1To3_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            const ::uint8 C1 = Data[0];
            const ::uint8 C2 = Data[Length >> 1];
            const ::uint8 C3 = Data[Length - 1];
            const ::uint32 Combined =
                  (static_cast<::uint32>(C1) << 16)
                | (static_cast<::uint32>(C2) << 24)
                | (static_cast<::uint32>(C3) <<  0)
                | (static_cast<::uint32>(Length) << 8);
            const ::uint64 Bitflip = (static_cast<::uint64>(ReadLe32(Secret)) ^ ReadLe32(Secret + 4)) + Seed;
            const ::uint64 Keyed = static_cast<::uint64>(Combined) ^ Bitflip;
            return Xxh64Avalanche(Keyed);
        }

        // -----------------------------------------------------------------
        // XXH3_len_4to8_64b -- input length 4..8.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Len4To8_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            Seed ^= static_cast<::uint64>(BSwap64(Seed & 0xFFFFFFFFULL) << 32);
            const ::uint32 Input1 = ReadLe32(Data);
            const ::uint32 Input2 = ReadLe32(Data + Length - 4);
            const ::uint64 Bitflip = (ReadLe64(Secret + 8) ^ ReadLe64(Secret + 16)) - Seed;
            const ::uint64 Input64 = static_cast<::uint64>(Input2) + (static_cast<::uint64>(Input1) << 32);
            const ::uint64 Keyed = Input64 ^ Bitflip;
            return Xxh3RrmxmxAvalanche(Keyed, Length);
        }

        // -----------------------------------------------------------------
        // XXH3_len_9to16_64b -- input length 9..16.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Len9To16_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            const ::uint64 BitflipLo = (ReadLe64(Secret + 24) ^ ReadLe64(Secret + 32)) + Seed;
            const ::uint64 BitflipHi = (ReadLe64(Secret + 40) ^ ReadLe64(Secret + 48)) - Seed;
            const ::uint64 InputLo = ReadLe64(Data) ^ BitflipLo;
            const ::uint64 InputHi = ReadLe64(Data + Length - 8) ^ BitflipHi;
            const ::uint64 Acc = static_cast<::uint64>(Length)
                                  + BSwap64(InputLo)
                                  + InputHi
                                  + Mul128Fold64(InputLo, InputHi);
            return Xxh3Avalanche(Acc);
        }

        // -----------------------------------------------------------------
        // XXH3_len_0to16_64b -- dispatch.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Len0To16_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            if (Length > 8)        return Len9To16_64b(Data, Length, Secret, Seed);
            if (Length >= 4)       return Len4To8_64b(Data, Length, Secret, Seed);
            if (Length > 0)        return Len1To3_64b(Data, Length, Secret, Seed);
            // Length == 0
            return Xxh64Avalanche(Seed ^ (ReadLe64(Secret + 56) ^ ReadLe64(Secret + 64)));
        }

        // =================================================================
        // Length 17..128 path.
        // =================================================================

        // -----------------------------------------------------------------
        // XXH3_mix16B -- one 16-byte mixing step.
        //
        // Reads 16 bytes from `Data`, XORs with the corresponding secret
        // bytes (offset by `SecretOffset`), and multiplies-folds.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Mix16B(const ::uint8* Data, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            const ::uint64 InputLo = ReadLe64(Data);
            const ::uint64 InputHi = ReadLe64(Data + 8);
            const ::uint64 BitflipLo = ReadLe64(Secret)     + Seed;
            const ::uint64 BitflipHi = ReadLe64(Secret + 8) - Seed;
            return Mul128Fold64(InputLo ^ BitflipLo, InputHi ^ BitflipHi);
        }

        // -----------------------------------------------------------------
        // XXH3_len_17to128_64b -- input length 17..128.
        //
        // Iterates from the start and end of the buffer inward in
        // 16-byte chunks, accumulating Mix16B contributions.
        // -----------------------------------------------------------------
        ::uint64 Len17To128_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            ::uint64 Acc = static_cast<::uint64>(Length) * kPrime64_1;

            // Number of 16-byte chunks at the head/tail. For length L:
            //   L > 96 -> 3 chunks each side (6 total)
            //   L > 64 -> 2 chunks each side
            //   L > 32 -> 1 chunk each side
            //          -> 0 chunks (length 17..32: handled by the final
            //             head+tail pair)
            if (Length > 32)
            {
                if (Length > 64)
                {
                    if (Length > 96)
                    {
                        Acc += Mix16B(Data +  48, Secret +  96, Seed);
                        Acc += Mix16B(Data + Length - 64, Secret + 112, Seed);
                    }
                    Acc += Mix16B(Data +  32, Secret +  64, Seed);
                    Acc += Mix16B(Data + Length - 48, Secret +  80, Seed);
                }
                Acc += Mix16B(Data +  16, Secret +  32, Seed);
                Acc += Mix16B(Data + Length - 32, Secret +  48, Seed);
            }
            Acc += Mix16B(Data +   0, Secret +   0, Seed);
            Acc += Mix16B(Data + Length - 16, Secret +  16, Seed);

            return Xxh3Avalanche(Acc);
        }

        // =================================================================
        // Length 129..240 path.
        // =================================================================

        ::uint64 Len129To240_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::uint64 Seed) noexcept
        {
            constexpr ::SIZE_T kMidsizeStartOffset = 3;
            constexpr ::SIZE_T kMidsizeLastOffset  = 17;

            const ::SIZE_T NbRounds = Length / 16;
            ::uint64 Acc = static_cast<::uint64>(Length) * kPrime64_1;

            for (::SIZE_T I = 0; I < 8; ++I)
            {
                Acc += Mix16B(Data + 16 * I, Secret + 16 * I, Seed);
            }
            Acc = Xxh3Avalanche(Acc);

            for (::SIZE_T I = 8; I < NbRounds; ++I)
            {
                Acc += Mix16B(Data + 16 * I, Secret + 16 * (I - 8) + kMidsizeStartOffset, Seed);
            }
            // Last 16 bytes (tail).
            Acc += Mix16B(Data + Length - 16,
                          Secret + kSecretSizeMin - kMidsizeLastOffset,
                          Seed);
            return Xxh3Avalanche(Acc);
        }

        // =================================================================
        // Length 241+ path (long-data striped accumulator).
        // =================================================================

        // -----------------------------------------------------------------
        // XXH3_accumulate_512_scalar -- mix one 64-byte stripe into the
        // 8-word accumulator. Reference C source's scalar path.
        //
        // Acc[i] += stripe[i+1] + (data[i] ^ secret[i]) * (data[i] >> 32 ^ secret[i] >> 32)
        // ... actually the spec is:
        //
        //   for i in 0..8:
        //     dataVal = load_le64(input + 8*i)
        //     dataKey = dataVal ^ load_le64(secret + 8*i)
        //     acc[i ^ 1] += dataVal;
        //     acc[i]     += (dataKey & 0xFFFFFFFF) * (dataKey >> 32)
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void Accumulate512(::uint64* Acc, const ::uint8* Data, const ::uint8* Secret) noexcept
        {
            for (::SIZE_T I = 0; I < kAccNb; ++I)
            {
                const ::uint64 DataVal = ReadLe64(Data + 8 * I);
                const ::uint64 DataKey = DataVal ^ ReadLe64(Secret + 8 * I);
                Acc[I ^ 1] += DataVal;
                Acc[I]     += (DataKey & 0xFFFFFFFFULL) * (DataKey >> 32);
            }
        }

        // -----------------------------------------------------------------
        // XXH3_scrambleAcc_scalar -- mix the accumulator at end of each
        // 1024-byte super-block.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void ScrambleAcc(::uint64* Acc, const ::uint8* Secret) noexcept
        {
            for (::SIZE_T I = 0; I < kAccNb; ++I)
            {
                const ::uint64 Key64 = ReadLe64(Secret + 8 * I);
                ::uint64 AccVal      = Acc[I];
                AccVal ^= AccVal >> 47;
                AccVal ^= Key64;
                AccVal *= kPrime32_1;
                Acc[I]  = AccVal;
            }
        }

        // -----------------------------------------------------------------
        // Accumulate -- the inner loop: iterate stripes of 64 bytes in
        // a block of 1024 bytes (16 stripes per block).
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void Accumulate(::uint64* Acc, const ::uint8* Data, const ::uint8* Secret, ::SIZE_T NbStripes) noexcept
        {
            for (::SIZE_T N = 0; N < NbStripes; ++N)
            {
                Accumulate512(Acc, Data + N * kStripeLen, Secret + N * 8);
            }
        }

        // -----------------------------------------------------------------
        // XXH3_mix2Accs -- mix two adjacent acc entries with a secret window.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint64 Mix2Accs(const ::uint64* Acc, const ::uint8* Secret) noexcept
        {
            return Mul128Fold64(Acc[0] ^ ReadLe64(Secret),
                                Acc[1] ^ ReadLe64(Secret + 8));
        }

        // -----------------------------------------------------------------
        // XXH3_mergeAccs -- collapse 8 acc words into one 64-bit hash.
        // -----------------------------------------------------------------
        ::uint64 MergeAccs(const ::uint64* Acc, const ::uint8* Secret, ::uint64 Start) noexcept
        {
            ::uint64 Result = Start;
            Result += Mix2Accs(Acc + 0, Secret +  0);
            Result += Mix2Accs(Acc + 2, Secret + 16);
            Result += Mix2Accs(Acc + 4, Secret + 32);
            Result += Mix2Accs(Acc + 6, Secret + 48);
            return Xxh3Avalanche(Result);
        }

        // -----------------------------------------------------------------
        // HashLongInternal -- the long-data striped path. Per spec:
        //
        //   Process input in 1024-byte blocks, 16 stripes per block.
        //   At end of each block, scramble the accumulator with a tail
        //   slice of the secret. The last block may be partial; the
        //   partial-stripe path uses a special end-of-input mixing.
        // -----------------------------------------------------------------
        ::uint64 HashLong_64b(const ::uint8* Data, ::SIZE_T Length, const ::uint8* Secret, ::SIZE_T SecretSize) noexcept
        {
            // Initialize accumulator with XXH3 starting values.
            ::uint64 Acc[8] = {
                kPrime32_3, kPrime64_1, kPrime64_2, kPrime64_3,
                kPrime64_4, kPrime32_2, kPrime64_5, kPrime32_1
            };

            const ::SIZE_T NbStripesPerBlock = (SecretSize - kStripeLen) / 8;
            const ::SIZE_T BlockLen          = kStripeLen * NbStripesPerBlock;
            const ::SIZE_T NbBlocks          = (Length - 1) / BlockLen;

            // Process all complete blocks.
            for (::SIZE_T N = 0; N < NbBlocks; ++N)
            {
                Accumulate(Acc, Data + N * BlockLen, Secret, NbStripesPerBlock);
                ScrambleAcc(Acc, Secret + SecretSize - kStripeLen);
            }

            // Last partial block.
            const ::SIZE_T NbStripes = ((Length - 1) - BlockLen * NbBlocks) / kStripeLen;
            Accumulate(Acc, Data + NbBlocks * BlockLen, Secret, NbStripes);

            // The very last stripe (always 64 bytes from the very end of
            // input; may overlap with already-processed bytes when
            // length is unaligned).
            Accumulate512(Acc, Data + Length - kStripeLen,
                          Secret + SecretSize - kStripeLen - kSecretLastAccStart);

            // Merge.
            return MergeAccs(Acc, Secret + 11, static_cast<::uint64>(Length) * kPrime64_1);
        }

        // -----------------------------------------------------------------
        // Seed-derived secret -- XXH3's "seeded" mode regenerates the
        // 192-byte secret from the given seed. We only call this for
        // seed != 0 because the engine's default is seed = 0.
        //
        // Reference: XXH3_initCustomSecret_scalar in xxhash.h.
        //
        // For seed = 0, callers MUST use kXxh3Secret directly (no
        // copy), so the default-seed path is bit-exact and
        // allocation-free.
        // -----------------------------------------------------------------
        void InitCustomSecret(::uint8* CustomSecret, ::uint64 Seed) noexcept
        {
            for (::SIZE_T I = 0; I < kSecretDefaultSize / 16; ++I)
            {
                const ::uint64 Lo = ReadLe64(kXxh3Secret + 16 * I)     + Seed;
                const ::uint64 Hi = ReadLe64(kXxh3Secret + 16 * I + 8) - Seed;
                // Store little-endian.
                for (int J = 0; J < 8; ++J)
                {
                    CustomSecret[16 * I + J]     = static_cast<::uint8>((Lo >> (8 * J)) & 0xFFu);
                    CustomSecret[16 * I + J + 8] = static_cast<::uint8>((Hi >> (8 * J)) & 0xFFu);
                }
            }
        }

    } // anonymous namespace

    // =====================================================================
    // FXxh3 public API.
    // =====================================================================

    ::uint64 FXxh3::Hash64(const void* Data, ::SIZE_T Length, ::uint64 Seed) noexcept
    {
        // Empty input gets the well-defined empty-input digest.
        if (Length == 0)
        {
            // Per XXH3 spec: empty input with seed=0 returns the
            // avalanche of the secret's first 16 bytes.
            return Len0To16_64b(static_cast<const ::uint8*>(Data), 0, kXxh3Secret, Seed);
        }

        const ::uint8* Bytes = static_cast<const ::uint8*>(Data);

        // Branch on length bracket.
        if (Length <= 16)
        {
            return Len0To16_64b(Bytes, Length, kXxh3Secret, Seed);
        }

        if (Length <= 128)
        {
            return Len17To128_64b(Bytes, Length, kXxh3Secret, Seed);
        }

        if (Length <= 240)
        {
            return Len129To240_64b(Bytes, Length, kXxh3Secret, Seed);
        }

        // Long-data path. The seed=0 case uses kXxh3Secret directly; the
        // seed!=0 case regenerates the secret on the stack.
        if (Seed == 0)
        {
            return HashLong_64b(Bytes, Length, kXxh3Secret, kSecretDefaultSize);
        }
        else
        {
            ::uint8 CustomSecret[kSecretDefaultSize];
            InitCustomSecret(CustomSecret, Seed);
            return HashLong_64b(Bytes, Length, CustomSecret, kSecretDefaultSize);
        }
    }

} // namespace XCore::Hash
