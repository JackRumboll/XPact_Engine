// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// TBitArray.cpp -- non-template body of TBitArray (Section 5.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 5.1.
//
// TBitArray's class layout + the trivial inline accessors (operator[],
// Set, Num, IsValidIndex) live in the public header. The non-trivial
// methods that benefit from platform-specific intrinsics ship here:
//
//   * FindFirstSet   -- _BitScanForward64 (Win64) / __builtin_ctzll
//                       (Clang/GCC).
//   * FindFirstClear -- as above, applied to ~word & trailing-bit-mask.
//   * CountSetBits   -- _mm_popcnt_u64 (Win64) / __builtin_popcountll
//                       (Clang/GCC).
//   * RemoveAt       -- O(N/64) word-shift loop; non-trivial enough
//                       to keep out of the header.
//   * Constructors, Reserve, Reset, Init, Add -- non-template-bodies
//                       that move into the .cpp for codegen-once.
//
// The intrinsic dispatch uses the XPACT_PLATFORM_* macros from
// XPactMacros.h:
//   * XPACT_PLATFORM_WIN64    -> <intrin.h>
//   * XPACT_PLATFORM_LINUX    -> __builtin_* (Clang/GCC native)
//   * XPACT_PLATFORM_ANDROID  -> __builtin_* (NDK Clang)
//
// =====================================================================

#include "Containers/TBitArray.h"
#include "Macros/XPactMacros.h"
#include "Macros/XCoreTypes.h"

#include <cstring>          // std::memset

#if XPACT_PLATFORM_WIN64
    // MSVC and Clang-cl both ship <intrin.h>; the intrinsic names
    // _BitScanForward64 + __popcnt64 are MS-x64 conventions.
    #include <intrin.h>
    #include <nmmintrin.h>   // _mm_popcnt_u64 (SSE4.2)
#endif

namespace XCore
{
    // =================================================================
    // Helper: platform-specific bit-scan-forward (count trailing zeros).
    //
    // Returns the 0-based index of the lowest set bit in `Word`.
    // Pre-condition: Word != 0. Caller must check before invoking.
    //
    // Win64: _BitScanForward64 sets the output parameter to the index.
    // Clang/GCC: __builtin_ctzll returns the index directly.
    // =================================================================
    namespace BitArrayPrivate
    {
        [[nodiscard]] XPACT_FORCEINLINE ::int32 CountTrailingZeros64(::uint64 Word) noexcept
        {
            XPACT_CHECK(Word != 0);
#if XPACT_PLATFORM_WIN64
            unsigned long Index = 0;
            // _BitScanForward64 returns nonzero on success; pre-condition
            // (Word != 0) guarantees success.
            _BitScanForward64(&Index, Word);
            return static_cast<::int32>(Index);
#else  // Linux + Android (Clang/GCC)
            return static_cast<::int32>(__builtin_ctzll(Word));
#endif
        }

        [[nodiscard]] XPACT_FORCEINLINE ::int32 PopCount64(::uint64 Word) noexcept
        {
#if XPACT_PLATFORM_WIN64
            // _mm_popcnt_u64 requires SSE4.2; the engine's SimdLevel::SSE42
            // default makes this safe across every supported Win64 target.
            // For pre-SSE4.2 builds (none in the supported set) the
            // fallback would be a 4-step bit-twiddle; not needed here.
            return static_cast<::int32>(_mm_popcnt_u64(Word));
#else  // Linux + Android (Clang/GCC)
            return static_cast<::int32>(__builtin_popcountll(Word));
#endif
        }

        // Mask of the low `Bits` bits set (e.g., Bits=3 -> 0b111).
        // Bits in [1, 63]. Bits = 0 returns 0 (no bits set); Bits = 64
        // would overflow shift -- this helper is only used for partial-
        // word masks, so Bits is always strictly less than 64.
        [[nodiscard]] XPACT_FORCEINLINE ::uint64 LowBitsMask(::int32 Bits) noexcept
        {
            XPACT_CHECK(Bits >= 0 && Bits < 64);
            return Bits == 0 ? ::uint64{0} : ((::uint64{1} << Bits) - 1);
        }
    } // namespace BitArrayPrivate

