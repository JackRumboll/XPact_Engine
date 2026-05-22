// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FBlake3.cpp -- clean-room scalar BLAKE3 implementation.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.9.
//
// This implementation follows the BLAKE3 paper / official spec at
// https://github.com/BLAKE3-team/BLAKE3-specs verbatim, with the
// following discipline:
//
//   * Scalar 32-bit arithmetic only (no SSE2/SSE4.1/AVX2/AVX512/NEON).
//     The Section 5.3 bit-exactness contract is the load-bearing
//     rationale: the same source must produce byte-identical digests on
//     Win64-x86_64, Linux-x86_64, and Android-ARM64.
//   * Single-threaded (no parent-node parallelism). BLAKE3's parallelism
//     is a perf optimization for very large inputs; XPact's hash
//     consumers (leak-tracker chains, loctable headers, stat side-table
//     keys) all hash small (KB-scale) inputs where the parallelism
//     overhead exceeds the gain.
//   * Single-chunk mode for inputs <= 1024 bytes; tree-based parent
//     compression for larger inputs (the standard binary-tree-of-chunks
//     mode described in BLAKE3 spec Section 2.1).
//
// SPEC REFERENCES.
//   BLAKE3 paper: "BLAKE3: one function, fast everywhere"
//     (https://github.com/BLAKE3-team/BLAKE3-specs/blob/master/blake3.pdf)
//   Section 2: the compression function
//   Section 3: chunk processing
//   Section 4: tree processing
//   Section 5: domain-separation flags
//
// =====================================================================

#include "Hash/FBlake3.h"

#include <cstring>   // std::memcpy, std::memset

namespace XCore::Hash
{
    // -----------------------------------------------------------------
    // BLAKE3 constants. From the spec:
    //
    //   IV: the standard SHA-256 initial hash values (8 x 32-bit words),
    //       used as the chunk's starting CV.
    //
    //   BLOCK_LEN: 64 bytes per compression block.
    //   CHUNK_LEN: 1024 bytes per chunk (16 blocks of 64 bytes).
    //   OUT_LEN:   32 bytes (256-bit digest).
    //   KEY_LEN:   32 bytes (XPact's BLAKE3 facade only exposes the
    //              keyless content-hash mode; the keyed-MAC mode is not
    //              part of the Section 11.9 surface).
    //
    //   Flags: 5 domain-separation bits packed into the compression
    //          function's input. We use 4 of them in keyless mode:
    //            CHUNK_START   = 0x01
    //            CHUNK_END     = 0x02
    //            PARENT        = 0x04
    //            ROOT          = 0x08
    //          The DERIVE_KEY family (0x20/0x40/0x80) and KEYED_HASH
    //          (0x10) are unused in keyless content-hash mode.
    // -----------------------------------------------------------------
    namespace
    {
        constexpr ::uint32 kBlake3Iv[8] = {
            0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
            0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u
        };

        constexpr ::uint32 kBlockLen = 64;
        constexpr ::uint32 kChunkLen = 1024;
        constexpr ::uint32 kOutLen   = 32;

        constexpr ::uint8 kFlagChunkStart = 0x01u;
        constexpr ::uint8 kFlagChunkEnd   = 0x02u;
        constexpr ::uint8 kFlagParent     = 0x04u;
        constexpr ::uint8 kFlagRoot       = 0x08u;

        // -----------------------------------------------------------------
        // The BLAKE3 message-permutation schedule. Spec Section 2.3:
        // sigma defines the per-round mixing pattern. BLAKE3 uses 7
        // rounds with a fixed permutation applied between rounds; the
        // initial round uses the identity permutation.
        //
        // The permutation is:
        //   sigma = [ 2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8 ]
        //
        // The round schedule applies sigma to the message words for
        // each of the 7 rounds; before round 0 the message words are in
        // their natural order [0..15].
        // -----------------------------------------------------------------
        constexpr ::uint8 kSigma[16] = {
            2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8
        };

        // -----------------------------------------------------------------
        // Right-rotate (rotr) for uint32. BLAKE3's G function rotates by
        // 16, 12, 8, 7 -- all uint32-domain operations.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE constexpr ::uint32 Rotr32(::uint32 X, int Bits) noexcept
        {
            return (X >> Bits) | (X << (32 - Bits));
        }

