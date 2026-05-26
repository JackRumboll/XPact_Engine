// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/UEParitySurface.cpp -- UE-parity API surface coverage.
// =====================================================================
//
// XCore-4a Rev 3 Round 2 audit FIX-R2-MED-NEW-3. Verifies the UE-parity
// methods added to FString:
//   * ToUpper / ToLower   -- ASCII case conversion
//   * LeftPad / RightPad  -- byte-length padding
//   * Reverse             -- byte-level reversal
//   * RemoveFromStart / RemoveFromEnd -- conditional prefix/suffix strip
//   * JoinBy              -- inverse of Split
//
// =====================================================================

#include "Containers/FString.h"
#include "Containers/TArray.h"
#include "HAL/FMemory.h"
#include "Macros/XCoreTypes.h"

#include <cstdio>
#include <cstring>

#define CHECK(Expr, Msg)                                                  \
    do {                                                                  \
        if (!(Expr))                                                      \
        {                                                                 \
            std::fprintf(stderr, "FAIL: %s\n", Msg);                      \
            ::XCore::HAL::FMemory::__Shutdown();                          \
            return 1;                                                     \
        }                                                                 \
    } while (0)

static bool StringEquals(const ::XCore::FString& S, const char* C)
{
    const ::int32 Len = static_cast<::int32>(std::strlen(C));
    if (S.LenBytes() != Len) return false;
    return std::memcmp(S.ToUtf8Ptr(), C, static_cast<size_t>(Len)) == 0;
}

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // -----------------------------------------------------------------
    // ToUpper / ToLower -- ASCII case conversion (locale-independent).
    // -----------------------------------------------------------------
    {
        ::XCore::FString S("Hello, World 123 abc XYZ");
        ::XCore::FString U = S.ToUpper();
        ::XCore::FString L = S.ToLower();
        CHECK(StringEquals(U, "HELLO, WORLD 123 ABC XYZ"), "ToUpper basic");
        CHECK(StringEquals(L, "hello, world 123 abc xyz"), "ToLower basic");
    }
    {
        ::XCore::FString S("");
        ::XCore::FString U = S.ToUpper();
        ::XCore::FString L = S.ToLower();
        CHECK(U.LenBytes() == 0, "ToUpper empty");
        CHECK(L.LenBytes() == 0, "ToLower empty");
    }
    {
        // UTF-8 bytes >= 0x80 must pass through unchanged.
        ::XCore::FString S("aBc\xC3\xA9");  // "aBcé"
        ::XCore::FString U = S.ToUpper();
        CHECK(U.LenBytes() == 5, "ToUpper UTF-8 length preserved");
        CHECK(static_cast<::uint8>(U.ToUtf8Ptr()[3]) == 0xC3U,
              "ToUpper UTF-8 lead byte unchanged");
        CHECK(static_cast<::uint8>(U.ToUtf8Ptr()[4]) == 0xA9U,
              "ToUpper UTF-8 continuation unchanged");
    }

    // -----------------------------------------------------------------
    // LeftPad / RightPad.
    // -----------------------------------------------------------------
    {
        ::XCore::FString S("42");
        ::XCore::FString L = S.LeftPad(5, '0');
        ::XCore::FString R = S.RightPad(5, '.');
        CHECK(StringEquals(L, "00042"), "LeftPad basic");
        CHECK(StringEquals(R, "42..."), "RightPad basic");
    }
    {
        // No-op when already at or over target.
        ::XCore::FString S("hello");
        ::XCore::FString L = S.LeftPad(3, ' ');
        ::XCore::FString R = S.RightPad(3, ' ');
        CHECK(StringEquals(L, "hello"), "LeftPad no-op when over");
        CHECK(StringEquals(R, "hello"), "RightPad no-op when over");
    }

    // -----------------------------------------------------------------
    // Reverse -- byte-level.
    // -----------------------------------------------------------------
    {
        ::XCore::FString S("abc123");
        CHECK(StringEquals(S.Reverse(), "321cba"), "Reverse ASCII");
        ::XCore::FString E("");
        CHECK(StringEquals(E.Reverse(), ""), "Reverse empty");
    }

    // -----------------------------------------------------------------
    // RemoveFromStart / RemoveFromEnd.
    // -----------------------------------------------------------------
    {
        ::XCore::FString S("PrefixSourceSuffix");
        CHECK(StringEquals(S.RemoveFromStart(::XCore::FString("Prefix")),
                           "SourceSuffix"),
              "RemoveFromStart present");
        CHECK(StringEquals(S.RemoveFromEnd(::XCore::FString("Suffix")),
                           "PrefixSource"),
              "RemoveFromEnd present");

        CHECK(StringEquals(S.RemoveFromStart(::XCore::FString("None")),
                           "PrefixSourceSuffix"),
              "RemoveFromStart absent: unchanged");
        CHECK(StringEquals(S.RemoveFromEnd(::XCore::FString("None")),
                           "PrefixSourceSuffix"),
              "RemoveFromEnd absent: unchanged");
    }

    // -----------------------------------------------------------------
    // JoinBy -- inverse of Split.
    // -----------------------------------------------------------------
    {
        ::XCore::TArray<::XCore::FString> Parts;
        Parts.Add(::XCore::FString("a"));
        Parts.Add(::XCore::FString("b"));
        Parts.Add(::XCore::FString("c"));
        ::XCore::FString Joined = ::XCore::FString::JoinBy(Parts, ::XCore::FString(","));
        CHECK(StringEquals(Joined, "a,b,c"), "JoinBy: three parts");
    }
    {
        ::XCore::TArray<::XCore::FString> Parts;
        ::XCore::FString Joined = ::XCore::FString::JoinBy(Parts, ::XCore::FString(","));
        CHECK(StringEquals(Joined, ""), "JoinBy: empty parts -> empty result");
    }
    {
        ::XCore::TArray<::XCore::FString> Parts;
        Parts.Add(::XCore::FString("only"));
        ::XCore::FString Joined = ::XCore::FString::JoinBy(Parts, ::XCore::FString("|"));
        CHECK(StringEquals(Joined, "only"), "JoinBy: single part unchanged");
    }
    {
        // Split + JoinBy round-trip.
        ::XCore::FString Original("alpha-beta-gamma");
        auto Pieces = Original.Split(::XCore::FString("-"));
        ::XCore::FString Joined = ::XCore::FString::JoinBy(Pieces, ::XCore::FString("-"));
        CHECK(StringEquals(Joined, "alpha-beta-gamma"), "Split + JoinBy round-trip");
    }

    ::XCore::HAL::FMemory::__Shutdown();
    std::printf("FString.UEParitySurface: PASS\n");
    return 0;
}