    // =================================================================
    // Constructors.
    // =================================================================

    TBitArray::TBitArray() noexcept
        : m_words()
        , m_numBits(0)
    {
    }

    TBitArray::TBitArray(bool Value, ::int32 NumBits)
        : m_words()
        , m_numBits(0)
    {
        Init(Value, NumBits);
    }

    TBitArray::TBitArray(const TBitArray& Other)
        : m_words(Other.m_words)
        , m_numBits(Other.m_numBits)
    {
    }

    TBitArray& TBitArray::operator=(const TBitArray& Other)
    {
        if (this != &Other)
        {
            m_words   = Other.m_words;
            m_numBits = Other.m_numBits;
        }
        return *this;
    }

    TBitArray::TBitArray(TBitArray&& Other) noexcept
        : m_words(::std::move(Other.m_words))
        , m_numBits(Other.m_numBits)
    {
        Other.m_numBits = 0;
    }

    TBitArray& TBitArray::operator=(TBitArray&& Other) noexcept
    {
        if (this != &Other)
        {
            m_words         = ::std::move(Other.m_words);
            m_numBits       = Other.m_numBits;
            Other.m_numBits = 0;
        }
        return *this;
    }

    // =================================================================
    // Capacity.
    // =================================================================

    void TBitArray::Reserve(::int32 NumBits)
    {
        XPACT_CHECK(NumBits >= 0);
        const ::int32 NeededWords = BitsToWords(NumBits);
        if (NeededWords > m_words.Max())
        {
            m_words.Reserve(NeededWords);
        }
    }

    void TBitArray::Reset(::int32 NewCapacityBitsHint) noexcept
    {
        const ::int32 NewCapacityWordsHint = BitsToWords(NewCapacityBitsHint);
        m_words.Reset(NewCapacityWordsHint);
        m_numBits = 0;
    }

    void TBitArray::Init(bool Value, ::int32 NumBits)
    {
        XPACT_CHECK(NumBits >= 0);

        // Drop existing contents.
        m_words.Reset(0);
        m_numBits = 0;

        if (NumBits == 0)
        {
            return;
        }

        const ::int32 NeededWords = BitsToWords(NumBits);
        m_words.Reserve(NeededWords);

        // Fill the underlying word buffer. We need to bump m_words.Num()
        // up to NeededWords. We do this by calling Add() in a loop, which
        // is the only public path that bumps m_num. The pattern uses the
        // word's pre-fill content; we'll memset over it next.
        const ::uint64 FillWord = Value ? ~::uint64{0} : ::uint64{0};
        for (::int32 W = 0; W < NeededWords; ++W)
        {
            m_words.Add(FillWord);
        }

        m_numBits = NumBits;

        // The trailing bits in the last word that exceed NumBits are
        // "don't care"; normalize them to 0 so FindFirstClear /
        // CountSetBits do not see ghost bits past Num().
        NormalizeTrailingBits();
    }

    // =================================================================
    // Mutators.
    // =================================================================

    ::int32 TBitArray::Add(bool Value)
    {
        // If we are about to write bit #m_numBits and it does not fit
        // in the current word capacity, grow the word buffer by one
        // word. The trigger is m_numBits == m_words.Num() * 64.
        const ::int32 WordIdx = m_numBits / BitsPerWord;
        if (WordIdx >= m_words.Num())
        {
            // Add a new zero-initialized word at the tail.
            m_words.Add(::uint64{0});
        }

        const ::int32 BitIdx = m_numBits % BitsPerWord;
        const ::uint64 Mask  = (::uint64{1} << BitIdx);
        if (Value)
        {
            m_words[WordIdx] |= Mask;
        }
        else
        {
            m_words[WordIdx] &= ~Mask;
        }

        const ::int32 NewIndex = m_numBits;
        ++m_numBits;
        return NewIndex;
    }

