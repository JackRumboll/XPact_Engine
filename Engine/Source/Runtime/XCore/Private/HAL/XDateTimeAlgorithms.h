// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// XDateTimeAlgorithms.h -- proleptic-Gregorian date arithmetic.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time).
//
// Howard Hinnant's proleptic-Gregorian algorithms (his "date.h" paper:
// https://howardhinnant.github.io/date_algorithms.html, published in
// the public domain). The two key functions:
//
//   days_from_civil(y, m, d) -> int64_t
//     Returns the number of days from 1970-01-01 to (y, m, d). Negative
//     for dates before the epoch.
//
//   civil_from_days(z) -> (year, month, day)
//     The inverse: given days-since-epoch, returns the (y, m, d) tuple.
//
// Both are pure arithmetic (no LUTs, no branches in the hot path beyond
// the obligatory leap-year tests). Bit-exact across all three XPact
// targets per Section 7.5 acceptance criterion D-extra.
//
// These primitives are reused by:
//   * FDateTime::Year() / Month() / Day() / Hour() / Minute() / Second()
//     -- accessors derived from m_unixMicros.
//   * FDateTime::ParseIso8601 -- validates each component, then reduces
//     to (y, m, d, h, m, s, us) -> m_unixMicros.
//   * FDateTime::ToIso8601 -- expands m_unixMicros into the field tuple
//     for the YYYY-MM-DDTHH:MM:SS.ffffffZ string.
//
// Algorithm provenance: the published paper is explicitly public domain.
// This implementation is a reimplementation in XPact's coding style (no
// literal copy of the original code), retaining the algorithmic
// correctness while matching XPact's namespace, type, and comment
// conventions.
//
// =====================================================================

#include "Macros/XCoreTypes.h"

#include <cstddef>
#include <cstdint>
#include <string_view>

