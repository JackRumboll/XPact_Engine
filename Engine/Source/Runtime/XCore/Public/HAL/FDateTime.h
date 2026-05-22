// Copyright Simgenics. All Rights Reserved.
#pragma once

// =====================================================================
// FDateTime.h -- UTC absolute time; microsecond precision; sim-path-banned.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time). FDateTime is the wall-clock
// absolute-time type; FTimespan (separate header) is the duration type.
//
// Pattern reference: UE Core has `DateTime.h` (Runtime/Core/Public/
// Misc/DateTime.h) with a similar 64-bit-tick-count shape. UE uses
// 100-nanosecond ticks counted from .NET's epoch (0001-01-01T00:00:00Z)
// while XPact uses microsecond precision counted from the Unix epoch
// (1970-01-01T00:00:00Z). The XPact choice is principled:
//   - 1 us precision is sufficient for every gameplay/sim use case
//     (matches FTimespan).
//   - Unix epoch is the cross-platform standard (POSIX time_t, JSON
//     epoch values in training-scenario timestamps and log files
//     emitted to disk, web-protocol Date headers via /1000).
//   - Range: int64 microseconds since 1970 = ~292,277 years on either
//     side; safe for any conceivable use.
//
// LOCKED -- NO PLATFORM CRT DATE ROUTINES (Section 7.5):
//   The engine NEVER calls localtime, mktime, gmtime, strftime, or any
//   glibc/MSVC/Bionic date library function. These are non-deterministic
//   across platforms (different timezone-DB revisions, leap-second
//   handling, DST cutoffs) and would silently corrupt FDateTime::
//   AddDays output across the three targets.
//
//   Phase 1b implementation: Howard Hinnant's date.h-style proleptic-
//   Gregorian algorithms (vendored or reimplemented in
//   `Private/HAL/XDateTimeAlgorithms.cpp`). Bit-exact across all three
//   targets given equal input.
//
// Sim-path discipline (Section 7.5): FDateTime is sim-path-banned at
// the type level. Its UtcNow() reads wall-clock time (non-deterministic
// by definition), and even the "pure arithmetic" methods (AddDays,
// operator-) compose with a wall-clock origin. Sim-path code that needs
// duration arithmetic uses FTimespan exclusively. The sim-path overlay
// header (Phase 1e) decorates the entire FDateTime type with
//   [[deprecated("not sim-path-safe; pure-duration code must use
//                FTimespan")]]
// so a stray FDateTime reference in a sim-path TU fails the build.
//
// =====================================================================

#include "FTimespan.h"
#include "Macros/XCoreFwd.h"      // FString forward decl (XCore namespace)
#include "Macros/XErrorTypes.h"   // FDateRangeError (XCore namespace)
#include "Macros/XResult.h"       // Result<T, E> alias (XCore namespace)

#include <compare>
#include <cstdint>
#include <string_view>

namespace XCore::HAL
{

// ---------------------------------------------------------------------
// Type aliases bringing the XCore-namespace declarations into the
// XCore::HAL scope used by this header.
//
// Subagent A's XErrorTypes.h declares FDateRangeError in namespace
// XCore (Section 13 lives at the XCore-4a public surface), and
// XResult.h declares Result<T, E> in namespace XCore. The spec at
// Section 7.5 places FDateTime in namespace XCore::HAL; the cleanest
// way to reference XCore::Result<XCore::HAL::FDateTime,
// XCore::FDateRangeError> in this header's method declarations is via
// using-aliases. Cross-references Section 13.1 "the spec's Section 13
// wording places them at 'the XCore-4a public surface' without further
// nesting" (XErrorTypes.h comment).
// ---------------------------------------------------------------------

using ::XCore::Result;            // Result<T, E> from XResult.h
using ::XCore::FDateRangeError;   // FDateRangeError enum from XErrorTypes.h
using ::XCore::FString;           // FString forward declaration from XCoreFwd.h

// ---------------------------------------------------------------------
// FDateTime -- 8-byte UTC absolute time (microsecond count since Unix
// epoch).
//
// Internal representation: signed int64 microseconds since 1970-01-01
// T00:00:00Z. Negative values represent dates before 1970 (the
// proleptic-Gregorian calendar back-projects cleanly via Howard
// Hinnant's date.h algorithms).
//
// Range: ~9.2e18 / 1e6 / 86400 / 365.2425 = ~292,277 years on either
// side of the epoch. The proleptic-Gregorian back-projection is well-
// defined within this range.
//
// ABI lock: 8 bytes; 8-byte alignment.
// ---------------------------------------------------------------------

class FDateTime
{
    int64_t m_unixMicros;  // microseconds since Unix epoch 1970-01-01T00:00:00Z

public:
    // -----------------------------------------------------------------
    // Constructors.
    //
    // The default constructor produces the Unix epoch (m_unixMicros =
    // 0). The explicit single-arg constructor takes raw microseconds
    // since the epoch. User code should prefer the FromXxx factory
    // methods below; the explicit ctor is primarily for the factories'
    // internal use.
    // -----------------------------------------------------------------
    constexpr FDateTime() noexcept : m_unixMicros(0) {}