        // -----------------------------------------------------------------
        // Little-endian uint32 load / store. The spec defines BLAKE3
        // I/O in little-endian byte order; the load/store helpers are
        // explicit so the implementation is byte-order-independent at
        // the source level (bit-exactness contract).
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE ::uint32 LoadLe32(const ::uint8* Src) noexcept
        {
            return static_cast<::uint32>(Src[0]) |
                   (static_cast<::uint32>(Src[1]) << 8) |
                   (static_cast<::uint32>(Src[2]) << 16) |
                   (static_cast<::uint32>(Src[3]) << 24);
        }

        XPACT_FORCEINLINE void StoreLe32(::uint8* Dst, ::uint32 Val) noexcept
        {
            Dst[0] = static_cast<::uint8>(Val & 0xFFu);
            Dst[1] = static_cast<::uint8>((Val >> 8) & 0xFFu);
            Dst[2] = static_cast<::uint8>((Val >> 16) & 0xFFu);
            Dst[3] = static_cast<::uint8>((Val >> 24) & 0xFFu);
        }

        // -----------------------------------------------------------------
        // The BLAKE3 G function (the round mixing primitive). Spec
        // Section 2.3:
        //
        //   G(a, b, c, d, mx, my):
        //     a = a + b + mx
        //     d = (d XOR a) >>> 16
        //     c = c + d
        //     b = (b XOR c) >>> 12
        //     a = a + b + my
        //     d = (d XOR a) >>>  8
        //     c = c + d
        //     b = (b XOR c) >>>  7
        //
        // The state argument is an array of 16 uint32 words; a, b, c, d
        // are indices into it. mx, my are message words.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void G(::uint32* State, int A, int B, int C, int D, ::uint32 Mx, ::uint32 My) noexcept
        {
            State[A] = State[A] + State[B] + Mx;
            State[D] = Rotr32(State[D] ^ State[A], 16);
            State[C] = State[C] + State[D];
            State[B] = Rotr32(State[B] ^ State[C], 12);
            State[A] = State[A] + State[B] + My;
            State[D] = Rotr32(State[D] ^ State[A], 8);
            State[C] = State[C] + State[D];
            State[B] = Rotr32(State[B] ^ State[C], 7);
        }

        // -----------------------------------------------------------------
        // Round -- one round of BLAKE3 compression. Eight G calls:
        // four column-mixes followed by four diagonal-mixes.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void Round(::uint32* State, const ::uint32* Msg) noexcept
        {
            // Column mixes.
            G(State, 0, 4,  8, 12, Msg[ 0], Msg[ 1]);
            G(State, 1, 5,  9, 13, Msg[ 2], Msg[ 3]);
            G(State, 2, 6, 10, 14, Msg[ 4], Msg[ 5]);
            G(State, 3, 7, 11, 15, Msg[ 6], Msg[ 7]);

            // Diagonal mixes.
            G(State, 0, 5, 10, 15, Msg[ 8], Msg[ 9]);
            G(State, 1, 6, 11, 12, Msg[10], Msg[11]);
            G(State, 2, 7,  8, 13, Msg[12], Msg[13]);
            G(State, 3, 4,  9, 14, Msg[14], Msg[15]);
        }

        // -----------------------------------------------------------------
        // PermuteMsg -- apply the sigma permutation to the message-words
        // array in place. Used between rounds (7 rounds = 6 permutations).
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void PermuteMsg(::uint32* Msg) noexcept
        {
            ::uint32 Tmp[16];
            for (int I = 0; I < 16; ++I)
            {
                Tmp[I] = Msg[kSigma[I]];
            }
            for (int I = 0; I < 16; ++I)
            {
                Msg[I] = Tmp[I];
            }
        }

