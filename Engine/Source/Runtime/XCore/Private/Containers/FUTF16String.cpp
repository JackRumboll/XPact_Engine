// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FUTF16String.cpp -- Win32 wide-char escape hatch implementation.
// =====================================================================
//
// XCore-4a Rev 3, Section 11.1 / Section 11.5 / Section 2 String-
// encoding row.
//
// On Win64, the standard conversion routines are
// WideCharToMultiByte / MultiByteToWideChar (in Windows.h). Including
// Windows.h from a header would propagate macro pollution
// engine-wide; we therefore confine the platform-specific include
// to this .cpp.
//
// On Linux/Android there is no syscall consumer; the conversion is
// done by hand. The hand-rolled converter handles BMP and
// supplementary-plane codepoints (surrogate pairs for U+10000+).
//
// =====================================================================

#include "Containers/FUTF16String.h"
#include "HAL/FMemory.h"
#include "HAL/FMemTag.h"
#include "Macros/XPactMacros.h"

#include <cstring>          // std::memcpy

namespace XCore
{

namespace
{
    constexpr ::XCore::HAL::FMemTag kAllocTag = ::XCore::HAL::FMemTag::Platform;

    // -----------------------------------------------------------------
    // Hand-rolled UTF-8 -> UTF-16 conversion.
    //
    // Returns the number of UTF-16 code units written into Dst, or
    // computes the required count if Dst is null. Codepoints >=
    // U+10000 are emitted as surrogate pairs (lead + trail).
    //
    // Caller responsibilities:
    //   * If Dst is null, the function returns the required UTF-16
    //     code unit count for sizing.
    //   * Otherwise DstCap must be at least the required count.
    // -----------------------------------------------------------------
    ::int32 ConvertUtf8ToUtf16(
        const char*  Utf8,
        ::int32      Utf8Len,
        wchar_t*     Dst,
        ::int32      DstCap) noexcept
    {
        ::int32 OutCount = 0;
        ::int32 I = 0;

        auto WriteUnit = [&](wchar_t Unit)
        {
            if (Dst != nullptr && OutCount < DstCap)
            {
                Dst[OutCount] = Unit;
            }
            ++OutCount;
        };

        while (I < Utf8Len)
        {
            const auto Lead = static_cast<::uint8>(Utf8[I]);

            char32_t Cp     = 0;
            ::int32  SeqLen = 1;

            if (Lead < 0x80U)
            {
                Cp     = static_cast<char32_t>(Lead);
                SeqLen = 1;
            }
            else if ((Lead & 0xE0U) == 0xC0U)
            {
                if (I + 1 >= Utf8Len) { WriteUnit(0xFFFD); ++I; continue; }
                const auto B1 = static_cast<::uint8>(Utf8[I + 1]);
                if ((B1 & 0xC0U) != 0x80U) { WriteUnit(0xFFFD); ++I; continue; }
                Cp     = (static_cast<char32_t>(Lead & 0x1FU) << 6)
                        | static_cast<char32_t>(B1 & 0x3FU);
                SeqLen = 2;
                if (Cp < 0x80U)         { WriteUnit(0xFFFD); ++I; continue; }
            }
            else if ((Lead & 0xF0U) == 0xE0U)
            {
                if (I + 2 >= Utf8Len) { WriteUnit(0xFFFD); ++I; continue; }
                const auto B1 = static_cast<::uint8>(Utf8[I + 1]);
                const auto B2 = static_cast<::uint8>(Utf8[I + 2]);
                if (((B1 & 0xC0U) != 0x80U) || ((B2 & 0xC0U) != 0x80U))
                { WriteUnit(0xFFFD); ++I; continue; }
                Cp = (static_cast<char32_t>(Lead & 0x0FU) << 12)
                   | (static_cast<char32_t>(B1   & 0x3FU) << 6)
                   |  static_cast<char32_t>(B2   & 0x3FU);
                SeqLen = 3;
                if (Cp < 0x800U)        { WriteUnit(0xFFFD); ++I; continue; }
                if (Cp >= 0xD800U && Cp <= 0xDFFFU) { WriteUnit(0xFFFD); ++I; continue; }
            }
            else if ((Lead & 0xF8U) == 0xF0U)
            {
                if (I + 3 >= Utf8Len) { WriteUnit(0xFFFD); ++I; continue; }
                const auto B1 = static_cast<::uint8>(Utf8[I + 1]);
                const auto B2 = static_cast<::uint8>(Utf8[I + 2]);
                const auto B3 = static_cast<::uint8>(Utf8[I + 3]);
                if (((B1 & 0xC0U) != 0x80U) || ((B2 & 0xC0U) != 0x80U) || ((B3 & 0xC0U) != 0x80U))
                { WriteUnit(0xFFFD); ++I; continue; }
                Cp = (static_cast<char32_t>(Lead & 0x07U) << 18)
                   | (static_cast<char32_t>(B1   & 0x3FU) << 12)
                   | (static_cast<char32_t>(B2   & 0x3FU) << 6)
                   |  static_cast<char32_t>(B3   & 0x3FU);
                SeqLen = 4;
                if (Cp < 0x10000U)      { WriteUnit(0xFFFD); ++I; continue; }
                if (Cp > 0x10FFFFU)     { WriteUnit(0xFFFD); ++I; continue; }
            }
            else
            {
                // Invalid lead byte.
                WriteUnit(0xFFFD);
                ++I;
                continue;
            }

            // Emit. BMP codepoints (< U+10000) are one UTF-16 unit;
            // supplementary-plane codepoints are surrogate pairs.
            if (Cp < 0x10000U)
            {
                WriteUnit(static_cast<wchar_t>(Cp));
            }
            else
            {
                const char32_t Adj = Cp - 0x10000U;
                const wchar_t  Hi  = static_cast<wchar_t>(0xD800U + (Adj >> 10));
                const wchar_t  Lo  = static_cast<wchar_t>(0xDC00U + (Adj & 0x3FFU));
                WriteUnit(Hi);
                WriteUnit(Lo);
            }

            I += SeqLen;
        }

        return OutCount;
    }

