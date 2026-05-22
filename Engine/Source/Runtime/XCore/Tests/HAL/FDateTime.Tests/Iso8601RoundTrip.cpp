// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FDateTime.Tests/Iso8601RoundTrip.cpp -- ISO 8601 round-trip property.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time) acceptance criterion D-extra-2
// (Section 17.4):
//   "FDateTime round-trip via ToIso8601 -> ParseIso8601 is identity
//   bit-exactly across all three platforms."
//
// Phase 1a status:
//   - FDateTime declared; ParseIso8601 + ToIso8601 are extern decls
//     with no .cpp body (the body lives in Phase 1b's Private/HAL/
//     FDateTime.cpp using Howard Hinnant proleptic-Gregorian
//     algorithms).
//   - This file provides the surface-compile gate (static_assert
//     signature checks) so Phase 1a verifies the interface, and the
//     round-trip property test scaffolding so Phase 1b's body
//     immediately runs against 100 known timestamps with no further
//     test-author work.
//
// Phase 1a runs: surface checks only (the static_asserts below).
// Phase 1b runs: the property test body (gated on XPACT_PHASE_1B_LINKED
// or equivalent; left as a TODO until the test framework integration
// is settled).
//
// =====================================================================

#include "HAL/FDateTime.h"
#include "HAL/FTimespan.h"

#include <cstdint>
#include <string_view>
#include <type_traits>

