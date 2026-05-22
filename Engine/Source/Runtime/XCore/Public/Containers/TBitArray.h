// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// TBitArray.h -- dynamic bitset, 64-bit-word stride (Section 5.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1 (`class TBitArray; // dynamic bitset, 64-
// bit-word stride`) + Section 5.5 (UE divergences) + dependency-graph
// step 7.
//
// TBitArray is a packed bit-storage container backed by an array of
// uint64_t words. Each word holds 64 bits, stored bit-0 = LSB at the
// in-word level. The backing storage is the existing internal primitive
// Detail::TArrayCore<uint64_t, DefaultAllocator>; TBitArray adds the
// bit-addressing layer on top.
//
// SURFACE (Section 5.1 + the dispatch's explicit list):
//   * Num() -- total number of bits.
//   * Reserve(NumBits) -- ensure capacity for at least NumBits.
//   * Reset() -- clear all bits + drop the buffer (matches TArray::Reset(0)).
//   * Init(Value, NumBits) -- size to NumBits, fill with Value (true/false).
//   * Add(bool) -- append a single bit at the tail.
//   * RemoveAt(int32) -- O(N) ordered removal of a bit (shifts following bits down).
//   * operator[](int32) const -- read a single bit. Const-only; writes go through Set().
//   * Set(int32, bool) -- write a single bit.
//   * FindFirstSet(int32 StartIndex = 0) -- first set bit >= StartIndex; INDEX_NONE if none.
//   * FindFirstClear(int32 StartIndex = 0) -- first clear bit >= StartIndex; INDEX_NONE if none.
//   * CountSetBits() -- total number of set bits.
//
// WHY 64-BIT WORDS.
//
// Section 5.1 names the 64-bit-word stride explicitly. Rationale:
//   * Hardware popcount intrinsics (_mm_popcnt_u64 / __builtin_popcountll)
//     are 64-bit-native; using 32-bit words doubles the call count.
//   * Bit-scan intrinsics (_BitScanForward64 / __builtin_ctzll) likewise.
//   * Cache-friendly: a single word covers 64 bits with one load.
//
// DETERMINISM (Section 5.3).
//
// All TBitArray operations are deterministic at the algorithm level.
// FindFirstSet and FindFirstClear iterate words in ascending order; the
// returned bit index is identical across Win64 / Linux / Android given
// the same input pattern. CountSetBits uses popcount, which is bit-exact
// across architectures.
//
// THREADING (Section 5.2).
//
// TBitArray is NOT thread-safe. Two threads mutating the same TBitArray
// is UB.
//
// PHASE 1C STATUS.
//
// The platform-intrinsic shims for FindFirstSet / FindFirstClear /
// CountSetBits live in TBitArray.cpp (private; non-template body).
// The public header declares the methods but defers the
// platform-specific intrinsic dispatch to the .cpp.
//
// =====================================================================

#include "Containers/TArrayCore.h"
#include "Containers/DefaultAllocator.h"
#include "Macros/XCoreTypes.h"
#include "Macros/XPactMacros.h"

namespace XCore
{
    // -----------------------------------------------------------------
    // TBitArray -- the packed bit container.
    //
    // Layout:
    //   TArrayCore<uint64_t, DefaultAllocator> m_words;   // backing words
    //   int32 m_numBits;                                  // bit count
    //
    // Invariants:
    //   * m_numBits >= 0
    //   * (m_numBits + 63) / 64 <= m_words.Num()    -- enough words
    //     for the bits. The trailing bits within the last word that
    //     exceed m_numBits are "don't care" but the container normalizes
    //     them to 0 after every mutation that may have set them.
    //   * m_words.Num() may be larger than the strict requirement
    //     (Reserve expands beyond strict need; Reset releases).
    // -----------------------------------------------------------------

    class TBitArray
    {
    public:
        // -------------------------------------------------------------
        // Layout-load-bearing constants.
        //
        // BitsPerWord = 64 is the stride contract; changing it would
        // invalidate the platform-intrinsic .cpp.
        // -------------------------------------------------------------
        static constexpr ::int32 BitsPerWord = 64;

        // -------------------------------------------------------------
        // Default ctor -- empty bit array (no words, 0 bits).
        // -------------------------------------------------------------
        TBitArray() noexcept;