    constexpr explicit FDateTime(int64_t UnixMicros) noexcept : m_unixMicros(UnixMicros) {}

    // -----------------------------------------------------------------
    // UtcNow -- read the platform wall-clock and return as FDateTime.
    //
    // NOT sim-path-safe. Reads the platform monotonic clock (per
    // FPlatformTime::Seconds) combined with the engine's at-startup-
    // calibrated UTC offset. The pattern is:
    //   1. At engine startup, capture (CLOCK_REALTIME_now,
    //      CLOCK_MONOTONIC_now) via a tight pair of syscalls.
    //   2. UtcNow() computes (CLOCK_MONOTONIC_now -
    //      calibrated_monotonic_origin) + calibrated_utc_origin.
    //   This avoids calling CLOCK_REALTIME directly (which can jump on
    //   NTP adjustments) and avoids platform CRT routines (which carry
    //   timezone-DB nondeterminism per the Section 7.5 lock).
    //
    // Phase 1b implementation: body lives in `Private/HAL/FDateTime.cpp`
    // and uses FPlatformTime internals; NOT platform CRT
    // gettimeofday/time.
    //
    // SIM-PATH DEPRECATION (Phase 1e overlay): the sim-path overlay
    // header decorates this method (and effectively the entire
    // FDateTime type) with [[deprecated("not sim-path-safe; ...")]].
    // -----------------------------------------------------------------
    static FDateTime UtcNow() noexcept;

    // -----------------------------------------------------------------
    // FromUnixTimestamp -- construct from Unix seconds (range-checked).
    //
    // Returns the FDateTime on success, or InvalidUnixTimestamp on
    // overflow (the multiply by 1e6 to convert seconds to microseconds
    // would overflow int64).
    //
    // Range guard: |Seconds| <= INT64_MAX / 1'000'000 ~= 9.2e12 seconds
    // = ~292,277 years.
    // -----------------------------------------------------------------
    static Result<FDateTime, FDateRangeError> FromUnixTimestamp(int64_t Seconds) noexcept;

    // -----------------------------------------------------------------
    // FromUnixMicros -- construct from Unix microseconds (range-checked).
    //
    // The check verifies the microsecond value lies within the
    // proleptic-Gregorian algorithm's representable range; an input
    // outside this range returns UnderflowYear or OverflowYear.
    // -----------------------------------------------------------------
    static Result<FDateTime, FDateRangeError> FromUnixMicros(int64_t Micros) noexcept;

    // -----------------------------------------------------------------
    // ISO 8601 round-trip (fix Rev 3 m4).
    //
    // ParseIso8601 supports:
    //   - YYYY-MM-DDTHH:MM:SS[.fff[fff]]Z   (UTC, optional fractional
    //                                        seconds up to microsecond
    //                                        precision)
    //   - YYYY-MM-DDTHH:MM:SS[.fff[fff]]+HH:MM   (UTC-offset form)
    //   - YYYY-MM-DDTHH:MM:SS[.fff[fff]]-HH:MM   (negative offset)
    //
    // ToIso8601 emits:
    //   YYYY-MM-DDTHH:MM:SS.ffffffZ   (microsecond precision; UTC;
    //                                  trailing-zero microseconds are
    //                                  ALWAYS emitted -- no truncation
    //                                  -- so the round-trip is stable)
    //
    // Both are bit-exact across all three platforms because the
    // implementation is pure arithmetic on m_unixMicros (Howard
    // Hinnant proleptic-Gregorian; no strftime; no strptime).
    //
    // Acceptance criterion D-extra-2 (Section 7.5 + Section 17.4):
    //   "FDateTime round-trip via ToIso8601 -> ParseIso8601 is
    //   identity bit-exactly across all three platforms."
    //
    // Phase 1b implementation: `Private/HAL/FDateTime.cpp`. Phase 1a
    // tests verify the surface compiles; the round-trip property test
    // ships gated on Phase 1b's body.
    // -----------------------------------------------------------------
    static Result<FDateTime, FDateRangeError> ParseIso8601(std::string_view Iso8601) noexcept;

    FString ToIso8601() const;

    // -----------------------------------------------------------------
    // Accessors.
    //
    // ToUnixMicros returns the raw int64 storage (the canonical
    // accessor; pure-read).
    // -----------------------------------------------------------------
    constexpr int64_t ToUnixMicros() const noexcept { return m_unixMicros; }