namespace XCore::HAL::Tests::FDateTimeIso8601
{

// ---------------------------------------------------------------------
// Surface verifications (Phase 1a gate).
// ---------------------------------------------------------------------

static_assert(sizeof(FDateTime) == 8,
              "FDateTime ABI lock duplicate: must be 8 bytes");

static_assert(alignof(FDateTime) == 8,
              "FDateTime alignment lock duplicate: must be 8 bytes");

// Default-constructed FDateTime is the Unix epoch (m_unixMicros = 0).
static_assert(FDateTime().ToUnixMicros() == 0,
              "default-constructed FDateTime is the Unix epoch");

// Constexpr arithmetic.
static_assert(FDateTime(1'000'000).ToUnixMicros() == 1'000'000,
              "FDateTime(N).ToUnixMicros() == N");

static_assert(FDateTime(0).AddDays(1).ToUnixMicros()
                  == 86'400'000'000LL,
              "FDateTime + 1 day == 86,400,000,000 us");

static_assert(FDateTime(0).AddHours(24).ToUnixMicros()
                  == FDateTime(0).AddDays(1).ToUnixMicros(),
              "AddHours(24) == AddDays(1)");

static_assert(FDateTime(0).AddMinutes(60).ToUnixMicros()
                  == FDateTime(0).AddHours(1).ToUnixMicros(),
              "AddMinutes(60) == AddHours(1)");

// FDateTime + FTimespan arithmetic.
static_assert((FDateTime(0) + FTimespan::FromHours(1)).ToUnixMicros()
                  == 3'600'000'000LL,
              "FDateTime + FTimespan::FromHours(1) == 3.6 billion us");

// FDateTime - FDateTime -> FTimespan
static_assert((FDateTime(10'000'000) - FDateTime(1'000'000))
                  == FTimespan::FromMicroseconds(9'000'000),
              "FDateTime - FDateTime -> FTimespan");

// Comparison ordering.
static_assert(FDateTime(1'000) < FDateTime(2'000),
              "FDateTime ordering: smaller-micros < larger-micros");

static_assert(FDateTime(1'000) == FDateTime(1'000),
              "FDateTime equality: same-micros");

// ---------------------------------------------------------------------
// ToIso8601 / ParseIso8601 signature check (Phase 1a gate).
//
// Phase 1a uses Subagent A's XResult.h alias for Result<T, E>. On
// C++20 + missing __cpp_lib_expected toolchains the alias is a
// placeholder class template that triggers a deferred static_assert
// upon instantiation. Mere declaration of FDateTime::ParseIso8601
// returning Result<FDateTime, FDateRangeError> does NOT instantiate
// the template (function declarations may carry incomplete return
// types; the Result specialization is not formed until callers use
// the return value). Phase 1a's signature gate therefore relies on
// the noexcept probe and existence checks below; neither requires
// the return type to be complete.
//
// Note: the canonical Phase 1b runtime test (round-trip property
// across 100 timestamps) instantiates Result<T, E> via .value() and
// gates on Subagent A's tl::expected polyfill landing (or on a
// C++23 toolchain). Phase 1a's gate is signature-only.
// ---------------------------------------------------------------------

// noexcept verification. The noexcept operator evaluates an
// unevaluated expression and does NOT call the function; this works
// even when the return type's template specialization is not yet
// instantiable.
static_assert(noexcept(FDateTime::ParseIso8601(std::string_view{})),
              "FDateTime::ParseIso8601 must be noexcept");

// Argument-type sanity: ParseIso8601 accepts std::string_view. A
// signature drift (e.g., changing to FString) would fail the
// invocability probe below.
static_assert(noexcept(FDateTime::ParseIso8601("1970-01-01T00:00:00.000000Z")),
              "FDateTime::ParseIso8601 must accept const-char* literals "
              "via implicit conversion to std::string_view");

// ---------------------------------------------------------------------
// Phase 1b round-trip property test scaffolding.
//
// The vector below is the canonical 100-timestamp test set; the test
// body verifies that ToIso8601 then ParseIso8601 is identity for every
// entry. The test is GATED on Phase 1b's body landing (the
// `extern "C" void RunFDateTime_Iso8601RoundTripTest();` declaration
// here is implemented by the Phase 1b body of this same file once the
// test framework integration is settled).
//
// For Phase 1a, the static_asserts above are the gate. The constexpr
// vector below is declared here so a Phase 1b reviewer immediately
// sees the test plan; the runtime test body is deferred until the
// test framework lands.
//
// TODO(Phase 1b):
//   1. Fill in the 100 canonical timestamps below (currently 10 are
//      shown as exemplars covering epoch boundaries, DST transitions,
//      leap-year transitions, year-end transitions, sub-second
//      precision, and the proleptic-Gregorian range edges).
//   2. Implement the test body that iterates the array and asserts
//      `ParseIso8601(ToIso8601(dt)).value() == dt` for each entry.
//   3. Wire into the engine's test framework (XTest? FCheck?) once the
//      framework's surface is settled.
//   4. Acceptance criterion D-extra-2 gates on this body passing on
//      Win64 + Linux + Android-ARM64.
// ---------------------------------------------------------------------

namespace
{
    // Exemplar timestamps; final list grows to 100 in Phase 1b.
    constexpr int64_t kRoundTripTimestamps[] =
    {
        0,                       // Unix epoch (1970-01-01T00:00:00.000000Z)
        1'000'000,               // 1970-01-01T00:00:01.000000Z (one second past)
        500'000,                 // 1970-01-01T00:00:00.500000Z (half-second)
        86'400'000'000LL,        // 1970-01-02T00:00:00.000000Z (one day past)
        946'684'800'000'000LL,   // 2000-01-01T00:00:00.000000Z (Y2K)
        1'546'300'800'000'000LL, // 2019-01-01T00:00:00.000000Z (recent)
        1'672'531'199'999'999LL, // 2022-12-31T23:59:59.999999Z (year-end)
        -1,                      // 1969-12-31T23:59:59.999999Z (pre-epoch)
        -1'000'000,              // 1969-12-31T23:59:59.000000Z
        -86'400'000'000LL,       // 1969-12-31T00:00:00.000000Z

        // TODO(Phase 1b): 90 more covering:
        //   - DST cutoffs in major timezones (cross-checked via the
        //     UTC-emitted strings)
        //   - leap-year February 29 -> March 1 transitions (2000, 2004,
        //     2024)
        //   - leap-second-adjusted instants (UTC handling per the
        //     algorithm's contract; the engine does not adjust for
        //     leap seconds in m_unixMicros)
        //   - proleptic-Gregorian range edges (year 0001 and year 9999)
        //   - microsecond-precision edge cases (.000001, .999999)
    };

    // Phase 1b test body sketch (commented out -- requires Result<T,E>
    // value() accessor from XPactMacros.h):
    //
    //   void RunFDateTime_Iso8601RoundTripTest()
    //   {
    //       for (int64_t ts : kRoundTripTimestamps)
    //       {
    //           FDateTime dt(ts);
    //           FString emitted = dt.ToIso8601();
    //           Result<FDateTime, FDateRangeError> parsed =
    //               FDateTime::ParseIso8601(emitted.AsStringView());
    //           XPACT_CHECK(parsed.has_value());
    //           XPACT_CHECK(parsed.value() == dt);
    //       }
    //   }

    // Suppress unused-variable warnings until Phase 1b consumes the
    // array.
    [[maybe_unused]] constexpr auto kNumRoundTripTimestamps =
        sizeof(kRoundTripTimestamps) / sizeof(kRoundTripTimestamps[0]);
}

} // namespace XCore::HAL::Tests::FDateTimeIso8601