        // -------------------------------------------------------------
        // Init-ctor -- size to NumBits, fill every bit with Value.
        //
        // Convenience for the canonical "give me a bit set of N bits
        // initially false" or "give me a bit set of N bits initially
        // true" pattern.
        // -------------------------------------------------------------
        TBitArray(bool Value, ::int32 NumBits);

        // -------------------------------------------------------------
        // Copy ctor / copy-assign -- deep copy.
        //
        // The backing TArrayCore<uint64_t> is copyable; the bit count
        // is a trivial scalar; copy semantics are well-defined.
        // -------------------------------------------------------------
        TBitArray(const TBitArray& Other);
        TBitArray& operator=(const TBitArray& Other);

        // -------------------------------------------------------------
        // Move ctor / move-assign.
        // -------------------------------------------------------------
        TBitArray(TBitArray&& Other) noexcept;
        TBitArray& operator=(TBitArray&& Other) noexcept;

        // -------------------------------------------------------------
        // Destructor (trivial; TArrayCore's destructor frees the buffer).
        // -------------------------------------------------------------
        ~TBitArray() noexcept = default;

        // =================================================================
        // Capacity / size.
        // =================================================================

        // -------------------------------------------------------------
        // Num -- total number of bits.
        // -------------------------------------------------------------
        [[nodiscard]] XPACT_FORCEINLINE ::int32 Num() const noexcept
        {
            return m_numBits;
        }

        [[nodiscard]] XPACT_FORCEINLINE bool IsEmpty() const noexcept
        {
            return m_numBits == 0;
        }

        [[nodiscard]] XPACT_FORCEINLINE bool IsValidIndex(::int32 Index) const noexcept
        {
            return Index >= 0 && Index < m_numBits;
        }

        // -------------------------------------------------------------
        // Reserve -- ensure capacity for at least NumBits bits.
        //
        // Rounds NumBits up to the next multiple of 64 to compute the
        // required word count, then delegates to TArrayCore::Reserve.
        // No-op if the current capacity is already sufficient.
        // -------------------------------------------------------------
        void Reserve(::int32 NumBits);

        // -------------------------------------------------------------
        // Reset -- clear all bits and release the underlying buffer.
        //
        // Equivalent to assigning a default-constructed TBitArray.
        // The optional NewCapacityBitsHint mirrors TArray::Reset's
        // hint parameter; 0 means drop the buffer.
        // -------------------------------------------------------------
        void Reset(::int32 NewCapacityBitsHint = 0) noexcept;

        // -------------------------------------------------------------
        // Init -- size to NumBits, fill every bit with Value.
        //
        // The current contents are dropped. Equivalent to:
        //   Reset(); Reserve(NumBits); for (int i = 0; i < NumBits; ++i) { Add(Value); }
        // but uses memset on the word array for speed.
        // -------------------------------------------------------------
        void Init(bool Value, ::int32 NumBits);

        // =================================================================
        // Bit-level access.
        // =================================================================

        // -------------------------------------------------------------
        // operator[](int32) -- read a single bit (const-only).
        //
        // Writes go through Set(). The const-only operator[] is
        // deliberate: a non-const reference-returning operator[] would
        // require a proxy-reference type (since bits are not
        // independently addressable), which is a notorious source of
        // bugs (TBitArray::Reference vs bool implicit-conversion
        // ambiguity). The Set(int32, bool) method is the unambiguous
        // write path.
        // -------------------------------------------------------------
        [[nodiscard]] bool operator[](::int32 Index) const noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            const ::int32 WordIdx = Index / BitsPerWord;
            const ::int32 BitIdx  = Index % BitsPerWord;
            return (m_words[WordIdx] & (::uint64{1} << BitIdx)) != 0;
        }

        // -------------------------------------------------------------
        // Set(int32, bool) -- write a single bit.
        // -------------------------------------------------------------
        void Set(::int32 Index, bool Value) noexcept
        {
            XPACT_CHECK(IsValidIndex(Index));
            const ::int32 WordIdx = Index / BitsPerWord;
            const ::int32 BitIdx  = Index % BitsPerWord;
            const ::uint64 Mask   = (::uint64{1} << BitIdx);
            if (Value)
            {
                m_words[WordIdx] |= Mask;
            }
            else
            {
                m_words[WordIdx] &= ~Mask;
            }
        }

