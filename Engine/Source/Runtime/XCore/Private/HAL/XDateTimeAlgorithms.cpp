// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// XDateTimeAlgorithms.cpp -- ISO 8601 parse/emit helpers + unit tests.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time).
//
// The Howard-Hinnant proleptic-Gregorian primitives (days_from_civil,
// civil_from_days, last_day_of_month) are constexpr-defined inline in
// XDateTimeAlgorithms.h so FDateTime's accessors can use them in
// constexpr contexts.
//
// This .cpp file adds two helper functions for the ISO 8601 surface:
//
//   ParseIso8601Components(view) -> Result<{y,m,d,h,m,s,us}, Error>
//     Validates the YYYY-MM-DDTHH:MM:SS[.fff[fff]][TZ] grammar and
//     returns the integer components. Pure-arithmetic / pure-string-
//     scan; no allocation; no strftime/strptime.
//
//   EmitIso8601(unixMicros, OutBuf, BufSize) -> bytes written
//     Formats a unix-micros value into YYYY-MM-DDTHH:MM:SS.ffffffZ.
//     Always emits 6 microsecond digits (no trailing-zero truncation)
//     so the round-trip is identity.
//
// Both helpers are exported via the namespace so FDateTime.cpp consumes
// them; the helpers are individually unit-testable via the test files
// under Tests/HAL/FDateTime.Tests/.
//
// =====================================================================

#include "XDateTimeAlgorithms.h"

#include <cstdint>
#include <cstdio>
#include <string_view>

namespace XCore::HAL::DateAlgorithms
{

// ParsedComponents + EParseStatus + the ISO 8601 helper signatures are
// declared in XDateTimeAlgorithms.h; the bodies live below.

// ---------------------------------------------------------------------
// ParseDigits -- scan N consecutive ASCII digits, advance cursor.
//
// Returns true on success, false if fewer than N digits found at the
// cursor. The cursor is advanced past the digits on success;
// unchanged on failure.
// ---------------------------------------------------------------------
namespace
{
    [[nodiscard]] bool ParseDigits(
        const char*& Cursor, const char* End,
        ::std::int32_t N, ::std::int32_t& OutValue) noexcept
    {
        if (End - Cursor < N)
        {
            return false;
        }
        ::std::int32_t Acc = 0;
        for (::std::int32_t i = 0; i < N; ++i)
        {
            const char C = Cursor[i];
            if (C < '0' || C > '9')
            {
                return false;
            }
            Acc = Acc * 10 + static_cast<::std::int32_t>(C - '0');
        }
        Cursor += N;
        OutValue = Acc;
        return true;
    }

    // Expect literal character at cursor; advance if matched.
    [[nodiscard]] bool ExpectChar(const char*& Cursor, const char* End, char Expected) noexcept
    {
        if (Cursor >= End || *Cursor != Expected)
        {
            return false;
        }
        ++Cursor;
        return true;
    }

    // Parse fractional seconds: [.|,] then 1-6 digits, padded to micros.
    // Returns Ok on success (filling OutMicros); Malformed otherwise.
    // The cursor is advanced past the fractional component on success.
    [[nodiscard]] EParseStatus ParseFractionalMicros(
        const char*& Cursor, const char* End,
        ::std::int32_t& OutMicros) noexcept
    {
        OutMicros = 0;
        if (Cursor >= End)
        {
            return EParseStatus::Ok;  // no fractional -> 0 us
        }
        if (*Cursor != '.' && *Cursor != ',')
        {
            return EParseStatus::Ok;
        }
        ++Cursor;  // consume the separator

        // Read up to 6 digits.
        ::std::int32_t Acc = 0;
        ::std::int32_t Count = 0;
        while (Cursor < End && Count < 6 && *Cursor >= '0' && *Cursor <= '9')
        {
            Acc = Acc * 10 + static_cast<::std::int32_t>(*Cursor - '0');
            ++Cursor;
            ++Count;
        }
        if (Count == 0)
        {
            return EParseStatus::Malformed;
        }
        // Pad to microseconds: if we read fewer than 6 digits, multiply.
        // e.g., "0.5" -> 5 -> 500000 us (5 * 10^5).
        for (::std::int32_t i = Count; i < 6; ++i)
        {
            Acc *= 10;
        }
        // Optionally skip any additional digits beyond microsecond
        // precision (silently truncate to us).
        while (Cursor < End && *Cursor >= '0' && *Cursor <= '9')
        {
            ++Cursor;
        }
        OutMicros = Acc;
        return EParseStatus::Ok;
    }

