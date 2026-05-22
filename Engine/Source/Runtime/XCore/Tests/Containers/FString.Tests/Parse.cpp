// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FString.Tests/Parse.cpp -- ToInt32/ToFloat/etc.; Result-error checks.
// =====================================================================

#include "Containers/FString.h"
#include "HAL/FMemory.h"

#include <cstdio>
#include <cmath>

int main()
{
    ::XCore::HAL::FMemory::__Init();

    // ToInt32 success.
    {
        ::XCore::FString S("42");
        auto R = S.ToInt32();
        if (!R.has_value() || R.value() != 42) { std::fprintf(stderr, "FAIL: ToInt32(42)\n"); return 1; }
    }
    {
        ::XCore::FString S("-100");
        auto R = S.ToInt32();
        if (!R.has_value() || R.value() != -100) { std::fprintf(stderr, "FAIL: ToInt32(-100)\n"); return 1; }
    }

    // ToInt32 empty.
    {
        ::XCore::FString S;
        auto R = S.ToInt32();
        if (R.has_value() || R.error() != ::XCore::FParseError::Empty) { std::fprintf(stderr, "FAIL: ToInt32 empty\n"); return 1; }
    }

    // ToInt32 malformed.
    {
        ::XCore::FString S("abc");
        auto R = S.ToInt32();
        if (R.has_value() || R.error() != ::XCore::FParseError::Malformed) { std::fprintf(stderr, "FAIL: ToInt32 abc\n"); return 1; }
    }

    // ToInt32 trailing garbage.
    {
        ::XCore::FString S("42abc");
        auto R = S.ToInt32();
        if (R.has_value() || R.error() != ::XCore::FParseError::Malformed) { std::fprintf(stderr, "FAIL: ToInt32 42abc\n"); return 1; }
    }

    // ToInt32 overflow.
    {
        ::XCore::FString S("9999999999999");
        auto R = S.ToInt32();
        if (R.has_value() || R.error() != ::XCore::FParseError::Overflow) { std::fprintf(stderr, "FAIL: ToInt32 overflow\n"); return 1; }
    }

    // ToInt64.
    {
        ::XCore::FString S("1234567890123");
        auto R = S.ToInt64();
        if (!R.has_value() || R.value() != static_cast<::int64>(1234567890123LL)) { std::fprintf(stderr, "FAIL: ToInt64\n"); return 1; }
    }

    // ToDouble.
    {
        ::XCore::FString S("3.14159");
        auto R = S.ToDouble();
        if (!R.has_value()) { std::fprintf(stderr, "FAIL: ToDouble missing\n"); return 1; }
        if (std::fabs(R.value() - 3.14159) > 1e-9) { std::fprintf(stderr, "FAIL: ToDouble value=%f\n", R.value()); return 1; }
    }

    // ToFloat.
    {
        ::XCore::FString S("1.5");
        auto R = S.ToFloat();
        if (!R.has_value() || R.value() != 1.5f) { std::fprintf(stderr, "FAIL: ToFloat\n"); return 1; }
    }

    // FromInt32 round-trip.
    {
        ::XCore::FString S = ::XCore::FString::FromInt32(-12345);
        auto R = S.ToInt32();
        if (!R.has_value() || R.value() != -12345) { std::fprintf(stderr, "FAIL: FromInt32 round-trip\n"); return 1; }
    }

    // FromDouble round-trip.
    {
        ::XCore::FString S = ::XCore::FString::FromDouble(2.5);
        auto R = S.ToDouble();
        if (!R.has_value() || R.value() != 2.5) { std::fprintf(stderr, "FAIL: FromDouble round-trip\n"); return 1; }
    }

    return 0;
}