    void TBitArray::RemoveAt(::int32 Index) noexcept
    {
        XPACT_CHECK(IsValidIndex(Index));

        const ::int32 WordIdx = Index / BitsPerWord;
        const ::int32 BitIdx  = Index % BitsPerWord;
        const ::int32 LastWordIdx = (m_numBits - 1) / BitsPerWord;

        // Step 1: shift bits within the starting word.
        //
        // Bits [BitIdx + 1, 63] of m_words[WordIdx] move down by one,
        // becoming bits [BitIdx, 62]. Bit 63 of m_words[WordIdx] is
        // filled by bit 0 of m_words[WordIdx + 1] in step 2.
        {
            const ::uint64 KeepLow  = m_words[WordIdx] & BitArrayPrivate::LowBitsMask(BitIdx);
            const ::uint64 ShiftHigh = (m_words[WordIdx] >> (BitIdx + 1)) << BitIdx;
            m_words[WordIdx] = KeepLow | ShiftHigh;
        }

        // Step 2: cascade-shift the remaining words.
        //
        // For each word at index > WordIdx, bit 0 of that word becomes
        // bit 63 of the previous word, and the word is shifted right by
        // one (bringing bit 0 into bit-63 slot for next iteration).
        for (::int32 W = WordIdx + 1; W <= LastWordIdx; ++W)
        {
            // Bit 0 of m_words[W] -> bit 63 of m_words[W - 1].
            m_words[W - 1] |= (m_words[W] & ::uint64{1}) << 63;

            // Shift m_words[W] right by 1 (the bit-0 we just pulled is
            // discarded; the high bit gets a zero-fill, which is the
            // correct semantic for the highest bit of a shrunk array).
            m_words[W] >>= 1;
        }

        --m_numBits;

        // If we removed the only bit in the last word, drop the word.
        if (m_numBits > 0 && BitsToWords(m_numBits) < m_words.Num())
        {
            // Remove the trailing zero word. Using RemoveAt on the
            // last index avoids a copy.
            m_words.RemoveAt(m_words.Num() - 1);
        }
        else if (m_numBits == 0)
        {
            // Drop all words.
            m_words.Reset(0);
        }

        NormalizeTrailingBits();
    }

    // =================================================================
    // Queries.
    // =================================================================

    ::int32 TBitArray::FindFirstSet(::int32 StartIndex) const noexcept
    {
        if (StartIndex < 0)
        {
            StartIndex = 0;
        }
        if (StartIndex >= m_numBits)
        {
            return INDEX_NONE;
        }

        const ::int32 NumWords = m_words.Num();
        if (NumWords == 0)
        {
            return INDEX_NONE;
        }

        // Walk word-by-word. The first word may have a "start-bit-offset"
        // mask applied so bits before StartIndex don't match.
        ::int32 WordIdx = StartIndex / BitsPerWord;
        ::int32 BitIdx  = StartIndex % BitsPerWord;

        // First-word case: mask off bits below BitIdx so the bit-scan
        // doesn't return them.
        ::uint64 Word = m_words[WordIdx];
        if (BitIdx > 0)
        {
            Word &= ~BitArrayPrivate::LowBitsMask(BitIdx);
        }
        if (Word != 0)
        {
            const ::int32 LocalBit = BitArrayPrivate::CountTrailingZeros64(Word);
            const ::int32 GlobalBit = WordIdx * BitsPerWord + LocalBit;
            return GlobalBit < m_numBits ? GlobalBit : INDEX_NONE;
        }

        // Subsequent words: scan for any nonzero word, then bit-scan.
        for (++WordIdx; WordIdx < NumWords; ++WordIdx)
        {
            Word = m_words[WordIdx];
            if (Word != 0)
            {
                const ::int32 LocalBit  = BitArrayPrivate::CountTrailingZeros64(Word);
                const ::int32 GlobalBit = WordIdx * BitsPerWord + LocalBit;
                return GlobalBit < m_numBits ? GlobalBit : INDEX_NONE;
            }
        }

        return INDEX_NONE;
    }