    // Parse timezone designator: "Z" or "+HH:MM" or "-HH:MM".
    // Returns Ok on success (OutOffsetSec set); Malformed otherwise.
    [[nodiscard]] EParseStatus ParseTimezone(
        const char*& Cursor, const char* End,
        ::std::int32_t& OutOffsetSec) noexcept
    {
        OutOffsetSec = 0;
        if (Cursor >= End)
        {
            // No timezone: per ISO 8601 this would be "local time"; we
            // treat absence as malformed since FDateTime is UTC-only.
            return EParseStatus::Malformed;
        }
        if (*Cursor == 'Z')
        {
            ++Cursor;
            return EParseStatus::Ok;
        }
        ::std::int32_t Sign = 0;
        if (*Cursor == '+')
        {
            Sign = +1;
            ++Cursor;
        }
        else if (*Cursor == '-')
        {
            Sign = -1;
            ++Cursor;
        }
        else
        {
            return EParseStatus::Malformed;
        }
        ::std::int32_t OffsetHr  = 0;
        ::std::int32_t OffsetMin = 0;
        if (!ParseDigits(Cursor, End, 2, OffsetHr))
        {
            return EParseStatus::Malformed;
        }
        if (!ExpectChar(Cursor, End, ':'))
        {
            return EParseStatus::Malformed;
        }
        if (!ParseDigits(Cursor, End, 2, OffsetMin))
        {
            return EParseStatus::Malformed;
        }
        if (OffsetHr > 23 || OffsetMin > 59)
        {
            return EParseStatus::OverflowComponent;
        }
        OutOffsetSec = Sign * (OffsetHr * 3600 + OffsetMin * 60);
        return EParseStatus::Ok;
    }
}

// ---------------------------------------------------------------------
// ParseIso8601Components -- the main grammar parser.
//
// Grammar (relaxed but documented):
//   YYYY '-' MM '-' DD 'T' HH ':' MM ':' SS [ '.' | ',' frac ] TZ
// where:
//   YYYY = 4-digit year (proleptic-Gregorian; year 0 accepted)
//   MM   = 2-digit month [1, 12]
//   DD   = 2-digit day-of-month [1, last_day_of_month(y, m)]
//   HH   = 2-digit hour [0, 23]
//   MM   = 2-digit minute [0, 59]
//   SS   = 2-digit second [0, 59]
//   frac = 1-6 digit fractional second (padded to us)
//   TZ   = 'Z' | '+'HH':'MM | '-'HH':'MM
//
// Negative years (e.g., "-0001-...") are NOT supported in this version;
// the dispatch instruction's 10 exemplar timestamps use post-1970 and
// epoch-relative pre-1970 dates, not extended-form negative years. A
// TODO documents the extended-form support if needed.
//
// Returns Ok on success; the caller maps Malformed and OverflowComponent
// to FDateRangeError variants.
// ---------------------------------------------------------------------
EParseStatus ParseIso8601Components(
    ::std::string_view Input, ParsedComponents& Out) noexcept
{
    const char* Cursor = Input.data();
    const char* End    = Cursor + Input.size();

    if (!ParseDigits(Cursor, End, 4, Out.Year))    return EParseStatus::Malformed;
    if (!ExpectChar(Cursor, End, '-'))             return EParseStatus::Malformed;
    if (!ParseDigits(Cursor, End, 2, Out.Month))   return EParseStatus::Malformed;
    if (!ExpectChar(Cursor, End, '-'))             return EParseStatus::Malformed;
    if (!ParseDigits(Cursor, End, 2, Out.Day))     return EParseStatus::Malformed;
    if (!ExpectChar(Cursor, End, 'T'))             return EParseStatus::Malformed;
    if (!ParseDigits(Cursor, End, 2, Out.Hour))    return EParseStatus::Malformed;
    if (!ExpectChar(Cursor, End, ':'))             return EParseStatus::Malformed;
    if (!ParseDigits(Cursor, End, 2, Out.Minute))  return EParseStatus::Malformed;
    if (!ExpectChar(Cursor, End, ':'))             return EParseStatus::Malformed;
    if (!ParseDigits(Cursor, End, 2, Out.Second))  return EParseStatus::Malformed;

    // Optional fractional seconds.
    const EParseStatus FracStatus = ParseFractionalMicros(Cursor, End, Out.Microsecond);
    if (FracStatus != EParseStatus::Ok)
    {
        return FracStatus;
    }

    // Timezone (mandatory; FDateTime is UTC-only).
    const EParseStatus TzStatus = ParseTimezone(Cursor, End, Out.UtcOffsetSec);
    if (TzStatus != EParseStatus::Ok)
    {
        return TzStatus;
    }

    // Must have consumed the entire input.
    if (Cursor != End)
    {
        return EParseStatus::Malformed;
    }

    // Component range validation.
    if (Out.Month < 1 || Out.Month > 12)             return EParseStatus::OverflowComponent;
    const ::std::int32_t MaxDay = last_day_of_month(Out.Year, Out.Month);
    if (Out.Day < 1 || Out.Day > MaxDay)             return EParseStatus::OverflowComponent;
    if (Out.Hour < 0 || Out.Hour > 23)               return EParseStatus::OverflowComponent;
    if (Out.Minute < 0 || Out.Minute > 59)           return EParseStatus::OverflowComponent;
    if (Out.Second < 0 || Out.Second > 59)           return EParseStatus::OverflowComponent;
    if (Out.Microsecond < 0 || Out.Microsecond > 999999) return EParseStatus::OverflowComponent;

    return EParseStatus::Ok;
}

// ---------------------------------------------------------------------
// EmitIso8601 -- format unix-micros as YYYY-MM-DDTHH:MM:SS.ffffffZ.
//
// Always emits 6 microsecond digits (no trailing-zero truncation) per
// Section 7.5: "microsecond precision is always emitted ... to ensure
// a stable round-trip with ParseIso8601."
//
// OutBuf must be at least 28 bytes (27 chars + nul):
//   "1970-01-01T00:00:00.000000Z\0" == 28 bytes
// Negative years would require an extended-format ("-YYYY-...") which
// is not currently supported; years < 0 are emitted with year-mod-10000
// which produces incorrect strings for negative-year inputs. A TODO
// documents this; the round-trip test set does NOT include negative
// years in this Phase 1b implementation.
//
// Returns: number of bytes written EXCLUDING the trailing nul. 0 on
// buffer-too-small (defensive).
// ---------------------------------------------------------------------
::std::size_t EmitIso8601(
    ::std::int64_t UnixMicros,
    char* OutBuf,
    ::std::size_t BufSize) noexcept
{
    // Required output is 27 bytes + nul = 28 bytes minimum.
    if (BufSize < 28 || OutBuf == nullptr)
    {
        return 0;
    }

    // Split into (days, micros-of-day).
    // Use C++20 division-rounded-toward-negative-infinity arithmetic
    // so negative micros yield (days = floor(micros / day-us)).
    constexpr ::std::int64_t MICROS_PER_SEC  = 1'000'000LL;
    constexpr ::std::int64_t MICROS_PER_MIN  = 60LL * MICROS_PER_SEC;
    constexpr ::std::int64_t MICROS_PER_HOUR = 60LL * MICROS_PER_MIN;
    constexpr ::std::int64_t MICROS_PER_DAY  = 24LL * MICROS_PER_HOUR;

    ::std::int64_t Days   = UnixMicros / MICROS_PER_DAY;
    ::std::int64_t Remain = UnixMicros % MICROS_PER_DAY;

    // C++11+ guarantees truncation-toward-zero for integer division.
    // For negative inputs we want floor-division (toward -infinity) so
    // (Days, Remain) lies in [0, MICROS_PER_DAY-1] when UnixMicros<0.
    if (Remain < 0)
    {
        --Days;
        Remain += MICROS_PER_DAY;
    }

    ::std::int32_t Year  = 0;
    ::std::int32_t Month = 0;
    ::std::int32_t Day   = 0;
    civil_from_days(Days, Year, Month, Day);

    const ::std::int32_t Hour    = static_cast<::std::int32_t>(Remain / MICROS_PER_HOUR);
    Remain                       %= MICROS_PER_HOUR;
    const ::std::int32_t Minute  = static_cast<::std::int32_t>(Remain / MICROS_PER_MIN);
    Remain                       %= MICROS_PER_MIN;
    const ::std::int32_t Second  = static_cast<::std::int32_t>(Remain / MICROS_PER_SEC);
    const ::std::int32_t Micros  = static_cast<::std::int32_t>(Remain % MICROS_PER_SEC);

    // TODO(Phase 2): handle negative-year emission (extended format
    // "-YYYY-MM-DD..." per ISO 8601 § 4.1.2.4). For Phase 1b the
    // four-digit-year format suffices (the round-trip test set covers
    // post-1970 dates + epoch-relative pre-1970 dates which still
    // produce four-digit year strings via the civil_from_days result).

    const int Written = ::std::snprintf(
        OutBuf, BufSize,
        "%04d-%02d-%02dT%02d:%02d:%02d.%06dZ",
        Year, Month, Day, Hour, Minute, Second, Micros);
    if (Written <= 0 || static_cast<::std::size_t>(Written) >= BufSize)
    {
        return 0;
    }
    return static_cast<::std::size_t>(Written);
}

// ---------------------------------------------------------------------
// ComponentsToUnixMicros -- inverse of EmitIso8601's split.
//
// Given a ParsedComponents, returns the corresponding unix-micros
// value. The caller is responsible for the timezone offset (subtract
// UtcOffsetSec * 1e6 from the result to convert local-time to UTC).
// ---------------------------------------------------------------------
::std::int64_t ComponentsToUnixMicros(const ParsedComponents& C) noexcept
{
    constexpr ::std::int64_t MICROS_PER_SEC  = 1'000'000LL;
    constexpr ::std::int64_t MICROS_PER_MIN  = 60LL * MICROS_PER_SEC;
    constexpr ::std::int64_t MICROS_PER_HOUR = 60LL * MICROS_PER_MIN;
    constexpr ::std::int64_t MICROS_PER_DAY  = 24LL * MICROS_PER_HOUR;

    const ::std::int64_t Days = days_from_civil(C.Year, C.Month, C.Day);
    ::std::int64_t Micros = Days * MICROS_PER_DAY;
    Micros += static_cast<::std::int64_t>(C.Hour)   * MICROS_PER_HOUR;
    Micros += static_cast<::std::int64_t>(C.Minute) * MICROS_PER_MIN;
    Micros += static_cast<::std::int64_t>(C.Second) * MICROS_PER_SEC;
    Micros += static_cast<::std::int64_t>(C.Microsecond);

    // Apply timezone offset to convert local -> UTC.
    Micros -= static_cast<::std::int64_t>(C.UtcOffsetSec) * MICROS_PER_SEC;

    return Micros;
}

} // namespace XCore::HAL::DateAlgorithms
