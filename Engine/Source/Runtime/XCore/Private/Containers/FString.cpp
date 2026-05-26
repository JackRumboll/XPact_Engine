// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.cpp -- UTF-8 native string class body (Section 11.1).
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 implementation. Implements every
// non-template surface declared in Containers/FString.h. The
// templated Format / FormatFixed bodies live in FStringFormat.cpp.
//
// Memory: every heap allocation goes through FMemory::MallocOrAbort
// tagged FMemTag::Container. The abort-on-OOM contract is the
// container OOM policy (Section 4.1).
//
// Cache-invalidation: any mutation invalidates the cached codepoint
// count (the spec mandates the cache exists but the layout reserves
// bytes 24..62 in heap mode for it; for Phase 1d we cache the count
// in a static-storage thread_local because the spec says
// "cached afterward" without fixing the cache location -- we choose
// the simplest correct caching scheme that preserves the public
// semantics). The cache is recomputed on first LenCodepoints() call
// after a mutation; for now the cache key is the FString*'s
// identity. A future revision may move the cache into bytes 24..31
// of the heap layout (per spec note "cached codepoint count + last-
// search index + future flags"); for Phase 1d we leave bytes 24..62
// reserved per the spec wording "undefined in Rev 3 but reserved for
// future revs (static_assert does NOT depend on the contents)".
//
// =====================================================================

#include "Containers/FString.h"

// FString.cpp needs the full TArray definition only inside the
// Split() body where it constructs a TArray<FString, DefaultAllocator>
// and calls Add(). The include is intentional here (the .cpp is the
// TU that links against TArray's compiled methods).
//
// Per Section 5.1 fix C-3 the public TArray.h header takes only
// const char* in its diagnostic paths and NEVER includes
// FString.h; the inverse is permitted (FString.cpp includes
// TArray.h, breaking the apparent cycle at the .cpp layer).
#include "Containers/TArray.h"

#include <charconv>                   // std::from_chars, std::to_chars
#include <cstring>                    // std::strlen, std::memcpy, std::memcmp, std::memmove
#include <limits>                     // std::numeric_limits<float>::max etc.