    // -----------------------------------------------------------------
    // Hand-rolled UTF-16 -> UTF-8 conversion.
    //
    // Returns the number of UTF-8 bytes written into Dst, or computes
    // the required count if Dst is null. Surrogate pairs are merged
    // into a single 4-byte UTF-8 sequence.
    // -----------------------------------------------------------------
    ::int32 ConvertUtf16ToUtf8(
        const wchar_t* Utf16,
        ::int32        Utf16Len,
        char*          Dst,
        ::int32        DstCap) noexcept
    {
        ::int32 OutCount = 0;
        ::int32 I = 0;

        auto WriteByte = [&](char B)
        {
            if (Dst != nullptr && OutCount < DstCap)
            {
                Dst[OutCount] = B;
            }
            ++OutCount;
        };

        while (I < Utf16Len)
        {
            const auto Unit = static_cast<::uint16>(Utf16[I]);
            char32_t Cp = 0;

            if (Unit >= 0xD800U && Unit <= 0xDBFFU)
            {
                // High surrogate; needs a low surrogate following.
                if (I + 1 >= Utf16Len)
                {
                    Cp = 0xFFFDU;
                    I += 1;
                }
                else
                {
                    const auto Low = static_cast<::uint16>(Utf16[I + 1]);
                    if (Low >= 0xDC00U && Low <= 0xDFFFU)
                    {
                        Cp = (static_cast<char32_t>(Unit - 0xD800U) << 10)
                           |  static_cast<char32_t>(Low - 0xDC00U);
                        Cp += 0x10000U;
                        I += 2;
                    }
                    else
                    {
                        Cp = 0xFFFDU;
                        I += 1;
                    }
                }
            }
            else if (Unit >= 0xDC00U && Unit <= 0xDFFFU)
            {
                // Stray low surrogate; emit U+FFFD.
                Cp = 0xFFFDU;
                I += 1;
            }
            else
            {
                Cp = static_cast<char32_t>(Unit);
                I += 1;
            }

            // Encode Cp as UTF-8.
            if (Cp < 0x80U)
            {
                WriteByte(static_cast<char>(Cp));
            }
            else if (Cp < 0x800U)
            {
                WriteByte(static_cast<char>(0xC0U | (Cp >> 6)));
                WriteByte(static_cast<char>(0x80U | (Cp & 0x3FU)));
            }
            else if (Cp < 0x10000U)
            {
                WriteByte(static_cast<char>(0xE0U | (Cp >> 12)));
                WriteByte(static_cast<char>(0x80U | ((Cp >> 6) & 0x3FU)));
                WriteByte(static_cast<char>(0x80U | (Cp & 0x3FU)));
            }
            else
            {
                WriteByte(static_cast<char>(0xF0U | (Cp >> 18)));
                WriteByte(static_cast<char>(0x80U | ((Cp >> 12) & 0x3FU)));
                WriteByte(static_cast<char>(0x80U | ((Cp >> 6)  & 0x3FU)));
                WriteByte(static_cast<char>(0x80U | (Cp & 0x3FU)));
            }
        }

        return OutCount;
    }
} // anonymous namespace

// =====================================================================
// Construction / destruction.
// =====================================================================

FUTF16String::FUTF16String() noexcept
    : m_data(nullptr)
    , m_len(0)
    , m_capacity(0)
{
    // Allocate a minimal 1-element buffer holding only the null
    // terminator so WideCStr() never returns nullptr.
    Allocate(0);
    m_data[0] = L'\0';
}

FUTF16String::~FUTF16String() noexcept
{
    Deallocate();
}

FUTF16String::FUTF16String(const FUTF16String& Other)
    : m_data(nullptr), m_len(0), m_capacity(0)
{
    AssignFromUtf16(Other.m_data, Other.m_len);
}

FUTF16String::FUTF16String(FUTF16String&& Other) noexcept
    : m_data(Other.m_data)
    , m_len(Other.m_len)
    , m_capacity(Other.m_capacity)
{
    Other.m_data     = nullptr;
    Other.m_len      = 0;
    Other.m_capacity = 0;
}

FUTF16String& FUTF16String::operator=(const FUTF16String& Other)
{
    if (this != &Other)
    {
        AssignFromUtf16(Other.m_data, Other.m_len);
    }
    return *this;
}

FUTF16String& FUTF16String::operator=(FUTF16String&& Other) noexcept
{
    if (this != &Other)
    {
        Deallocate();
        m_data           = Other.m_data;
        m_len            = Other.m_len;
        m_capacity       = Other.m_capacity;
        Other.m_data     = nullptr;
        Other.m_len      = 0;
        Other.m_capacity = 0;
    }
    return *this;
}

// =====================================================================
// Conversion factories.
// =====================================================================

FUTF16String FUTF16String::FromFString(const FString& Src)
{
    const char*   Utf8 = Src.ToUtf8Ptr();
    const ::int32 Len  = Src.LenBytes();

    // Two-pass: compute required UTF-16 length first, then allocate
    // exactly. This is the standard idiom for hand-rolled
    // conversion (avoiding over-allocation).
    const ::int32 RequiredUtf16 = ConvertUtf8ToUtf16(Utf8, Len, nullptr, 0);

    FUTF16String Result;
    if (RequiredUtf16 > Result.m_capacity)
    {
        Result.Deallocate();
        Result.Allocate(RequiredUtf16);
    }
    Result.m_len = ConvertUtf8ToUtf16(Utf8, Len, Result.m_data, Result.m_capacity);
    Result.m_data[Result.m_len] = L'\0';
    return Result;
}

FString FUTF16String::ToFString(const FUTF16String& Src)
{
    const wchar_t* Utf16 = Src.m_data;
    const ::int32  Len   = Src.m_len;

    const ::int32 RequiredUtf8 = ConvertUtf16ToUtf8(Utf16, Len, nullptr, 0);
    if (RequiredUtf8 == 0) return FString{};

    // Allocate a buffer for the UTF-8 bytes, convert, then construct
    // an FString from the bytes. The FString constructor copies, so
    // a local stack buffer would suffice for small strings; for
    // large strings we use a heap buffer via FMemory.
    char* TempBuf = static_cast<char*>(
        ::XCore::HAL::FMemory::MallocOrAbort(
            static_cast<::SIZE_T>(RequiredUtf8),
            static_cast<::SIZE_T>(8),
            kAllocTag));
    const ::int32 Written = ConvertUtf16ToUtf8(Utf16, Len, TempBuf, RequiredUtf8);
    FString Result(TempBuf, Written);
    ::XCore::HAL::FMemory::Free(TempBuf);
    return Result;
}

// =====================================================================
// Internal allocator dispatch.
// =====================================================================

void FUTF16String::Allocate(::int32 NewLen) noexcept
{
    XPACT_CHECK(NewLen >= 0);
    // +1 for null terminator.
    const ::SIZE_T BytesNeeded =
        static_cast<::SIZE_T>(NewLen + 1) * sizeof(wchar_t);
    m_data = static_cast<wchar_t*>(
        ::XCore::HAL::FMemory::MallocOrAbort(
            BytesNeeded,
            static_cast<::SIZE_T>(alignof(wchar_t) < 8 ? 8 : alignof(wchar_t)),
            kAllocTag));
    m_capacity = NewLen;
    m_len      = 0;
    m_data[0]  = L'\0';
}

void FUTF16String::Deallocate() noexcept
{
    if (m_data != nullptr)
    {
        ::XCore::HAL::FMemory::Free(m_data);
        m_data     = nullptr;
        m_len      = 0;
        m_capacity = 0;
    }
}

void FUTF16String::AssignFromUtf16(const wchar_t* Src, ::int32 SrcLen)
{
    if (SrcLen > m_capacity || m_data == nullptr)
    {
        Deallocate();
        Allocate(SrcLen);
    }
    if (Src != nullptr && SrcLen > 0)
    {
        ::std::memcpy(m_data, Src,
            static_cast<::SIZE_T>(SrcLen) * sizeof(wchar_t));
    }
    m_len = SrcLen;
    m_data[SrcLen] = L'\0';
}

} // namespace XCore