    ::int32 TBitArray::FindFirstClear(::int32 StartIndex) const noexcept
    {
        if (StartIndex < 0)
        {
            StartIndex = 0;
        }
        if (StartIndex >= m_numBits)
        {
            return INDEX_NONE;
        }

        const ::int32 NumWords = m_words.Num();
        if (NumWords == 0)
        {
            return INDEX_NONE;
        }

        // Walk word-by-word looking for a zero bit. The inverted word
        // (~Word) has its zeros where the original had ones; bit-scan-
        // forward on the inversion finds the lowest zero in the
        // original.
        ::int32 WordIdx = StartIndex / BitsPerWord;
        ::int32 BitIdx  = StartIndex % BitsPerWord;

        // First-word case: mask off bits below BitIdx in the INVERSION
        // so we don't match a bit before StartIndex.
        ::uint64 Inverted = ~m_words[WordIdx];
        if (BitIdx > 0)
        {
            Inverted &= ~BitArrayPrivate::LowBitsMask(BitIdx);
        }
        if (Inverted != 0)
        {
            const ::int32 LocalBit  = BitArrayPrivate::CountTrailingZeros64(Inverted);
            const ::int32 GlobalBit = WordIdx * BitsPerWord + LocalBit;
            // The trailing bits past m_numBits are normalized to 0, so
            // ~word has them as 1 -- they would match. Bound-check the
            // result.
            return GlobalBit < m_numBits ? GlobalBit : INDEX_NONE;
        }

        // Subsequent words.
        for (++WordIdx; WordIdx < NumWords; ++WordIdx)
        {
            Inverted = ~m_words[WordIdx];
            if (Inverted != 0)
            {
                const ::int32 LocalBit  = BitArrayPrivate::CountTrailingZeros64(Inverted);
                const ::int32 GlobalBit = WordIdx * BitsPerWord + LocalBit;
                return GlobalBit < m_numBits ? GlobalBit : INDEX_NONE;
            }
        }

        return INDEX_NONE;
    }

    ::int32 TBitArray::CountSetBits() const noexcept
    {
        const ::int32 NumWords = m_words.Num();
        if (NumWords == 0 || m_numBits == 0)
        {
            return 0;
        }

        // Sum popcount across every full word. The last word may have
        // trailing don't-care bits; they are normalized to 0 by
        // NormalizeTrailingBits at every mutation site, so the popcount
        // is exact without an additional mask here. (Defensive masking
        // would be possible but is redundant given the normalization
        // invariant.)
        ::int32 Total = 0;
        for (::int32 W = 0; W < NumWords; ++W)
        {
            Total += BitArrayPrivate::PopCount64(m_words[W]);
        }
        return Total;
    }

    // =================================================================
    // Helpers.
    // =================================================================

    void TBitArray::NormalizeTrailingBits() noexcept
    {
        if (m_numBits == 0)
        {
            return;
        }
        const ::int32 NumWords = m_words.Num();
        if (NumWords == 0)
        {
            return;
        }

        // The last word's "live" bits are bit 0 to bit (m_numBits-1) % 64.
        // Bits above that are "don't care"; zero them.
        const ::int32 LastWordIdx = NumWords - 1;
        const ::int32 LiveBitsInLastWord = m_numBits - LastWordIdx * BitsPerWord;
        if (LiveBitsInLastWord < BitsPerWord)
        {
            const ::uint64 KeepMask = BitArrayPrivate::LowBitsMask(LiveBitsInLastWord);
            m_words[LastWordIdx] &= KeepMask;
        }
    }

} // namespace XCore
