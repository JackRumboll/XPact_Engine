// Copyright Simgenics. All Rights Reserved.

// =====================================================================
// FDateTime.Tests/Iso8601RoundTripRuntime.cpp -- 100-timestamp body.
// =====================================================================
//
// XCore-4a Rev 3, Section 7.5 acceptance criterion D-extra-2 (Section
// 17.4): "FDateTime round-trip via ToIso8601 -> ParseIso8601 is
// identity bit-exactly across all three platforms."
//
// Phase 1b implementation: bypasses the FString-returning ToIso8601 (which
// returns a default-constructed FString until Phase 1g) via the
// `extern "C" XPACT_TEST_*` shims defined in Private/HAL/FDateTime.cpp.
// The shims expose the underlying byte-buffer emit + parse paths
// directly so the round-trip property is verifiable today without
// blocking on the FString.h dependency.
//
// Test set: 100+ timestamps covering:
//   * Epoch boundaries (0, +/-1, +/-1e6, +/-86400)
//   * Pre-epoch dates
//   * Y2K (2000-01-01)
//   * 2038 boundary (signed-int32 unix-seconds rollover; us-count is
//     well-defined beyond)
//   * Leap-year February 29 transitions (2000, 2004, 2020, 2024, 2096
//     special-cases the divisible-by-100 rule)
//   * Year-end transitions (2022-12-31T23:59:59.999999)
//   * Far-future +2100, +2200, +9999
//   * Microsecond-precision edge cases (.000001, .999999)
//
// The test exhaustively verifies: for each timestamp, emit -> parse
// returns the original micros.
//
// =====================================================================

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>

// Test shims from FDateTime.cpp (declared extern "C").
extern "C" std::size_t XPACT_TEST_EmitIso8601(
    std::int64_t UnixMicros, char* OutBuf, std::size_t BufSize) noexcept;
extern "C" int XPACT_TEST_ParseIso8601(
    const char* InBuf, std::size_t InLen, std::int64_t* OutMicros) noexcept;

namespace
{
    // Helper to compute days-from-epoch for a given (y,m,d) via the
    // documented Howard-Hinnant arithmetic. We re-derive the test
    // timestamps via constexpr math so the test set is bit-exact
    // across the three platforms.
    constexpr std::int64_t DaysFromCivil(int y, int m, int d) noexcept
    {
        if (m <= 2) y -= 1;
        const int era = (y >= 0 ? y : y - 399) / 400;
        const unsigned yoe = static_cast<unsigned>(y - era * 400);
        const unsigned doy = (153u * static_cast<unsigned>(m + (m > 2 ? -3 : 9)) + 2u) / 5u + static_cast<unsigned>(d - 1);
        const unsigned doe = yoe * 365u + yoe / 4u - yoe / 100u + doy;
        return static_cast<std::int64_t>(era) * 146097LL +
               static_cast<std::int64_t>(doe) - 719468LL;
    }

    constexpr std::int64_t MICROS_PER_DAY = 86'400'000'000LL;
    constexpr std::int64_t MICROS_PER_HOUR = 3'600'000'000LL;
    constexpr std::int64_t MICROS_PER_MIN  = 60'000'000LL;
    constexpr std::int64_t MICROS_PER_SEC  = 1'000'000LL;

    constexpr std::int64_t MakeTs(int y, int m, int d, int h, int mn, int s, int us) noexcept
    {
        return DaysFromCivil(y, m, d) * MICROS_PER_DAY +
               static_cast<std::int64_t>(h)  * MICROS_PER_HOUR +
               static_cast<std::int64_t>(mn) * MICROS_PER_MIN +
               static_cast<std::int64_t>(s)  * MICROS_PER_SEC +
               static_cast<std::int64_t>(us);
    }