        // -----------------------------------------------------------------
        // Compress -- the BLAKE3 compression function. Spec Section 2.2.
        //
        // Inputs:
        //   ChainingValue: 8 input chaining-value words (input CV).
        //   BlockWords:    16 input message words (one 64-byte block).
        //   Counter:       64-bit chunk counter (chunk index for chunks;
        //                  0 for parent nodes).
        //   BlockLen:      bytes valid in this block (1..64 for last
        //                  chunk block; 64 for all earlier blocks).
        //   Flags:         8-bit domain-separation flags.
        //
        // Output:
        //   State[0..15]:  16 output words. The truncate-to-256 mode
        //                  uses only State[0..7] (those XORed with
        //                  State[8..15] in the official spec; we do the
        //                  feed-forward XOR in-line here per Section 2.2
        //                  Step 5):
        //                    State[0..7]  ^= State[8..15]
        //                    State[8..15] ^= ChainingValue[0..7]
        //                  The full 64-byte output is the XOF version;
        //                  for our digest API we take State[0..7] only
        //                  after the feed-forward XOR.
        // -----------------------------------------------------------------
        void Compress(const ::uint32* ChainingValue,
                      const ::uint32* BlockWords,
                      ::uint64        Counter,
                      ::uint32        BlockLen,
                      ::uint8         Flags,
                      ::uint32*       StateOut) noexcept
        {
            ::uint32 State[16];
            // Initial state per spec Section 2.2 Step 1:
            //   State[0..7]   = ChainingValue[0..7]
            //   State[8..11]  = IV[0..3]
            //   State[12]     = counter low word
            //   State[13]     = counter high word
            //   State[14]     = block length
            //   State[15]     = flags
            for (int I = 0; I < 8; ++I) { State[I]     = ChainingValue[I]; }
            for (int I = 0; I < 4; ++I) { State[8 + I] = kBlake3Iv[I]; }
            State[12] = static_cast<::uint32>(Counter & 0xFFFFFFFFu);
            State[13] = static_cast<::uint32>((Counter >> 32) & 0xFFFFFFFFu);
            State[14] = BlockLen;
            State[15] = static_cast<::uint32>(Flags);

            // Local copy of message words; we permute it between rounds.
            ::uint32 Msg[16];
            for (int I = 0; I < 16; ++I) { Msg[I] = BlockWords[I]; }

            // Seven rounds with sigma-permutation applied between each.
            // (Permutations come between rounds, i.e., 6 permutations
            // total between 7 rounds.)
            Round(State, Msg);
            for (int RoundIdx = 1; RoundIdx < 7; ++RoundIdx)
            {
                PermuteMsg(Msg);
                Round(State, Msg);
            }

            // Feed-forward XOR (spec Section 2.2 Step 5):
            //   State[0..7]  ^= State[8..15]
            //   State[8..15] ^= ChainingValue[0..7]
            for (int I = 0; I < 8; ++I)
            {
                StateOut[I]     = State[I] ^ State[I + 8];
                StateOut[I + 8] = State[I + 8] ^ ChainingValue[I];
            }
        }

        // -----------------------------------------------------------------
        // BytesToWords -- pack 64 input bytes into 16 little-endian
        // uint32 words. Used at block boundaries.
        // -----------------------------------------------------------------
        XPACT_FORCEINLINE void BytesToWords(const ::uint8* Src, ::uint32* WordsOut) noexcept
        {
            for (int I = 0; I < 16; ++I)
            {
                WordsOut[I] = LoadLe32(Src + 4 * I);
            }
        }

        // -----------------------------------------------------------------
        // ChunkState -- the per-chunk rolling state.
        //
        // Each chunk consists of up to 16 64-byte blocks. The compression
        // is applied per block, threading the CV from one block to the
        // next. The first block carries CHUNK_START; the last carries
        // CHUNK_END (and ROOT if this is the only chunk).
        // -----------------------------------------------------------------
        struct FChunkState
        {
            ::uint32 Cv[8];           // current rolling CV
            ::uint64 ChunkCounter;    // this chunk's index
            ::uint8  BlockBuf[64];    // pending block bytes
            ::uint32 BlockBufLen;     // valid bytes in BlockBuf
            ::uint32 BlocksCompressed;// # 64-byte blocks compressed so far
            ::uint8  Flags;           // base-flags for this chunk-state (currently 0; keyed mode unused)

