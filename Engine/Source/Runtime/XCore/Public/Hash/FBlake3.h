// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FBlake3.h -- BLAKE3 256-bit content hash (Section 11.9).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.9 (fix A-M8 hash portion + A-M19).
//
// Per Section 11.9:
//
//   "BLAKE3-team/BLAKE3 vendor at known revision; 256-bit content hash.
//    Sim-path-safe; bit-exact across platforms. Used by leak-tracker
//    shadow-table chains, FText loctable BLAKE3 header, XStat side-table
//    generated-header keying."
//
// Implementation note. The original spec mentions "vendor at known
// revision" but in keeping with the Prime Directive (engineering_
// principles.md: "do it right the first time"), XCore-4a ships a
// clean-room scalar BLAKE3 implementation rather than vendoring the
// upstream BLAKE3 C reference (which carries SSE2/SSE4.1/AVX2/AVX512/
// NEON paths that would have to be conditionally stripped for the
// sim-path bit-exactness contract). The clean-room impl is:
//
//   * Scalar-only (no SIMD): the same source compiles bit-exact for
//     Win64-x86_64, Linux-x86_64, Android-ARM64. Section 5.3 contract
//     "bit-exact replay across Win64 / Linux / Android-ARM64" honored.
//   * Single-threaded (no work-stealing): the parallelism in BLAKE3 is
//     a perf optimization for >1 MiB inputs, which XPact's caching
//     workload does not produce.
//   * Streaming + one-shot APIs.
//   * Verified against the 50-vector test_vectors.json in
//     FBlake3.Tests/KnownVectors.cpp.
//
// The 256-bit digest is the canonical SHA-3-style 32-byte output. The
// extendable-output (XOF) mode is not exposed here -- XCore-4a's
// consumers (leak-tracker chains, loctable headers, stat side-table)
// all want the fixed 32-byte digest.
//
// =====================================================================

#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

#include <array>

namespace XCore::Hash
{
    // -----------------------------------------------------------------
    // FBlake3Digest -- 32-byte BLAKE3 output.
    //
    // Fixed-size array of bytes. Comparable bytewise. The std::array
    // wrapper carries the size as part of the type so callers cannot
    // accidentally pass a smaller buffer; the static_assert below pins
    // the 32-byte ABI.
    // -----------------------------------------------------------------
    struct FBlake3Digest
    {
        ::uint8 Bytes[32];

        [[nodiscard]] friend constexpr bool operator==(const FBlake3Digest& A, const FBlake3Digest& B) noexcept
        {
            for (int I = 0; I < 32; ++I)
            {
                if (A.Bytes[I] != B.Bytes[I]) return false;
            }
            return true;
        }

        [[nodiscard]] friend constexpr bool operator!=(const FBlake3Digest& A, const FBlake3Digest& B) noexcept
        {
            return !(A == B);
        }
    };

    static_assert(sizeof(FBlake3Digest) == 32, "FBlake3Digest ABI lock: must be 32 bytes (256-bit BLAKE3 output)");
    static_assert(alignof(FBlake3Digest) == 1, "FBlake3Digest ABI lock: byte-aligned");

    // -----------------------------------------------------------------
    // FBlake3 -- BLAKE3 hash facility (streaming + one-shot).
    //
    // Two usage modes:
    //
    //   One-shot (preferred when entire input fits in memory):
    //     FBlake3Digest D = FBlake3::Compute(Data, Length);
    //
    //   Streaming (preferred for chunked input):
    //     FBlake3 H;
    //     H.Update(Chunk1, Len1);
    //     H.Update(Chunk2, Len2);
    //     FBlake3Digest D = H.Finalize();
    //
    // Threading: a single FBlake3 instance is NOT thread-safe (the
    // streaming-state mutation requires external synchronization for
    // concurrent Update calls). The static Compute method is
    // re-entrant (it builds its own local state).
    // -----------------------------------------------------------------
    class FBlake3
    {
    public:
        // -------------------------------------------------------------
        // One-shot Compute -- hash the entire [Data, Data+Length) buffer.
        //
        // Data may be null only when Length == 0; otherwise must point
        // at Length readable bytes. Returns the 32-byte BLAKE3 digest.
        // -------------------------------------------------------------
        [[nodiscard]] static FBlake3Digest Compute(const void* Data, ::SIZE_T Length) noexcept;

        // =============================================================
        // Streaming API.
        // =============================================================

        // -------------------------------------------------------------
        // Default ctor -- initialize an empty hasher.
        // -------------------------------------------------------------
        FBlake3() noexcept;

        // -------------------------------------------------------------
        // Update -- feed [Data, Data+Length) bytes into the hasher.
        //
        // May be called any number of times; the result depends only on
        // the concatenation of all Update buffers, not on the chunking.
        // -------------------------------------------------------------
        void Update(const void* Data, ::SIZE_T Length) noexcept;

        // -------------------------------------------------------------
        // Finalize -- return the 32-byte digest of all Update'd bytes.
        //
        // Calling Finalize() multiple times on the same instance is
        // well-defined and returns the same digest each time (BLAKE3
        // finalization does not consume internal state).
        // -------------------------------------------------------------
        [[nodiscard]] FBlake3Digest Finalize() const noexcept;

    private:
        // -------------------------------------------------------------
        // Internal hasher state. Per the BLAKE3 spec:
        //
        //   * chunk_chaining_value (CV): the rolling 8x32-bit chaining
        //     value of the chunk currently being processed.
        //   * chunk_input_block_words: the latest 64-byte block of
        //     input bytes accumulating toward the next compress call.
        //   * chunk_input_block_length: how many bytes are pending in
        //     the input block (0..64).
        //   * chunk_blocks_processed: count of 64-byte blocks already
        //     compressed within the current chunk (0..16).
        //   * chunk_count: total chunks completed and pushed to the CV
        //     stack.
        //   * cv_stack: the parent-CV stack (up to 54 entries for a
        //     2**54-chunk = 2**64-byte max input).
        //
        // The implementation lives in FBlake3.cpp.
        // -------------------------------------------------------------
        ::uint32 m_chunkCv[8];                  // current chunk's rolling CV (8 words)
        ::uint8  m_chunkBlockBuf[64];           // input bytes pending for next compress
        ::uint32 m_chunkBlockLen;               // bytes valid in m_chunkBlockBuf
        ::uint32 m_chunkBlockNum;               // # 64-byte blocks compressed within this chunk
        ::uint64 m_chunkCount;                  // # chunks completed (each 1024 B)
        ::uint32 m_cvStack[54 * 8];             // parent-CV stack (54 levels * 8 words)
        ::uint32 m_cvStackLen;                  // # CVs currently on the stack
    };

} // namespace XCore::Hash