    // 100+ test timestamps. Most are constructed via MakeTs so the
    // assertion failure mode names a recognisable date.
    constexpr std::int64_t kRoundTripTimestamps[] =
    {
        // --- Epoch boundaries ---
        0,                                          // 1970-01-01T00:00:00.000000Z
        1,                                          // +1 us
        999'999,                                    // 1970-01-01T00:00:00.999999Z
        1'000'000,                                  // +1 second
        86'400'000'000LL,                           // +1 day
        // --- Y2K + signed-int32 boundary + recent ---
        MakeTs(2000, 1, 1, 0, 0, 0, 0),
        MakeTs(2000, 2, 29, 12, 0, 0, 0),           // leap-year Feb 29
        MakeTs(2000, 3, 1, 0, 0, 0, 0),
        MakeTs(2004, 2, 29, 23, 59, 59, 999'999),
        MakeTs(2020, 2, 29, 0, 0, 0, 0),
        MakeTs(2024, 2, 29, 12, 0, 0, 500'000),
        MakeTs(2038, 1, 19, 3, 14, 7, 0),           // signed-int32 boundary
        MakeTs(2038, 1, 19, 3, 14, 8, 0),           // post-boundary
        MakeTs(2022, 12, 31, 23, 59, 59, 999'999),  // year-end edge
        MakeTs(2023, 1, 1, 0, 0, 0, 0),
        MakeTs(2026, 5, 22, 12, 0, 0, 0),           // current dev cycle
        // --- Pre-epoch (negative micros) ---
        -1,                                          // 1969-12-31T23:59:59.999999Z
        -1'000'000,                                  // -1 second
        -86'400'000'000LL,                           // -1 day
        MakeTs(1969, 12, 31, 23, 59, 59, 999'999),
        MakeTs(1900, 1, 1, 0, 0, 0, 0),
        MakeTs(1800, 6, 15, 12, 0, 0, 0),
        // --- Far future ---
        MakeTs(2100, 1, 1, 0, 0, 0, 0),
        MakeTs(2200, 6, 30, 12, 0, 0, 0),
        MakeTs(2400, 2, 29, 0, 0, 0, 0),  // 2400 IS a leap year (divisible by 400)
        MakeTs(2500, 12, 31, 23, 59, 59, 999'999),
        MakeTs(9999, 12, 31, 23, 59, 59, 999'999),
        // --- Microsecond edge cases ---
        MakeTs(2026, 5, 22, 12, 0, 0, 1),
        MakeTs(2026, 5, 22, 12, 0, 0, 999'998),
        MakeTs(2026, 5, 22, 12, 0, 0, 999'999),
        // --- Month / day variations (each month-end) ---
        MakeTs(2026, 1, 31, 0, 0, 0, 0),
        MakeTs(2026, 2, 28, 0, 0, 0, 0),
        MakeTs(2026, 3, 31, 0, 0, 0, 0),
        MakeTs(2026, 4, 30, 0, 0, 0, 0),
        MakeTs(2026, 5, 31, 0, 0, 0, 0),
        MakeTs(2026, 6, 30, 0, 0, 0, 0),
        MakeTs(2026, 7, 31, 0, 0, 0, 0),
        MakeTs(2026, 8, 31, 0, 0, 0, 0),
        MakeTs(2026, 9, 30, 0, 0, 0, 0),
        MakeTs(2026, 10, 31, 0, 0, 0, 0),
        MakeTs(2026, 11, 30, 0, 0, 0, 0),
        MakeTs(2026, 12, 31, 0, 0, 0, 0),
        // --- Hours/Minutes/Seconds full range ---
        MakeTs(2026, 5, 22, 0, 0, 0, 0),
        MakeTs(2026, 5, 22, 1, 0, 0, 0),
        MakeTs(2026, 5, 22, 12, 0, 0, 0),
        MakeTs(2026, 5, 22, 23, 0, 0, 0),
        MakeTs(2026, 5, 22, 23, 59, 0, 0),
        MakeTs(2026, 5, 22, 23, 59, 59, 0),
        MakeTs(2026, 5, 22, 23, 59, 59, 999'999),
        // --- Non-leap-year February 29 boundary (should still parse
        //     Feb 28 correctly) ---
        MakeTs(2023, 2, 28, 0, 0, 0, 0),  // 2023 NOT a leap year
        MakeTs(2100, 2, 28, 0, 0, 0, 0),  // 2100 NOT a leap year (div by 100, not 400)
        // --- Specific historic dates ---
        MakeTs(1969, 7, 20, 20, 17, 0, 0),  // Apollo 11 landing
        MakeTs(2001, 9, 11, 8, 46, 0, 0),
        MakeTs(2020, 3, 11, 0, 0, 0, 0),    // WHO pandemic declaration
        // --- Padding to 100+ entries (microsecond walks at various
        //     base instants) ---
        MakeTs(2026, 1, 1, 0, 0, 0, 1),
        MakeTs(2026, 1, 1, 0, 0, 0, 10),
        MakeTs(2026, 1, 1, 0, 0, 0, 100),
        MakeTs(2026, 1, 1, 0, 0, 0, 1000),
        MakeTs(2026, 1, 1, 0, 0, 0, 10'000),
        MakeTs(2026, 1, 1, 0, 0, 0, 100'000),
        MakeTs(2026, 1, 1, 0, 0, 0, 500'000),
        MakeTs(2026, 1, 1, 0, 0, 0, 999'000),
        MakeTs(2026, 1, 1, 0, 0, 0, 999'999),
        MakeTs(2026, 1, 1, 0, 0, 1, 0),
        MakeTs(2026, 1, 1, 0, 1, 0, 0),
        MakeTs(2026, 1, 1, 1, 0, 0, 0),
        MakeTs(2026, 1, 2, 0, 0, 0, 0),
        MakeTs(2026, 2, 1, 0, 0, 0, 0),
        MakeTs(2027, 1, 1, 0, 0, 0, 0),
        MakeTs(2028, 2, 29, 0, 0, 0, 0),  // 2028 IS leap (div by 4)
        MakeTs(2032, 2, 29, 0, 0, 0, 0),
        MakeTs(2036, 2, 29, 0, 0, 0, 0),
        MakeTs(2040, 2, 29, 0, 0, 0, 0),
        MakeTs(2044, 2, 29, 0, 0, 0, 0),
        MakeTs(2048, 2, 29, 0, 0, 0, 0),
        MakeTs(2052, 2, 29, 0, 0, 0, 0),
        MakeTs(2056, 2, 29, 0, 0, 0, 0),
        MakeTs(2060, 2, 29, 0, 0, 0, 0),
        MakeTs(2064, 2, 29, 0, 0, 0, 0),
        MakeTs(2068, 2, 29, 0, 0, 0, 0),
        MakeTs(2072, 2, 29, 0, 0, 0, 0),
        MakeTs(2076, 2, 29, 0, 0, 0, 0),
        MakeTs(2080, 2, 29, 0, 0, 0, 0),
        MakeTs(2084, 2, 29, 0, 0, 0, 0),
        MakeTs(2088, 2, 29, 0, 0, 0, 0),
        MakeTs(2092, 2, 29, 0, 0, 0, 0),
        MakeTs(2096, 2, 29, 0, 0, 0, 0),  // 2096 IS leap; 2100 is NOT
        // --- Single-second walk through a leap-year February ---
        MakeTs(2024, 2, 28, 23, 59, 58, 999'999),
        MakeTs(2024, 2, 28, 23, 59, 59, 999'999),
        MakeTs(2024, 2, 29, 0, 0, 0, 0),
        MakeTs(2024, 2, 29, 0, 0, 0, 1),
        MakeTs(2024, 2, 29, 23, 59, 59, 999'999),
        MakeTs(2024, 3, 1, 0, 0, 0, 0),
        // --- Year-end across a non-leap to non-leap (1970 -> 1971) ---
        MakeTs(1970, 12, 31, 23, 59, 59, 999'999),
        MakeTs(1971, 1, 1, 0, 0, 0, 0),
        // --- Additional padding to comfortably exceed 100 entries ---
        MakeTs(1980, 6, 15, 0, 0, 0, 0),
        MakeTs(1990, 6, 15, 0, 0, 0, 0),
        MakeTs(2010, 6, 15, 0, 0, 0, 0),
        MakeTs(2030, 6, 15, 0, 0, 0, 0),
        MakeTs(2050, 6, 15, 0, 0, 0, 0),
        MakeTs(2070, 6, 15, 0, 0, 0, 0),
        MakeTs(2090, 6, 15, 0, 0, 0, 0),
        MakeTs(2110, 6, 15, 0, 0, 0, 0),
    };

    constexpr std::size_t kNumTimestamps =
        sizeof(kRoundTripTimestamps) / sizeof(kRoundTripTimestamps[0]);
}

int main()
{
    static_assert(kNumTimestamps >= 100,
                  "Iso8601RoundTrip must verify at least 100 timestamps");

    int Failures = 0;
    for (std::size_t i = 0; i < kNumTimestamps; ++i)
    {
        const std::int64_t Original = kRoundTripTimestamps[i];
        char Buf[64];
        const std::size_t Written = XPACT_TEST_EmitIso8601(Original, Buf, sizeof(Buf));
        if (Written == 0)
        {
            std::cerr << "FAIL[" << i << "] EmitIso8601 returned 0 bytes for "
                      << Original << " us\n";
            ++Failures;
            continue;
        }

        std::int64_t Parsed = 0;
        const int Ok = XPACT_TEST_ParseIso8601(Buf, Written, &Parsed);
        if (!Ok)
        {
            std::cerr << "FAIL[" << i << "] ParseIso8601 rejected emitted string \""
                      << Buf << "\" (original=" << Original << ")\n";
            ++Failures;
            continue;
        }

        if (Parsed != Original)
        {
            std::cerr << "FAIL[" << i << "] round-trip diverged: original="
                      << Original << ", emitted=\"" << Buf
                      << "\", parsed=" << Parsed << "\n";
            ++Failures;
        }
    }

    if (Failures > 0)
    {
        std::cerr << "Iso8601RoundTrip: FAIL (" << Failures << "/"
                  << kNumTimestamps << " mismatches)\n";
        return 1;
    }
    std::cout << "Iso8601RoundTrip: PASS (" << kNumTimestamps
              << " timestamps round-tripped identity)\n";
    return 0;
}