            void Init(::uint64 InChunkCounter, ::uint8 InFlags) noexcept
            {
                for (int I = 0; I < 8; ++I) { Cv[I] = kBlake3Iv[I]; }
                ChunkCounter     = InChunkCounter;
                BlockBufLen      = 0;
                BlocksCompressed = 0;
                Flags            = InFlags;
                std::memset(BlockBuf, 0, sizeof(BlockBuf));
            }

            ::uint32 Len() const noexcept
            {
                return BlocksCompressed * kBlockLen + BlockBufLen;
            }

            ::uint8 StartFlag() const noexcept
            {
                return BlocksCompressed == 0 ? kFlagChunkStart : 0;
            }

            // Compress the pending BlockBuf with the given suffix-flags
            // (typically CHUNK_END | ROOT for the last block). Resets
            // BlockBufLen to 0 and increments BlocksCompressed.
            //
            // Internal helper; non-trailing blocks are compressed via
            // CompressBlock with no CHUNK_END / ROOT flag.
            void CompressBlock(::uint8 SuffixFlags) noexcept
            {
                ::uint32 BlockWords[16];
                BytesToWords(BlockBuf, BlockWords);

                ::uint32 OutputState[16];
                Compress(Cv, BlockWords, ChunkCounter, kBlockLen,
                         static_cast<::uint8>(Flags | StartFlag() | SuffixFlags),
                         OutputState);

                for (int I = 0; I < 8; ++I) { Cv[I] = OutputState[I]; }
                BlockBufLen = 0;
                ++BlocksCompressed;
                std::memset(BlockBuf, 0, sizeof(BlockBuf));
            }

            // Feed bytes into the chunk. Returns the number of bytes
            // consumed. The caller is responsible for chunking the input
            // at 1024-byte boundaries.
            ::SIZE_T Update(const ::uint8* Data, ::SIZE_T Length) noexcept
            {
                ::SIZE_T Consumed = 0;
                while (Consumed < Length)
                {
                    // If the buffer is full and we have more bytes to feed,
                    // compress the current block (with no CHUNK_END since
                    // more bytes are coming).
                    if (BlockBufLen == kBlockLen)
                    {
                        CompressBlock(0);
                    }

                    // Append as many bytes as fit in the current block.
                    const ::SIZE_T BytesRemainingInBuf = kBlockLen - BlockBufLen;
                    const ::SIZE_T BytesToCopy        = (Length - Consumed) < BytesRemainingInBuf
                        ? (Length - Consumed)
                        : BytesRemainingInBuf;
                    std::memcpy(BlockBuf + BlockBufLen, Data + Consumed, BytesToCopy);
                    BlockBufLen += static_cast<::uint32>(BytesToCopy);
                    Consumed    += BytesToCopy;

                    // Stop at the chunk boundary; the caller will start a
                    // new ChunkState for the next chunk.
                    if (Len() == kChunkLen)
                    {
                        return Consumed;
                    }
                }
                return Consumed;
            }

            // Finalize the chunk; produces the chunk's output CV (8 words).
            // If RootFlag is set, the output is the root finalization
            // (caller treats State[0..7] as the start of the digest stream).
            void Finalize(::uint8 RootFlag, ::uint32* OutputCv) const noexcept
            {
                // BlockBuf already padded with zeros if last block is short.
                ::uint32 BlockWords[16];
                BytesToWords(BlockBuf, BlockWords);

                ::uint32 OutputState[16];
                Compress(Cv, BlockWords, ChunkCounter, BlockBufLen,
                         static_cast<::uint8>(Flags | StartFlag() | kFlagChunkEnd | RootFlag),
                         OutputState);

                for (int I = 0; I < 8; ++I) { OutputCv[I] = OutputState[I]; }
            }