    // -----------------------------------------------------------------
    // Arithmetic methods (AddDays / AddHours / AddMinutes).
    //
    // Each adds a fixed-microsecond multiple to the current count.
    // NOT calendar-aware: AddDays(1) advances 86,400,000,000 us
    // regardless of whether a DST boundary or leap second falls
    // within. This is the principled behaviour: calendar-aware
    // arithmetic would require timezone-DB lookups (non-deterministic
    // across platforms; banned by Section 7.5).
    //
    // For DST-aware "next 9 AM in the user's timezone" arithmetic,
    // higher-level UI code computes the offset explicitly and adds it
    // here. The HAL surface guarantees only that
    //   FDateTime + (FTimespan::FromHours(24)) == FDateTime + AddDays(1)
    // bit-exactly across all three platforms.
    //
    // Note: per Section 7.5 spec, these are declared `constexpr`. The
    // body is `+ FTimespan::FromXxx(N).TotalMicroseconds()` which is
    // constexpr-friendly. Inlining here keeps the AddXxx-as-arithmetic
    // story complete in the header.
    // -----------------------------------------------------------------
    constexpr FDateTime AddDays(int Days) const noexcept
    {
        return FDateTime(m_unixMicros + FTimespan::FromDays(Days).TotalMicroseconds());
    }

    constexpr FDateTime AddHours(int Hours) const noexcept
    {
        return FDateTime(m_unixMicros + FTimespan::FromHours(Hours).TotalMicroseconds());
    }

    constexpr FDateTime AddMinutes(int Minutes) const noexcept
    {
        return FDateTime(m_unixMicros + FTimespan::FromMinutes(Minutes).TotalMicroseconds());
    }

    // -----------------------------------------------------------------
    // Operator overloads -- FDateTime + FTimespan, FDateTime -
    // FTimespan, FDateTime - FDateTime.
    //
    // FDateTime + FTimespan -> FDateTime (shift by duration)
    // FDateTime - FTimespan -> FDateTime (shift by negative duration)
    // FDateTime - FDateTime -> FTimespan (duration between two instants)
    //
    // All bit-exact across platforms.
    // -----------------------------------------------------------------
    constexpr FDateTime operator+(FTimespan Span) const noexcept
    {
        return FDateTime(m_unixMicros + Span.TotalMicroseconds());
    }

    constexpr FDateTime operator-(FTimespan Span) const noexcept
    {
        return FDateTime(m_unixMicros - Span.TotalMicroseconds());
    }

    constexpr FTimespan operator-(FDateTime Other) const noexcept
    {
        return FTimespan(m_unixMicros - Other.m_unixMicros);
    }

    // -----------------------------------------------------------------
    // Comparison operators.
    //
    // C++20 operator==/operator<=> defaulted; provides ==, !=, <, >,
    // <=, >=. The comparison is integer comparison on m_unixMicros
    // which is bit-exact across all three platforms.
    // -----------------------------------------------------------------
    constexpr bool operator==(FDateTime Other) const noexcept = default;
    constexpr auto operator<=>(FDateTime Other) const noexcept = default;
};

// ---------------------------------------------------------------------
// ABI locks per Section 7.5 spec.
// ---------------------------------------------------------------------

static_assert(sizeof(FDateTime) == 8,
              "FDateTime ABI lock: 8-byte unix-micros count");

static_assert(alignof(FDateTime) == 8,
              "FDateTime 8-byte alignment");

} // namespace XCore::HAL

// =====================================================================
// TODO(Phase 1b):
//   - Implement FDateTime::UtcNow body in Private/HAL/FDateTime.cpp.
//     Uses FPlatformTime internals + at-startup-calibrated UTC offset.
//     MUST NOT use platform CRT gettimeofday / time / localtime / etc.
//   - Implement FDateTime::FromUnixTimestamp / FromUnixMicros with
//     range checks.
//   - Implement FDateTime::ParseIso8601 / ToIso8601 in
//     Private/HAL/XDateTimeAlgorithms.cpp using Howard Hinnant's
//     proleptic-Gregorian algorithms. Pure arithmetic on m_unixMicros;
//     bit-exact across all three platforms. Acceptance criterion
//     D-extra-2 gates on this body.
//
// TODO(Phase 1a integration):
//   - Once Macros/XPactMacros.h ships from Subagent A, replace the
//     local Result<T, E> forward declaration with a `#include
//     "Macros/XPactMacros.h"`. Replace the local FDateRangeError
//     declaration with the canonical one.
//
// TODO(Phase 1g):
//   - Once FString.h is shipped, replace the FString forward
//     declaration with `#include "FString.h"` (or, preferably, a
//     `StringFwd.h` analogue).
// =====================================================================