namespace XCore::HAL::DateAlgorithms
{

// ---------------------------------------------------------------------
// days_from_civil -- proleptic-Gregorian (y, m, d) -> days-since-epoch.
//
// Pre-conditions:
//   * y >= INT_MIN / 366 + 1; y <= INT_MAX / 366 - 1 (~5.8 million-year
//     range; we operate well within this).
//   * 1 <= m <= 12
//   * 1 <= d <= last day of month m in year y
//
// The algorithm rebases years so January and February belong to the
// previous "civil" year; this makes the leap-day count computation
// uniform (no special-case for the Feb 29 boundary).
//
// Returns: days since 1970-01-01 as int64. 0 == 1970-01-01.
// ---------------------------------------------------------------------
[[nodiscard]] constexpr ::std::int64_t days_from_civil(
    ::std::int32_t y, ::std::int32_t m, ::std::int32_t d) noexcept
{
    // Rebase: January/February are treated as months 13/14 of the
    // previous year. This makes the leap-day arithmetic uniform.
    if (m <= 2)
    {
        y -= 1;
    }

    // Era: 400-year cycle. Each era has exactly 146,097 days
    // (= 400*365 + 97 leap days). yoe = year-of-era in [0, 399].
    // doy = day-of-year [0, 365] starting from March 1 (the rebase
    // makes year boundaries fall on March 1 instead of January 1).
    const ::std::int32_t era = (y >= 0 ? y : y - 399) / 400;
    const ::std::uint32_t yoe = static_cast<::std::uint32_t>(y - era * 400);

    // doy formula from the Hinnant paper:
    //   doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + (d - 1)
    const ::std::uint32_t doy =
        (153u * static_cast<::std::uint32_t>(m + (m > 2 ? -3 : 9)) + 2u) / 5u +
        static_cast<::std::uint32_t>(d - 1);

    // doe = day-of-era [0, 146096]
    const ::std::uint32_t doe = yoe * 365u + yoe / 4u - yoe / 100u + doy;

    // Total days from 0000-03-01; subtract 719468 to rebase to
    // 1970-01-01.
    return static_cast<::std::int64_t>(era) * 146097LL +
           static_cast<::std::int64_t>(doe) - 719468LL;
}

// ---------------------------------------------------------------------
// civil_from_days -- days-since-epoch -> (y, m, d).
//
// Inverse of days_from_civil. The output is passed via three int32
// references because C++20 doesn't have a clean structured-binding
// alternative that constexpr-cleanly composes (std::tuple in constexpr
// landed in C++23).
// ---------------------------------------------------------------------
constexpr void civil_from_days(
    ::std::int64_t z,
    ::std::int32_t& OutYear,
    ::std::int32_t& OutMonth,
    ::std::int32_t& OutDay) noexcept
{
    // Rebase to 0000-03-01 origin (z = 0 becomes 1970-01-01 as input;
    // we shift by +719468 to align with the era cycle).
    z += 719468LL;

    const ::std::int64_t era = (z >= 0 ? z : z - 146096LL) / 146097LL;
    const ::std::uint32_t doe = static_cast<::std::uint32_t>(z - era * 146097LL);

    // yoe = year-of-era. The +1 / -1 corrections handle the leap-day
    // distribution across the 400-year cycle.
    const ::std::uint32_t yoe =
        (doe - doe / 1460u + doe / 36524u - doe / 146096u) / 365u;

    const ::std::int32_t y = static_cast<::std::int32_t>(yoe) +
                             static_cast<::std::int32_t>(era * 400LL);

    const ::std::uint32_t doy =
        doe - (365u * yoe + yoe / 4u - yoe / 100u);

    // mp = month-of-year shifted so March = 0.
    const ::std::uint32_t mp = (5u * doy + 2u) / 153u;

    OutDay   = static_cast<::std::int32_t>(doy - (153u * mp + 2u) / 5u + 1u);
    OutMonth = static_cast<::std::int32_t>(mp < 10u ? mp + 3u : mp - 9u);

    // Re-rebase year: if month <= 2 (Jan/Feb), the civil year is one
    // greater than the era-aligned year (because we computed using the
    // March-anchored cycle).
    OutYear = y + (OutMonth <= 2 ? 1 : 0);
}

// ---------------------------------------------------------------------
// last_day_of_month -- helper for ParseIso8601 component validation.
//
// Returns the last valid day-of-month for (year, month) per the
// proleptic-Gregorian calendar. Handles February's leap-year case.
// ---------------------------------------------------------------------
[[nodiscard]] constexpr ::std::int32_t last_day_of_month(
    ::std::int32_t y, ::std::int32_t m) noexcept
{
    if (m == 2)
    {
        // Leap year: divisible by 4, except divisible by 100 unless
        // also divisible by 400.
        const bool bLeap = (y % 4 == 0 && y % 100 != 0) || (y % 400 == 0);
        return bLeap ? 29 : 28;
    }
    // 30 days: April, June, September, November (4, 6, 9, 11).
    if (m == 4 || m == 6 || m == 9 || m == 11)
    {
        return 30;
    }
    // 31 days: January, March, May, July, August, October, December.
    return 31;
}

// ---------------------------------------------------------------------
// ISO 8601 helpers (declared here; defined in XDateTimeAlgorithms.cpp).
//
// ParsedComponents: the seven-field tuple ParseIso8601Components emits.
// EParseStatus: parse-result classification.
// ParseIso8601Components: validates the YYYY-MM-DDTHH:MM:SS[.fff[fff]]TZ
//                         grammar and returns the integer components.
// EmitIso8601: format unix-micros as YYYY-MM-DDTHH:MM:SS.ffffffZ.
// ComponentsToUnixMicros: inverse of EmitIso8601's component split.
// ---------------------------------------------------------------------

struct ParsedComponents
{
    ::std::int32_t Year;
    ::std::int32_t Month;
    ::std::int32_t Day;
    ::std::int32_t Hour;
    ::std::int32_t Minute;
    ::std::int32_t Second;
    ::std::int32_t Microsecond;
    ::std::int32_t UtcOffsetSec;
};

enum class EParseStatus : ::std::uint8_t
{
    Ok                = 0,
    Malformed         = 1,
    OverflowComponent = 2,
};

[[nodiscard]] EParseStatus ParseIso8601Components(
    ::std::string_view Input, ParsedComponents& Out) noexcept;

::std::size_t EmitIso8601(
    ::std::int64_t UnixMicros, char* OutBuf, ::std::size_t BufSize) noexcept;

[[nodiscard]] ::std::int64_t ComponentsToUnixMicros(const ParsedComponents& C) noexcept;

} // namespace XCore::HAL::DateAlgorithms