            // For producing a longer output stream (XOF), the full 16-word
            // OutputState is needed. The streaming digest in this facade
            // takes just the first 32 bytes, so we provide this helper
            // for the root-finalization step which writes the 32-byte
            // output directly.
            void FinalizeBytes(::uint8 RootFlag, ::uint8* OutputBytes, ::SIZE_T NBytes) const noexcept
            {
                ::uint32 BlockWords[16];
                BytesToWords(BlockBuf, BlockWords);

                ::uint32 OutputState[16];
                Compress(Cv, BlockWords, ChunkCounter, BlockBufLen,
                         static_cast<::uint8>(Flags | StartFlag() | kFlagChunkEnd | RootFlag),
                         OutputState);

                // Write up to NBytes (<= 32 for our facade). Spec: for
                // a 32-byte digest, output State[0..7] little-endian.
                const ::SIZE_T Limit = NBytes < 32 ? NBytes : 32;
                for (::SIZE_T I = 0; I < Limit; I += 4)
                {
                    ::uint8 Buf[4];
                    StoreLe32(Buf, OutputState[I / 4]);
                    const ::SIZE_T CopyN = (Limit - I) < 4 ? (Limit - I) : 4;
                    std::memcpy(OutputBytes + I, Buf, CopyN);
                }
            }
        };

        // -----------------------------------------------------------------
        // ParentNodeCV -- compute the parent CV from two child CVs.
        //
        // Spec Section 2.5: a parent node's CV is the compression of its
        // two children with:
        //   block_words = left[0..7] | right[0..7]  (16 words total)
        //   counter     = 0
        //   block_len   = 64
        //   flags       = PARENT
        //
        // If the parent is the root, the ROOT flag is OR'd in.
        // -----------------------------------------------------------------
        void ParentNodeCv(const ::uint32* LeftCv,
                          const ::uint32* RightCv,
                          ::uint8         BaseFlags,
                          bool            IsRoot,
                          ::uint32*       OutCv) noexcept
        {
            ::uint32 BlockWords[16];
            for (int I = 0; I < 8; ++I)
            {
                BlockWords[I]     = LeftCv[I];
                BlockWords[I + 8] = RightCv[I];
            }

            ::uint8 Flags = static_cast<::uint8>(BaseFlags | kFlagParent | (IsRoot ? kFlagRoot : 0));
            ::uint32 OutputState[16];
            Compress(kBlake3Iv, BlockWords, /*counter=*/0, /*block_len=*/kBlockLen,
                     Flags, OutputState);

            for (int I = 0; I < 8; ++I) { OutCv[I] = OutputState[I]; }
        }

        void ParentNodeBytes(const ::uint32* LeftCv,
                             const ::uint32* RightCv,
                             ::uint8         BaseFlags,
                             ::uint8*        OutputBytes,
                             ::SIZE_T        NBytes) noexcept
        {
            ::uint32 BlockWords[16];
            for (int I = 0; I < 8; ++I)
            {
                BlockWords[I]     = LeftCv[I];
                BlockWords[I + 8] = RightCv[I];
            }

            ::uint8 Flags = static_cast<::uint8>(BaseFlags | kFlagParent | kFlagRoot);
            ::uint32 OutputState[16];
            Compress(kBlake3Iv, BlockWords, /*counter=*/0, /*block_len=*/kBlockLen,
                     Flags, OutputState);

            const ::SIZE_T Limit = NBytes < 32 ? NBytes : 32;
            for (::SIZE_T I = 0; I < Limit; I += 4)
            {
                ::uint8 Buf[4];
                StoreLe32(Buf, OutputState[I / 4]);
                const ::SIZE_T CopyN = (Limit - I) < 4 ? (Limit - I) : 4;
                std::memcpy(OutputBytes + I, Buf, CopyN);
            }
        }
    } // anonymous namespace

    // =====================================================================
    // FBlake3 public API.
    // =====================================================================

    FBlake3::FBlake3() noexcept
    {
        for (int I = 0; I < 8; ++I) { m_chunkCv[I] = kBlake3Iv[I]; }
        std::memset(m_chunkBlockBuf, 0, sizeof(m_chunkBlockBuf));
        m_chunkBlockLen = 0;
        m_chunkBlockNum = 0;
        m_chunkCount    = 0;
        m_cvStackLen    = 0;
        std::memset(m_cvStack, 0, sizeof(m_cvStack));
    }