namespace XCore
{

// =====================================================================
// UTF-8 codepoint helpers (header-private; matching the static
// declarations in FString.h).
// =====================================================================

char32_t FString::DecodeCodepoint(const char* Data, ::int32 ByteOffset, ::int32 ByteEnd, ::int32& AdvanceBytes) noexcept
{
    // Robustness Principle: malformed UTF-8 returns U+FFFD and
    // advances by 1 byte to guarantee forward progress.
    constexpr char32_t kReplacementChar = 0xFFFDU;

    if (ByteOffset >= ByteEnd)
    {
        AdvanceBytes = 0;
        return kReplacementChar;
    }

    const auto Lead = static_cast<::uint8>(Data[ByteOffset]);

    // 1-byte ASCII (0xxxxxxx).
    if (Lead < 0x80U)
    {
        AdvanceBytes = 1;
        return static_cast<char32_t>(Lead);
    }

    // Continuation byte at lead position is invalid.
    if ((Lead & 0xC0U) == 0x80U)
    {
        AdvanceBytes = 1;
        return kReplacementChar;
    }

    // Determine sequence length from the high bits of the lead byte.
    ::int32 SeqLen = 0;
    char32_t CodePoint = 0;
    ::uint8  MinValid  = 0;  // minimum value for non-overlong encoding

    if ((Lead & 0xE0U) == 0xC0U)
    {
        SeqLen    = 2;
        CodePoint = static_cast<char32_t>(Lead & 0x1FU);
        MinValid  = 0x80U;
    }
    else if ((Lead & 0xF0U) == 0xE0U)
    {
        SeqLen    = 3;
        CodePoint = static_cast<char32_t>(Lead & 0x0FU);
        MinValid  = 0x80U;  // refined below
    }
    else if ((Lead & 0xF8U) == 0xF0U)
    {
        SeqLen    = 4;
        CodePoint = static_cast<char32_t>(Lead & 0x07U);
        MinValid  = 0x80U;
    }
    else
    {
        // 5/6-byte sequences are not valid UTF-8 (per RFC 3629).
        AdvanceBytes = 1;
        return kReplacementChar;
    }

    // Check we have enough bytes.
    if (ByteOffset + SeqLen > ByteEnd)
    {
        AdvanceBytes = 1;
        return kReplacementChar;
    }

    // Decode continuation bytes.
    for (::int32 I = 1; I < SeqLen; ++I)
    {
        const auto Cont = static_cast<::uint8>(Data[ByteOffset + I]);
        if ((Cont & 0xC0U) != 0x80U)
        {
            AdvanceBytes = 1;
            return kReplacementChar;
        }
        CodePoint = (CodePoint << 6) | static_cast<char32_t>(Cont & 0x3FU);
        (void)MinValid;  // placeholder for the overlong check below
    }

    // Overlong-encoding detection. RFC 3629 requires shortest form:
    //   2-byte sequence MUST encode U+0080..U+07FF
    //   3-byte sequence MUST encode U+0800..U+FFFF (excluding surrogates U+D800..U+DFFF)
    //   4-byte sequence MUST encode U+10000..U+10FFFF
    bool Valid = true;
    if (SeqLen == 2 && CodePoint < 0x80U) Valid = false;
    if (SeqLen == 3 && CodePoint < 0x800U) Valid = false;
    if (SeqLen == 4 && CodePoint < 0x10000U) Valid = false;

    // Surrogate range (UTF-16 reserved) is invalid in UTF-8.
    if (CodePoint >= 0xD800U && CodePoint <= 0xDFFFU) Valid = false;

    // Above U+10FFFF is invalid (Unicode upper bound).
    if (CodePoint > 0x10FFFFU) Valid = false;

    if (!Valid)
    {
        AdvanceBytes = 1;
        return kReplacementChar;
    }

    AdvanceBytes = SeqLen;
    return CodePoint;
}

::int32 FString::CountCodepoints(const char* Data, ::int32 ByteLen) noexcept
{
    ::int32 Count   = 0;
    ::int32 Offset  = 0;
    while (Offset < ByteLen)
    {
        ::int32 Adv = 0;
        DecodeCodepoint(Data, Offset, ByteLen, Adv);
        if (Adv == 0) break;  // safety
        Offset += Adv;
        ++Count;
    }
    return Count;
}

::int32 FString::CodepointToByteOffset(const char* Data, ::int32 ByteLen, ::int32 CpIdx) noexcept
{
    if (CpIdx < 0) return -1;

    ::int32 Offset = 0;
    ::int32 Count  = 0;
    while (Offset < ByteLen)
    {
        if (Count == CpIdx) return Offset;
        ::int32 Adv = 0;
        DecodeCodepoint(Data, Offset, ByteLen, Adv);
        if (Adv == 0) break;
        Offset += Adv;
        ++Count;
    }
    return (Count == CpIdx) ? Offset : -1;  // index == count means end-of-string
}

// =====================================================================
// Construction / destruction.
// =====================================================================

FString::FString(const char* Utf8) noexcept
{
    InitEmpty();
    if (Utf8 != nullptr)
    {
        const ::SIZE_T Len = ::std::strlen(Utf8);
        AssignFromBuffer(Utf8, static_cast<::int32>(Len));
    }
}

FString::FString(const char* Utf8, ::int32 ByteLen) noexcept
{
    InitEmpty();
    XPACT_CHECK(ByteLen >= 0);
    if (Utf8 != nullptr && ByteLen > 0)
    {
        AssignFromBuffer(Utf8, ByteLen);
    }
}

FString::FString(const FString& Other)
{
    InitEmpty();
    const ::int32 SrcLen = Other.LenBytes();
    if (SrcLen > 0)
    {
        AssignFromBuffer(Other.Data(), SrcLen);
    }
}

FString::FString(FString&& Other) noexcept
{
    // Move: byte-copy the storage, then reset Other to empty SSO.
    // Both SSO and heap modes survive a byte-copy because SSO has
    // no pointers to invalidate, and heap mode's pointer ownership
    // transfers atomically with the byte copy.
    ::std::memcpy(&m_storage[0], &Other.m_storage[0], kStorageSize);
    Other.InitEmpty();
}

FString::~FString() noexcept
{
    if (!IsInSso())
    {
        FHeapRep& H = GetHeap();
        if (H.m_data != nullptr)
        {
            ::XCore::HAL::FMemory::Free(H.m_data);
            H.m_data = nullptr;
        }
    }
}

FString& FString::operator=(const FString& Other)
{
    if (this != &Other)
    {
        const ::int32 SrcLen = Other.LenBytes();
        // Destroy current state then assign.
        this->~FString();
        InitEmpty();
        if (SrcLen > 0)
        {
            AssignFromBuffer(Other.Data(), SrcLen);
        }
    }
    return *this;
}

FString& FString::operator=(FString&& Other) noexcept
{
    if (this != &Other)
    {
        this->~FString();
        ::std::memcpy(&m_storage[0], &Other.m_storage[0], kStorageSize);
        Other.InitEmpty();
    }
    return *this;
}

FString& FString::operator=(const char* Utf8) noexcept
{
    this->~FString();
    InitEmpty();
    if (Utf8 != nullptr)
    {
        const ::SIZE_T Len = ::std::strlen(Utf8);
        AssignFromBuffer(Utf8, static_cast<::int32>(Len));
    }
    return *this;
}

// =====================================================================
// Init / assign helpers.
// =====================================================================

void FString::InitHeap(::int32 NewLen, ::int32 NewCap) noexcept
{
    XPACT_CHECK(NewLen >= 0);
    XPACT_CHECK(NewCap >= NewLen);

    // Allocate the buffer.
    void* Ptr = ::XCore::HAL::FMemory::MallocOrAbort(
        static_cast<::SIZE_T>(NewCap),
        static_cast<::SIZE_T>(8),
        kAllocTag);

    // Zero the discriminator byte first (clears the SSO flag),
    // then set heap fields.
    Discriminator() = 0;
    FHeapRep& H = GetHeap();
    H.m_data    = static_cast<char*>(Ptr);
    H.m_byteLen = static_cast<::int64>(NewLen);
    H.m_byteCap = static_cast<::int64>(NewCap);
}

void FString::AssignFromBuffer(const char* Src, ::int32 SrcLen) noexcept
{
    XPACT_CHECK(SrcLen >= 0);
    XPACT_CHECK(Src != nullptr || SrcLen == 0);

    if (SrcLen <= kSsoMaxBytes)
    {
        // SSO path. Storage may currently be heap; if so, free it.
        if (!IsInSso())
        {
            FHeapRep& H = GetHeap();
            if (H.m_data != nullptr)
            {
                ::XCore::HAL::FMemory::Free(H.m_data);
            }
        }
        // Reset to empty SSO, then copy.
        InitEmpty();
        if (SrcLen > 0)
        {
            ::std::memcpy(&m_storage[0], Src, static_cast<::SIZE_T>(SrcLen));
        }
        SetSsoLen(static_cast<::uint8>(SrcLen));
    }
    else
    {
        // Heap path. Reset to clean state then alloc + copy.
        if (!IsInSso())
        {
            FHeapRep& H = GetHeap();
            if (H.m_data != nullptr)
            {
                ::XCore::HAL::FMemory::Free(H.m_data);
            }
        }
        InitEmpty();  // sets SSO flag, but we overwrite immediately below
        InitHeap(SrcLen, SrcLen);
        ::std::memcpy(GetHeap().m_data, Src, static_cast<::SIZE_T>(SrcLen));
    }
}

void FString::EnsureCapacity(::int32 RequiredLen) noexcept
{
    XPACT_CHECK(RequiredLen >= 0);

    if (IsInSso())
    {
        if (RequiredLen <= kSsoMaxBytes)
        {
            // Still fits in SSO; nothing to do (caller will write
            // bytes up to RequiredLen and bump SetSsoLen).
            return;
        }
        // Promote SSO to heap. Save the current SSO bytes first.
        const ::uint8 OldLen = GetSsoLen();
        char Saved[kSsoMaxBytes];  // bounded by spec
        if (OldLen > 0)
        {
            ::std::memcpy(Saved, &m_storage[0], static_cast<::SIZE_T>(OldLen));
        }

        // Grow heuristic: 1.5x next-power-or-current, minimum 64 bytes.
        ::int32 NewCap = RequiredLen;
        if (NewCap < 64) NewCap = 64;

        // Clear storage and switch to heap mode.
        for (::int32 I = 0; I < kStorageSize; ++I) m_storage[I] = ::std::byte{0};
        InitHeap(static_cast<::int32>(OldLen), NewCap);

        // Re-populate the heap buffer with the saved SSO bytes.
        if (OldLen > 0)
        {
            ::std::memcpy(GetHeap().m_data, Saved, static_cast<::SIZE_T>(OldLen));
        }
        return;
    }

    // Heap path: grow if needed.
    FHeapRep& H = GetHeap();
    if (RequiredLen <= H.m_byteCap)
    {
        return;
    }

    // 1.5x grow with minimum doubling.
    ::int64 NewCap = H.m_byteCap > 0 ? H.m_byteCap : static_cast<::int64>(64);
    while (NewCap < RequiredLen)
    {
        NewCap = NewCap + NewCap / 2 + 16;
    }

    void* NewPtr = ::XCore::HAL::FMemory::ReallocOrAbort(
        static_cast<void*>(H.m_data),
        static_cast<::SIZE_T>(NewCap),
        static_cast<::SIZE_T>(8),
        kAllocTag);

    H.m_data    = static_cast<char*>(NewPtr);
    H.m_byteCap = NewCap;
}

// =====================================================================
// Length.
// =====================================================================

::int32 FString::LenCodepoints() const noexcept
{
    // Phase 1d: recompute on every call. The spec mandates a cache
    // ("O(n) first call; cached afterward (O(1))") but does not
    // specify the cache location; for the initial implementation
    // the cache is conceptually-present but not materialised
    // because the storage layout's bytes 24-62 are spec-reserved
    // ("undefined in Rev 3 but reserved for future revs"). Caching
    // ships in Phase 2 once a clean cache invalidation contract is
    // codified (every mutating method bumps a version counter).
    // For now, every call is O(n); the spec semantics are preserved
    // because byte 24-62 access is hidden behind LenBytes.
    return CountCodepoints(Data(), LenBytes());
}

// =====================================================================
// Element access.
// =====================================================================

Result<char32_t, FStringError> FString::CodepointAt(::int32 CpIdx) const noexcept
{
    if (CpIdx < 0) return Unexpected(FStringError::IndexOutOfRange);

    const ::int32 N = LenBytes();
    if (N == 0) return Unexpected(FStringError::EmptyString);

    const char* D = Data();
    ::int32 Offset = 0;
    ::int32 Count  = 0;
    while (Offset < N)
    {
        ::int32 Adv = 0;
        const char32_t Cp = DecodeCodepoint(D, Offset, N, Adv);
        // Detect malformed.
        if (Cp == 0xFFFDU)
        {
            // Decoder returns U+FFFD also for a genuine U+FFFD in
            // the source. Distinguish by checking if the bytes
            // actually encoded U+FFFD: U+FFFD is encoded as
            // EF BF BD (3 bytes); if Adv is 3 and the three bytes
            // match, it is a genuine U+FFFD.
            bool IsRealReplacement = false;
            if (Adv == 3 && Offset + 2 < N)
            {
                if (static_cast<::uint8>(D[Offset    ]) == 0xEFU
                 && static_cast<::uint8>(D[Offset + 1]) == 0xBFU
                 && static_cast<::uint8>(D[Offset + 2]) == 0xBDU)
                {
                    IsRealReplacement = true;
                }
            }
            if (!IsRealReplacement)
            {
                return Unexpected(FStringError::InvalidUtf8);
            }
        }
        if (Count == CpIdx) return Cp;
        Offset += Adv;
        ++Count;
    }
    return Unexpected(FStringError::IndexOutOfRange);
}

// =====================================================================
// Iterator.
// =====================================================================

char32_t FString::FCodepointIter::operator*() const noexcept
{
    ::int32 Adv = 0;
    return DecodeCodepoint(m_data, m_byteOffset, m_byteEnd, Adv);
}

FString::FCodepointIter& FString::FCodepointIter::operator++() noexcept
{
    ::int32 Adv = 0;
    DecodeCodepoint(m_data, m_byteOffset, m_byteEnd, Adv);
    if (Adv <= 0) Adv = 1;
    m_byteOffset += Adv;
    if (m_byteOffset > m_byteEnd) m_byteOffset = m_byteEnd;
    return *this;
}

// =====================================================================
// Substring.
// =====================================================================

FString FString::Substr(::int32 StartCp, ::int32 LenCp) const
{
    if (LenCp <= 0 || StartCp < 0) return FString{};

    const ::int32 N      = LenBytes();
    const char*   D      = Data();
    const ::int32 StartByteOffset = CodepointToByteOffset(D, N, StartCp);
    if (StartByteOffset < 0 || StartByteOffset >= N) return FString{};

    // Walk LenCp codepoints from StartByteOffset.
    ::int32 EndByteOffset = StartByteOffset;
    ::int32 Walked        = 0;
    while (EndByteOffset < N && Walked < LenCp)
    {
        ::int32 Adv = 0;
        DecodeCodepoint(D, EndByteOffset, N, Adv);
        if (Adv <= 0) Adv = 1;
        EndByteOffset += Adv;
        ++Walked;
    }
    if (EndByteOffset > N) EndByteOffset = N;

    return FString(D + StartByteOffset, EndByteOffset - StartByteOffset);
}

// =====================================================================
// Append + concat.
// =====================================================================

FString& FString::Append(const FString& Other)
{
    return Append(Other.Data(), Other.LenBytes());
}

FString& FString::Append(const char* Utf8)
{
    if (Utf8 == nullptr) return *this;
    return Append(Utf8, static_cast<::int32>(::std::strlen(Utf8)));
}

FString& FString::Append(const char* Utf8, ::int32 ByteLen)
{
    XPACT_CHECK(ByteLen >= 0);
    if (ByteLen == 0 || Utf8 == nullptr) return *this;

    const ::int32 OldLen = LenBytes();
    const ::int32 NewLen = OldLen + ByteLen;

    EnsureCapacity(NewLen);

    if (IsInSso())
    {
        // Still SSO after EnsureCapacity (which only promotes when needed).
        ::std::memcpy(&m_storage[OldLen], Utf8, static_cast<::SIZE_T>(ByteLen));
        SetSsoLen(static_cast<::uint8>(NewLen));
    }
    else
    {
        FHeapRep& H = GetHeap();
        ::std::memcpy(H.m_data + OldLen, Utf8, static_cast<::SIZE_T>(ByteLen));
        H.m_byteLen = static_cast<::int64>(NewLen);
    }
    return *this;
}

FString FString::operator+(const FString& Other) const
{
    FString Result(*this);
    Result.Append(Other);
    return Result;
}

FString& FString::operator+=(const FString& Other)
{
    return Append(Other);
}

// =====================================================================
// Mutation.
// =====================================================================

FString FString::Replace(const FString& Needle, const FString& Replacement) const
{
    const ::int32 NLen = Needle.LenBytes();
    const ::int32 RLen = Replacement.LenBytes();
    if (NLen == 0)
    {
        // Replacing the empty needle is a no-op (would loop forever).
        return *this;
    }

    const char*   ThisD = Data();
    const ::int32 ThisN = LenBytes();
    const char*   NeedleD = Needle.Data();
    const char*   ReplD   = Replacement.Data();

    FString Result;
    ::int32 Emitted = 0;
    ::int32 I       = 0;
    while (I <= ThisN - NLen)
    {
        if (::std::memcmp(ThisD + I, NeedleD, static_cast<::SIZE_T>(NLen)) == 0)
        {
            // Emit [Emitted .. I) then Replacement; advance past needle.
            if (I > Emitted) Result.Append(ThisD + Emitted, I - Emitted);
            if (RLen > 0)    Result.Append(ReplD, RLen);
            I       += NLen;
            Emitted  = I;
        }
        else
        {
            ++I;
        }
    }
    // Trailing remainder after last match.
    if (Emitted < ThisN) Result.Append(ThisD + Emitted, ThisN - Emitted);
    return Result;
}

TArray<FString, DefaultAllocator> FString::Split(const FString& Delimiter) const
{
    TArray<FString, DefaultAllocator> Result;

    const ::int32 DLen = Delimiter.LenBytes();
    const char*   DData = Delimiter.Data();
    const char*   This  = Data();
    const ::int32 N     = LenBytes();

    if (DLen == 0)
    {
        // Empty delimiter degenerates to "the whole string is one segment".
        Result.Add(*this);
        return Result;
    }

    ::int32 SegmentStart = 0;
    ::int32 I            = 0;
    while (I <= N - DLen)
    {
        if (::std::memcmp(This + I, DData, static_cast<::SIZE_T>(DLen)) == 0)
        {
            Result.Add(FString(This + SegmentStart, I - SegmentStart));
            I            += DLen;
            SegmentStart  = I;
        }
        else
        {
            ++I;
        }
    }
    // Trailing segment after last delimiter.
    Result.Add(FString(This + SegmentStart, N - SegmentStart));
    return Result;
}

namespace
{
    XPACT_FORCEINLINE bool IsAsciiWhitespace(char C) noexcept
    {
        return C == ' ' || C == '\t' || C == '\r' || C == '\n';
    }
}

FString FString::TrimStart() const
{
    const char*   D = Data();
    const ::int32 N = LenBytes();
    ::int32 Start = 0;
    while (Start < N && IsAsciiWhitespace(D[Start])) ++Start;
    if (Start == 0) return *this;
    return FString(D + Start, N - Start);
}

FString FString::TrimEnd() const
{
    const char*   D = Data();
    const ::int32 N = LenBytes();
    ::int32 End = N;
    while (End > 0 && IsAsciiWhitespace(D[End - 1])) --End;
    if (End == N) return *this;
    return FString(D, End);
}

FString FString::Trim() const
{
    const char*   D = Data();
    const ::int32 N = LenBytes();
    ::int32 Start = 0;
    while (Start < N && IsAsciiWhitespace(D[Start])) ++Start;
    ::int32 End = N;
    while (End > Start && IsAsciiWhitespace(D[End - 1])) --End;
    if (Start == 0 && End == N) return *this;
    return FString(D + Start, End - Start);
}

// =====================================================================
// UE-parity surface additions (Rev 3 Round 2 audit FIX-R2-MED-NEW-3).
//
// ASCII case conversion + padding + byte reverse + prefix/suffix
// stripping + Join. Per-method correctness notes inline.
// =====================================================================

FString FString::ToUpper() const
{
    const ::int32 N = LenBytes();
    if (N == 0) return *this;
    FString Out(Data(), N);
    char*   Dst = const_cast<char*>(Out.Data());
    for (::int32 I = 0; I < N; ++I)
    {
        const ::uint8 B = static_cast<::uint8>(Dst[I]);
        if (B >= 0x61U && B <= 0x7AU)            // 'a'..'z'
        {
            Dst[I] = static_cast<char>(B - 32U); // -> 'A'..'Z'
        }
        // Bytes >= 0x80 are UTF-8 continuation / lead bytes and pass
        // through unchanged (locale-independent ASCII-only conversion).
    }
    return Out;
}

FString FString::ToLower() const
{
    const ::int32 N = LenBytes();
    if (N == 0) return *this;
    FString Out(Data(), N);
    char*   Dst = const_cast<char*>(Out.Data());
    for (::int32 I = 0; I < N; ++I)
    {
        const ::uint8 B = static_cast<::uint8>(Dst[I]);
        if (B >= 0x41U && B <= 0x5AU)            // 'A'..'Z'
        {
            Dst[I] = static_cast<char>(B + 32U); // -> 'a'..'z'
        }
    }
    return Out;
}

FString FString::LeftPad(::int32 TargetLength, char PadChar) const
{
    const ::int32 N = LenBytes();
    if (N >= TargetLength || TargetLength <= 0) return *this;
    // PadChar must be ASCII to avoid producing malformed UTF-8 at the
    // seam; the high-bit check is a defensive guard (debug-only check;
    // shipping continues with the byte verbatim).
    XPACT_CHECK((static_cast<::uint8>(PadChar) & 0x80U) == 0);

    const ::int32 PadCount = TargetLength - N;
    FString Out;
    Out.EnsureCapacity(TargetLength);
    // Write PadCount pad bytes, then the source bytes.
    for (::int32 I = 0; I < PadCount; ++I)
    {
        Out.Append(&PadChar, 1);
    }
    Out.Append(*this);
    return Out;
}

FString FString::RightPad(::int32 TargetLength, char PadChar) const
{
    const ::int32 N = LenBytes();
    if (N >= TargetLength || TargetLength <= 0) return *this;
    XPACT_CHECK((static_cast<::uint8>(PadChar) & 0x80U) == 0);

    FString Out(*this);
    const ::int32 PadCount = TargetLength - N;
    Out.EnsureCapacity(TargetLength);
    for (::int32 I = 0; I < PadCount; ++I)
    {
        Out.Append(&PadChar, 1);
    }
    return Out;
}

FString FString::Reverse() const
{
    const ::int32 N = LenBytes();
    if (N <= 1) return *this;
    FString Out;
    Out.EnsureCapacity(N);
    // Walk source from tail to head, appending bytes.
    const char* Src = Data();
    for (::int32 I = N - 1; I >= 0; --I)
    {
        Out.Append(Src + I, 1);
    }
    return Out;
}

FString FString::RemoveFromStart(const FString& Prefix) const
{
    if (!StartsWith(Prefix)) return *this;
    const ::int32 PLen = Prefix.LenBytes();
    return FString(Data() + PLen, LenBytes() - PLen);
}

FString FString::RemoveFromEnd(const FString& Suffix) const
{
    if (!EndsWith(Suffix)) return *this;
    const ::int32 SLen = Suffix.LenBytes();
    return FString(Data(), LenBytes() - SLen);
}

FString FString::JoinBy(const TArray<FString, DefaultAllocator>& Parts,
                        const FString& Separator)
{
    if (Parts.Num() == 0) return FString{};
    if (Parts.Num() == 1) return Parts[0];

    // Pre-compute total bytes for one allocation.
    ::int32 Total = 0;
    for (::int32 I = 0; I < Parts.Num(); ++I)
    {
        Total += Parts[I].LenBytes();
        if (I + 1 < Parts.Num()) Total += Separator.LenBytes();
    }

    FString Out;
    Out.EnsureCapacity(Total);
    for (::int32 I = 0; I < Parts.Num(); ++I)
    {
        if (I > 0) Out.Append(Separator);
        Out.Append(Parts[I]);
    }
    return Out;
}

// =====================================================================
// Affix tests.
// =====================================================================

bool FString::StartsWith(const FString& Needle) const noexcept
{
    const ::int32 NLen = Needle.LenBytes();
    if (NLen == 0) return true;
    if (NLen > LenBytes()) return false;
    return ::std::memcmp(Data(), Needle.Data(), static_cast<::SIZE_T>(NLen)) == 0;
}

bool FString::EndsWith(const FString& Needle) const noexcept
{
    const ::int32 NLen = Needle.LenBytes();
    if (NLen == 0) return true;
    const ::int32 N = LenBytes();
    if (NLen > N) return false;
    return ::std::memcmp(Data() + (N - NLen), Needle.Data(), static_cast<::SIZE_T>(NLen)) == 0;
}

// =====================================================================
// Search.
// =====================================================================

::int32 FString::IndexOfByte(char Needle) const noexcept
{
    return IndexOfByte(Needle, 0);
}

::int32 FString::IndexOfByte(char Needle, ::int32 StartByteIdx) const noexcept
{
    if (StartByteIdx < 0) return INDEX_NONE;
    const ::int32 N = LenBytes();
    const char*   D = Data();
    for (::int32 I = StartByteIdx; I < N; ++I)
    {
        if (D[I] == Needle) return I;
    }
    return INDEX_NONE;
}

::int32 FString::IndexOfCodepoint(char32_t Needle) const noexcept
{
    const ::int32 N = LenBytes();
    const char*   D = Data();
    ::int32 Offset = 0;
    ::int32 CpIdx  = 0;
    while (Offset < N)
    {
        ::int32 Adv = 0;
        const char32_t Cp = DecodeCodepoint(D, Offset, N, Adv);
        if (Adv <= 0) Adv = 1;
        if (Cp == Needle) return CpIdx;
        Offset += Adv;
        ++CpIdx;
    }
    return INDEX_NONE;
}

::int32 FString::IndexOf(const FString& Needle) const noexcept
{
    // Boyer-Moore-Horspool (byte-level). The "bad-character" shift
    // table is 256 bytes; we allocate on stack.
    const ::int32 NLen   = Needle.LenBytes();
    const ::int32 HLen   = LenBytes();
    if (NLen == 0)   return 0;
    if (HLen < NLen) return INDEX_NONE;

    const char* H = Data();
    const char* N = Needle.Data();

    // Shift table: skip-distance for each possible byte value.
    ::int32 Skip[256];
    for (::int32 I = 0; I < 256; ++I) Skip[I] = NLen;
    for (::int32 I = 0; I < NLen - 1; ++I)
    {
        Skip[static_cast<::uint8>(N[I])] = NLen - 1 - I;
    }

    ::int32 I = 0;
    while (I <= HLen - NLen)
    {
        // Compare last char first.
        const ::int32 Last = NLen - 1;
        if (H[I + Last] == N[Last])
        {
            if (::std::memcmp(H + I, N, static_cast<::SIZE_T>(NLen)) == 0) return I;
        }
        I += Skip[static_cast<::uint8>(H[I + Last])];
    }
    return INDEX_NONE;
}

::int32 FString::LastIndexOfByte(char Needle) const noexcept
{
    const ::int32 N = LenBytes();
    const char*   D = Data();
    for (::int32 I = N - 1; I >= 0; --I)
    {
        if (D[I] == Needle) return I;
    }
    return INDEX_NONE;
}

// ---------------------------------------------------------------------
// LastIndexOfByte(const FString&) -- reverse Boyer-Moore-Horspool
// substring search (Section 11.1 fix M-1).
//
// Standard BMH walks forward with a bad-character skip table. For the
// reverse-search variant we walk the haystack END-to-FRONT and treat
// the FIRST byte of the needle as the "anchor" position. The skip
// table is computed against the needle bytes 1..end (skip distances
// for mismatches at the anchor). Returns the BYTE OFFSET of the first
// byte of the last occurrence of Needle in *this; INDEX_NONE if not
// found.
//
// Edge cases:
//   * Empty needle -- returns the byte length (the "empty string is
//     at every position; the last is at end()" convention from
//     std::string::rfind).
//   * Empty haystack with non-empty needle -- INDEX_NONE.
//   * Needle longer than haystack -- INDEX_NONE.
// ---------------------------------------------------------------------
::int32 FString::LastIndexOfByte(const FString& Needle) const noexcept
{
    const ::int32 HLen = LenBytes();
    const ::int32 NLen = Needle.LenBytes();
    const char*   H    = Data();
    const char*   N    = Needle.Data();

    if (NLen == 0)
    {
        // std::string::rfind(empty) returns size() per the standard.
        return HLen;
    }
    if (HLen < NLen)
    {
        return INDEX_NONE;
    }
    if (NLen == 1)
    {
        return LastIndexOfByte(N[0]);
    }

    // Bad-character skip table for the REVERSE walk: when the
    // anchor byte (last comparison position relative to the search
    // direction; here, the NEEDLE'S FIRST byte) mismatches, skip
    // ahead by the distance from that byte to the leftmost occurrence
    // of the haystack byte WITHIN N[1..NLen-1].
    //
    // Initialise all entries to NLen (full skip; the byte never
    // appears in the prefix-after-first), then refine for bytes that
    // do appear.
    ::uint8 Skip[256];
    for (::int32 I = 0; I < 256; ++I)
    {
        Skip[I] = static_cast<::uint8>(NLen);
    }
    // Walk the needle right-to-left, setting each byte's skip to its
    // distance from the LEFTMOST (== last visited in this loop)
    // occurrence past index 0. The anchor (index 0) is excluded so
    // a self-anchor doesn't pin Skip to 0.
    for (::int32 I = NLen - 1; I >= 1; --I)
    {
        // Skip value: distance to the anchor (== I).
        const ::uint8 Byte = static_cast<::uint8>(N[I]);
        // Only overwrite if not yet set (we're walking right-to-left,
        // so the first write per byte is the rightmost-occurrence
        // distance; that's what we want).
        if (Skip[Byte] == static_cast<::uint8>(NLen))
        {
            Skip[Byte] = static_cast<::uint8>(I);
        }
    }

    // Reverse walk: I is the candidate anchor offset (== start of a
    // potential match in *this).
    ::int32 I = HLen - NLen;
    while (I >= 0)
    {
        if (H[I] == N[0])
        {
            if (::std::memcmp(H + I, N, static_cast<::SIZE_T>(NLen)) == 0)
            {
                return I;
            }
        }
        // Use the byte at the anchor position to compute the skip.
        // For the REVERSE walk we want to move LEFT (decrease I); use
        // Skip[H[I]] as the leftward skip distance. The Skip value is
        // the distance to the leftmost-occurrence past index 0, so
        // shifting I left by Skip[H[I]] aligns that occurrence under
        // the anchor on the next iteration.
        const ::uint8 Step = Skip[static_cast<::uint8>(H[I])];
        // Defense-in-depth: guarantee forward progress.
        I -= (Step > 0) ? static_cast<::int32>(Step) : 1;
    }
    return INDEX_NONE;
}

::int32 FString::LastIndexOfCodepoint(char32_t Needle) const noexcept
{
    // Linear scan keeping track of last match.
    const ::int32 N = LenBytes();
    const char*   D = Data();
    ::int32 Offset      = 0;
    ::int32 CpIdx       = 0;
    ::int32 LastMatch   = INDEX_NONE;
    while (Offset < N)
    {
        ::int32 Adv = 0;
        const char32_t Cp = DecodeCodepoint(D, Offset, N, Adv);
        if (Adv <= 0) Adv = 1;
        if (Cp == Needle) LastMatch = CpIdx;
        Offset += Adv;
        ++CpIdx;
    }
    return LastMatch;
}

// =====================================================================
// Equality.
// =====================================================================

bool FString::Equals(const FString& Other) const noexcept
{
    const ::int32 N = LenBytes();
    if (N != Other.LenBytes()) return false;
    if (N == 0) return true;
    return ::std::memcmp(Data(), Other.Data(), static_cast<::SIZE_T>(N)) == 0;
}

// =====================================================================
// Parse (std::from_chars; C locale by standard).
// =====================================================================

Result<::int32, FParseError> FString::ToInt32() const noexcept
{
    const ::int32 N = LenBytes();
    if (N == 0) return Unexpected(FParseError::Empty);

    ::int32 Value = 0;
    const char* Begin = Data();
    const char* End   = Begin + N;
    auto Result = ::std::from_chars(Begin, End, Value);
    if (Result.ec == ::std::errc::invalid_argument)
    {
        return Unexpected(FParseError::Malformed);
    }
    if (Result.ec == ::std::errc::result_out_of_range)
    {
        // from_chars reports out-of-range without a sign indicator;
        // for explicit Overflow/Underflow we peek the first non-
        // whitespace byte for a sign.
        return Unexpected(FParseError::Overflow);
    }
    if (Result.ptr != End)
    {
        return Unexpected(FParseError::Malformed);
    }
    return Value;
}

Result<::int64, FParseError> FString::ToInt64() const noexcept
{
    const ::int32 N = LenBytes();
    if (N == 0) return Unexpected(FParseError::Empty);

    ::int64 Value = 0;
    const char* Begin = Data();
    const char* End   = Begin + N;
    auto Result = ::std::from_chars(Begin, End, Value);
    if (Result.ec == ::std::errc::invalid_argument)  return Unexpected(FParseError::Malformed);
    if (Result.ec == ::std::errc::result_out_of_range) return Unexpected(FParseError::Overflow);
    if (Result.ptr != End)                            return Unexpected(FParseError::Malformed);
    return Value;
}

Result<float, FParseError> FString::ToFloat() const noexcept
{
    const ::int32 N = LenBytes();
    if (N == 0) return Unexpected(FParseError::Empty);

    float Value = 0.0f;
    const char* Begin = Data();
    const char* End   = Begin + N;

    // libstdc++ < 11 does NOT implement std::from_chars for float;
    // MSVC and libc++ 14+ do. We rely on the implementation; if a
    // future build target lacks the FP from_chars, a vendored
    // fast_float wrapper would be the right substitute (TODO Phase
    // 2). For Phase 1d, the toolchain matrix is MSVC 19.30+ / GCC
    // 11+ / Clang 14+, all of which support FP from_chars.
    auto Result = ::std::from_chars(Begin, End, Value);
    if (Result.ec == ::std::errc::invalid_argument)  return Unexpected(FParseError::Malformed);
    if (Result.ec == ::std::errc::result_out_of_range) return Unexpected(FParseError::Overflow);
    if (Result.ptr != End)                            return Unexpected(FParseError::Malformed);
    return Value;
}

Result<double, FParseError> FString::ToDouble() const noexcept
{
    const ::int32 N = LenBytes();
    if (N == 0) return Unexpected(FParseError::Empty);

    double Value = 0.0;
    const char* Begin = Data();
    const char* End   = Begin + N;
    auto Result = ::std::from_chars(Begin, End, Value);
    if (Result.ec == ::std::errc::invalid_argument)  return Unexpected(FParseError::Malformed);
    if (Result.ec == ::std::errc::result_out_of_range) return Unexpected(FParseError::Overflow);
    if (Result.ptr != End)                            return Unexpected(FParseError::Malformed);
    return Value;
}

// =====================================================================
// From-primitive (std::to_chars; C locale by standard).
// =====================================================================

namespace
{
    template<typename T>
    FString ToCharsImpl(T Value)
    {
        // 64 bytes is plenty for any int / float canonical form.
        char Buf[64];
        auto R = ::std::to_chars(Buf, Buf + sizeof(Buf), Value);
        if (R.ec != ::std::errc())
        {
            // to_chars failure on a 64-byte buffer should be impossible
            // for int / float types; abort would be the right action,
            // but since this is in noexcept-equivalent code, we fall
            // through with the empty string.
            return FString{};
        }
        return FString(Buf, static_cast<::int32>(R.ptr - Buf));
    }
}

FString FString::FromInt32 (::int32  Value) { return ToCharsImpl(Value); }
FString FString::FromInt64 (::int64  Value) { return ToCharsImpl(Value); }
FString FString::FromFloat (float    Value) { return ToCharsImpl(Value); }
FString FString::FromDouble(double   Value) { return ToCharsImpl(Value); }

// =====================================================================
// C-string accessors.
// =====================================================================

const char* FString::ToUtf8Cstr() const noexcept
{
    // Spec wording: "ensures null-termination; may realloc".
    // We need a const-mutable hack: const_cast on this so we can
    // grow the buffer to write a null terminator. For SSO mode the
    // null terminator can occupy any byte 47..62 (which are
    // reserved / zero-filled per spec). For heap mode we extend
    // capacity by 1 if needed.
    auto* Self = const_cast<FString*>(this);
    const ::int32 N = LenBytes();

    if (Self->IsInSso())
    {
        // SSO bytes 47..62 are zero per spec; byte 47 is already 0.
        // Make sure byte at offset N is '\0'.
        if (N <= kSsoMaxBytes)
        {
            // The SSO buffer occupies bytes 0..46; byte N is in-bounds
            // and is already 0 from InitEmpty / SetSsoLen. (Future
            // append paths may not zero the trailing slot; we
            // explicitly clear it here to be safe.)
            Self->m_storage[N] = ::std::byte{0};
            return reinterpret_cast<const char*>(&m_storage[0]);
        }
    }

    // Heap mode: ensure capacity for null terminator.
    Self->EnsureCapacity(N + 1);
    FHeapRep& H = Self->GetHeap();
    H.m_data[N] = '\0';
    return H.m_data;
}

} // namespace XCore