        // =================================================================
        // Mutation.
        // =================================================================

        // -------------------------------------------------------------
        // Add(bool) -- append a single bit at the tail.
        //
        // If the new bit doesn't fit in the current word capacity (i.e.,
        // m_numBits is a multiple of 64 AND m_numBits / 64 == m_words.Num()),
        // a new word is added (which itself grows the underlying buffer
        // per TArrayCore's growth protocol).
        //
        // Returns the index of the new bit.
        // -------------------------------------------------------------
        ::int32 Add(bool Value);

        // -------------------------------------------------------------
        // RemoveAt(int32) -- O(N/64) ordered removal of a bit.
        //
        // Shifts all bits at positions > Index one position toward the
        // front (i.e., the bit that was at Index+1 ends up at Index,
        // etc.). The last bit is dropped.
        //
        // The implementation walks words in ascending order: for each
        // word at index >= WordIdx, it shifts the word's bits down by
        // one and pulls the bit-0 of the next word into bit-63 of the
        // current word.
        // -------------------------------------------------------------
        void RemoveAt(::int32 Index) noexcept;

        // =================================================================
        // Bit-level queries.
        // =================================================================

        // -------------------------------------------------------------
        // FindFirstSet -- first set bit at or after StartIndex.
        //
        // Returns INDEX_NONE if no set bit found. Uses platform-specific
        // bit-scan-forward intrinsics (_BitScanForward64 on Win64;
        // __builtin_ctzll on Clang/GCC) for the per-word scan.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 FindFirstSet(::int32 StartIndex = 0) const noexcept;

        // -------------------------------------------------------------
        // FindFirstClear -- first clear bit at or after StartIndex.
        //
        // Returns INDEX_NONE if no clear bit found within [StartIndex,
        // Num()). Note: the trailing bits beyond Num() within the last
        // partial word are "don't care"; the implementation masks them
        // before scanning so they cannot produce false positives.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 FindFirstClear(::int32 StartIndex = 0) const noexcept;

        // -------------------------------------------------------------
        // CountSetBits -- total number of set bits across the array.
        //
        // Uses platform popcount intrinsics. The trailing bits beyond
        // Num() within the last partial word are masked off before
        // popcount.
        // -------------------------------------------------------------
        [[nodiscard]] ::int32 CountSetBits() const noexcept;

    private:
        // -------------------------------------------------------------
        // Helper: round UP to the next multiple of 64 (returning the
        // required word count).
        // -------------------------------------------------------------
        [[nodiscard]] static XPACT_FORCEINLINE ::int32 BitsToWords(::int32 NumBits) noexcept
        {
            // (NumBits + 63) / 64 rounds up. The intermediate addition
            // fits in int32 for NumBits up to ~2.1 billion; sufficient
            // for the realistic use cases (a 1B-bit array is ~125 MB).
            return (NumBits + (BitsPerWord - 1)) / BitsPerWord;
        }

        // -------------------------------------------------------------
        // Helper: zero out trailing bits in the last word that exceed
        // m_numBits. Called after every mutation that may have set
        // them. The trailing bits are "don't care" but normalizing
        // them ensures FindFirstSet / CountSetBits do not see ghost
        // bits past Num().
        // -------------------------------------------------------------
        void NormalizeTrailingBits() noexcept;

        // -------------------------------------------------------------
        // Backing storage. The word count is BitsToWords(m_numBits).
        // Per Section 5.1 the type is TArrayCore<uint64_t,
        // DefaultAllocator>; we use the Detail primitive directly
        // (the public TArray<uint64_t> would also work but adds a
        // class layer that's pure overhead here).
        // -------------------------------------------------------------
        ::XCore::Detail::TArrayCore<::uint64, ::XCore::DefaultAllocator> m_words;

        // Number of bits in the array. May be less than 64 *
        // m_words.Num() (the trailing bits in the last word are
        // "don't care" and zeroed-by-convention).
        ::int32 m_numBits;
    };

} // namespace XCore