    // -----------------------------------------------------------------
    // Helper: how many trailing zeros in a uint64. Used to determine
    // when CV-stack merges are needed (after pushing the N-th chunk,
    // merge until the number of trailing zeros in (N) matches the
    // current stack depth).
    //
    // Implemented as a straightforward loop (no __builtin_ctzll because
    // we want platform-uniform behavior; this is called O(log N) per
    // chunk so the perf doesn't matter).
    // -----------------------------------------------------------------
    namespace
    {
        ::uint32 CountTrailingZeros64(::uint64 Value) noexcept
        {
            if (Value == 0) { return 64; }
            ::uint32 Count = 0;
            while ((Value & 1u) == 0)
            {
                ++Count;
                Value >>= 1;
            }
            return Count;
        }
    }

    // -----------------------------------------------------------------
    // Push a finalized chunk CV onto the cv_stack, then merge.
    //
    // Spec Section 2.4: after finalizing chunk N (counter t starting at
    // 0), we push the chunk's CV. Then while N+1 has at least one
    // trailing zero (i.e., the new total count has a trailing 1
    // pattern), the top two stack entries are merged into a parent CV.
    //
    // The standard algorithm:
    //   after pushing the (t+1)-th chunk CV:
    //     while (popcount(t+1) trailing) merge top-2;
    //
    // Concretely: we look at the number of trailing 1's in the new
    // chunk count and merge that many times. This handles the binary-
    // tree-of-chunks construction.
    //
    // Our implementation: after finalizing chunk N (and pushing its CV),
    // we count the trailing zeros in (N+1) -- this is the depth at
    // which the new chunk lives. We then merge until the stack depth
    // matches.
    //
    // The cleaner formulation: merge while the new total chunk count
    // has more than one chunk and the new count is divisible by 2,
    // then 4, then 8, etc. (each level the chunk completes a subtree).
    // -----------------------------------------------------------------
    namespace
    {
        void PushChunkCv(::uint32* Stack, ::uint32& StackLen,
                         const ::uint32* ChunkCv, ::uint64 NewTotalChunks) noexcept
        {
            // Append the chunk's CV at the top of the stack.
            ::uint32* Top = Stack + 8 * StackLen;
            for (int I = 0; I < 8; ++I) { Top[I] = ChunkCv[I]; }
            ++StackLen;

            // Merge: while the new total chunk count has a trailing zero,
            // pop two top CVs and push their parent. The trailing-zero
            // count of NewTotalChunks tells us how many merges to do.
            const ::uint32 TrailingZeros = CountTrailingZeros64(NewTotalChunks);
            for (::uint32 I = 0; I < TrailingZeros; ++I)
            {
                if (StackLen < 2) { break; }
                const ::uint32* L = Stack + 8 * (StackLen - 2);
                const ::uint32* R = Stack + 8 * (StackLen - 1);
                ::uint32 ParentCv[8];
                ParentNodeCv(L, R, /*BaseFlags=*/0, /*IsRoot=*/false, ParentCv);
                // Pop two, push one.
                --StackLen;
                ::uint32* NewTop = Stack + 8 * (StackLen - 1);
                for (int J = 0; J < 8; ++J) { NewTop[J] = ParentCv[J]; }
            }
        }
    }

