// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FTimespan.Tests/Arithmetic.cpp -- FTimespan arithmetic identities.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 (Date/time). Verifies addition,
// subtraction, and factory-method-equivalence identities on FTimespan.
//
// FTimespan is sim-path-safe (pure arithmetic on int64); every test
// below uses constexpr evaluation so the entire test runs at compile
// time and the static_assert errors fire at the test-author's edit
// site. NO runtime body required -- Phase 1a's FTimespan has full
// inline-constexpr bodies.
//
// =====================================================================

#include "HAL/FTimespan.h"

#include <cstdint>

namespace XCore::HAL::Tests::FTimespanArithmetic
{

// ---------------------------------------------------------------------
// FromXxx round-trip with TotalMicroseconds.
//
// The factory methods MUST produce the correct microsecond count for
// every supported unit; this is the load-bearing invariant for the
// entire FTimespan surface.
// ---------------------------------------------------------------------

static_assert(FTimespan::FromMicroseconds(1).TotalMicroseconds() == 1,
              "FromMicroseconds(1) == 1 us");

static_assert(FTimespan::FromMilliseconds(1).TotalMicroseconds() == 1'000,
              "FromMilliseconds(1) == 1,000 us");

static_assert(FTimespan::FromSeconds(1.0).TotalMicroseconds() == 1'000'000,
              "FromSeconds(1) == 1,000,000 us");

static_assert(FTimespan::FromMinutes(1).TotalMicroseconds() == 60'000'000,
              "FromMinutes(1) == 60,000,000 us");

static_assert(FTimespan::FromHours(1).TotalMicroseconds() == 3'600'000'000LL,
              "FromHours(1) == 3,600,000,000 us");

static_assert(FTimespan::FromDays(1).TotalMicroseconds() == 86'400'000'000LL,
              "FromDays(1) == 86,400,000,000 us");

// ---------------------------------------------------------------------
// TotalSeconds round-trip.
// ---------------------------------------------------------------------

static_assert(FTimespan::FromSeconds(1.5).TotalSeconds() == 1.5,
              "FromSeconds(1.5).TotalSeconds() == 1.5");

static_assert(FTimespan::FromMilliseconds(2'500).TotalSeconds() == 2.5,
              "FromMilliseconds(2500).TotalSeconds() == 2.5");

// ---------------------------------------------------------------------
// Addition identities.
//
// FromHours(2) + FromHours(3) == FromHours(5)  -- the prompt's
// explicit example. All four overloads of arithmetic compose
// correctly.
// ---------------------------------------------------------------------

static_assert((FTimespan::FromHours(2) + FTimespan::FromHours(3))
                  == FTimespan::FromHours(5),
              "FromHours(2) + FromHours(3) == FromHours(5)");

static_assert((FTimespan::FromMinutes(30) + FTimespan::FromMinutes(30))
                  == FTimespan::FromHours(1),
              "FromMinutes(30) + FromMinutes(30) == FromHours(1)");

static_assert((FTimespan::FromSeconds(60.0) + FTimespan::FromSeconds(60.0))
                  == FTimespan::FromMinutes(2),
              "FromSeconds(60) + FromSeconds(60) == FromMinutes(2)");

static_assert((FTimespan::FromMilliseconds(1'000) + FTimespan::FromMilliseconds(1'000))
                  == FTimespan::FromSeconds(2.0),
              "FromMilliseconds(1000) + FromMilliseconds(1000) == FromSeconds(2)");

// ---------------------------------------------------------------------
// Subtraction identities.
//
//   x - y produces the signed difference; a fully-negative span is a
//   first-class value.
// ---------------------------------------------------------------------

static_assert((FTimespan::FromHours(5) - FTimespan::FromHours(3))
                  == FTimespan::FromHours(2),
              "FromHours(5) - FromHours(3) == FromHours(2)");

static_assert((FTimespan::FromHours(3) - FTimespan::FromHours(5))
                  == FTimespan::FromHours(-2),
              "FromHours(3) - FromHours(5) == FromHours(-2)");

// Identity: span - span == zero
static_assert((FTimespan::FromHours(5) - FTimespan::FromHours(5))
                  == FTimespan(),
              "span - span == zero");

// ---------------------------------------------------------------------
// Compound-assign identities.
//
// constexpr-friendly compound-assign exists in C++20 (via the
// constexpr operator+=/-= bodies); verify via a constexpr-lambda.
// ---------------------------------------------------------------------

constexpr bool VerifyCompoundAssign()
{
    FTimespan x = FTimespan::FromHours(2);
    x += FTimespan::FromHours(3);
    if (!(x == FTimespan::FromHours(5))) return false;

    x -= FTimespan::FromHours(2);
    if (!(x == FTimespan::FromHours(3))) return false;

    return true;
}

static_assert(VerifyCompoundAssign(),
              "FTimespan operator+=/-= behave correctly");

// ---------------------------------------------------------------------
// Comparison operator coverage.
//
// C++20 defaulted operator<=> provides ==, !=, <, <=, >, >=. Verify
// each: identity comparison, strict ordering, and equality propagation.
// ---------------------------------------------------------------------

static_assert(FTimespan::FromHours(1) < FTimespan::FromHours(2),
              "ordering: FromHours(1) < FromHours(2)");

static_assert(FTimespan::FromHours(2) > FTimespan::FromHours(1),
              "ordering: FromHours(2) > FromHours(1)");

static_assert(FTimespan::FromHours(2) <= FTimespan::FromHours(2),
              "ordering: FromHours(2) <= FromHours(2)");

static_assert(FTimespan::FromHours(2) >= FTimespan::FromHours(2),
              "ordering: FromHours(2) >= FromHours(2)");

static_assert(FTimespan::FromHours(2) != FTimespan::FromHours(3),
              "FromHours(2) != FromHours(3)");

// ---------------------------------------------------------------------
// Zero-span (default-constructed) identities.
// ---------------------------------------------------------------------

static_assert(FTimespan().TotalMicroseconds() == 0,
              "default-constructed FTimespan is zero");

static_assert(FTimespan() == FTimespan(0),
              "default-constructed == FTimespan(0)");

static_assert((FTimespan::FromHours(5) + FTimespan()) == FTimespan::FromHours(5),
              "FromHours(5) + zero == FromHours(5)");

// ---------------------------------------------------------------------
// Negative-span sanity.
//
// Negative durations are well-defined; the engineering principle is
// "no silent normalisation" -- the user supplied a negative value, we
// keep it that way.
// ---------------------------------------------------------------------

static_assert(FTimespan::FromSeconds(-1.0).TotalMicroseconds() == -1'000'000,
              "FromSeconds(-1) == -1,000,000 us (negative span preserved)");

static_assert(FTimespan::FromHours(-2).TotalSeconds() == -7'200.0,
              "FromHours(-2).TotalSeconds() == -7200");

// ---------------------------------------------------------------------
// ABI lock duplication (mirror of FTimespan.h; catches drift).
// ---------------------------------------------------------------------

static_assert(sizeof(FTimespan) == 8,
              "FTimespan ABI lock duplicate: must be 8 bytes");

} // namespace XCore::HAL::Tests::FTimespanArithmetic
