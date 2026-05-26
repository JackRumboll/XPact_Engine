// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FName.cpp -- FName public API (ctors, ToString, GetTypeHash).
// =====================================================================
//
// XCore-4b Rev 3, Section 4. Implements every method declared in
// Public/Reflection/FName.h. The intern-table itself lives in
// Private/Reflection/FNamePool.cpp; this file is the thin facade that
// constructs FName handles by routing through the pool.
//
// =====================================================================

#include "Reflection/FName.h"
#include "Reflection/FNameEntry.h"
#include "Reflection/FNamePool.h"

#include "Containers/FString.h"
#include "Hash/FXxh3.h"

#include <cstring>     // std::strlen

namespace XCore::Reflect
{

// =====================================================================
// Helpers (file-private).
// =====================================================================

namespace
{
    // Parse a trailing "_N" numbered suffix per spec §4.4.
    //
    // Inputs:  Utf8 / ByteLen  -- input bytes (may be modified by this
    //                              function via the OutBaseLen output).
    // Outputs: OutBaseLen      -- length of the base (without "_N") portion.
    //          OutSerialNumber -- parsed suffix value (0 if no suffix).
    //
    // The split occurs only when ALL of the following hold:
    //   * ByteLen >= 3 (room for "_" + at least one digit)
    //   * There exists a position i (1 <= i <= ByteLen-1) such that
    //     bytes[i] == '_' and bytes[i+1..ByteLen-1] are all ASCII digits
    //     (1..10 digits) with no leading zero (unless the value is "0"
    //     exactly, which we treat as NOT a numbered suffix since
    //     SerialNumber == 0 means "no suffix").
    //   * The parsed value fits in uint32 (1 <= N <= UINT32_MAX).
    //
    // UE's behaviour (NameTypes.h:241 ParseNumberFromName): accepts 1..10
    // ASCII digits, rejects leading zeros except for "0" itself, requires
    // the value to fit in MAX_int32. XPact widens to uint32 because our
    // SerialNumber is uint32.
    //
    // When no suffix is detected, OutBaseLen = ByteLen and OutSerialNumber = 0.
    //
    // The split is greedy in the "find the LAST underscore that starts
    // a valid digit run" sense, mirroring UE.
    void ParseNumberedSuffix(const char* Utf8,
                              ::int32 ByteLen,
                              ::int32& OutBaseLen,
                              ::uint32& OutSerialNumber) noexcept
    {
        OutBaseLen      = ByteLen;
        OutSerialNumber = 0;

        if (ByteLen < 3 || Utf8 == nullptr)
        {
            return;
        }

        // Walk backwards from the end to find the underscore boundary.
        // We accept up to 10 trailing digits (uint32_max == 4 294 967 295
        // is exactly 10 digits).
        ::int32 DigitStart = ByteLen;
        while (DigitStart > 1 && DigitStart > ByteLen - 11)
        {
            const char Ch = Utf8[DigitStart - 1];
            if (Ch >= '0' && Ch <= '9')
            {
                --DigitStart;
            }
            else
            {
                break;
            }
        }

        // Need at least one digit + an underscore separator.
        if (DigitStart >= ByteLen)
        {
            return;
        }
        if (DigitStart < 1 || Utf8[DigitStart - 1] != '_')
        {
            return;
        }

        // Leading-zero rejection: "Actor_05" is NOT a numbered name.
        // The only exception is the single character "0", which we
        // also reject (SerialNumber == 0 means "no suffix").
        const ::int32 DigitCount = ByteLen - DigitStart;
        if (DigitCount == 0 || DigitCount > 10)
        {
            return;
        }
        if (DigitCount > 1 && Utf8[DigitStart] == '0')
        {
            return;
        }
        if (DigitCount == 1 && Utf8[DigitStart] == '0')
        {
            // Single "0" is not a numbered suffix; treat as plain.
            return;
        }

        // Parse the digit run. Use ::uint64 accumulator to detect overflow
        // before truncating to ::uint32.
        ::uint64 Value = 0;
        for (::int32 I = 0; I < DigitCount; ++I)
        {
            Value = Value * 10 + static_cast<::uint64>(Utf8[DigitStart + I] - '0');
        }

        // Reject overflow.
        if (Value > 0xFFFF'FFFFull)
        {
            return;
        }

        OutBaseLen      = DigitStart - 1;  // exclude the trailing "_"
        OutSerialNumber = static_cast<::uint32>(Value);
    }

    // The literal bytes returned by GetBaseBytes() for NAME_None.
    inline constexpr const char kNoneLiteral[]   = "None";
    inline constexpr ::int32    kNoneLiteralLen  = 4;
} // anonymous

// =====================================================================
// FName ctors.
// =====================================================================

FName::FName(const char* Utf8) noexcept
{
    if (Utf8 == nullptr)
    {
        Index        = kNoneIndex;
        SerialNumber = 0;
        return;
    }

    const ::SIZE_T Len = std::strlen(Utf8);
    XPACT_CHECK(Len <= kFNameMaxLength);

    ::int32  BaseLen      = 0;
    ::uint32 ParsedSuffix = 0;
    ParseNumberedSuffix(Utf8, static_cast<::int32>(Len), BaseLen, ParsedSuffix);

    Index        = FNamePool::Get().Intern(Utf8, BaseLen);
    SerialNumber = ParsedSuffix;
}

FName::FName(const char* Utf8, ::int32 ByteLen) noexcept
{
    if (Utf8 == nullptr || ByteLen <= 0)
    {
        Index        = kNoneIndex;
        SerialNumber = 0;
        return;
    }

    XPACT_CHECK(static_cast<::SIZE_T>(ByteLen) <= kFNameMaxLength);

    ::int32  BaseLen      = 0;
    ::uint32 ParsedSuffix = 0;
    ParseNumberedSuffix(Utf8, ByteLen, BaseLen, ParsedSuffix);

    Index        = FNamePool::Get().Intern(Utf8, BaseLen);
    SerialNumber = ParsedSuffix;
}

FName::FName(const ::XCore::FString& Source) noexcept
    : FName(Source.ToUtf8Ptr(), Source.LenBytes())
{
}

FName::FName(const char* Utf8, ::int32 ByteLen, ::uint32 Suffix) noexcept
{
    // The (Utf8, ByteLen, Suffix) ctor intentionally does NOT parse a
    // trailing "_N" -- the caller has explicitly partitioned the base
    // bytes and the suffix. Useful for cases like
    // FName("Actor", 5, 7) producing the equivalent of FName("Actor_7")
    // without going through the parse path.
    if (Utf8 == nullptr || ByteLen <= 0)
    {
        Index        = kNoneIndex;
        SerialNumber = Suffix;
        return;
    }

    XPACT_CHECK(static_cast<::SIZE_T>(ByteLen) <= kFNameMaxLength);
    Index        = FNamePool::Get().Intern(Utf8, ByteLen);
    SerialNumber = Suffix;
}

// =====================================================================
// Accessors / conversions.
// =====================================================================

const char* FName::GetBaseBytes() const noexcept
{
    if (Index == kNoneIndex)
    {
        return kNoneLiteral;
    }

    const FNameEntry* Entry = FNamePool::Get().FindEntry(Index);
    if (Entry == nullptr)
    {
        // Invalid index: return the "None" sentinel rather than crashing.
        // Diagnostic paths (IsValid) catch the inconsistency.
        return kNoneLiteral;
    }
    return Entry->GetBytes();
}

::int32 FName::GetBaseLength() const noexcept
{
    if (Index == kNoneIndex)
    {
        return kNoneLiteralLen;
    }

    const FNameEntry* Entry = FNamePool::Get().FindEntry(Index);
    if (Entry == nullptr)
    {
        return kNoneLiteralLen;
    }
    return static_cast<::int32>(Entry->GetLength());
}

bool FName::IsValid() const noexcept
{
    return FNamePool::Get().IsValidIndex(Index);
}

::XCore::FString FName::ToString() const
{
    ::XCore::FString Out;
    AppendString(Out);
    return Out;
}

::XCore::FString FName::ToFString() const
{
    return ToString();
}

void FName::AppendString(::XCore::FString& Out) const
{
    const char* BaseBytes  = GetBaseBytes();
    const ::int32 BaseLen  = GetBaseLength();

    Out.Append(BaseBytes, BaseLen);

    if (SerialNumber != 0)
    {
        // Append "_N" where N is the decimal SerialNumber.
        // Use a small stack buffer to format the digit run; max 10
        // digits for uint32 + underscore + NUL = 12 bytes.
        char Buf[16];
        Buf[0] = '_';

        // Walk the integer's digits least-significant first into a
        // temporary then reverse into the output buffer.
        char     DigitsLo[10];
        ::int32  NumDigits = 0;
        ::uint32 Tmp       = SerialNumber;
        do
        {
            DigitsLo[NumDigits++] = static_cast<char>('0' + (Tmp % 10));
            Tmp /= 10;
        } while (Tmp != 0);

        // Reverse into Buf[1..NumDigits].
        for (::int32 I = 0; I < NumDigits; ++I)
        {
            Buf[1 + I] = DigitsLo[NumDigits - 1 - I];
        }

        Out.Append(Buf, NumDigits + 1);  // +1 for the leading underscore
    }
}

// =====================================================================
// Hash (declared in XCoreFwd.h; defined here where FXxh3 is available).
// =====================================================================
//
// Per spec §4.5: "GetTypeHash(FName) is FXxh3-64 of the 8-byte handle.
// Specifically: hash the two uint32 values as an 8-byte sequence."
//
// The two-hash split (byte hash for intern lookup; handle hash for
// TMap<FName, V>) is intentional -- intern-table identity (the bytes)
// and FName-as-key identity (the handle) live at different layers.
// =====================================================================

::uint64 GetTypeHash(FName Name) noexcept
{
    // Hash the two uint32 fields as an 8-byte sequence. The bit pattern
    // of the FName struct on a little-endian target is Index (low 4 bytes)
    // + SerialNumber (high 4 bytes); on big-endian the order flips but
    // we don't ship to BE targets, and FXxh3-64 is bit-exact across the
    // supported targets (Win64 x86_64 / Linux x86_64 / Android ARM64).
    return ::XCore::Hash::FXxh3::Hash64(&Name, sizeof(FName), /*Seed=*/0);
}

} // namespace XCore::Reflect