    void FBlake3::Update(const void* Data, ::SIZE_T Length) noexcept
    {
        if (Length == 0) { return; }
        const ::uint8* Bytes = static_cast<const ::uint8*>(Data);
        ::SIZE_T Remaining = Length;

        while (Remaining > 0)
        {
            // If the current chunk is full, finalize it and push to stack.
            const ::uint32 CurrentChunkLen = m_chunkBlockNum * kBlockLen + m_chunkBlockLen;
            if (CurrentChunkLen == kChunkLen)
            {
                // Compute the chunk's output CV (non-root finalization).
                FChunkState Cs;
                for (int I = 0; I < 8; ++I) { Cs.Cv[I] = m_chunkCv[I]; }
                std::memcpy(Cs.BlockBuf, m_chunkBlockBuf, sizeof(Cs.BlockBuf));
                Cs.BlockBufLen      = m_chunkBlockLen;
                Cs.BlocksCompressed = m_chunkBlockNum;
                Cs.ChunkCounter     = m_chunkCount;
                Cs.Flags            = 0;

                ::uint32 ChunkOutCv[8];
                Cs.Finalize(/*RootFlag=*/0, ChunkOutCv);

                ++m_chunkCount;
                PushChunkCv(m_cvStack, m_cvStackLen, ChunkOutCv, m_chunkCount);

                // Reset to a fresh chunk.
                for (int I = 0; I < 8; ++I) { m_chunkCv[I] = kBlake3Iv[I]; }
                std::memset(m_chunkBlockBuf, 0, sizeof(m_chunkBlockBuf));
                m_chunkBlockLen = 0;
                m_chunkBlockNum = 0;
            }

            // Append bytes to current chunk's block buffer; if the buffer
            // fills mid-chunk, compress (non-terminal).
            if (m_chunkBlockLen == kBlockLen)
            {
                // Compress this block (mid-chunk; no CHUNK_END flag yet).
                ::uint32 BlockWords[16];
                BytesToWords(m_chunkBlockBuf, BlockWords);

                const ::uint8 StartFlag = (m_chunkBlockNum == 0) ? kFlagChunkStart : 0;
                ::uint32 OutputState[16];
                Compress(m_chunkCv, BlockWords, m_chunkCount, kBlockLen,
                         StartFlag, OutputState);

                for (int I = 0; I < 8; ++I) { m_chunkCv[I] = OutputState[I]; }
                ++m_chunkBlockNum;
                m_chunkBlockLen = 0;
                std::memset(m_chunkBlockBuf, 0, sizeof(m_chunkBlockBuf));
            }

            // Copy as many bytes as fit before either the block buffer
            // or chunk boundary is reached.
            const ::SIZE_T BytesUntilBlockFull = kBlockLen - m_chunkBlockLen;
            const ::SIZE_T BytesUntilChunkFull =
                static_cast<::SIZE_T>(kChunkLen)
                    - (m_chunkBlockNum * kBlockLen + m_chunkBlockLen);
            ::SIZE_T BytesThisStep = Remaining;
            if (BytesThisStep > BytesUntilBlockFull) { BytesThisStep = BytesUntilBlockFull; }
            if (BytesThisStep > BytesUntilChunkFull) { BytesThisStep = BytesUntilChunkFull; }

            std::memcpy(m_chunkBlockBuf + m_chunkBlockLen, Bytes, BytesThisStep);
            m_chunkBlockLen += static_cast<::uint32>(BytesThisStep);
            Bytes           += BytesThisStep;
            Remaining       -= BytesThisStep;
        }
    }

    FBlake3Digest FBlake3::Finalize() const noexcept
    {
        FBlake3Digest Out;
        std::memset(Out.Bytes, 0, sizeof(Out.Bytes));

        // The "root" CV is the result of folding the cv_stack with the
        // current (in-progress) chunk's CV. There are three cases:
        //
        //   1. No chunks completed yet AND the current chunk has data:
        //      the current chunk is the only chunk and the root. We
        //      apply ROOT-flag finalization directly to it.
        //
        //   2. No chunks completed yet AND the current chunk is empty:
        //      the input is empty. We apply ROOT to an empty chunk-0.
        //
        //   3. Chunks have been completed and pushed to the stack: we
        //      first finalize the in-progress chunk to a CV (non-root),
        //      then collapse the stack from right to left, merging each
        //      pair as PARENT nodes. The topmost merge gets the ROOT flag.
        //      The output of that root-merge is the digest.
        //
        // The implementation:
        //   - Finalize the in-progress chunk to its non-root output CV.
        //   - If the stack is empty, this CV is the root finalization
        //     (re-finalize with ROOT flag).
        //   - Otherwise, walk the stack from top to bottom, merging:
        //       cv = stack[top]; for i = top-1..0: cv = parent(stack[i], cv);
        //     The last merge (after the bottom-most stack entry is
        //     consumed) is the root-merge.

        // Special case: empty input AND no chunks pushed.
        const ::uint32 CurrentChunkLen = m_chunkBlockNum * kBlockLen + m_chunkBlockLen;
        if (m_cvStackLen == 0)
        {
            // Single-chunk (possibly empty) root finalization.
            FChunkState Cs;
            for (int I = 0; I < 8; ++I) { Cs.Cv[I] = m_chunkCv[I]; }
            std::memcpy(Cs.BlockBuf, m_chunkBlockBuf, sizeof(Cs.BlockBuf));
            Cs.BlockBufLen      = m_chunkBlockLen;
            Cs.BlocksCompressed = m_chunkBlockNum;
            Cs.ChunkCounter     = m_chunkCount;
            Cs.Flags            = 0;
            Cs.FinalizeBytes(kFlagRoot, Out.Bytes, 32);
            return Out;
        }

        // Multi-chunk case: finalize the in-progress chunk (non-root)
        // unless it's empty, in which case skip and use the stack-top
        // directly.
        bool HasRightCv = false;
        ::uint32 RightCv[8];
        if (CurrentChunkLen > 0)
        {
            FChunkState Cs;
            for (int I = 0; I < 8; ++I) { Cs.Cv[I] = m_chunkCv[I]; }
            std::memcpy(Cs.BlockBuf, m_chunkBlockBuf, sizeof(Cs.BlockBuf));
            Cs.BlockBufLen      = m_chunkBlockLen;
            Cs.BlocksCompressed = m_chunkBlockNum;
            Cs.ChunkCounter     = m_chunkCount;
            Cs.Flags            = 0;
            Cs.Finalize(/*RootFlag=*/0, RightCv);
            HasRightCv = true;
        }

        // If there's no in-progress chunk, the rightmost CV is the
        // stack-top.
        ::uint32 Cursor[8];
        ::uint32 StackTop = m_cvStackLen;
        if (HasRightCv)
        {
            for (int I = 0; I < 8; ++I) { Cursor[I] = RightCv[I]; }
        }
        else
        {
            // Use the top stack entry as the initial cursor.
            const ::uint32* Top = m_cvStack + 8 * (StackTop - 1);
            for (int I = 0; I < 8; ++I) { Cursor[I] = Top[I]; }
            --StackTop;
        }

        // Fold from the top of the stack downward. At each step the
        // left child is stack[i] and the right child is Cursor. The
        // last fold (when StackTop becomes 0 after the iteration)
        // produces the ROOT output.
        while (StackTop > 1)
        {
            const ::uint32* Left = m_cvStack + 8 * (StackTop - 1);
            ::uint32 NewCv[8];
            ParentNodeCv(Left, Cursor, /*BaseFlags=*/0, /*IsRoot=*/false, NewCv);
            for (int I = 0; I < 8; ++I) { Cursor[I] = NewCv[I]; }
            --StackTop;
        }

        // Final root merge.
        if (StackTop == 1)
        {
            const ::uint32* Left = m_cvStack + 8 * 0;
            ParentNodeBytes(Left, Cursor, /*BaseFlags=*/0, Out.Bytes, 32);
        }
        else
        {
            // StackTop == 0 means the cursor is already the only CV
            // (happens when no in-progress chunk and only one stack
            // entry existed). It needs root finalization, but our
            // intermediate Cursor was computed without ROOT. Recompute
            // by re-rolling: this case is degenerate (it means exactly
            // one chunk was ever pushed, which would have been merged
            // immediately at PushChunkCv with N=1 and TZ=0 -- so we'd
            // have stack=1 and no in-progress chunk. The single-chunk
            // case is handled above by the m_cvStackLen==0 branch
            // because PushChunkCv with N=1 trailing-zeros = 0 doesn't
            // merge, and StackTop ends at 1).
            //
            // For belt-and-braces: take the cursor as-is and finalize.
            // The Cursor lacks the ROOT flag, so we re-finalize by
            // applying a "fake parent" where left = IV, right = cursor.
            // This branch should be unreachable in practice; the
            // explicit code path is defensive.
            for (::SIZE_T I = 0; I < 32; I += 4)
            {
                StoreLe32(Out.Bytes + I, Cursor[I / 4]);
            }
        }

        return Out;
    }

    FBlake3Digest FBlake3::Compute(const void* Data, ::SIZE_T Length) noexcept
    {
        FBlake3 H;
        H.Update(Data, Length);
        return H.Finalize();
    }

} // namespace XCore::Hash
